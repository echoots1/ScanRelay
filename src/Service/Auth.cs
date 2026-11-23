using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ScanRelay.Admin;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Install;
using ScanRelay.Core.Settings;

namespace ScanRelay.Service;

public static class Schemes
{
    public const string Smart = "Smart";
    public const string SetupOpen = "SetupOpen";
    public const string Oidc = "oidc";
}

/// <summary>
/// Until setup is finished the admin screen has no sign-in, so it answers only on the
/// server itself. The window closes for good once a member of the admin group has signed in.
/// An installer Repair can reopen it.
/// </summary>
public sealed class SetupGate
{
    private readonly SettingsStore _settings;
    private readonly InstallConfig _install;
    private readonly AuditLog _audit;
    private readonly string _installStamp;
    private volatile bool _open = true;

    public SetupGate(SettingsStore settings, InstallConfig install, AuditLog audit, InstallStamp stamp)
    {
        _settings = settings;
        _install = install;
        _audit = audit;
        _installStamp = stamp.Value;
    }

    public bool IsOpen => _open;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var s = await _settings.GetAsync(ct);
        var reopened = _install.ReopenSetup && await _settings.GetStateAsync("reopenHandled", ct) != _installStamp;
        _open = !s.SetupComplete || reopened;
    }

    /// <summary>Called when a member of the admin group signs in. Ends the open window.</summary>
    public async Task CloseAsync(Actor actor, CancellationToken ct = default)
    {
        if (!_open) return;
        await _settings.UpdateAsync(s => s with { SetupComplete = true }, actor, ct);
        await _settings.SetStateAsync("reopenHandled", _installStamp, ct);
        await _audit.WriteAsync(actor, "Setup finished", detail: "The admin screen now needs sign-in.", ct: ct);
        _open = false;
    }

    /// <summary>True when the request comes from the server itself and not through a proxy.</summary>
    public static bool IsLocal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return false;
        return !context.Request.Headers.ContainsKey("X-Forwarded-For") && !context.Request.Headers.ContainsKey("Forwarded");
    }
}

/// <summary>Identifies this install of the program files, so a Repair's "reopen setup" is honoured once.</summary>
public sealed record InstallStamp(string Value);

/// <summary>While setup is open, a local visitor acts as an Admin named Setup.</summary>
public sealed class SetupOpenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SetupGate gate)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!gate.IsOpen || !SetupGate.IsLocal(Context)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(
            [new Claim("name", Actor.Setup.Name), new Claim(ClaimTypes.Role, Roles.Admin), new Claim("setup", "true")],
            Schemes.SetupOpen, "name", ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Schemes.SetupOpen)));
    }
}

/// <summary>Works out a person's role from the admin and helpdesk groups named in Settings.</summary>
public sealed class RoleResolver(SettingsStore settings, ICloud cloud)
{
    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var roles = new List<string>();
        if (s.AdminGroupId is { } admins && await cloud.IsMemberAsync(userId, admins, ct)) roles.Add(Roles.Admin);
        if (s.HelpdeskGroupId is { } helpdesk && await cloud.IsMemberAsync(userId, helpdesk, ct)) roles.Add(Roles.Helpdesk);
        return roles;
    }

    public static ClaimsPrincipal BuildPrincipal(Guid userId, string name, string? userName, IEnumerable<string> roles, DateTimeOffset checkedAt)
    {
        var claims = new List<Claim>
        {
            new("oid", userId.ToString()),
            new("name", name),
            new("preferred_username", userName ?? ""),
            new("roles_checked", checkedAt.ToUnixTimeSeconds().ToString()),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", ClaimTypes.Role));
    }
}

