using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Printers;
using ScanRelay.MockCloud;
using ScanRelay.MockDevices;
using ScanRelay.Service;

namespace ScanRelay.DevHarness;

/// <summary>
/// Runs the whole app on a development machine with nothing real behind it: a mock
/// Microsoft 365, a mock printer, and the sample module signed with a throwaway key.
/// The browser tests drive it, and it is the quickest way to look at the admin screen.
///
///   dotnet run --project tests/DevHarness
///
/// Admin screen: http://localhost:5080   Control: http://localhost:5081
/// </summary>
internal static class HarnessProgram
{
    private static async Task Main(string[] args)
    {
        var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5080;
        var work = Path.Combine(Path.GetTempPath(), "scanrelay-harness-" + Guid.NewGuid().ToString("N"));
        var dataFolder = Path.Combine(work, "data");
        var modulesFolder = Path.Combine(work, "modules");
        Directory.CreateDirectory(work);

        await using var cloud = await MockCloudServer.StartAsync();
        await using var printer = await MockPrinter.StartAsync();
        var state = cloud.State;
        var admin = state.AddUser("Alex Admin");
        var helper = state.AddUser("Harper Helpdesk");
        state.AddUser("Sam Stranger");
        var staff = new[] { "Ann Example", "Bob Example", "Cy Example", "Dee Example", "Eve Example", "Finn Example" }.Select(n => state.AddUser(n)).ToArray();
        state.AddUser("Nora NoDrive", hasDrive: false);
        state.AddUser("Manual Person");
        state.AddUser("Alerts", "alerts@example.com");
        state.AddGroup("ScanRelay Admins", admin);
        state.AddGroup("ScanRelay Helpdesk", helper);
        state.AddGroup("All Staff", [.. staff, admin]);
        state.AddGroup("Front Office", staff[0], staff[1]);
        state.SignInAs = admin.Id;

        // Package, sign and install the sample module as the release pipeline and installer would.
        var key = ModuleSigning.NewKeyPair();
        var keys = new TrustedKeys([key.PublicPem]);
        var packagePath = Path.Combine(work, "scanrelay-sample-1.0.0" + ModulePackage.Extension);
        ModulePackage.Create(PrepareSample(work), packagePath);
        var signature = ModuleSigning.Sign(key.PrivatePem, File.ReadAllBytes(packagePath));
        new ModuleStore(modulesFolder, keys).Install(packagePath, signature, allowCustom: false, DateTime.UtcNow);
        var releaseFolder = Path.Combine(work, "release");
        Directory.CreateDirectory(releaseFolder);
        File.Copy(packagePath, Path.Combine(releaseFolder, Path.GetFileName(packagePath)));
        var index = Encoding.UTF8.GetBytes(ModuleCatalog.Build(releaseFolder, DateTime.UtcNow));
        state.ReleaseAssets[ModuleCatalog.IndexName] = index;
        state.ReleaseAssets[ModuleCatalog.IndexName + ".sig"] = Encoding.UTF8.GetBytes(ModuleSigning.Sign(key.PrivatePem, index));

        var url = $"http://localhost:{port}";
        var app = ServiceHost.Build(
            [
                $"--urls={url}", $"--Url={url}", $"--DataFolder={dataFolder}", $"--ModulesFolder={modulesFolder}",
                $"--Cloud:Login={cloud.LoginUrl}", $"--Cloud:Graph={cloud.GraphUrl}",
                $"--Cloud:GitHubApi={cloud.BaseUrl}/github-api", $"--Cloud:GitHubDownload={cloud.BaseUrl}/github",
                "--PrinterAccount=scanrelay-printer", "--ShareName=Scans$", "--ServerName=scanserver", "--PrinterPasswordProtected=plain:share-password",
                "--environment=Development",
            ],
            builder =>
            {
                builder.Services.AddSingleton(keys);
                builder.Services.AddSingleton<IInstalledPrinters>(new FakeInstalledPrinters(printer));
            });
        await ServiceHost.StartCoreAsync(app);

        // A small control surface, so a test can play the parts of the printer and the Entra admin.
        var control = WebApplication.CreateSlimBuilder();
        control.Logging.ClearProviders();
        control.WebHost.UseUrls($"http://localhost:{port + 1}");
        var c = control.Build();
        c.MapPost("/sign-in-as/{name}", (string name) =>
        {
            var user = state.Users.Values.FirstOrDefault(u => u.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (user is null) return Results.NotFound();
            state.SignInAs = user.Id;
            return Results.Ok();
        });
        c.MapPost("/consent", () =>
        {
            foreach (var registration in state.Applications.Values) registration.ConsentGranted = true;
            return Results.Ok();
        });
        c.MapPost("/scan/{name}", (string name, string? file) =>
        {
            var user = state.Users.Values.First(u => u.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
            var folder = Path.Combine(dataFolder, "Inbox", user.Id.ToString());
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, file ?? $"SCN_{DateTime.Now:HHmmssfff}.pdf"), "%PDF-1.4 harness scan");
            return Results.Ok();
        });
        c.MapPost("/scan-to-folder/{folder}", (string folder, string? file) =>
        {
            Directory.CreateDirectory(Path.Combine(dataFolder, "Inbox", folder));
            File.WriteAllText(Path.Combine(dataFolder, "Inbox", folder, file ?? "scan.pdf"), "%PDF-1.4 harness scan");
            return Results.Ok();
        });
        c.MapPost("/printer/broken/{broken:bool}", (bool broken) =>
        {
            printer.Broken = broken;
            return Results.Ok();
        });
        c.MapPost("/graph/fail/{count:int}", (int count) =>
        {
            state.FailStatus = 500;
            state.FailNextGraphRequests = count;
            return Results.Ok();
        });
        c.MapPost("/group/{group}/remove/{name}", (string group, string name) =>
        {
            var g = state.Groups.Values.First(x => x.DisplayName == group);
            g.Members.Remove(state.Users.Values.First(u => u.DisplayName == name).Id);
            return Results.Ok();
        });
        c.MapPost("/group/{group}/clear", (string group) =>
        {
            state.Groups.Values.First(x => x.DisplayName == group).Members.Clear();
            return Results.Ok();
        });
        c.MapPost("/release/{tag}", (string tag, string? security) =>
        {
            state.Release = (tag, security is null ? tag : tag + " [security]", "Fixes and improvements.\n\nSteps: the normal update.");
            return Results.Ok();
        });
        c.MapGet("/state", () => Results.Json(new
        {
            tenant = state.TenantId,
            printer = new { printer.Address, printer.Port, printer.AdminUser, printer.AdminPassword },
            printerEntries = printer.Managed.Select(e => e.Name).Order(),
            printerOther = printer.Unmanaged.Select(e => e.Name),
            outbox = state.Outbox.Select(m => new { m.Subject, m.To }),
            oneDrive = state.Users.Values.ToDictionary(u => u.DisplayName, u => state.Files(u, "Dokumente/Scans").Select(f => f.Name)),
            dataFolder,
        }));
        await c.StartAsync();

        Console.WriteLine($"Admin screen:  {url}");
        Console.WriteLine($"Control:       http://localhost:{port + 1}/state");
        Console.WriteLine($"Mock printer:  {printer.Address}:{printer.Port}  ({printer.AdminUser} / {printer.AdminPassword})");
        Console.WriteLine("Signs in as Alex Admin. Groups: ScanRelay Admins, ScanRelay Helpdesk, All Staff, Front Office.");
        await app.RunAsync();
    }

    private static string PrepareSample(string work)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanRelay.slnx"))) dir = dir.Parent;
        var output = Path.Combine(dir!.FullName, "modules", "scanrelay", "sample", "bin", configuration, "net10.0");
        var source = Path.Combine(work, "sample-src");
        Directory.CreateDirectory(source);
        File.Copy(Path.Combine(output, "ScanRelay.Modules.Sample.dll"), Path.Combine(source, "ScanRelay.Modules.Sample.dll"));
        File.Copy(Path.Combine(output, "module.yaml"), Path.Combine(source, "module.yaml"));
        return source;
    }

    private sealed class FakeInstalledPrinters(MockPrinter printer) : IInstalledPrinters
    {
        public IReadOnlyList<InstalledPrinter> List() =>
        [
            new("Front office printer", printer.Address, "ScanRelay MockPrinter 1000"),
            new("Old printer in the basement", "192.0.2.50", "Unknown Laser 9000"),
        ];
    }
}
