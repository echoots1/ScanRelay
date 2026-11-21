using System.Text.Json;

namespace ScanRelay.Core.Settings;

/// <summary>A setting whose meaning changed in some version, and why an admin should look at it again.</summary>
public sealed record ChangedSetting(int SinceVersion, string Setting, string Why);

/// <summary>
/// When an update changes what a setting means, the admin screen asks for the new value
/// at the first sign-in after the update. This keeps the list of settings still to be looked at.
/// </summary>
public sealed class SettingsReview(SettingsStore settings)
{
    /// <summary>Raised whenever a release changes what a setting means.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Every such change so far. A release that changes a setting's meaning adds a line and raises the version.</summary>
    public static readonly IReadOnlyList<ChangedSetting> History = [];

    private const string VersionKey = "settingsVersion";
    private const string PendingKey = "settingsToReview";

    /// <summary>Run once at start-up, after the database has upgraded itself.</summary>
    public Task NoteUpgradeAsync(CancellationToken ct = default) => NoteUpgradeAsync(CurrentVersion, History, ct);

    public async Task NoteUpgradeAsync(int currentVersion, IReadOnlyList<ChangedSetting> history, CancellationToken ct = default)
    {
        var stored = await settings.GetStateAsync(VersionKey, ct);
        if (int.TryParse(stored, out var before) && before < currentVersion)
        {
            var pending = (await GetPendingAsync(ct)).Concat(history.Where(h => h.SinceVersion > before)).DistinctBy(h => h.Setting).ToList();
            await settings.SetStateAsync(PendingKey, pending.Count == 0 ? null : JsonSerializer.Serialize(pending), ct);
        }

        // A new install has nothing to review: its settings were chosen with their current meaning.
        if (stored != currentVersion.ToString()) await settings.SetStateAsync(VersionKey, currentVersion.ToString(), ct);
    }

    public async Task<IReadOnlyList<ChangedSetting>> GetPendingAsync(CancellationToken ct = default)
    {
        var stored = await settings.GetStateAsync(PendingKey, ct);
        if (stored is null) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ChangedSetting>>(stored) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Called when an Admin saves Settings: they have seen the page.</summary>
    public Task ClearAsync(CancellationToken ct = default) => settings.SetStateAsync(PendingKey, null, ct);
}
