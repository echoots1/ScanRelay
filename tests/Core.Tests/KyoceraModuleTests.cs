using ScanRelay.Core.Modules;
using ScanRelay.MockDevices;
using ScanRelay.ModuleSdk;
using ScanRelay.Modules.KyoceraEcosys;

namespace ScanRelay.Core.Tests;

public sealed class KyoceraModuleTests : IAsyncLifetime
{
    private const string Share = "Scans$";
    private readonly List<string> _log = [];
    private MockKyocera _printer = null!;
    private KyoceraAdapter _adapter = null!;
    private Device _device = null!;

    public async Task InitializeAsync()
    {
        _printer = await MockKyocera.StartAsync();
        _adapter = new KyoceraAdapter(new ModuleContext("kyocera-ecosys", new HttpClient(), _log.Add))
        {
            ClientFactory = d => new KyoceraClient(new HttpClient(), $"http://{d.Address}:{d.Options["port"]}") { Wait = (_, _) => Task.CompletedTask },
        };
        _device = DeviceWith();
    }

    public async Task DisposeAsync() => await _printer.DisposeAsync();

    private Device DeviceWith(string first = "100", string last = "199") => new(
        _printer.Address, null, null,
        new Dictionary<string, string> { ["port"] = _printer.Port.ToString(), ["secure"] = "false", ["first_number"] = first, ["last_number"] = last });

    private static Entry Person(Guid id, string name, string? email = null) =>
        new(id.ToString(), name, new ScanDestination(ScanProtocol.Smb, "scanserver", $"{Share}\\{id}", "scanserver\\scanrelay-printer", "share-password"), email);

    private List<KyoceraContact> Ours => [.. _printer.Contacts.Values.Where(c => c.Id >= 100).OrderBy(c => c.Id)];

    private List<KyoceraContact> Theirs => [.. _printer.Contacts.Values.Where(c => c.Id < 100).OrderBy(c => c.Id)];

