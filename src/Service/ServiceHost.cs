using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ScanRelay.Admin;
using ScanRelay.Admin.Components;
using ScanRelay.Core;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Data;
using ScanRelay.Core.Install;
using ScanRelay.Core.Jobs;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Printers;
using ScanRelay.Core.Settings;
using ScanRelay.Core.Sync;

namespace ScanRelay.Service;

/// <summary>Builds the one process that is the service, the admin screen and the background work.</summary>
public static class ServiceHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Host.UseWindowsService(options => options.ServiceName = "ScanRelay");

        // What the installer decided sits beside the program files. The command line can
        // override it on a development machine.
        var installFile = Path.Combine(AppContext.BaseDirectory, InstallConfig.FileName);
        builder.Configuration.AddJsonFile(installFile, optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables("SCANRELAY_");
        builder.Configuration.AddCommandLine(args);

        var install = new InstallConfig();
        builder.Configuration.Bind(install);
        if (string.IsNullOrWhiteSpace(install.DataFolder)) install.DataFolder = DefaultDataFolder();
        if (string.IsNullOrWhiteSpace(install.ModulesFolder)) install.ModulesFolder = Path.Combine(AppContext.BaseDirectory, "Modules");
        var dataFolder = new DataFolder(install.DataFolder);
        var stamp = new InstallStamp(File.Exists(installFile) ? File.GetLastWriteTimeUtc(installFile).ToString("o") : "none");

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.AddProvider(new FileLoggerProvider(dataFolder.Logs));
        if (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            AddEventLog(builder.Logging);
        }

        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        builder.Services.AddScanRelayCore(install, dataFolder);
        builder.Services.AddSingleton(stamp);
        builder.Services.AddHostedService<IntakeWorker>();
        builder.Services.AddHostedService<ScheduleWorker>();
        builder.Services.AddScanRelayAuth();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddScanRelayAdmin();

        // An explicit --urls is for development and tests. Otherwise listen where the installer said.
        if (string.IsNullOrEmpty(builder.Configuration["urls"]))
        {
            var url = new Uri(install.Url);
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(url.Port, listen =>
            {
                if (url.Scheme == Uri.UriSchemeHttps) listen.UseHttps(WebCertificate(install, url.Host));
            }));
        }

        configure?.Invoke(builder);
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            // Until sign-in is set up, the screen answers only on the server itself.
            var gate = context.RequestServices.GetRequiredService<SetupGate>();
            if (gate.IsOpen && !SetupGate.IsLocal(context) && !context.Request.Path.StartsWithSegments("/health"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("ScanRelay is not set up yet. Open the admin screen on the server itself to finish setup.");
                return;
            }

            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets();
        MapEndpoints(app);
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization(Roles.Viewer);
        return app;
    }

    public static async Task StartCoreAsync(WebApplication app)
    {
        await app.Services.InitializeScanRelayAsync();
        await app.Services.GetRequiredService<SetupGate>().RefreshAsync();

        // A changed tenant or app registration takes effect at the next sign-in, without a restart.
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var cache = app.Services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
        var tokens = app.Services.GetRequiredService<AppTokenProvider>();
        settings.Changed += _ =>
        {
            cache.TryRemove(Schemes.Oidc);
            tokens.Reset();
        };

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ScanRelay");
        var install = app.Services.GetRequiredService<InstallConfig>();
        logger.LogInformation("ScanRelay {Version} starting. Admin screen: {Url}", ModuleHost.AppVersion, install.Url);
        _ = app.Services.GetRequiredService<ModuleHost>().Modules;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", async (IDbContextFactory<ScanRelayDbContext> factory) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var ok = await db.Database.CanConnectAsync();
            return Results.Json(
                new { status = ok ? "ok" : "degraded", version = ModuleHost.AppVersion.ToString(3) },
                statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        app.MapGet("/signin", async (HttpContext context, SettingsStore settings, string? returnUrl) =>
        {
            var s = await settings.GetAsync();
            if (string.IsNullOrEmpty(s.TenantId) || string.IsNullOrEmpty(s.ClientId) || s.AdminGroupId is null) return Results.Redirect("/setup");
            var target = IsLocalPath(returnUrl) ? returnUrl! : "/";
            return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [Schemes.Oidc]);
        }).AllowAnonymous();

        app.MapGet("/signout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/denied?reason=signed-out");
        }).AllowAnonymous();

        app.MapGet("/denied", (string? reason) => Results.Content(Pages.Simple(reason switch
        {
            "signed-out" => ("Signed out", "You are signed out of ScanRelay.", true),
            "not-allowed" => ("No access", "You signed in, but you are not in the group that can open the ScanRelay admin screen. Ask IT to add you to it.", true),
            "failed" => ("Sign-in failed", "The sign-in could not be completed. Try again. If it keeps failing, an administrator can open Settings from the server and run the health check.", true),
            _ => ("No access", "Your role does not allow that page.", false),
        }), "text/html; charset=utf-8")).AllowAnonymous();

        // Opening a scan is for the Admin role only, and is written to the audit log.
        app.MapGet("/files/job/{id:guid}", async (Guid id, ClaimsPrincipal user, IDbContextFactory<ScanRelayDbContext> factory, JobPipeline pipeline, AuditLog audit) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var job = await db.Jobs.FindAsync(id);
            if (job is null) return Results.NotFound();
            var path = pipeline.StorePath(job);
            if (job.FileDeletedUtc is not null || !File.Exists(path)) return Results.NotFound("The file is no longer on the server.");
            await audit.WriteAsync(ToActor(user), "Scan file opened", id.ToString(), $"Folder {job.FolderName}");
            var contentType = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream";
            return Results.File(path, contentType, job.UploadedFileName is { Length: > 0 } name && Path.HasExtension(name) ? name : job.OriginalFileName);
        }).RequireAuthorization(Roles.Admin);

        // The file for applying a printer's list by hand when a push fails.
        app.MapGet("/files/printer/{id:int}/import", async (int id, ClaimsPrincipal user, IDbContextFactory<ScanRelayDbContext> factory, PrinterService printers, AuditLog audit) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var printer = await db.Printers.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id);
            if (printer is null) return Results.NotFound();
            var wanted = printer.Entries.Select(e => new WantedEntry(e.PersonId, e.DisplayName)).OrderBy(e => e.DisplayName).ToList();
            var file = await printers.ExportAsync(printer, wanted);
            if (file is null) return Results.NotFound("This printer's module could not produce an import file.");
            await audit.WriteAsync(ToActor(user), "Import file downloaded", printer.Name);
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).RequireAuthorization(Roles.Admin);

        // The public half of the Graph certificate, for creating the app registration by hand.
        app.MapGet("/files/graph-certificate", (GraphCertificate certificate) =>
            Results.File(Convert.FromBase64String(certificate.PublicKeyBase64()), "application/pkix-cert", "scanrelay-graph.cer"))
            .RequireAuthorization(Roles.Admin);

        app.MapGet("/files/settings-export", async (ClaimsPrincipal user, SettingsStore settings, AuditLog audit) =>
        {
            var json = await settings.ExportAsync();
            await audit.WriteAsync(ToActor(user), "Settings exported");
            return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "scanrelay-settings.json");
        }).RequireAuthorization(Roles.Admin);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddEventLog(ILoggingBuilder logging) => logging.AddEventLog(o => o.SourceName = "ScanRelay");

    /// <summary>True for a path on this site. Anything that a browser could read as another site is refused.</summary>
    public static bool IsLocalPath(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) && !url.Any(char.IsControl);

    public static Actor ToActor(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("oid"), out var id) ? new Actor(id, user.Identity?.Name ?? id.ToString()) : Actor.Setup;

    private static X509Certificate2 WebCertificate(InstallConfig install, string host)
    {
        if (!string.IsNullOrWhiteSpace(install.WebCertThumbprint))
        {
            return GraphCertificate.FindInMachineStore(install.WebCertThumbprint)
                ?? throw new InvalidOperationException(
                    "The web certificate chosen by the installer is not in the machine certificate store, or the service cannot read its key. Run the installer's Repair.");
        }

        // No installer has run: a development machine. Make a certificate that lasts as long as the process.
        using var temporary = GraphCertificate.CreateSelfSigned($"CN={host}", DateTimeOffset.UtcNow, years: 1, [host, "localhost"]);
        return X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null);
    }

    private static string DefaultDataFolder() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScanRelay")
            : Path.Combine(AppContext.BaseDirectory, "data");
}

internal static class Pages
{
    /// <summary>A plain page for the few moments outside the admin screen.</summary>
    public static string Simple((string Title, string Text, bool OfferSignIn) page)
    {
        Func<string, string> encode = System.Net.WebUtility.HtmlEncode!;
        var link = page.OfferSignIn ? "<a href=\"/signin\">Sign in</a>" : "<a href=\"/\">Back to the Dashboard</a>";
        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{encode(page.Title)}} · ScanRelay</title>
            <style>body{font:16px/1.5 system-ui,sans-serif;background:#f4f5f7;color:#1c2330;margin:0;display:grid;place-items:center;min-height:100vh}
            main{background:#fff;border:1px solid #d9dde3;border-radius:8px;padding:32px;max-width:420px}h1{font-size:20px;margin:0 0 12px}
            a{display:inline-block;margin-top:16px;color:#0b5cad}</style></head>
            <body><main><h1>{{encode(page.Title)}}</h1><p>{{encode(page.Text)}}</p>
            {{link}}</main></body></html>
            """;
    }
}
