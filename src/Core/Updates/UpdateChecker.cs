using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScanRelay.Core.Alerts;
using ScanRelay.Core.Install;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Updates;

public sealed record AppUpdate(string Version, string Url, string Notes, bool Security, DateTime FirstSeenUtc, DateTime? EmailedUtc, DateTime? RemindedUtc);

public sealed record ModuleUpdate(string Id, string Installed, string Available);

public sealed record UpdateStatus(AppUpdate? App, IReadOnlyList<ModuleUpdate> Modules, DateTime? CheckedUtc, string? Problem);

/// <summary>
/// Once a day, looks at GitHub for a newer release and newer modules. It tells the
/// admins and explains what to do. It never installs anything.
/// </summary>
public sealed class UpdateChecker(
    HttpClient http,
    InstallConfig install,
    SettingsStore settings,
    ModuleHost modules,
    ModuleCatalog catalog,
    AlertService alerts,
    TimeProvider time,
    ILogger<UpdateChecker> logger)
{
    private const string StateKey = "updates";
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromDays(14);

    public async Task<UpdateStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var stored = await settings.GetStateAsync(StateKey, ct);
        if (stored is null) return new UpdateStatus(null, [], null, null);
        try
        {
            var status = JsonSerializer.Deserialize<UpdateStatus>(stored)!;

            // Once the new version is installed, the notice goes away.
            return status.App is { } app && System.Version.Parse(app.Version) <= ModuleHost.AppVersion ? status with { App = null } : status;
        }
        catch (JsonException)
        {
            return new UpdateStatus(null, [], null, null);
        }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var previous = await GetStatusAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (!s.UpdateCheck || !install.UpdateCheck)
        {
            // A server with the update check turned off gets no notices.
            var off = new UpdateStatus(null, [], null, null);
            await settings.SetStateAsync(StateKey, null, ct);
            return off;
        }

        AppUpdate? app = previous.App;
        IReadOnlyList<ModuleUpdate> moduleUpdates = previous.Modules;
        string? problem = null;
        try
        {
            app = await ReadLatestReleaseAsync(previous.App, now, ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or FormatException or TaskCanceledException)
        {
            problem = "Could not check GitHub for a new version: " + e.Message;
        }

        try
        {
            var index = await catalog.FetchAsync(ct);
            moduleUpdates = [.. modules.Modules
                .Where(m => !m.IsCustom)
                .Select(m => (Installed: m, Latest: index.Modules
                    .Where(c => c.Id == m.Id && c.WorksWithApp(ModuleHost.AppVersion))
                    .OrderByDescending(c => System.Version.Parse(c.Version)).FirstOrDefault()))
                .Where(x => x.Latest is not null && System.Version.Parse(x.Latest.Version) > System.Version.Parse(x.Installed.Installed.Version))
                .Select(x => new ModuleUpdate(x.Installed.Id, x.Installed.Installed.Version, x.Latest!.Version))];
        }
        catch (ModuleException e)
        {
            problem ??= "Could not check GitHub for module updates: " + e.Message;
        }

        // One email per new version, and one reminder after 14 days if it has not been installed.
        if (app is not null && app.EmailedUtc is null)
        {
            if (await SendAsync(app, reminder: false, ct)) app = app with { EmailedUtc = now };
        }
        else if (app is { EmailedUtc: { } sent, RemindedUtc: null } && now - sent >= ReminderAfter)
        {
            if (await SendAsync(app, reminder: true, ct)) app = app with { RemindedUtc = now };
        }

        var fresh = moduleUpdates.Where(m => !previous.Modules.Contains(m)).ToList();
        if (fresh.Count > 0)
        {
            await alerts.SendToAdminsAsync(
                "ScanRelay: " + (fresh.Count == 1 ? "a module update is available" : $"{fresh.Count} module updates are available"),
                string.Join("\n", fresh.Select(m => $"{m.Id}: {m.Installed} → {m.Available}"))
                + "\n\nTo update, run the ScanRelay installer on the server and open the Modules step.",
                ct);
        }

        if (problem is not null) logger.LogWarning("{Problem}", problem);
        var status = new UpdateStatus(app, moduleUpdates, now, problem);
        await settings.SetStateAsync(StateKey, JsonSerializer.Serialize(status), ct);
        return status;
    }

    private async Task<AppUpdate?> ReadLatestReleaseAsync(AppUpdate? previous, DateTime now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{install.Cloud.GitHubApi.TrimEnd('/')}/repos/{install.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("ScanRelay");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = System.Version.Parse(tag.TrimStart('v', 'V'));
        if (version <= ModuleHost.AppVersion) return null;
        var text = version.ToString(3);
        if (previous?.Version == text) return previous;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        return new AppUpdate(
            text,
            root.GetProperty("html_url").GetString() ?? "",
            notes.Length > 6000 ? notes[..6000] + "…" : notes,
            (name + notes).Contains("[security]", StringComparison.OrdinalIgnoreCase),
            now,
            null,
            null);
    }

    private Task<bool> SendAsync(AppUpdate app, bool reminder, CancellationToken ct)
    {
        var subject = "ScanRelay: " + (app.Security ? "security update " : "version ") + app.Version + " is available" + (reminder ? " (reminder)" : "");
        var body = new StringBuilder()
            .AppendLine($"Installed version: {ModuleHost.AppVersion}")
            .AppendLine($"New version: {app.Version}")
            .AppendLine(app.Security ? "This release fixes a security problem." : "This release does not say it fixes a security problem.")
            .AppendLine()
            .AppendLine("What changed, and the steps for this version:")
            .AppendLine(string.IsNullOrWhiteSpace(app.Notes) ? "(The release has no notes.)" : app.Notes.Trim())
            .AppendLine()
            .AppendLine("The normal update:")
            .AppendLine("1. Download the new installer from the link below.")
            .AppendLine("2. Run it on the server as an administrator.")
            .AppendLine("3. The installer stops the service, backs up the database, replaces the program files, updates the installed modules and starts the service.")
            .AppendLine("Scans are not lost during an update. Printers keep writing to the share and the waiting files are processed when the service starts.")
            .AppendLine()
            .AppendLine("Installer: " + app.Url)
            .ToString();
        return alerts.SendToAdminsAsync(subject, body, ct);
    }
}
