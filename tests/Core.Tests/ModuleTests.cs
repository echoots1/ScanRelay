using ScanRelay.Core.Data;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Printers;
using ScanRelay.MockDevices;

namespace ScanRelay.Core.Tests;

public sealed class ModuleTests : IDisposable
{
    private readonly SampleModule _sample = new();

    public void Dispose() => _sample.Cleanup();

    private Task<TestEnvironment> StartAsync() =>
        TestEnvironment.StartAsync(_sample.Register, prepare: i => i.ModulesFolder = _sample.ModulesFolder);

    private static Printer PrinterFor(MockPrinter mock, TestEnvironment env) => new()
    {
        Id = 1,
        Name = "Front office",
        Address = mock.Address,
        ModuleId = "scanrelay-sample",
        AdminUser = mock.AdminUser,
        AdminPasswordProtected = env.Get<Security.SecretProtector>().ProtectBytes(mock.AdminPassword),
        OptionsJson = $$"""{"port":"{{mock.Port}}"}""",
    };

    [Fact]
    public void The_sample_module_data_passes_the_schema()
    {
        var data = ModuleData.Parse(File.ReadAllText(Path.Combine(SampleModule.BuildOutput(), "module.yaml")));

        Assert.Equal("scanrelay-sample", data.Id);
        Assert.Equal(100, data.EntryLimit());
        Assert.True(data.MatchesWindowsName("ScanRelay MockPrinter 1000 PCL6"));
        Assert.True(data.MatchesWindowsName("mockprinter 1000"));
        Assert.False(data.MatchesWindowsName("Some Other Printer"));
    }

    [Fact]
    public void Every_module_in_the_repo_passes_the_schema()
    {
        var dir = new DirectoryInfo(SampleModule.BuildOutput());
        while (!File.Exists(Path.Combine(dir!.FullName, "ScanRelay.slnx"))) dir = dir.Parent;
        var files = Directory.EnumerateFiles(Path.Combine(dir.FullName, "modules"), "module.yaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        Assert.NotEmpty(files);
        Assert.All(files, f => ModuleData.Parse(File.ReadAllText(f)));
    }

    [Theory]
    [InlineData("id: scanrelay-sample", "id: Bad Id")]
    [InlineData("version: 1.0.0", "version: one")]
    [InlineData("protocols: [smb, ftp]", "protocols: [smb, carrier-pigeon]")]
    [InlineData("smb_versions: [\"2.0\", \"3.0\"]", "smb_versions: [\"1.0\", \"2.0\"]")]
    [InlineData("share_rights: [create, write]", "share_rights: [read]")]
    [InlineData("managed_range: [900, 999]", "managed_range: [999, 900]")]
    [InlineData("status: official", "status: blessed")]
    [InlineData("adapter: ScanRelay.Modules.Sample.dll", "adapter: ../evil.dll")]
    public void Module_data_that_breaks_the_schema_is_refused(string from, string to)
    {
        var yaml = File.ReadAllText(Path.Combine(SampleModule.BuildOutput(), "module.yaml"));
        Assert.Contains(from, yaml);

        Assert.Throws<ModuleException>(() => ModuleData.Parse(yaml.Replace(from, to)));
    }

    [Fact]
    public void An_official_package_installs_and_an_altered_one_is_refused()
    {
        var store = new ModuleStore(_sample.ModulesFolder, _sample.Keys);
        var (path, signature) = _sample.Pack();

        var installed = store.Install(path, signature, allowCustom: false, DateTime.UtcNow);
        Assert.Equal("official", installed.Status);
        Assert.Equal(installed, Assert.Single(store.Read()));

        var bytes = File.ReadAllBytes(path);
        bytes[^10] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        var error = Assert.ThrowsAny<ModuleException>(() => store.Install(path, signature, allowCustom: false, DateTime.UtcNow));
        Assert.Single(store.Read());
        Assert.NotNull(error.Message);
    }

    [Fact]
    public void A_package_signed_by_someone_else_is_only_accepted_as_custom()
    {
        var store = new ModuleStore(_sample.ModulesFolder, _sample.Keys);
        var (path, signature) = _sample.Pack(signWith: ModuleSigning.NewKeyPair().PrivatePem);

        Assert.Throws<ModuleException>(() => store.Install(path, signature, allowCustom: false, DateTime.UtcNow));
        Assert.Throws<ModuleException>(() => store.Install(path, null, allowCustom: false, DateTime.UtcNow));

        var installed = store.Install(path, null, allowCustom: true, DateTime.UtcNow);
        Assert.Equal("custom", installed.Status);
        Assert.True(store.Remove("scanrelay-sample"));
        Assert.Empty(store.Read());
        Assert.Empty(Directory.EnumerateFiles(_sample.ModulesFolder, "*" + ModulePackage.Extension));
    }

    [Fact]
    public async Task The_service_loads_an_installed_module_and_matches_printer_models_to_it()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();

        var module = Assert.Single(env.Get<ModuleHost>().Modules);

        Assert.Null(module.Problem);
        Assert.True(module.Usable);
        Assert.False(module.IsCustom);
        Assert.Equal("scanrelay-sample", module.Adapter!.Id);
        Assert.Single(env.Get<ModuleHost>().Match("ScanRelay MockPrinter 1000"));
        Assert.Empty(env.Get<ModuleHost>().Match("Unknown 9000"));
    }

