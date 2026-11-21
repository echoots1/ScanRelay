using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using ScanRelay.Core.Alerts;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Data;
using ScanRelay.Core.Install;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Health;

public enum HealthState
{
    Ok,
    Warning,
    Problem,
}

/// <summary>One line of the health check: what was looked at, how it is, and what to do if it is not right.</summary>
public sealed record HealthItem(string Name, HealthState State, string Detail);

/// <summary>Checks everything setup depends on and says exactly what is missing.</summary>
public sealed class HealthCheck(
    IDbContextFactory<ScanRelayDbContext> factory,
    DataFolder data,
    InstallConfig install,
    SettingsStore settings,
    ICloud cloud,
    GraphCertificate graphCertificate,
    ModuleHost modules,
    AlertService alerts,
    TimeProvider time)
{
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<HealthItem>> RunAsync(CancellationToken ct = default)
    {
        var items = new List<HealthItem>();
        var s = await settings.GetAsync(ct);

        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await db.Settings.CountAsync(ct);
            items.Add(new("Database", HealthState.Ok, "Open and readable."));
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            items.Add(new("Database", HealthState.Problem, e.Message));
        }

        items.Add(CheckFolder());

        if (string.IsNullOrEmpty(s.TenantId) || string.IsNullOrEmpty(s.ClientId))
        {
            items.Add(new("Entra connection", HealthState.Problem, "Not set up yet. Open Settings and connect to Entra."));
        }
        else
        {
            await CheckCloudAsync(s, items, ct);
        }

        items.Add(CheckCertificate("Graph certificate", TryGet(graphCertificate.Get)));
        if (WebCertificate() is { } web) items.Add(CheckCertificate("Web certificate", web));

        var broken = modules.Modules.Where(m => !m.Usable).ToList();
        items.Add(modules.Modules.Count == 0
            ? new("Printer modules", HealthState.Warning, "No modules are installed. Add them through the installer before adding printers.")
            : broken.Count == 0
                ? new("Printer modules", HealthState.Ok, $"{modules.Modules.Count} loaded.")
                : new("Printer modules", HealthState.Problem, string.Join(" ", broken.Select(m => $"{m.Id}: {m.Problem}"))));
        return items;
    }

    /// <summary>Emails the admins when a certificate is within 30 days of expiring, and again within 7.</summary>
    public async Task AlertOnExpiringCertificatesAsync(CancellationToken ct = default)
    {
        var certificates = new List<(string Name, X509Certificate2? Certificate)>
        {
            ("Graph certificate", TryGet(graphCertificate.Get)),
            ("web certificate", WebCertificate()),
        };
        foreach (var (name, certificate) in certificates)
        {
            if (certificate is null) continue;
            var left = certificate.NotAfter.ToUniversalTime() - time.GetUtcNow().UtcDateTime;
            var stage = left <= TimeSpan.FromDays(7) ? "7" : left <= ExpiryWarning ? "30" : null;
            var key = $"certAlert:{certificate.Thumbprint}";
            if (stage is null || await settings.GetStateAsync(key, ct) == stage) continue;
            await settings.SetStateAsync(key, stage, ct);
            var how = name.StartsWith("Graph", StringComparison.Ordinal)
                ? "Renew it by running the installer's Repair, then connect to Entra again from Settings."
                : "Assign a renewed certificate by running the installer and choosing Modify.";
            await alerts.RaiseAsync(AlertKind.CertificateExpiring, $"The {name} expires on {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}. {how}", ct);
        }
    }

    private async Task CheckCloudAsync(AppSettings s, List<HealthItem> items, CancellationToken ct)
    {
        IReadOnlyList<string> granted;
        try
        {
            granted = await cloud.GetGrantedPermissionsAsync(ct);
            var organisation = await cloud.GetOrganizationNameAsync(ct);
            items.Add(new("Entra connection", HealthState.Ok, $"Signed in to {organisation} with the certificate."));
        }
        catch (CloudException e)
        {
            items.Add(new("Entra connection", HealthState.Problem, e.Message));
            return;
        }

        var missing = AppRegistration.ApplicationPermissionNames.Except(granted).ToList();
        items.Add(missing.Count == 0
            ? new("Permissions", HealthState.Ok, "All four application permissions are approved.")
            : new("Permissions", HealthState.Problem, $"Not approved yet: {string.Join(", ", missing)}. Someone with the Privileged Role Administrator or Global Administrator role must open the consent link in Settings."));
        if (missing.Count > 0) return;

        items.Add(await CheckGroupAsync("Admin group", s.AdminGroupId, required: true, ct));
        items.Add(await CheckGroupAsync("Default sync group", s.DefaultSyncGroupId, required: true, ct));
        if (s.HelpdeskGroupId is not null) items.Add(await CheckGroupAsync("Helpdesk group", s.HelpdeskGroupId, required: false, ct));

        items.Add(string.IsNullOrWhiteSpace(s.AlertSender)
            ? new("Alert emails", HealthState.Warning, "No alert sender is chosen, so no alert emails are sent. Failures still show on the Dashboard.")
            : new("Alert emails", HealthState.Ok,
                $"Sent from {s.AlertSender}. Mail.Send lets the app send as any mailbox unless Exchange Online limits it. Whether that limit is in place cannot be seen from here; Settings shows how to add it."));
    }

    private async Task<HealthItem> CheckGroupAsync(string name, Guid? id, bool required, CancellationToken ct)
    {
        if (id is null) return new(name, required ? HealthState.Problem : HealthState.Warning, "Not chosen yet.");
        try
        {
            var group = await cloud.GetGroupAsync(id.Value, ct);
            if (group is null) return new(name, HealthState.Problem, "The group no longer exists in Entra. Choose another in Settings.");
            var count = (await cloud.GetGroupMembersAsync(id.Value, ct)).Count;
            return new(name, count == 0 ? HealthState.Warning : HealthState.Ok, $"{group.DisplayName}, {count} member(s).");
        }
        catch (CloudException e)
        {
            return new(name, HealthState.Problem, e.Message);
        }
    }

    private HealthItem CheckFolder()
    {
        try
        {
            var probe = Path.Combine(data.Store, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Directory.Exists(data.Inbox)
                ? new("Data folder", HealthState.Ok, "The service can write to it.")
                : new("Data folder", HealthState.Problem, "The Inbox folder is missing. Run the installer's Repair.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new("Data folder", HealthState.Problem, "The service cannot write to the data folder. Run the installer's Repair. " + e.Message);
        }
    }

    private HealthItem CheckCertificate(string name, X509Certificate2? certificate)
    {
        if (certificate is null) return new(name, HealthState.Problem, "Not found, or the service cannot read its key. Run the installer's Repair.");
        var left = certificate.NotAfter.ToUniversalTime() - time.GetUtcNow().UtcDateTime;
        var until = $"Valid until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}.";
        return left <= TimeSpan.Zero ? new(name, HealthState.Problem, "Expired. " + until)
            : left <= ExpiryWarning ? new(name, HealthState.Warning, until + $" That is in {(int)left.TotalDays} day(s).")
            : new(name, HealthState.Ok, until);
    }

    private X509Certificate2? WebCertificate() =>
        string.IsNullOrWhiteSpace(install.WebCertThumbprint) ? null : GraphCertificate.FindInMachineStore(install.WebCertThumbprint);

    private static X509Certificate2? TryGet(Func<X509Certificate2> get)
    {
        try
        {
            return get();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Security.Cryptography.CryptographicException or IOException)
        {
            return null;
        }
    }
}
