using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ScanRelay.Core.Audit;

namespace ScanRelay.Admin;

public static class Roles
{
    /// <summary>Everything.</summary>
    public const string Admin = "Admin";

    /// <summary>View jobs and runs, and re-run them.</summary>
    public const string Helpdesk = "Helpdesk";

    /// <summary>Policy: may open the admin screen at all.</summary>
    public const string Viewer = "Viewer";
}

/// <summary>Who is using this browser tab.</summary>
public sealed class CurrentUser(AuthenticationStateProvider auth)
{
    public async Task<ClaimsPrincipal> GetAsync() => (await auth.GetAuthenticationStateAsync()).User;

    public async Task<Actor> ActorAsync()
    {
        var user = await GetAsync();
        return Guid.TryParse(user.FindFirstValue("oid"), out var id) ? new Actor(id, user.Identity?.Name ?? id.ToString()) : Actor.Setup;
    }

    public async Task<bool> IsAdminAsync() => (await GetAsync()).IsInRole(Roles.Admin);
}

/// <summary>
/// Whether the person is using a browser on the server itself. Some things only make sense
/// there, such as opening setup, which runs on the server's own screen.
/// </summary>
public sealed class LocalVisitor(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor)
{
    /// <summary>The link that opens setup at its Modules step. The installer registers it on the server.</summary>
    public const string ModulesSetupPath = "/local/modules-setup";

    // Read once, when the page's connection is made: that request comes from the person's own browser.
    public bool IsLocal { get; } = accessor.HttpContext is { } context && IsLocalRequest(context);

    /// <summary>True when the request comes from the server itself and not through a proxy.</summary>
    public static bool IsLocalRequest(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return false;
        return !context.Request.Headers.ContainsKey("X-Forwarded-For") && !context.Request.Headers.ContainsKey("Forwarded");
    }
}

public static class AdminRegistration
{
    public static IServiceCollection AddScanRelayAdmin(this IServiceCollection services) => services.AddScoped<CurrentUser>().AddScoped<LocalVisitor>();
}

public static class Fmt
{
    /// <summary>A stored UTC time in the server's own time zone.</summary>
    public static string Time(DateTime? utc) =>
        utc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "";

    public static string Ago(DateTime? utc, DateTime nowUtc)
    {
        if (utc is not { } t) return "never";
        var span = nowUtc - t;
        return span.TotalSeconds < 60 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalHours < 48 ? $"{(int)span.TotalHours} h ago"
            : $"{(int)span.TotalDays} days ago";
    }

    public static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