    [Fact]
    public async Task A_package_changed_after_install_is_not_loaded()
    {
        var installed = _sample.InstallOfficial();
        var (other, _) = _sample.Pack(y => y.Replace("version: 1.0.0", "version: 1.0.1"));
        File.Copy(other, Path.Combine(_sample.ModulesFolder, installed.File), overwrite: true);
        await using var env = await StartAsync();

        var module = Assert.Single(env.Get<ModuleHost>().Modules);

        Assert.False(module.Usable);
        Assert.Contains("changed since it was installed", module.Problem);
    }

    [Fact]
    public async Task A_module_marked_official_without_the_projects_signature_is_not_loaded()
    {
        var installed = _sample.InstallOfficial();
        File.WriteAllText(
            Path.Combine(_sample.ModulesFolder, installed.File + ModulePackage.SignatureExtension),
            ModuleSigning.Sign(ModuleSigning.NewKeyPair().PrivatePem, File.ReadAllBytes(Path.Combine(_sample.ModulesFolder, installed.File))));
        await using var env = await StartAsync();

        Assert.Contains("signature", Assert.Single(env.Get<ModuleHost>().Modules).Problem);
    }

    [Fact]
    public async Task A_module_with_no_version_for_this_app_pauses_pushes_to_its_printers()
    {
        _sample.InstallOfficial(y => y.Replace("min: 0.1.0", "min: 0.0.1\n  max: 0.0.2"));
        await using var env = await StartAsync();
        await using var mock = await MockPrinter.StartAsync();

        var module = Assert.Single(env.Get<ModuleHost>().Modules);
        var outcome = await env.Get<PrinterService>().PushAsync(PrinterFor(mock, env), [new WantedEntry(Guid.NewGuid(), "Sam Scanner")]);

        Assert.False(module.Usable);
        Assert.True(outcome.Paused);
        Assert.Contains("paused", outcome.Error);
        Assert.Empty(mock.Managed);
    }

    [Fact]
    public async Task A_push_fills_the_reserved_range_and_leaves_other_entries_alone()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        await using var mock = await MockPrinter.StartAsync();
        var printer = PrinterFor(mock, env);
        var before = mock.Unmanaged;
        var (sam, robin) = (new WantedEntry(Guid.NewGuid(), "Sam Scanner"), new WantedEntry(Guid.NewGuid(), "Robin Reader"));
        mock.AddressBook[950] = new MockEntry(950, "Left over from a test", null, "smb", "x", "y", "z", null);

