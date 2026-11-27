using ScanRelay.Core.Modules;
using ScanRelay.MockDevices;
using ScanRelay.ModuleSdk;
using ScanRelay.Modules.BrotherAds;

namespace ScanRelay.Core.Tests;

public sealed class BrotherModuleTests : IAsyncLifetime
{
    private const string SharePassword = "sh&re <pass>&&word";
    private readonly List<string> _log = [];
    private MockBrother _scanner = null!;
    private BrotherAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        // As the real scanner was found: slot 1 a Network profile, the rest FTP, all empty. Two are then used for other things.
        _scanner = await MockBrother.StartAsync();
        for (var i = 1; i < 25; i++) _scanner.Profiles[i] = new BrotherMockProfile(1);
        _scanner.Profiles[2] = new BrotherMockProfile(1, "Accounts FTP");
        _scanner.Profiles[5] = new BrotherMockProfile(0, "Shared folder", @"\\fileserver\public\scans", "someone", "their-password");
        _adapter = new BrotherAdapter(new ModuleContext("brother-ads", new HttpClient(), _log.Add)) { UiFactory = d => new BrotherWebUi(d.Address) };
    }

    public async Task DisposeAsync() => await _scanner.DisposeAsync();

    private Device Device(string? password = null) => new(_scanner.Address, null, password ?? _scanner.Password, new Dictionary<string, string>());

    private static Entry Person(Guid id, string name) =>
        new(id.ToString(), name, new ScanDestination(ScanProtocol.Smb, "scanserver", $"Scans$\\{id}", "scanserver\\scanrelay-printer", SharePassword), "someone@example.com");

    private List<BrotherMockProfile> Ours => [.. _scanner.Profiles.Where(p => p.FolderPath.Contains(@"\Scans$\"))];

    private List<BrotherMockProfile> Theirs => [.. _scanner.Profiles.Where(p => p.Name.Length > 0 && !p.FolderPath.Contains(@"\Scans$\"))];

    [Fact]
    public void The_module_data_passes_the_schema_and_says_the_method_is_unofficial()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "ScanRelay.slnx"))) dir = dir.Parent;
        var data = ModuleData.Parse(File.ReadAllText(Path.Combine(dir.FullName, "modules", "brother", "ads", "module.yaml")));

        Assert.Equal(_adapter.Id, data.Id);
        Assert.True(data.Unofficial);
        Assert.Equal(25, data.EntryLimit());
        Assert.True(data.MatchesWindowsName("Brother ADS-4900W LAN"));
        Assert.Equal(["3.20"], data.TestedFirmware);
    }

    [Fact]
    public async Task Test_connection_reads_the_model_and_proves_the_sign_in()
    {
        var good = await _adapter.ProbeAsync(Device());
        var wrong = await _adapter.ProbeAsync(Device("not-it"));

        Assert.True(good.Reachable, good.Error);
        Assert.Equal("ADS-4900W", good.Model);
        Assert.False(wrong.Reachable);
        Assert.Contains("refused the Web Based Management password", wrong.Error);
    }

    [Fact]
    public async Task A_push_gives_each_person_a_network_profile_and_leaves_other_profiles_alone()
    {
        var before = Theirs;
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());

        var result = await _adapter.ApplyAsync(Device(), [Person(bob, "Bob Example"), Person(ann, "Ann Example")]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal((2, 0, 0), (result.Added, result.Updated, result.Removed));
        Assert.Equal(before, Theirs);
        Assert.Equal(["Ann Example", "Bob Example"], Ours.Select(p => p.Name).Order());
        var profile = Ours.Single(p => p.Name == "Ann Example");
        Assert.Equal(($@"\\scanserver\Scans$\{ann}", @"scanserver\scanrelay-printer", 0), (profile.FolderPath, profile.FolderUser, profile.Type));

        // The password arrives whole, though it holds the characters the scanner's own format uses.
        Assert.Equal(SharePassword, profile.FolderPass);

        // The empty Network slot is used first; one FTP slot had to be made a Network one for the second person.
        Assert.Equal("Ann Example", _scanner.Profiles[0].Name);
        Assert.Equal(1, _scanner.TypeChanges);
    }

    [Fact]
    public async Task Pushing_the_same_list_again_changes_nothing_on_the_scanner()
    {
        var list = Enumerable.Range(1, 5).Select(i => Person(Guid.NewGuid(), $"Person {i}")).ToList();
        await _adapter.ApplyAsync(Device(), list);
        var (saves, kinds) = (_scanner.Saves, _scanner.TypeChanges);

        var again = await _adapter.ApplyAsync(Device(), list);

        Assert.True(again.Succeeded, again.Error);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
        Assert.Equal((saves, kinds), (_scanner.Saves, _scanner.TypeChanges));
    }

    [Fact]
    public async Task Joiners_leavers_and_renames_are_all_carried_out()
    {
        var (ann, bob, cy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _adapter.ApplyAsync(Device(), [Person(ann, "Ann"), Person(bob, "Bob")]);
        var before = Theirs;

        var result = await _adapter.ApplyAsync(Device(), [Person(bob, "Robert"), Person(cy, "Cy")]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal((1, 1, 1), (result.Added, result.Updated, result.Removed));
        Assert.Equal(["Cy", "Robert"], Ours.Select(p => p.Name).Order());
        Assert.Equal(before, Theirs);
    }

    [Fact]
    public async Task An_empty_list_clears_only_our_profiles()
    {
        await _adapter.ApplyAsync(Device(), [Person(Guid.NewGuid(), "Ann"), Person(Guid.NewGuid(), "Bob")]);
        var before = Theirs;

        var result = await _adapter.ApplyAsync(Device(), []);

        Assert.Equal(2, result.Removed);
        Assert.Empty(Ours);
        Assert.Equal(before, Theirs);
    }

    [Fact]
    public async Task A_changed_share_password_reaches_every_profile_at_the_next_push()
    {
        var id = Guid.NewGuid();
        await _adapter.ApplyAsync(Device(), [Person(id, "Ann")]);
        var changed = Person(id, "Ann");
        changed = changed with { Destination = changed.Destination with { Password = "a-new-password" } };

        var result = await _adapter.ApplyAsync(Device(), [changed]);

        Assert.Equal(1, result.Updated);
        Assert.Equal("a-new-password", Assert.Single(Ours).FolderPass);
    }

    [Fact]
    public async Task Names_longer_than_the_scanner_keeps_are_cut_and_do_not_cause_a_save_every_time()
    {
        var list = new[] { Person(Guid.NewGuid(), "Bartholomew Featherstonehaugh") };
        await _adapter.ApplyAsync(Device(), list);

        var again = await _adapter.ApplyAsync(Device(), list);

        Assert.Equal("Bartholomew Fe", Assert.Single(Ours).Name);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
    }

    [Fact]
    public async Task More_people_than_free_slots_is_refused_before_anything_is_changed()
    {
        var list = Enumerable.Range(1, 24).Select(i => Person(Guid.NewGuid(), $"Person {i:00}")).ToList();

        var result = await _adapter.ApplyAsync(Device(), list);

        Assert.False(result.Succeeded);
        Assert.Contains("2 are used for other things, which leaves room for 23 people, and 24 are on its list", result.Error);
        Assert.Contains("Nothing was changed", result.Error);
        Assert.Empty(Ours);
        Assert.Equal(0, _scanner.Saves);
    }

    [Fact]
    public async Task Every_free_slot_can_be_filled()
    {
        var list = Enumerable.Range(1, 23).Select(i => Person(Guid.NewGuid(), $"Person {i:00}")).ToList();

        var result = await _adapter.ApplyAsync(Device(), list);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(23, Ours.Count);
        Assert.Equal(2, Theirs.Count);
    }

    [Fact]
    public async Task Our_profiles_can_be_read_back_for_the_drift_check()
    {
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());
        await _adapter.ApplyAsync(Device(), [Person(ann, "Ann"), Person(bob, "Bob")]);

        var read = await _adapter.ReadManagedAsync(Device());

        Assert.Equal(new[] { ann.ToString(), bob.ToString() }.Order(), read!.Select(e => e.Key).Order());
        Assert.All(read!, e => Assert.Equal(("scanserver", ""), (e.Destination.Host, e.Destination.Password)));
    }

    [Theory]
    [InlineData(533, "Auto Start Scan mode is on")]
    [InlineData(520, "it is in use")]
    public async Task A_scanner_that_refuses_a_save_is_reported_with_its_own_reason(int status, string expected)
    {
        _scanner.BusyStatus = status;

        var result = await _adapter.ApplyAsync(Device(), [Person(Guid.NewGuid(), "Ann")]);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Error);
        Assert.Contains(_log, l => l.Contains("The exchange with"));
        Assert.DoesNotContain(_log, l => l.Contains(SharePassword) || l.Contains(_scanner.Password));
    }

    [Fact]
    public async Task A_missing_password_or_a_scanner_that_is_off_is_reported_not_thrown()
    {
        var noPassword = await _adapter.ApplyAsync(new Device(_scanner.Address, null, null, new Dictionary<string, string>()), [Person(Guid.NewGuid(), "Ann")]);
        Assert.Contains("Web Based Management password", noPassword.Error);

        await _scanner.DisposeAsync();
        var off = await _adapter.ApplyAsync(Device(), [Person(Guid.NewGuid(), "Ann")]);
        Assert.False(off.Succeeded);
        Assert.Contains("Could not reach the scanner", off.Error);
        _scanner = await MockBrother.StartAsync();
    }

    [Fact]
    public async Task The_file_for_applying_by_hand_lists_each_person_without_a_password()
    {
        var id = Guid.NewGuid();

        var file = await _adapter.ExportAsync(Device(), [Person(id, "Ann Example")]);

        var text = System.Text.Encoding.UTF8.GetString(file.Content);
        Assert.Contains($"\"Ann Example\",\"\\\\scanserver\\Scans$\\{id}\",\"scanserver\\scanrelay-printer\"", text);
        Assert.DoesNotContain(SharePassword, text);
    }
}
