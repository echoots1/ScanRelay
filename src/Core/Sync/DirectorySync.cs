using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScanRelay.Core.Alerts;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Data;
using ScanRelay.Core.Printers;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Sync;

/// <summary>What one printer's list would change by.</summary>
public sealed record PrinterChange(int PrinterId, string Name, List<string> Add, List<string> Remove, List<string> Rename, int Total)
{
    public bool Differs => Add.Count + Remove.Count + Rename.Count > 0;
}

/// <summary>What a sync would change. Stored on the run so the Runs page can show it.</summary>
public sealed record SyncChanges(List<string> AddPeople, List<string> RemovePeople, List<string> Skipped, List<PrinterChange> Printers)
{
    public int LargestRemoval => Math.Max(RemovePeople.Count, Printers.Count == 0 ? 0 : Printers.Max(p => p.Remove.Count));
}

/// <summary>
/// Reads the groups and manual people, works out which folders and printer entries
/// should exist, and makes it so. Printers are always handed their whole list, so
/// adds and removals are the same operation and a missed run corrects itself.
/// </summary>
public sealed class DirectorySync(
    IDbContextFactory<ScanRelayDbContext> factory,
    DataFolder data,
    SettingsStore settings,
    ICloud cloud,
    PrinterService printers,
    AlertService alerts,
    AuditLog audit,
    TimeProvider time,
    ILogger<DirectorySync> logger)
{
    private const string QuietSyncKey = "lastQuietSync";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _noOneDrive = new();

    /// <summary>Raised whenever a run changes, so open pages can refresh.</summary>
    public event Action? Changed;

    public bool IsRunning => _oneAtATime.CurrentCount == 0;

    /// <summary>Runs a sync or a preview. Returns the run's id, or 0 for a scheduled sync that found nothing to change and so was not recorded.</summary>
    public async Task<int> RunAsync(RunKind kind, RunTrigger trigger, Actor actor, CancellationToken ct = default)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var held = await db.Runs.Where(r => r.Status == RunStatus.Held).ToListAsync(ct);
            if (kind == RunKind.Sync && held.Count > 0)
            {
                // A stopped run is waiting for an Admin. The schedule does not pile more on top;
                // a person pressing Sync now replaces it with a fresh look.
                if (trigger == RunTrigger.Schedule) return held[0].Id;
                foreach (var old in held)
                {
                    old.Status = RunStatus.Cancelled;
                    old.FinishedUtc = time.GetUtcNow().UtcDateTime;
                    old.Detail = Describe(old.Detail, "Replaced by a newer sync.");
                }
            }

            var run = new Run
            {
                Kind = kind,
                Trigger = trigger,
                Status = RunStatus.Running,
                StartedUtc = time.GetUtcNow().UtcDateTime,
                Actor = trigger == RunTrigger.Manual ? actor.Name : null,
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
            Changed?.Invoke();
            await ExecuteAsync(db, run, approved: false, onlyPrinter: null, force: false, ct);

            // An hourly check that found nothing to do is not worth a line on the Runs page.
            // Only the time is kept, so the Dashboard can still say when the list was last checked.
            if (trigger == RunTrigger.Schedule && kind == RunKind.Sync && run.Status == RunStatus.Succeeded
                && run.PeopleAdded == 0 && run.PeopleRemoved == 0 && run.Pushes.Count == 0)
            {
                db.Runs.Remove(run);
                await db.SaveChangesAsync(ct);
                await settings.SetStateAsync(QuietSyncKey, time.GetUtcNow().UtcDateTime.ToString("o"), ct);
                Changed?.Invoke();
                return 0;
            }

            return run.Id;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>When a scheduled sync last ran and found nothing to change. Such syncs are not listed as runs.</summary>
    public async Task<DateTime?> LastQuietSyncAsync(CancellationToken ct = default) =>
        DateTime.TryParse(await settings.GetStateAsync(QuietSyncKey, ct), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when.ToUniversalTime() : null;

    /// <summary>Lets a run the change guard stopped go ahead.</summary>
    public async Task<string?> ApproveAsync(int runId, Actor actor, CancellationToken ct = default)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.Runs.FindAsync([runId], ct);
            if (run is not { Status: RunStatus.Held }) return "That run is not waiting for approval.";
            run.Status = RunStatus.Running;
            run.Actor = actor.Name;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, "Sync approved", $"Run {runId}", ct: ct);
            await ExecuteAsync(db, run, approved: true, onlyPrinter: null, force: false, ct);
            return null;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public async Task<string?> CancelAsync(int runId, Actor actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var run = await db.Runs.FindAsync([runId], ct);
        if (run is not { Status: RunStatus.Held }) return "That run is not waiting for approval.";
        run.Status = RunStatus.Cancelled;
        run.FinishedUtc = time.GetUtcNow().UtcDateTime;
        run.Actor = actor.Name;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "Sync cancelled", $"Run {runId}", ct: ct);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Pushes the full list to one printer now, whether or not it differs. Used by Push now and by re-running a failed push.</summary>
    public async Task<int> PushPrinterAsync(int printerId, Actor actor, CancellationToken ct = default)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = new Run
            {
                Kind = RunKind.Sync,
                Trigger = RunTrigger.Manual,
                Status = RunStatus.Running,
                StartedUtc = time.GetUtcNow().UtcDateTime,
                Actor = actor.Name,
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, "Push to printer", $"Printer {printerId}", ct: ct);
            await ExecuteAsync(db, run, approved: true, onlyPrinter: printerId, force: true, ct);
            return run.Id;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Once a night: compares each printer that can be read back with the wanted list and repairs differences.</summary>
    public async Task<int?> DriftCheckAsync(CancellationToken ct = default)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var all = await db.Printers.Include(p => p.Entries).ToListAsync(ct);
            Run? run = null;
            foreach (var printer in all)
            {
                var actual = await printers.ReadBackAsync(printer, ct);
                if (actual is null) continue;
                var wanted = printer.Entries.Select(e => new WantedEntry(e.PersonId, e.DisplayName, e.Email)).OrderBy(e => e.DisplayName).ToList();
                var same = actual.Count == wanted.Count
                    && actual.OrderBy(a => a.PersonId).SequenceEqual(wanted.OrderBy(w => w.PersonId));
                if (same) continue;

                if (run is null)
                {
                    run = new Run { Kind = RunKind.DriftCheck, Trigger = RunTrigger.Schedule, Status = RunStatus.Running, StartedUtc = time.GetUtcNow().UtcDateTime };
                    db.Runs.Add(run);
                    await db.SaveChangesAsync(ct);
                }

                await PushAndRecordAsync(db, run, printer, wanted, ct);
            }

            if (run is null) return null;
            run.Status = run.Pushes.Any(p => p.Status != PushStatus.Succeeded) ? RunStatus.Failed : RunStatus.Succeeded;
            run.FinishedUtc = time.GetUtcNow().UtcDateTime;
            run.Detail = Describe(null, $"{run.Pushes.Count} printer(s) did not match the wanted list and were repaired.");
            await db.SaveChangesAsync(ct);
            Changed?.Invoke();
            return run.Id;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public static (string? Message, SyncChanges? Changes) ReadDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return (null, null);
        try
        {
            var stored = JsonSerializer.Deserialize<StoredDetail>(detail);
            return (stored?.Message, stored?.Changes);
        }
        catch (JsonException)
        {
            return (detail, null);
        }
    }

    private static string Describe(string? existing, string message, SyncChanges? changes = null) =>
        JsonSerializer.Serialize(new StoredDetail(message, changes ?? ReadDetail(existing).Changes), Json);

    private async Task ExecuteAsync(ScanRelayDbContext db, Run run, bool approved, int? onlyPrinter, bool force, CancellationToken ct)
    {
        try
        {
            var s = await settings.GetAsync(ct);
            if (s.DefaultSyncGroupId is null)
            {
                await EndAsync(db, run, RunStatus.Failed, "No default sync group has been chosen in Settings.", null, ct);
                return;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var allPrinters = await db.Printers.Include(p => p.Entries).ToListAsync(ct);
            var people = await db.People.Include(p => p.Printers).ToListAsync(ct);

            // 1. Read the group members from Graph.
            var groupIds = allPrinters.Select(p => p.SyncGroupId).Append(s.DefaultSyncGroupId).Where(g => g is not null).Select(g => g!.Value).Distinct().ToList();
            var members = new Dictionary<Guid, IReadOnlyList<DirectoryUser>>();
            foreach (var groupId in groupIds) members[groupId] = await cloud.GetGroupMembersAsync(groupId, ct);

            var candidates = members.Values.SelectMany(m => m).DistinctBy(u => u.Id).ToDictionary(u => u.Id);
            var manualIds = people.Where(p => p.Source == PersonSource.Manual).Select(p => p.Id).ToHashSet();
            foreach (var id in manualIds.Where(id => !candidates.ContainsKey(id)))
            {
                if (await cloud.GetUserAsync(id, ct) is { } user) candidates[id] = user;
            }

            // Only enabled accounts with a OneDrive are included. Guests are skipped unless a setting turns them on.
            var skipped = new List<string>();
            var known = people.Where(p => p.RemovedUtc is null).Select(p => p.Id).ToHashSet();
            var excluded = people.Where(p => p.Excluded).Select(p => p.Id).ToHashSet();
            var eligible = new Dictionary<Guid, DirectoryUser>();
            foreach (var user in candidates.Values.OrderBy(u => u.DisplayName))
            {
                if (excluded.Contains(user.Id)) continue;
                if (!user.Enabled || (user.IsGuest && !s.IncludeGuests)) continue;
                if (!known.Contains(user.Id))
                {
                    // One person's OneDrive being missing, locked or otherwise unusable must not stop the sync for everyone else.
                    string? reason = null;
                    try
                    {
                        if (!await HasOneDriveAsync(user.Id, ct)) reason = "no OneDrive";
                    }
                    catch (CloudException e) when (!e.IsTransient)
                    {
                        reason = e.Status == 423 ? "their OneDrive is locked or blocked" : "their OneDrive could not be opened: " + e.Message;
                    }

                    if (reason is not null)
                    {
                        skipped.Add($"{user.DisplayName} ({reason})");
                        continue;
                    }
                }

                eligible[user.Id] = user;
            }

            // 2. Work out the wanted state: one folder per person, and for each printer its full list.
            var wantedByPrinter = new Dictionary<int, List<WantedEntry>>();
            foreach (var printer in allPrinters)
            {
                var groupMembers = members[printer.SyncGroupId ?? s.DefaultSyncGroupId.Value].Select(u => u.Id).ToHashSet();
                wantedByPrinter[printer.Id] = [.. eligible.Values
                    .Where(u => groupMembers.Contains(u.Id) || people.Any(p => p.Id == u.Id && p.Source == PersonSource.Manual
                        && (p.OnAllPrinters || p.Printers.Any(l => l.PrinterId == printer.Id))))
                    .Select(u => new WantedEntry(u.Id, u.DisplayName, s.IncludeEmail ? u.Mail : null))
                    .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
            }

            var defaultMembers = members[s.DefaultSyncGroupId.Value].Select(u => u.Id).ToHashSet();
            var wantedPeople = eligible.Values
                .Where(u => defaultMembers.Contains(u.Id) || manualIds.Contains(u.Id) || wantedByPrinter.Values.Any(w => w.Any(e => e.PersonId == u.Id)))
                .ToDictionary(u => u.Id);

            // 3. Compare with what the database says exists now.
            var toAdd = wantedPeople.Values.Where(u => !known.Contains(u.Id)).ToList();
            var toRemove = people.Where(p => p.RemovedUtc is null && !wantedPeople.ContainsKey(p.Id)).ToList();
            var changes = new SyncChanges(
                [.. toAdd.Select(u => u.DisplayName)],
                [.. toRemove.Select(p => p.DisplayName)],
                skipped,
                [.. allPrinters.Select(p => Compare(p, wantedByPrinter[p.Id]))]);
            run.PeopleAdded = toAdd.Count;
            run.PeopleRemoved = toRemove.Count;

            if (run.Kind == RunKind.Preview)
            {
                await EndAsync(db, run, RunStatus.Succeeded, "Preview only. Nothing was changed.", changes, ct);
                return;
            }

            // Safety: a run that would remove more than the limit stops and waits for an Admin.
            if (!approved && changes.LargestRemoval > s.ChangeGuardLimit)
            {
                await EndAsync(db, run, RunStatus.Held,
                    $"This sync would remove {changes.LargestRemoval} entries, more than the change guard limit of {s.ChangeGuardLimit}. Nothing was changed.", changes, ct, finished: false);
                await alerts.RaiseAsync(AlertKind.ChangeGuard,
                    $"A sync at {run.StartedUtc:u} would remove {changes.LargestRemoval} entries (limit {s.ChangeGuardLimit}). It is waiting for approval.", ct);
                return;
            }

            // 4. Apply: people and their folders first, so a scan made right after the push has somewhere to go.
            foreach (var user in wantedPeople.Values)
            {
                var person = people.FirstOrDefault(p => p.Id == user.Id);
                if (person is null)
                {
                    person = new Person { Id = user.Id, DisplayName = user.DisplayName, UserPrincipalName = user.UserPrincipalName, Source = PersonSource.Group, FirstSeenUtc = now };
                    db.People.Add(person);
                    people.Add(person);
                }

                person.DisplayName = user.DisplayName;
                person.UserPrincipalName = user.UserPrincipalName;
                person.Email = user.Mail;
                person.LastSeenUtc = now;
                person.RemovedUtc = null;
                Directory.CreateDirectory(Path.Combine(data.Inbox, user.Id.ToString()));
            }

            foreach (var person in toRemove) person.RemovedUtc = now;
            await db.SaveChangesAsync(ct);

            // For each printer that differs, hand its module the full wanted list.
            var failures = 0;
            foreach (var printer in allPrinters)
            {
                if (onlyPrinter is not null && printer.Id != onlyPrinter) continue;
                var change = changes.Printers.First(c => c.PrinterId == printer.Id);
                if (!change.Differs && !force) continue;
                if (!await PushAndRecordAsync(db, run, printer, wantedByPrinter[printer.Id], ct)) failures++;
            }

            // Removed people: files still waiting are processed as normal jobs; the folder goes once it is empty.
            RemoveEmptyFolders(people.Where(p => p.RemovedUtc is not null));

            var summary = failures > 0 ? $"{failures} push(es) failed." : "Finished.";
            await EndAsync(db, run, failures > 0 ? RunStatus.Failed : RunStatus.Succeeded, summary, changes, ct);
        }
        catch (CloudException e)
        {
            logger.LogWarning("Sync run {Run} failed: {Reason}", run.Id, e.Message);
            await EndAsync(db, run, RunStatus.Failed, "Could not read from Microsoft Graph: " + e.Message, null, ct);
            await alerts.RaiseAsync(AlertKind.PushFailed, $"The sync at {run.StartedUtc:u} could not read from Microsoft Graph: {e.Message}", ct);
        }
    }

    private async Task<bool> PushAndRecordAsync(ScanRelayDbContext db, Run run, Printer printer, List<WantedEntry> wanted, CancellationToken ct)
    {
        var push = new Push { Run = run, Printer = printer, Status = PushStatus.Pending, StartedUtc = time.GetUtcNow().UtcDateTime };
        db.Pushes.Add(push);
        await db.SaveChangesAsync(ct);

        var outcome = await printers.PushAsync(printer, wanted, ct);
        push.FinishedUtc = time.GetUtcNow().UtcDateTime;
        push.Status = outcome.Succeeded ? PushStatus.Succeeded : outcome.Paused ? PushStatus.Paused : PushStatus.Failed;
        (push.Added, push.Updated, push.Removed, push.Error) = (outcome.Added, outcome.Updated, outcome.Removed, outcome.Error);
        if (outcome.Succeeded)
        {
            db.PrinterEntries.RemoveRange(printer.Entries);
            printer.Entries = [.. wanted.Select(w => new PrinterEntry { PrinterId = printer.Id, PersonId = w.PersonId, DisplayName = w.DisplayName, Email = w.Email, PushedUtc = push.FinishedUtc.Value })];
            logger.LogInformation("Pushed {Count} entries to printer {Printer}", wanted.Count, printer.Name);
        }
        else
        {
            logger.LogWarning("Push to printer {Printer} failed: {Reason}", printer.Name, outcome.Error);
            await alerts.RaiseAsync(AlertKind.PushFailed, $"Push to {printer.Name} at {push.StartedUtc:u}: {outcome.Error}", ct);
        }

        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return outcome.Succeeded;
    }

    private static PrinterChange Compare(Printer printer, List<WantedEntry> wanted)
    {
        var held = printer.Entries.ToDictionary(e => e.PersonId, e => (e.DisplayName, e.Email));
        var want = wanted.ToDictionary(e => e.PersonId, e => e.DisplayName);
        return new PrinterChange(
            printer.Id,
            printer.Name,
            [.. wanted.Where(e => !held.ContainsKey(e.PersonId)).Select(e => e.DisplayName)],
            [.. held.Where(h => !want.ContainsKey(h.Key)).Select(h => h.Value.DisplayName).Order()],
            [.. wanted.Where(e => held.TryGetValue(e.PersonId, out var now) && (now.DisplayName != e.DisplayName || !string.Equals(now.Email, e.Email, StringComparison.OrdinalIgnoreCase))).Select(e => e.DisplayName)],
            wanted.Count);
    }

    private void RemoveEmptyFolders(IEnumerable<Person> removed)
    {
        foreach (var person in removed)
        {
            var folder = Path.Combine(data.Inbox, person.Id.ToString());
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            catch (IOException)
            {
                // A scan arrived just now. The next sync tries again.
            }
        }
    }

    private async Task<bool> HasOneDriveAsync(Guid userId, CancellationToken ct)
    {
        // Someone found without a OneDrive is not asked about again for a day.
        if (_noOneDrive.TryGetValue(userId, out var when) && time.GetUtcNow() - when < TimeSpan.FromHours(24)) return false;
        if (await cloud.HasOneDriveAsync(userId, ct))
        {
            _noOneDrive.TryRemove(userId, out _);
            return true;
        }

        _noOneDrive[userId] = time.GetUtcNow();
        return false;
    }

    private async Task EndAsync(ScanRelayDbContext db, Run run, RunStatus status, string message, SyncChanges? changes, CancellationToken ct, bool finished = true)
    {
        run.Status = status;
        run.Detail = Describe(run.Detail, message, changes);
        if (finished) run.FinishedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
    }

    private sealed record StoredDetail(string? Message, SyncChanges? Changes);
}
