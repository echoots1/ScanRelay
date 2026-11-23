using Microsoft.EntityFrameworkCore;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Data;
using ScanRelay.Core.Sync;
using ScanRelay.MockCloud;
using ScanRelay.MockDevices;

namespace ScanRelay.Core.Tests;

public sealed class SyncTests : IAsyncLifetime
{
    private static readonly Actor Admin = new(Guid.NewGuid(), "Alex Admin");
    private readonly SampleModule _sample = new();
    private TestEnvironment _env = null!;
    private MockPrinter _mock = null!;
    private MockGroup _staff = null!;
    private MockUser[] _people = null!;

    public async Task InitializeAsync()
    {
        _sample.InstallOfficial();
        _env = await TestEnvironment.StartAsync(_sample.Register, prepare: i => i.ModulesFolder = _sample.ModulesFolder);
        await _env.ConnectAsync();
        _mock = await MockPrinter.StartAsync();
        _people = [.. new[] { "Ann", "Bob", "Cy", "Dee" }.Select(n => _env.State.AddUser(n + " Example"))];
        _staff = _env.State.AddGroup("All Staff", _people);
        await _env.Settings.UpdateAsync(s => s with { DefaultSyncGroupId = _staff.Id, DefaultSyncGroupName = "All Staff" }, Admin);
    }

    public async Task DisposeAsync()
    {
        await _mock.DisposeAsync();
        await _env.DisposeAsync();
        _sample.Cleanup();
    }

    private DirectorySync Sync => _env.Get<DirectorySync>();

    private async Task<ScanRelayDbContext> DbAsync() => await _env.Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync();