    [Fact]
    public void The_module_data_passes_the_schema_and_says_the_method_is_unofficial()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "ScanRelay.slnx"))) dir = dir.Parent;
        var data = ModuleData.Parse(File.ReadAllText(Path.Combine(dir.FullName, "modules", "kyocera", "ecosys", "module.yaml")));

        Assert.Equal(_adapter.Id, data.Id);
        Assert.True(data.Unofficial);
        Assert.Contains("does not document", data.Method);
        Assert.True(data.MatchesWindowsName("Kyocera ECOSYS M8124cidn KX"));
        Assert.Equal(100, data.EntryLimit());
        Assert.Equal(["2P4_S000.002.204"], data.TestedFirmware);
    }

    [Fact]
    public async Task Test_connection_reports_the_model_and_firmware()
    {
        var probe = await _adapter.ProbeAsync(_device);

        Assert.True(probe.Reachable);
        Assert.Equal("ECOSYS M8124cidn", probe.Model);
        Assert.Equal("2P4_S000.002.204", probe.Firmware);
    }

    [Fact]
    public async Task A_push_adds_people_in_the_reserved_numbers_and_leaves_scan_to_email_alone()
    {
        var before = Theirs;
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());

        var result = await _adapter.ApplyAsync(_device, [Person(ann, "Ann Example"), Person(bob, "Bob Example")]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal((2, 0, 0), (result.Added, result.Updated, result.Removed));
        Assert.Equal(before, Theirs);
        Assert.Equal([100, 101], Ours.Select(c => c.Id));
        var contact = Ours[0];
        Assert.Equal("Ann Example", contact.Name);
        Assert.Equal("scanserver", contact.SmbServer);
        Assert.Equal($"Scans$\\{ann}", contact.SmbPath);
        Assert.Equal(445, contact.SmbPort);
        Assert.Equal("scanserver\\scanrelay-printer", contact.SmbLogin);
        Assert.Equal("share-password", contact.SmbPassword);
    }

    [Fact]
    public async Task Pushing_the_same_list_twice_writes_nothing_the_second_time()
    {
        var list = Enumerable.Range(1, 60).Select(i => Person(Guid.NewGuid(), $"Person {i}")).ToList();
        await _adapter.ApplyAsync(_device, list);
        var writes = _printer.Writes;

        var again = await _adapter.ApplyAsync(_device, list);

        Assert.True(again.Succeeded, again.Error);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
        Assert.Equal(writes, _printer.Writes);
        Assert.Equal(60, Ours.Count);
    }

    [Fact]
    public async Task Joiners_leavers_and_renames_go_in_one_push_and_numbers_are_reused()
    {
        var (ann, bob, cy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _adapter.ApplyAsync(_device, [Person(ann, "Ann"), Person(bob, "Bob")]);
        var before = Theirs;

        var result = await _adapter.ApplyAsync(_device, [Person(bob, "Robert"), Person(cy, "Cy")]);

        Assert.Equal((1, 1, 1), (result.Added, result.Updated, result.Removed));
        Assert.Equal(["Cy", "Robert"], Ours.Select(c => c.Name).Order());
        Assert.Equal(101, Ours.Single(c => c.Name == "Robert").Id);
        Assert.Equal(100, Ours.Single(c => c.Name == "Cy").Id);
        Assert.Equal(before, Theirs);
    }

    [Fact]
    public async Task An_empty_list_removes_only_our_contacts()
    {
        await _adapter.ApplyAsync(_device, [Person(Guid.NewGuid(), "Ann"), Person(Guid.NewGuid(), "Bob")]);
        var before = Theirs;

        var result = await _adapter.ApplyAsync(_device, []);

        Assert.Equal(2, result.Removed);
        Assert.Empty(Ours);
        Assert.Equal(before, Theirs);
    }

    [Fact]
    public async Task A_contact_someone_else_put_in_the_reserved_numbers_is_not_touched()
    {
        _printer.Contacts[100] = new KyoceraContact(100, "Added at the panel", Email: "panel@example.com");
        _printer.Contacts[101] = new KyoceraContact(101, "Someone's own folder", SmbServer: "fileserver", SmbPath: "home\\someone", SmbLogin: "someone");

        var result = await _adapter.ApplyAsync(_device, [Person(Guid.NewGuid(), "Ann")]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Added at the panel", _printer.Contacts[100].Name);
        Assert.Equal("Someone's own folder", _printer.Contacts[101].Name);
        Assert.Equal("Ann", _printer.Contacts[102].Name);

        await _adapter.ApplyAsync(_device, []);
        Assert.Equal(2, Ours.Count);
    }

    [Fact]
    public async Task A_contact_of_ours_that_someone_gave_a_fax_number_is_no_longer_ours_to_change()
    {
        var ann = Guid.NewGuid();
        await _adapter.ApplyAsync(_device, [Person(ann, "Ann")]);
        _printer.Contacts[100] = _printer.Contacts[100] with { Fax = "5550123" };

        await _adapter.ApplyAsync(_device, []);

        Assert.Equal("5550123", _printer.Contacts[100].Fax);
    }

    [Fact]
    public async Task Each_persons_email_address_goes_on_their_contact_so_scan_to_email_works()
    {
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());

        var result = await _adapter.ApplyAsync(_device, [Person(ann, "Ann", "ann@example.com"), Person(bob, "Bob")]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("ann@example.com", _printer.Contacts[100].Email);
        Assert.Equal($"Scans$\\{ann}", _printer.Contacts[100].SmbPath);
        Assert.Equal("", _printer.Contacts[101].Email);
        var read = await _adapter.ReadManagedAsync(_device);
        Assert.Equal(["ann@example.com", null], read!.Select(e => e.Email));
    }

    [Fact]
    public async Task A_changed_or_removed_email_address_is_pushed_and_an_unchanged_one_is_not()
    {
        var ann = Guid.NewGuid();
        await _adapter.ApplyAsync(_device, [Person(ann, "Ann", "ann@example.com")]);
        var writes = _printer.Writes;

        Assert.Equal(0, (await _adapter.ApplyAsync(_device, [Person(ann, "Ann", "ANN@example.com")])).Updated);
        Assert.Equal(writes, _printer.Writes);

        Assert.Equal(1, (await _adapter.ApplyAsync(_device, [Person(ann, "Ann", "ann.new@example.com")])).Updated);
        Assert.Equal("ann.new@example.com", _printer.Contacts[100].Email);

        Assert.Equal(1, (await _adapter.ApplyAsync(_device, [Person(ann, "Ann")])).Updated);
        Assert.Equal("", _printer.Contacts[100].Email);
        Assert.Equal($"Scans$\\{ann}", _printer.Contacts[100].SmbPath);
    }

    [Fact]
    public async Task Our_contacts_can_be_read_back_for_the_drift_check()
    {
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());
        await _adapter.ApplyAsync(_device, [Person(ann, "Ann"), Person(bob, "Bob")]);

        var read = await _adapter.ReadManagedAsync(_device);

        Assert.Equal([ann.ToString(), bob.ToString()], read!.Select(e => e.Key));
        Assert.Equal(["Ann", "Bob"], read!.Select(e => e.DisplayName));
        Assert.All(read!, e => Assert.Equal("", e.Destination.Password));
    }

    [Fact]
    public async Task A_changed_share_password_is_pushed_when_the_printer_gives_passwords_back()
    {
        var ann = Guid.NewGuid();
        await _adapter.ApplyAsync(_device, [Person(ann, "Ann")]);
        var changed = Person(ann, "Ann");
        changed = changed with { Destination = changed.Destination with { Password = "new-password" } };

        var result = await _adapter.ApplyAsync(_device, [changed]);

        Assert.Equal(1, result.Updated);
        Assert.Equal("new-password", _printer.Contacts[100].SmbPassword);
    }

    [Fact]
    public async Task A_printer_that_hides_passwords_is_not_rewritten_on_every_push()
    {
        var list = new[] { Person(Guid.NewGuid(), "Ann") };
        await _adapter.ApplyAsync(_device, list);
        _printer.HidePasswords = true;
        var writes = _printer.Writes;

        var result = await _adapter.ApplyAsync(_device, list);

        Assert.Equal(0, result.Updated);
        Assert.Equal(writes, _printer.Writes);
    }

    [Fact]
    public async Task A_long_address_book_is_read_across_pages()
    {
        for (var i = 4; i <= 99; i++) _printer.Contacts[i] = new KyoceraContact(i, $"Email {i}", Email: $"e{i}@example.com");
        var list = Enumerable.Range(1, 100).Select(i => Person(Guid.NewGuid(), $"Person {i}")).ToList();

        var result = await _adapter.ApplyAsync(_device, list);
        var again = await _adapter.ApplyAsync(_device, list);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(100, result.Added);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
        Assert.Equal(199, _printer.Contacts.Count);
        Assert.True(_printer.Operations.Count(o => o == "get_personal_address_list") >= 4);
    }

    [Fact]
    public async Task When_the_reserved_numbers_run_out_the_push_says_so()
    {
        var list = Enumerable.Range(1, 6).Select(i => Person(Guid.NewGuid(), $"Person {i}")).ToList();

        var result = await _adapter.ApplyAsync(DeviceWith("100", "104"), list);

        Assert.False(result.Succeeded);
        Assert.Contains("no free contact numbers left between 100 and 104", result.Error);
        Assert.Equal(5, result.Added);
    }

    [Fact]
    public async Task Numbers_the_printer_does_not_allow_are_reported_with_a_reason()
    {
        var result = await _adapter.ApplyAsync(DeviceWith("900", "999"), [Person(Guid.NewGuid(), "Ann")]);

        Assert.False(result.Succeeded);
        Assert.Contains("contact number may be outside what this model allows", result.Error);
        Assert.Equal(3, _printer.Contacts.Count);
    }

    [Fact]
    public async Task A_name_longer_than_the_printer_allows_is_cut()
    {
        var result = await _adapter.ApplyAsync(_device, [Person(Guid.NewGuid(), "Bartholomew Maximilian Featherstonehaugh-Cholmondeley")]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Bartholomew Maximilian Featherst", Ours[0].Name);
        Assert.Equal(32, Ours[0].Name.Length);
    }

    [Fact]
    public async Task A_printer_waking_from_sleep_is_given_a_moment()
    {
        _printer.BusyAnswers = 2;

        var result = await _adapter.ApplyAsync(_device, [Person(Guid.NewGuid(), "Ann")]);

        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task A_printer_with_sign_in_switched_on_is_reported_not_thrown()
    {
        _printer.RequiresSignIn = true;

        var push = await _adapter.ApplyAsync(_device, [Person(Guid.NewGuid(), "Ann")]);
        var probe = await _adapter.ProbeAsync(_device);

        Assert.False(push.Succeeded);
        Assert.Contains("cannot sign in", push.Error);
        Assert.False(probe.Reachable);
    }

    [Fact]
    public async Task A_printer_that_is_switched_off_is_reported_not_thrown()
    {
        await _printer.DisposeAsync();

        var push = await _adapter.ApplyAsync(_device, [Person(Guid.NewGuid(), "Ann")]);

        Assert.False(push.Succeeded);
        Assert.Contains("Could not reach the printer", push.Error);
        _printer = await MockKyocera.StartAsync();
    }

    [Fact]
    public async Task The_first_write_test_adds_changes_and_deletes_one_contact_and_proves_the_rest_untouched()
    {
        var before = _printer.Contacts.Values.OrderBy(c => c.Id).ToList();
        var report = new StringWriter();

        var ok = await KyoceraProbe.WriteTest.RunAsync(new HttpClient(), $"http://{_printer.Address}:{_printer.Port}", 150, report);

        Assert.True(ok, report.ToString());
        Assert.Equal(before, _printer.Contacts.Values.OrderBy(c => c.Id));
        Assert.Equal(4, _printer.Writes);
        Assert.Contains("nothing else was touched", report.ToString());
        Assert.DoesNotContain("Throwaway1", report.ToString());
        Assert.DoesNotContain("old-password", report.ToString());
    }

    [Fact]
    public async Task The_write_test_refuses_a_number_that_is_in_use_and_stops_cleanly_on_a_refusal()
    {
        var report = new StringWriter();
        Assert.False(await KyoceraProbe.WriteTest.RunAsync(new HttpClient(), $"http://{_printer.Address}:{_printer.Port}", 2, report));
        Assert.Contains("already in use, so nothing was done", report.ToString());
        Assert.Equal(0, _printer.Writes);

        Assert.False(await KyoceraProbe.WriteTest.RunAsync(new HttpClient(), $"http://{_printer.Address}:{_printer.Port}", 900, report));
        Assert.Contains("STOPPED", report.ToString());
        Assert.Equal(0, _printer.Writes);
    }

    [Fact]
    public async Task The_file_for_applying_by_hand_lists_each_person_without_the_password()
    {
        var file = await _adapter.ExportAsync(_device, [Person(Guid.NewGuid(), "Ann \"Annie\" Example")]);

        var text = System.Text.Encoding.UTF8.GetString(file.Content);
        Assert.StartsWith("Number,Name,Host name,Path,Port,Login user name,E-mail address", text);
        Assert.Contains("100,\"Ann \"\"Annie\"\" Example\",\"scanserver\"", text);
        Assert.DoesNotContain("share-password", text);
    }
}
