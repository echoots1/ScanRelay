using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Data;
using ScanRelay.Core.Security;

namespace ScanRelay.Core.Settings;

/// <summary>Reads and writes settings, secrets and small pieces of service state in the database.</summary>
public sealed class SettingsStore(
    IDbContextFactory<ScanRelayDbContext> factory,
    SecretProtector protector,
    AuditLog audit,
    TimeProvider time)
{
    private const string StatePrefix = "state:";
    private const string SecretPrefix = "secret:";
    private static readonly PropertyInfo[] Properties = typeof(AppSettings).GetProperties();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppSettings? _cached;

    /// <summary>Raised after settings are saved.</summary>
    public event Action<AppSettings>? Changed;

    public async Task<AppSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } cached) return cached;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null) return _cached;
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.Settings.AsNoTracking()
                .Where(s => !s.IsSecret && !s.Key.StartsWith(StatePrefix))
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            return _cached = FromRows(rows);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Saves the settings and writes one audit entry per value that changed.</summary>
    public async Task SaveAsync(AppSettings settings, Actor actor, CancellationToken ct = default)
    {
        var problems = settings.Validate();
        if (problems.Count > 0) throw new SettingsException(problems);

        var before = ToRows(await GetAsync(ct));
        var after = ToRows(settings);
        var now = time.GetUtcNow().UtcDateTime;

        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            foreach (var (key, value) in after)
            {
                if (before.TryGetValue(key, out var old) && old == value) continue;
                var row = await db.Settings.FindAsync([key], ct);
                if (row is null) db.Settings.Add(new Setting { Key = key, Value = value, UpdatedUtc = now });
                else (row.Value, row.UpdatedUtc) = (value, now);
                db.AuditLog.Add(new AuditEntry
                {
                    AtUtc = now,
                    ActorId = actor.Id,
                    ActorName = actor.Name,
                    Action = "Setting changed",
                    Target = key,
                    Detail = $"{old ?? "(none)"} → {value ?? "(none)"}",
                });
            }

            await db.SaveChangesAsync(ct);
            _cached = settings;
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke(settings);
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, Actor actor, CancellationToken ct = default) =>
        await SaveAsync(change(await GetAsync(ct)), actor, ct);

    /// <summary>Stores a secret encrypted. It is never shown again.</summary>
    public async Task SetSecretAsync(string name, string? value, Actor actor, CancellationToken ct = default)
    {
        await SetRowAsync(SecretPrefix + name, value is null ? null : protector.Protect(value), isSecret: true, ct);
        await audit.WriteAsync(actor, value is null ? "Secret cleared" : "Secret set", name, ct: ct);
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken ct = default)
    {
        var value = await GetRowAsync(SecretPrefix + name, ct);
        return value is null ? null : protector.Unprotect(value);
    }

    /// <summary>Small values the service remembers between runs, such as when it last checked for updates.</summary>
    public Task<string?> GetStateAsync(string name, CancellationToken ct = default) => GetRowAsync(StatePrefix + name, ct);

    public Task SetStateAsync(string name, string? value, CancellationToken ct = default) =>
        SetRowAsync(StatePrefix + name, value, isSecret: false, ct);

    /// <summary>A moment the service remembers, such as when it last sent a certain alert. Null when it never has.</summary>
    public async Task<DateTime?> GetStateTimeAsync(string name, CancellationToken ct = default) =>
        DateTime.TryParse(await GetStateAsync(name, ct), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when.ToUniversalTime() : null;

    public Task SetStateTimeAsync(string name, DateTime utc, CancellationToken ct = default) => SetStateAsync(name, utc.ToString("o"), ct);

    /// <summary>The settings as one JSON document, for backup or moving servers. Secrets are left out.</summary>
    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        var rows = ToRows(await GetAsync(ct));
        rows.Remove(nameof(AppSettings.SetupComplete));
        var document = new SettingsExport("ScanRelay settings", 1, rows);
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Replaces the settings with an exported file. Whether setup is complete is never imported.</summary>
    public async Task ImportAsync(string json, Actor actor, CancellationToken ct = default)
    {
        SettingsExport? document;
        try
        {
            document = JsonSerializer.Deserialize<SettingsExport>(json);
        }
        catch (JsonException)
        {
            throw new SettingsException(["That file is not a ScanRelay settings export."]);
        }

        if (document?.Settings is null || document.Format != "ScanRelay settings")
        {
            throw new SettingsException(["That file is not a ScanRelay settings export."]);
        }

        var current = await GetAsync(ct);
        var rows = new Dictionary<string, string?>(document.Settings)
        {
            [nameof(AppSettings.SetupComplete)] = current.SetupComplete.ToString(),
        };
        AppSettings imported;
        try
        {
            imported = FromRows(rows);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new SettingsException(["That file contains a value that could not be read."]);
        }

        await SaveAsync(imported, actor, ct);
        await audit.WriteAsync(actor, "Settings imported", ct: ct);
    }

    private async Task<string?> GetRowAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;
    }

    private async Task SetRowAsync(string key, string? value, bool isSecret, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Settings.FindAsync([key], ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (value is null)
        {
            if (row is not null) db.Settings.Remove(row);
        }
        else if (row is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value, IsSecret = isSecret, UpdatedUtc = now });
        }
        else
        {
            (row.Value, row.UpdatedUtc) = (value, now);
        }

        await db.SaveChangesAsync(ct);
    }

    private static Dictionary<string, string?> ToRows(AppSettings settings) =>
        Properties.ToDictionary(
            p => p.Name,
            p => p.GetValue(settings) switch
            {
                null => null,
                var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
            });

    private static AppSettings FromRows(IReadOnlyDictionary<string, string?> rows)
    {
        var settings = new AppSettings();
        foreach (var property in Properties)
        {
            if (!rows.TryGetValue(property.Name, out var text)) continue;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = string.IsNullOrEmpty(text) && (type != typeof(string))
                ? null
                : type == typeof(string) ? text
                : type == typeof(Guid) ? Guid.Parse(text!)
                : type == typeof(bool) ? bool.Parse(text!)
                : type == typeof(int) ? int.Parse(text!, System.Globalization.CultureInfo.InvariantCulture)
                : throw new NotSupportedException(type.Name);
            if (value is null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null) continue;
            property.SetValue(settings, value);
        }

        return settings;
    }

    private sealed record SettingsExport(string Format, int Version, Dictionary<string, string?> Settings);
}

public sealed class SettingsException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
