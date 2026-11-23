using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScanRelay.Core.Alerts;
using ScanRelay.Core.Data;
using ScanRelay.Core.Install;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Security;
using ScanRelay.Core.Settings;
using ScanRelay.ModuleSdk;

namespace ScanRelay.Core.Printers;

/// <summary>One person as a printer should list them.</summary>
/// <param name="Email">Their email address, when entries carry one so scan to email keeps working.</param>
public sealed record WantedEntry(Guid PersonId, string DisplayName, string? Email = null);

public sealed record PushOutcome(bool Succeeded, int Added, int Updated, int Removed, string? Error, bool Paused = false);

/// <summary>Everything that talks to a printer goes through here, and from here to the printer's module.</summary>
public sealed class PrinterService(
    IDbContextFactory<ScanRelayDbContext> factory,
    ModuleHost modules,
    InstallConfig install,
    SecretProtector protector,
    SettingsStore settings,
    AlertService alerts,
    TimeProvider time,
    ILogger<PrinterService> logger)
{
    /// <summary>How long a module gets for one call to a printer.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The key of the entry that "push a test entry" adds. Its scans go to a folder no person owns.</summary>
    public static readonly Guid TestEntryId = Guid.Parse("00000000-0000-4000-8000-00005ca17e57");

    public async Task<ProbeResult> ProbeAsync(Printer printer, CancellationToken ct = default)
    {
        var (module, problem) = Resolve(printer);
        if (module is null) return new ProbeResult(false, null, null, problem);
        var result = await GuardAsync(
            token => module.Adapter!.ProbeAsync(ToDevice(printer, module), token),
            e => new ProbeResult(false, null, null, e),
            ct);
        if (result.Reachable) await MarkReachableAsync(printer.Id, ct);
        return result;
    }

    /// <summary>Makes the printer's managed entries equal to the wanted list.</summary>
    public async Task<PushOutcome> PushAsync(Printer printer, IReadOnlyList<WantedEntry> wanted, CancellationToken ct = default)
    {
        var (module, problem) = Resolve(printer);
        if (module is null) return new PushOutcome(false, 0, 0, 0, problem, Paused: true);
        if (module.Data!.EntryLimit() is { } limit && wanted.Count > limit)
        {
            return new PushOutcome(false, 0, 0, 0, $"This printer holds at most {limit} entries and {wanted.Count} people are on its list. Give it its own smaller group.");
        }

        var entries = ToEntries(printer, module, wanted);
        var result = await GuardAsync(
            token => module.Adapter!.ApplyAsync(ToDevice(printer, module), entries, token),
            e => new ApplyResult(false, 0, 0, 0, e),
            ct);
        if (result.Succeeded) await MarkReachableAsync(printer.Id, ct);
        return new PushOutcome(result.Succeeded, result.Added, result.Updated, result.Removed, result.Succeeded ? null : result.Error ?? "The module reported a failure without a reason.");
    }

    /// <summary>
    /// Pushes what the printer already holds plus one test entry, so a scan can prove the
    /// printer reaches the share before the full list goes out. The next push removes it.
    /// </summary>
    public async Task<PushOutcome> PushTestEntryAsync(Printer printer, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.Combine(install.DataFolder ?? "", "Inbox", TestEntryId.ToString()));
        await using var db = await factory.CreateDbContextAsync(ct);
        var held = await db.PrinterEntries.Where(e => e.PrinterId == printer.Id).Select(e => new WantedEntry(e.PersonId, e.DisplayName, e.Email)).ToListAsync(ct);
        return await PushAsync(printer, [.. held, new WantedEntry(TestEntryId, "ScanRelay test")], ct);
    }

    /// <summary>What the printer holds in the managed scope, or null when it cannot be read back.</summary>
    public async Task<IReadOnlyList<WantedEntry>?> ReadBackAsync(Printer printer, CancellationToken ct = default)
    {
        var (module, _) = Resolve(printer);
        if (module is null) return null;
        var entries = await GuardAsync<IReadOnlyList<Entry>?>(
            token => module.Adapter!.ReadManagedAsync(ToDevice(printer, module), token), _ => null, ct);
        return entries?.Where(e => Guid.TryParse(e.Key, out _)).Select(e => new WantedEntry(Guid.Parse(e.Key), e.DisplayName, string.IsNullOrWhiteSpace(e.Email) ? null : e.Email)).ToList();
    }

    /// <summary>The file for applying the list by hand when a push fails.</summary>
    public async Task<ImportFile?> ExportAsync(Printer printer, IReadOnlyList<WantedEntry> wanted, CancellationToken ct = default)
    {
        var (module, _) = Resolve(printer);
        if (module is null) return null;
        var entries = ToEntries(printer, module, wanted);
        return await GuardAsync<ImportFile?>(token => module.Adapter!.ExportAsync(ToDevice(printer, module), entries, token)!, _ => null, ct);
    }

    /// <summary>Probes every printer and raises an alert for any that has been out of reach for a day.</summary>
    public async Task CheckReachabilityAsync(CancellationToken ct = default)
    {
        List<Printer> printers;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            printers = await db.Printers.AsNoTracking().ToListAsync(ct);
        }

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var printer in printers)
        {
            var probe = await ProbeAsync(printer, ct);
            if (probe.Reachable) continue;
            var since = printer.LastReachableUtc ?? printer.CreatedUtc;
            if (now - since < TimeSpan.FromHours(24)) continue;
            var key = $"unreachableAlert:{printer.Id}";
            if (DateTime.TryParse(await settings.GetStateAsync(key, ct), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) && now - last < TimeSpan.FromHours(24))
            {
                continue;
            }

            await settings.SetStateAsync(key, now.ToString("o"), ct);
            await alerts.RaiseAsync(AlertKind.PrinterUnreachable, $"{printer.Name} ({printer.Address}) has not answered since {since:u}. {probe.Error}", ct);
        }
    }

    /// <summary>The printer's settings: the module's defaults with this printer's own values on top.</summary>
    public static Dictionary<string, string> EffectiveOptions(Printer printer, ModuleData data)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["protocol"] = data.Scan.Protocols.FirstOrDefault() ?? "smb",
        };
        if (data.Scan.SmbVersions.Count > 0) options["smb_version"] = data.Scan.SmbVersions[^1];
        foreach (var (k, v) in data.Options) options[k] = v;
        try
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(printer.OptionsJson) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(v)) options[k] = v;
            }
        }
        catch (JsonException)
        {
        }

        return options;
    }

    private (LoadedModule? Module, string? Problem) Resolve(Printer printer)
    {
        var module = modules.Find(printer.ModuleId);
        if (module is null) return (null, $"The module \"{printer.ModuleId}\" is not installed. Add it through the installer.");
        return module.Usable ? (module, null) : (null, $"The module \"{printer.ModuleId}\" cannot be used. {module.Problem}");
    }

    private Device ToDevice(Printer printer, LoadedModule module) => new(
        printer.Address,
        printer.AdminUser,
        printer.AdminPasswordProtected is { } p ? protector.UnprotectBytes(p) : null,
        EffectiveOptions(printer, module.Data!));

    private List<Entry> ToEntries(Printer printer, LoadedModule module, IReadOnlyList<WantedEntry> wanted)
    {
        var options = EffectiveOptions(printer, module.Data!);
        var protocol = options["protocol"].ToLowerInvariant() switch
        {
            "ftp" => ScanProtocol.Ftp,
            "sftp" => ScanProtocol.Sftp,
            _ => ScanProtocol.Smb,
        };
        var host = options.GetValueOrDefault("scan_host") is { Length: > 0 } h ? h : install.ServerName ?? Environment.MachineName;
        var account = install.PrinterAccount ?? "";
        var user = protocol == ScanProtocol.Smb && !account.Contains('\\') ? $"{host}\\{account}" : account;
        var password = PrinterAccountSecret.Read(install);
        var share = install.ShareName ?? "";
        return [.. wanted.Select(w => new Entry(
            w.PersonId.ToString(),
            w.DisplayName,
            new ScanDestination(
                protocol,
                host,
                protocol == ScanProtocol.Smb ? $"{share}\\{w.PersonId}" : $"{options.GetValueOrDefault("scan_root", "").TrimEnd('/')}/{w.PersonId}",
                user,
                password),
            w.Email))];
    }

    private async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> call, Func<string, T> onError, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            return await call(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return onError("The printer did not answer in time.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A module is outside code. Whatever it throws must not stop the service.
            logger.LogWarning("A module call failed: {Type}: {Reason}", e.GetType().Name, e.Message);
            return onError(e.Message);
        }
    }

    private async Task MarkReachableAsync(int printerId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Printers.Where(p => p.Id == printerId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.LastReachableUtc, time.GetUtcNow().UtcDateTime), ct);
    }
}

/// <summary>The printer account's password, as the installer stored it.</summary>
public static class PrinterAccountSecret
{
    private const string PlainPrefix = "plain:";

    public static string Read(InstallConfig install)
    {
        var stored = install.PrinterPasswordProtected;
        if (string.IsNullOrEmpty(stored)) return "";
        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal)) return stored[PlainPrefix.Length..];
        if (!OperatingSystem.IsWindows()) return "";
        return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
            Convert.FromBase64String(stored), null, System.Security.Cryptography.DataProtectionScope.LocalMachine));
    }

    /// <summary>Protects the password so that only this machine can read it back.</summary>
    public static string Protect(string password) =>
        OperatingSystem.IsWindows()
            ? Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(password), null, System.Security.Cryptography.DataProtectionScope.LocalMachine))
            : PlainPrefix + password;
}