        var outcome = await env.Get<PrinterService>().PushAsync(printer, [sam, robin]);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal((2, 0, 1), (outcome.Added, outcome.Updated, outcome.Removed));
        Assert.Equal(before, mock.Unmanaged);
        var entry = mock.Managed.Single(e => e.Key == sam.PersonId.ToString());
        Assert.Equal("Sam Scanner", entry.Name);
        Assert.Equal("smb", entry.Protocol);
        Assert.Equal("scanserver", entry.Host);
        Assert.Equal($"Scans$\\{sam.PersonId}", entry.Path);
        Assert.Equal("scanserver\\scanrelay-printer", entry.User);
    }

    [Fact]
    public async Task Pushing_the_same_list_twice_changes_nothing_the_second_time()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        await using var mock = await MockPrinter.StartAsync();
        var printer = PrinterFor(mock, env);
        var list = Enumerable.Range(1, 5).Select(i => new WantedEntry(Guid.NewGuid(), $"Person {i}")).ToList();

        await env.Get<PrinterService>().PushAsync(printer, list);
        var writes = mock.Writes;
        var again = await env.Get<PrinterService>().PushAsync(printer, list);

        Assert.True(again.Succeeded);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
        Assert.Equal(writes, mock.Writes);
    }

    [Fact]
    public async Task A_new_list_adds_renames_and_removes_in_one_push_and_can_be_read_back()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        await using var mock = await MockPrinter.StartAsync();
        var printer = PrinterFor(mock, env);
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await env.Get<PrinterService>().PushAsync(printer, [new(a, "Ann"), new(b, "Bob")]);

        var outcome = await env.Get<PrinterService>().PushAsync(printer, [new(b, "Robert"), new(c, "Cy")]);

        Assert.Equal((1, 1, 1), (outcome.Added, outcome.Updated, outcome.Removed));
        var read = await env.Get<PrinterService>().ReadBackAsync(printer);
        Assert.Equal([new WantedEntry(c, "Cy"), new WantedEntry(b, "Robert")], read!.OrderBy(e => e.DisplayName));
    }

    [Fact]
    public async Task More_people_than_the_printer_holds_is_refused_with_a_reason()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        await using var mock = await MockPrinter.StartAsync();
        var list = Enumerable.Range(1, 101).Select(i => new WantedEntry(Guid.NewGuid(), $"Person {i}")).ToList();

        var outcome = await env.Get<PrinterService>().PushAsync(PrinterFor(mock, env), list);

        Assert.False(outcome.Succeeded);
        Assert.Contains("at most 100", outcome.Error);
        Assert.Empty(mock.Managed);
    }

    [Fact]
    public async Task A_wrong_password_or_a_broken_printer_is_reported_not_thrown()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        await using var mock = await MockPrinter.StartAsync();
        var printer = PrinterFor(mock, env);
        var service = env.Get<PrinterService>();

        Assert.True((await service.ProbeAsync(printer)).Reachable);
        Assert.Equal("MockPrinter 1000", (await service.ProbeAsync(printer)).Model);

        mock.AdminPassword = "changed";
        var refused = await service.PushAsync(printer, [new(Guid.NewGuid(), "Ann")]);
        Assert.Contains("refused the admin sign-in", refused.Error);

        mock.Broken = true;
        Assert.False((await service.ProbeAsync(printer)).Reachable);
        Assert.Contains("500", (await service.PushAsync(printer, [])).Error);
    }

    [Fact]
    public async Task The_import_file_lists_the_entries_without_the_password()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        await using var mock = await MockPrinter.StartAsync();
        env.Install.PrinterPasswordProtected = PrinterAccountSecret.Protect("share-secret");

        var file = await env.Get<PrinterService>().ExportAsync(PrinterFor(mock, env), [new(Guid.NewGuid(), "Ann \"Annie\" Example")]);

        var text = System.Text.Encoding.UTF8.GetString(file!.Content);
        Assert.EndsWith(".csv", file.FileName);
        Assert.Contains("\"Ann \"\"Annie\"\" Example\"", text);
        Assert.DoesNotContain("share-secret", text);
    }

    [Fact]
    public async Task A_printer_whose_module_is_not_installed_says_so()
    {
        await using var env = await StartAsync();
        await using var mock = await MockPrinter.StartAsync();

        var outcome = await env.Get<PrinterService>().PushAsync(PrinterFor(mock, env), []);

        Assert.Contains("not installed", outcome.Error);
    }
}