/// <summary>Points sign-in at whatever tenant and app registration Settings currently holds.</summary>
public sealed class ConfigureOidc(SettingsStore settings, InstallConfig install) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) => Configure(Schemes.Oidc, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != Schemes.Oidc) return;
        var s = settings.GetAsync().GetAwaiter().GetResult();
        var login = install.Cloud.Login.TrimEnd('/');
        var connected = !string.IsNullOrEmpty(s.TenantId) && !string.IsNullOrEmpty(s.ClientId);
        options.Authority = $"{login}/{(connected ? s.TenantId : "organizations")}/v2.0";
        options.ClientId = connected ? s.ClientId : "not-connected";
        options.RequireHttpsMetadata = login.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.CallbackPath = "/signin-oidc";
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.TokenValidationParameters.NameClaimType = "name";
        options.Events = new OpenIdConnectEvents
        {
            // The service proves who it is with its certificate. There is no client secret.
            OnAuthorizationCodeReceived = context =>
            {
                var tokens = context.HttpContext.RequestServices.GetRequiredService<AppTokenProvider>();
                context.TokenEndpointRequest!.ClientAssertionType = AppTokenProvider.ClientAssertionType;
                context.TokenEndpointRequest.ClientAssertion = tokens.NewAssertion(s.TenantId!, s.ClientId!);
                return Task.CompletedTask;
            },
            // Through a reverse proxy, the address the browser used is the proxy's, and that is where
            // Entra must send the person back. The proxy may or may not pass its own name on, so
            // either its name or its forwarding headers count.
            OnRedirectToIdentityProvider = async context =>
            {
                var current = await context.HttpContext.RequestServices.GetRequiredService<SettingsStore>().GetAsync();
                if (current.ProxyUrl() is { } proxy && AuthSetup.CameThroughProxy(context.Request, proxy))
                {
                    context.ProtocolMessage.RedirectUri = AppRegistration.RedirectUri(proxy);
                }
            },
            OnTokenValidated = async context =>
            {
                var services = context.HttpContext.RequestServices;
                var principal = context.Principal!;
                if (!Guid.TryParse(principal.FindFirstValue("oid"), out var userId))
                {
                    context.Fail("The sign-in did not say who you are.");
                    return;
                }

                IReadOnlyList<string> roles;
                try
                {
                    roles = await services.GetRequiredService<RoleResolver>().GetRolesAsync(userId, context.HttpContext.RequestAborted);
                }
                catch (CloudException e)
                {
                    context.Fail("Your group membership could not be checked: " + e.Message);
                    return;
                }

                if (roles.Count == 0)
                {
                    context.Fail("not-allowed");
                    return;
                }

                var name = principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username") ?? userId.ToString();
                context.Principal = RoleResolver.BuildPrincipal(userId, name, principal.FindFirstValue("preferred_username"), roles, DateTimeOffset.UtcNow);
                var audit = services.GetRequiredService<AuditLog>();
                var actor = new Actor(userId, name);
                await audit.WriteAsync(actor, "Signed in", detail: string.Join(", ", roles));
                if (roles.Contains(Roles.Admin)) await services.GetRequiredService<SetupGate>().CloseAsync(actor);
            },
            OnRemoteFailure = context =>
            {
                var reason = context.Failure?.Message == "not-allowed" ? "not-allowed" : "failed";
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ScanRelay.SignIn")
                    .LogWarning("A sign-in did not complete: {Reason}", context.Failure?.Message);
                context.Response.Redirect("/denied?reason=" + reason);
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }
}

public static class AuthSetup
{
    public static bool CameThroughProxy(HttpRequest request, string proxyUrl) =>
        string.Equals(request.Host.Host, new Uri(proxyUrl).Host, StringComparison.OrdinalIgnoreCase)
        || request.Headers.ContainsKey("X-Forwarded-For") || request.Headers.ContainsKey("X-Forwarded-Host") || request.Headers.ContainsKey("Forwarded");


    /// <summary>How often a signed-in person's group membership is looked at again.</summary>
    public static readonly TimeSpan RecheckRoles = TimeSpan.FromMinutes(30);

    public static void AddScanRelayAuth(this IServiceCollection services)
    {
        services.AddSingleton<SetupGate>();
        services.AddSingleton<RoleResolver>();
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, ConfigureOidc>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = Schemes.Smart;
                options.DefaultChallengeScheme = Schemes.Oidc;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddPolicyScheme(Schemes.Smart, null, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var gate = context.RequestServices.GetRequiredService<SetupGate>();
                    return gate.IsOpen && SetupGate.IsLocal(context) ? Schemes.SetupOpen : CookieAuthenticationDefaults.AuthenticationScheme;
                };
                options.ForwardChallenge = Schemes.Oidc;
                options.ForwardForbid = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, SetupOpenHandler>(Schemes.SetupOpen, null)
            .AddCookie(options =>
            {
                options.Cookie.Name = "ScanRelay";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.AccessDeniedPath = "/denied";
                options.Events.OnValidatePrincipal = RecheckAsync;
            })
            .AddOpenIdConnect(Schemes.Oidc, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy(Roles.Viewer, p => p.RequireRole(Roles.Admin, Roles.Helpdesk))
            .AddPolicy(Roles.Admin, p => p.RequireRole(Roles.Admin));
        services.AddCascadingAuthenticationState();
    }

    /// <summary>Someone removed from the admin group loses access within half an hour, not at the end of their session.</summary>
    private static async Task RecheckAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal!;
        if (!long.TryParse(principal.FindFirstValue("roles_checked"), out var seconds) || !Guid.TryParse(principal.FindFirstValue("oid"), out var userId))
        {
            context.RejectPrincipal();
            return;
        }

        if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds) < RecheckRoles) return;
        try
        {
            var roles = await context.HttpContext.RequestServices.GetRequiredService<RoleResolver>().GetRolesAsync(userId, context.HttpContext.RequestAborted);
            if (roles.Count == 0)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            context.ReplacePrincipal(RoleResolver.BuildPrincipal(
                userId, principal.FindFirstValue("name") ?? "", principal.FindFirstValue("preferred_username"), roles, DateTimeOffset.UtcNow));
            context.ShouldRenew = true;
        }
        catch (CloudException)
        {
            // Graph is having trouble. Keep the person signed in and look again next time.
        }
    }
}