    private async Task<Printer> AddPrinterAsync(string name = "Front office", MockPrinter? mock = null, Guid? group = null)
    {
        mock ??= _mock;
        await using var db = await DbAsync();
        var printer = new Printer
        {
            Name = name,
            Address = mock.Address,
            ModuleId = "scanrelay-sample",
            AdminUser = mock.AdminUser,
            AdminPasswordProtected = _env.Get<Security.SecretProtector>().ProtectBytes(mock.AdminPassword),
            OptionsJson = $$"""{"port":"{{mock.Port}}"}""",
            SyncGroupId = group,
            CreatedUtc = DateTime.UtcNow,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer;
    }

    private async Task<Run> RunAsync(RunKind kind = RunKind.Sync, RunTrigger trigger = RunTrigger.Manual)
    {
        var id = await Sync.RunAsync(kind, trigger, Admin);
        await using var db = await DbAsync();
        return await db.Runs.Include(r => r.Pushes).SingleAsync(r => r.Id == id);
    }

    private bool FolderExists(MockUser user) => Directory.Exists(Path.Combine(_env.Data.Inbox, user.Id.ToString()));

    [Fact]
    public async Task A_sync_creates_a_folder_per_person_and_fills_each_printer()
    {
        await AddPrinterAsync();

        var run = await RunAsync();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(4, run.PeopleAdded);
        Assert.All(_people, p => Assert.True(FolderExists(p)));
        Assert.Equal(["Ann Example", "Bob Example", "Cy Example", "Dee Example"], _mock.Managed.Select(e => e.Name).Order());
        var push = Assert.Single(run.Pushes);
        Assert.Equal(PushStatus.Succeeded, push.Status);
        Assert.Equal(4, push.Added);
        await using var db = await DbAsync();
        Assert.Equal(4, await db.PrinterEntries.CountAsync());
        Assert.Equal(4, await db.People.CountAsync(p => p.Source == PersonSource.Group));
    }

    [Fact]
    public async Task A_second_sync_with_no_changes_pushes_nothing()
    {
        await AddPrinterAsync();
        await RunAsync();
        var writes = _mock.Writes;

        var run = await RunAsync(trigger: RunTrigger.Schedule);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Empty(run.Pushes);
        Assert.Equal(writes, _mock.Writes);
        Assert.Null(run.Actor);
    }

    [Fact]
    public async Task Joiners_leavers_and_renames_reach_the_printer_and_the_folders()
    {
        await AddPrinterAsync();
        await RunAsync();
        var eve = _env.State.AddUser("Eve Example");
        _staff.Members.Add(eve.Id);
        _staff.Members.Remove(_people[0].Id);
        _env.State.Users[_people[1].Id] = _people[1] with { DisplayName = "Robert Example" };

        var run = await RunAsync();

        Assert.Equal((1, 1), (run.PeopleAdded, run.PeopleRemoved));
        Assert.Equal(["Cy Example", "Dee Example", "Eve Example", "Robert Example"], _mock.Managed.Select(e => e.Name).Order());
        Assert.True(FolderExists(eve));
        Assert.False(FolderExists(_people[0]));
        var push = Assert.Single(run.Pushes);
        Assert.Equal((1, 1, 1), (push.Added, push.Updated, push.Removed));
        var (_, changes) = DirectorySync.ReadDetail(run.Detail);
        Assert.Equal(["Eve Example"], changes!.AddPeople);
        Assert.Equal(["Ann Example"], changes.RemovePeople);
        Assert.Equal(["Robert Example"], changes.Printers[0].Rename);
    }

    [Fact]
    public async Task A_leavers_waiting_scans_are_still_delivered_before_the_folder_goes()
    {
        await AddPrinterAsync();
        await RunAsync();
        var ann = _people[0];
        var waiting = Path.Combine(_env.Data.Inbox, ann.Id.ToString(), "last.pdf");
        await File.WriteAllTextAsync(waiting, "%PDF-last");
        _staff.Members.Remove(ann.Id);

        await RunAsync();
        Assert.True(FolderExists(ann));

        var pipeline = _env.Get<Jobs.JobPipeline>();
        await pipeline.ScanInboxAsync();
        await Task.Delay(Jobs.JobPipeline.QuietTime + TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, await pipeline.RunOnceAsync());
        Assert.Single(_env.State.Files(ann, "Dokumente/Scans"));

        await RunAsync();
        Assert.False(FolderExists(ann));
    }

    [Fact]
    public async Task Only_enabled_accounts_with_a_onedrive_are_included_and_guests_follow_the_setting()
    {
        await AddPrinterAsync();
        var disabled = _env.State.AddUser("Disabled Person", enabled: false);
        var noDrive = _env.State.AddUser("Nora NoDrive", hasDrive: false);
        var guest = _env.State.AddUser("Guest Person", guest: true);
        foreach (var u in new[] { disabled, noDrive, guest }) _staff.Members.Add(u.Id);

        var run = await RunAsync();

        Assert.Equal(4, run.PeopleAdded);
        Assert.False(FolderExists(disabled));
        Assert.False(FolderExists(noDrive));
        Assert.False(FolderExists(guest));
        Assert.Equal(["Nora NoDrive (no OneDrive)"], DirectorySync.ReadDetail(run.Detail).Changes!.Skipped);

        await _env.Settings.UpdateAsync(s => s with { IncludeGuests = true }, Admin);
        Assert.Equal(1, (await RunAsync()).PeopleAdded);
        Assert.True(FolderExists(guest));
    }

    [Fact]
    public async Task One_person_with_a_locked_onedrive_is_skipped_and_everyone_else_is_synced()
    {
        await AddPrinterAsync();
        var locked = _env.State.AddUser("Lee Locked");
        locked.DriveLocked = true;
        _staff.Members.Add(locked.Id);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(4, run.PeopleAdded);
        Assert.Equal(4, _mock.Managed.Count);
        Assert.False(FolderExists(locked));
        Assert.Equal(["Lee Locked (their OneDrive is locked or blocked)"], DirectorySync.ReadDetail(run.Detail).Changes!.Skipped);
    }

    [Fact]
    public async Task A_printer_can_have_its_own_smaller_group()
    {
        await using var small = await MockPrinter.StartAsync();
        var frontDesk = _env.State.AddGroup("Front Desk", _people[0], _people[1]);
        var outsider = _env.State.AddUser("Only On Small");
        frontDesk.Members.Add(outsider.Id);
        await AddPrinterAsync();
        await AddPrinterAsync("Small printer", small, frontDesk.Id);

        await RunAsync();

        Assert.Equal(4, _mock.Managed.Count);
        Assert.Equal(["Ann Example", "Bob Example", "Only On Small"], small.Managed.Select(e => e.Name).Order());
        Assert.True(FolderExists(outsider));
    }

    [Fact]
    public async Task Manual_people_and_exclusions_are_kept_through_every_sync()
    {
        await using var second = await MockPrinter.StartAsync();
        var first = await AddPrinterAsync();
        var other = await AddPrinterAsync("Second", second);
        var everywhere = _env.State.AddUser("Manual Everywhere");
        var oneOnly = _env.State.AddUser("Manual One");
        await using (var db = await DbAsync())
        {
            db.People.Add(new Person { Id = everywhere.Id, DisplayName = everywhere.DisplayName, UserPrincipalName = everywhere.Upn, Source = PersonSource.Manual, OnAllPrinters = true, RemovedUtc = DateTime.UtcNow });
            var limited = new Person { Id = oneOnly.Id, DisplayName = oneOnly.DisplayName, UserPrincipalName = oneOnly.Upn, Source = PersonSource.Manual, OnAllPrinters = false, RemovedUtc = DateTime.UtcNow };
            limited.Printers.Add(new PersonPrinter { PrinterId = other.Id });
            db.People.Add(limited);
            db.People.Add(new Person { Id = _people[3].Id, DisplayName = "Dee Example", UserPrincipalName = _people[3].Upn, Excluded = true, RemovedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await RunAsync();
        await RunAsync();

        Assert.Equal(["Ann Example", "Bob Example", "Cy Example", "Manual Everywhere"], _mock.Managed.Select(e => e.Name).Order());
        Assert.Equal(["Ann Example", "Bob Example", "Cy Example", "Manual Everywhere", "Manual One"], second.Managed.Select(e => e.Name).Order());
        Assert.False(FolderExists(_people[3]));
        Assert.True(FolderExists(oneOnly));
        await using var check = await DbAsync();
        Assert.True((await check.People.SingleAsync(p => p.Id == _people[3].Id)).Excluded);
        Assert.Equal(PersonSource.Manual, (await check.People.SingleAsync(p => p.Id == everywhere.Id)).Source);
        Assert.NotNull(first);
    }

    [Fact]
    public async Task A_preview_shows_what_would_change_and_changes_nothing()
    {
        await AddPrinterAsync();

        var run = await RunAsync(RunKind.Preview);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var (message, changes) = DirectorySync.ReadDetail(run.Detail);
        Assert.Contains("Nothing was changed", message);
        Assert.Equal(4, changes!.AddPeople.Count);
        Assert.Equal(4, changes.Printers[0].Add.Count);
        Assert.Empty(_mock.Managed);
        Assert.False(FolderExists(_people[0]));
        await using var db = await DbAsync();
        Assert.Empty(await db.People.ToListAsync());
    }

    [Fact]
    public async Task An_emptied_group_is_stopped_by_the_change_guard_until_an_admin_decides()
    {
        var admin = _env.State.AddUser("Alex Admin");
        var admins = _env.State.AddGroup("Admins", admin);
        _env.State.AddUser("Alerts", "alerts@example.com");
        await _env.Settings.UpdateAsync(s => s with { AdminGroupId = admins.Id, AlertSender = "alerts@example.com", ChangeGuardLimit = 2 }, Admin);
        await AddPrinterAsync();
        await RunAsync();
        _staff.Members.Clear();

        var held = await RunAsync(trigger: RunTrigger.Schedule);

        Assert.Equal(RunStatus.Held, held.Status);
        Assert.Null(held.FinishedUtc);
        Assert.Contains("would remove 4 entries", DirectorySync.ReadDetail(held.Detail).Message);
        Assert.Equal(4, _mock.Managed.Count);
        Assert.All(_people, p => Assert.True(FolderExists(p)));
        Assert.Equal("ScanRelay: A sync was stopped by the change guard", Assert.Single(_env.State.Outbox).Subject);

        // The schedule does not pile more runs or emails on top of the one waiting.
        Assert.Equal(held.Id, (await RunAsync(trigger: RunTrigger.Schedule)).Id);
        Assert.Single(_env.State.Outbox);

        Assert.Null(await Sync.ApproveAsync(held.Id, Admin));

        Assert.Empty(_mock.Managed);
        Assert.All(_people, p => Assert.False(FolderExists(p)));
        await using var db = await DbAsync();
        var approved = await db.Runs.SingleAsync(r => r.Id == held.Id);
        Assert.Equal(RunStatus.Succeeded, approved.Status);
        Assert.Equal("Alex Admin", approved.Actor);
        Assert.NotNull(await Sync.ApproveAsync(held.Id, Admin));
    }

    [Fact]
    public async Task A_held_run_can_be_cancelled_and_removals_within_the_limit_go_through()
    {
        await _env.Settings.UpdateAsync(s => s with { ChangeGuardLimit = 2 }, Admin);
        await AddPrinterAsync();
        await RunAsync();
        _staff.Members.Clear();
        var held = await RunAsync();

        Assert.Null(await Sync.CancelAsync(held.Id, Admin));
        Assert.Equal(4, _mock.Managed.Count);

        _staff.Members.Add(_people[0].Id);
        _staff.Members.Add(_people[1].Id);
        var run = await RunAsync();
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(2, _mock.Managed.Count);
    }

    [Fact]
    public async Task A_failed_push_is_recorded_alerts_and_corrects_itself_on_the_next_run()
    {
        await AddPrinterAsync();
        _mock.Broken = true;

        var failed = await RunAsync();

        Assert.Equal(RunStatus.Failed, failed.Status);
        var push = Assert.Single(failed.Pushes);
        Assert.Equal(PushStatus.Failed, push.Status);
        Assert.Contains("500", push.Error);
        Assert.All(_people, p => Assert.True(FolderExists(p)));

        _mock.Broken = false;
        var retried = await RunAsync(trigger: RunTrigger.Schedule);

        Assert.Equal(RunStatus.Succeeded, retried.Status);
        Assert.Equal(4, _mock.Managed.Count);
    }

    [Fact]
    public async Task Push_now_sends_the_full_list_to_one_printer_even_when_nothing_differs()
    {
        var printer = await AddPrinterAsync();
        await RunAsync();
        foreach (var slot in _mock.Managed) _mock.AddressBook.TryRemove(slot.Slot, out _);

        var runId = await Sync.PushPrinterAsync(printer.Id, Admin);

        Assert.Equal(4, _mock.Managed.Count);
        await using var db = await DbAsync();
        Assert.Equal(PushStatus.Succeeded, (await db.Pushes.SingleAsync(p => p.RunId == runId)).Status);
    }

    [Fact]
    public async Task The_nightly_drift_check_repairs_a_printer_that_was_changed_by_hand()
    {
        await AddPrinterAsync();
        await RunAsync();
        Assert.Null(await Sync.DriftCheckAsync());

        var victim = _mock.Managed[0];
        _mock.AddressBook.TryRemove(victim.Slot, out _);
        _mock.AddressBook[990] = new MockEntry(990, "Added at the panel", Guid.NewGuid().ToString(), "smb", "x", "y", "z", null);
        var unmanaged = _mock.Unmanaged;

        var runId = await Sync.DriftCheckAsync();

        Assert.NotNull(runId);
        Assert.Equal(["Ann Example", "Bob Example", "Cy Example", "Dee Example"], _mock.Managed.Select(e => e.Name).Order());
        Assert.Equal(unmanaged, _mock.Unmanaged);
        await using var db = await DbAsync();
        Assert.Equal(RunKind.DriftCheck, (await db.Runs.SingleAsync(r => r.Id == runId)).Kind);
    }

    [Fact]
    public async Task Without_a_sync_group_the_run_fails_with_a_reason()
    {
        await _env.Settings.UpdateAsync(s => s with { DefaultSyncGroupId = null }, Admin);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("No default sync group", DirectorySync.ReadDetail(run.Detail).Message);
    }

    [Fact]
    public async Task A_graph_outage_fails_the_run_and_changes_nothing()
    {
        await AddPrinterAsync();
        await RunAsync();
        _env.State.FailStatus = 500;
        _env.State.FailNextGraphRequests = 1;

        var run = await RunAsync();

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(4, _mock.Managed.Count);
        Assert.All(_people, p => Assert.True(FolderExists(p)));
    }

    [Fact]
    public async Task A_printer_unreachable_for_a_day_raises_one_alert()
    {
        var admin = _env.State.AddUser("Alex Admin");
        var admins = _env.State.AddGroup("Admins", admin);
        _env.State.AddUser("Alerts", "alerts@example.com");
        await _env.Settings.UpdateAsync(s => s with { AdminGroupId = admins.Id, AlertSender = "alerts@example.com" }, Admin);
        var printer = await AddPrinterAsync();
        await using (var db = await DbAsync())
        {
            await db.Printers.Where(p => p.Id == printer.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.CreatedUtc, DateTime.UtcNow.AddHours(-30)));
        }

        var service = _env.Get<Printers.PrinterService>();
        await service.CheckReachabilityAsync();
        Assert.Empty(_env.State.Outbox);

        await using (var db = await DbAsync())
        {
            await db.Printers.Where(p => p.Id == printer.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.LastReachableUtc, DateTime.UtcNow.AddHours(-25)));
        }

        _mock.Broken = true;
        await service.CheckReachabilityAsync();
        await service.CheckReachabilityAsync();

        Assert.Equal("ScanRelay: A printer has been unreachable for 24 hours", Assert.Single(_env.State.Outbox).Subject);
    }
}
