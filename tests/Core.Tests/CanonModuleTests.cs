using ScanRelay.Core.Modules;
using ScanRelay.MockDevices;
using ScanRelay.ModuleSdk;
using ScanRelay.Modules.CanonImageClass;

namespace ScanRelay.Core.Tests;

public sealed class CanonModuleTests : IAsyncLifetime
{
    private const string SharePassword = "the-share-password";
    private readonly List<string> _log = [];
    private MockCanon _printer = null!;
    private CanonAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        _printer = await MockCanon.StartAsync();
        _printer.Entries.Add(new CanonMockEntry("ScanRelay", "scanserver", "Scans$", "scanrelay-printer", SharePassword));
        _adapter = new CanonAdapter(new ModuleContext("canon-imageclass", new HttpClient(), _log.Add))
        {
            UiFactory = d => new CanonRemoteUi(d.Address) { PollEvery = TimeSpan.FromMilliseconds(5) },
        };
    }

    public async Task DisposeAsync() => await _printer.DisposeAsync();

    private Device Device(string? pin = null, Dictionary<string, string>? options = null) =>
        new(_printer.Address, _printer.ManagerId, pin ?? _printer.ManagerPin, options ?? []);

    private static Entry Person(Guid id, string name) =>
        new(id.ToString(), name, new ScanDestination(ScanProtocol.Smb, "scanserver", $"Scans$\\{id}", "scanserver\\scanrelay-printer", "ignored-here"), "someone@example.com");

    private List<CanonMockEntry> Ours => [.. _printer.Entries.Where(e => e.Path.Length > 40)];

    private List<CanonMockEntry> Theirs => [.. _printer.Entries.Where(e => e.Path.Length <= 40)];

    [Fact]
    public void The_module_data_passes_the_schema_and_says_the_method_is_unofficial()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "ScanRelay.slnx"))) dir = dir.Parent;
        var data = ModuleData.Parse(File.ReadAllText(Path.Combine(dir.FullName, "modules", "canon", "imageclass", "module.yaml")));

        Assert.Equal(_adapter.Id, data.Id);
        Assert.True(data.Unofficial);
        Assert.Contains("named exactly \"ScanRelay\"", data.Method);
        Assert.True(data.MatchesWindowsName("Canon MF642C/643C/644C UFR II"));
        Assert.Empty(data.TestedFirmware);
    }

    [Fact]
    public async Task Test_connection_reads_the_model_and_proves_the_sign_in()
    {
        var good = await _adapter.ProbeAsync(Device());
        var wrongPin = await _adapter.ProbeAsync(Device(pin: "0000000"));

        Assert.True(good.Reachable, good.Error);
        Assert.Equal("MF642C/643C/644C", good.Model);
        Assert.False(wrongPin.Reachable);
        Assert.Contains("refused the System Manager ID and PIN", wrongPin.Error);
    }

    [Fact]
    public async Task A_push_adds_people_with_the_templates_password_and_sends_everything_else_back_untouched()
    {
        var before = Theirs;
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());

        var result = await _adapter.ApplyAsync(Device(), [Person(bob, "Bob Example"), Person(ann, "Ann Example")]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal((2, 0, 0), (result.Added, result.Updated, result.Removed));
        Assert.Equal(before, Theirs);
        Assert.Equal(["Ann Example", "Bob Example"], Ours.Select(e => e.Name));
        var entry = Ours[0];
        Assert.Equal(("scanserver", $"Scans$\\{ann}", "scanrelay-printer"), (entry.Host, entry.Path, entry.UserName));

        // The password the printer ends up with is the real one, though the module never knew it.
        Assert.Equal(SharePassword, entry.Password);
        Assert.Equal(1, _printer.Imports);
    }

    [Fact]
    public async Task Pushing_the_same_list_again_loads_nothing_onto_the_printer()
    {
        var list = Enumerable.Range(1, 30).Select(i => Person(Guid.NewGuid(), $"Person {i}")).ToList();
        await _adapter.ApplyAsync(Device(), list);

        var again = await _adapter.ApplyAsync(Device(), list);

        Assert.True(again.Succeeded, again.Error);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
        Assert.Equal(1, _printer.Imports);
        Assert.Equal(30, Ours.Count);
    }

    [Fact]
    public async Task Joiners_leavers_and_renames_go_in_one_load()
    {
        var (ann, bob, cy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _adapter.ApplyAsync(Device(), [Person(ann, "Ann"), Person(bob, "Bob")]);
        var before = Theirs;

        var result = await _adapter.ApplyAsync(Device(), [Person(bob, "Robert"), Person(cy, "Cy")]);

        Assert.Equal((1, 1, 1), (result.Added, result.Updated, result.Removed));
        Assert.Equal(["Cy", "Robert"], Ours.Select(e => e.Name));
        Assert.All(Ours, e => Assert.Equal(SharePassword, e.Password));
        Assert.Equal(before, Theirs);
        Assert.Equal(2, _printer.Imports);
    }

    [Fact]
    public async Task An_empty_list_removes_only_our_entries_and_keeps_the_template()
    {
        await _adapter.ApplyAsync(Device(), [Person(Guid.NewGuid(), "Ann"), Person(Guid.NewGuid(), "Bob")]);
        var before = Theirs;

        var result = await _adapter.ApplyAsync(Device(), []);

        Assert.Equal(2, result.Removed);
        Assert.Empty(Ours);
        Assert.Equal(before, Theirs);
        Assert.Contains(_printer.Entries, e => e.Name == "ScanRelay");
    }

    [Fact]
    public async Task A_new_share_password_typed_into_the_template_reaches_everyone_at_the_next_push()
    {
        var list = new[] { Person(Guid.NewGuid(), "Ann"), Person(Guid.NewGuid(), "Bob") };
        await _adapter.ApplyAsync(Device(), list);
        var index = _printer.Entries.FindIndex(e => e.Name == "ScanRelay");
        _printer.Entries[index] = _printer.Entries[index] with { Password = "a-new-password" };

        var result = await _adapter.ApplyAsync(Device(), list);

        Assert.Equal(2, result.Updated);
        Assert.All(Ours, e => Assert.Equal("a-new-password", e.Password));
    }

    [Fact]
    public async Task Without_the_template_entry_nothing_is_changed_and_the_reason_says_what_to_do()
    {
        _printer.Entries.RemoveAll(e => e.Name == "ScanRelay");

        var result = await _adapter.ApplyAsync(Device(), [Person(Guid.NewGuid(), "Ann")]);

        Assert.False(result.Succeeded);
        Assert.Contains("Make one on the printer by hand", result.Error);
        Assert.Equal(0, _printer.Imports);
    }

    [Fact]
    public async Task A_template_under_another_name_can_be_chosen_per_printer()
    {
        var index = _printer.Entries.FindIndex(e => e.Name == "ScanRelay");
        _printer.Entries[index] = _printer.Entries[index] with { Name = "Scan account" };

        var result = await _adapter.ApplyAsync(Device(options: new() { ["template_name"] = "Scan account" }), [Person(Guid.NewGuid(), "Ann")]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(SharePassword, Assert.Single(Ours).Password);
    }

    [Fact]
    public async Task Names_longer_than_the_printer_keeps_are_cut_and_do_not_cause_a_load_every_time()
    {
        var list = new[] { Person(Guid.NewGuid(), "Bartholomew Featherstonehaugh") };
        await _adapter.ApplyAsync(Device(), list);

        var again = await _adapter.ApplyAsync(Device(), list);

        Assert.Equal("Bartholomew Feat", Assert.Single(Ours).Name);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
        Assert.Equal(1, _printer.Imports);
    }

    [Fact]
    public async Task Our_entries_can_be_read_back_for_the_drift_check()
    {
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());
        await _adapter.ApplyAsync(Device(), [Person(ann, "Ann"), Person(bob, "Bob")]);

        var read = await _adapter.ReadManagedAsync(Device());

        Assert.Equal(new[] { ann.ToString(), bob.ToString() }.Order(), read!.Select(e => e.Key).Order());
        Assert.All(read!, e => Assert.Equal("", e.Destination.Password));
    }

    [Fact]
    public async Task A_printer_that_does_not_keep_everything_is_reported()
    {
        _printer.Capacity = 5;
        var list = Enumerable.Range(1, 10).Select(i => Person(Guid.NewGuid(), $"Person {i:00}")).ToList();

        var result = await _adapter.ApplyAsync(Device(), list);

        Assert.False(result.Succeeded);
        Assert.Contains("did not keep it as sent", result.Error);
    }

    [Fact]
    public async Task A_printer_with_an_address_book_pin_needs_it_in_the_printers_settings()
    {
        _printer.AddressBookPin = "4321";

        var without = await _adapter.ApplyAsync(Device(), [Person(Guid.NewGuid(), "Ann")]);
        var with = await _adapter.ApplyAsync(Device(options: new() { ["address_book_pin"] = "4321" }), [Person(Guid.NewGuid(), "Ann")]);

        Assert.False(without.Succeeded);
        Assert.Contains("address book PIN", without.Error);
        Assert.True(with.Succeeded, with.Error);
    }

    [Fact]
    public async Task Missing_sign_in_details_or_a_printer_that_is_off_are_reported_not_thrown()
    {
        var noSignIn = await _adapter.ApplyAsync(new Device(_printer.Address, null, null, new Dictionary<string, string>()), []);
        Assert.Contains("System Manager ID and PIN", noSignIn.Error);

        await _printer.DisposeAsync();
        var off = await _adapter.ApplyAsync(Device(), []);
        Assert.False(off.Succeeded);
        Assert.Contains("Could not reach the printer's Remote UI", off.Error);
        _printer = await MockCanon.StartAsync();
    }

    [Theory]
    [InlineData("Remote UI: Login: Somebody's Printer: MF642C/643C/644C", "Remote UI: Login: (printer name): MF642C/643C/644C")]
    [InlineData("Remote UI: Export: Room 2: Colour: MF642C/643C/644C", "Remote UI: Export: (printer name): MF642C/643C/644C")]
    [InlineData("Something else", "Something else")]
    public void A_report_leaves_out_the_name_the_owner_gave_the_printer(string title, string expected)
    {
        Assert.Equal(expected, CanonRemoteUi.WithoutPrinterName(title));
    }

    [Theory]
    [InlineData("/m_import_dev_mng_export.html?err=3", "export is still being performed")]
    [InlineData("/m_import_dev_mng_export.html?err=2", "import is still being performed")]
    [InlineData("/m_import_dev_mng_export.html?err=1", "PIN is incorrect")]
    [InlineData("/m_import_dev_mng_import.html?err=5", "wants its address book PIN")]
    [InlineData("/m_import_dev_mng_export.html", "without a reason this module knows")]
    public void The_printers_own_reason_for_refusing_is_passed_on(string address, string expected)
    {
        Assert.Contains(expected, CanonRemoteUi.ExplainRefusal(address, "export"));
    }

    [Fact]
    public void A_recorded_page_leaves_the_printers_name_out_of_both_places_it_appears()
    {
        var page = "<html><head><title>Remote UI: Export: Somebody's Printer: MF642C/643C/644C</title></head><body><span id=\"deviceName\">Somebody's Printer / MF642C/643C/644C / Somebody's Printer</span><p>Done</p></body></html>";

        var recorded = CanonRemoteUi.WithoutPrinterNames(page);

        Assert.DoesNotContain("Somebody", recorded);
        Assert.Contains("<p>Done</p>", recorded);
    }

    [Fact]
    public async Task The_file_for_applying_by_hand_lists_each_person_without_a_password()
    {
        var file = await _adapter.ExportAsync(Device(), [Person(Guid.NewGuid(), "Ann Example")]);

        var text = System.Text.Encoding.UTF8.GetString(file.Content);
        Assert.StartsWith("Name,Host name,Folder path,User name", text);
        Assert.Contains("\"Ann Example\",\"scanserver\"", text);
        Assert.DoesNotContain("ignored-here", text);
    }
}
