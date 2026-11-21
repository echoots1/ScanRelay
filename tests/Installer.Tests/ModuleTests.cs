using System.IO.Compression;
using System.Net;
using System.Text;
using ScanRelay.Core.Install;
using ScanRelay.Core.Modules;

namespace ScanRelay.Installer.Tests;

public sealed class ModuleTests : IDisposable
{
    private static readonly Version App = new(0, 1, 0);
    private readonly string _work = Directory.CreateTempSubdirectory("scanrelay-setup-").FullName;
    private readonly (string PrivatePem, string PublicPem) _projectKey = ModuleSigning.NewKeyPair();
    private readonly SetupLog _log = new(null, null, null, 0);

    private string ModulesFolder => Path.Combine(_work, "Modules");
    private TrustedKeys Keys => new([_projectKey.PublicPem]);
    private ModuleInstaller Installer => new(ModulesFolder, Keys, _log, TimeProvider.System);

    public void Dispose() => Directory.Delete(_work, recursive: true);

    /// <summary>Builds a module package the way the release pipeline does, without needing a real adapter.</summary>
    private string Pack(string id, string version = "1.0.0", string shareRights = "create, write", int sdk = 1, string minApp = "0.1.0", string? maxApp = null)
    {
        var path = Path.Combine(_work, $"{id}-{version}-{Guid.NewGuid():N}{ModulePackage.Extension}");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(ModuleData.FileName).Open()))
        {
            writer.Write($"""
                id: {id}
                version: {version}
                vendor: Example
                family: Example printers
                sdk_version: {sdk}
                app_versions:
                  min: {minApp}
                {(maxApp is null ? "" : "  max: " + maxApp)}
                adapter: Adapter.dll
                scan:
                  protocols: [smb]
                  smb_versions: ["3.0"]
                  share_rights: [{shareRights}]
                """);
        }

        using (var adapter = zip.CreateEntry("Adapter.dll").Open()) adapter.Write([1, 2, 3]);
        return path;
    }

    private string Sign(string path) => ModuleSigning.Sign(_projectKey.PrivatePem, File.ReadAllBytes(path));

    private static InstalledModule Record(string id, string version, string status = "official") =>
        new(id, version, id + ".srmod", "hash", status, DateTime.UnixEpoch);

    private static CatalogEntry Entry(string id, string version, string minApp = "0.1.0", string? maxApp = null) =>
        new(id, version, "Example", "Example", [], [], minApp, maxApp, $"{id}-{version}.srmod", "hash", false, []);

    private static ModulePlan Plan(
        IReadOnlyList<InstalledModule> installed,
        IReadOnlyList<CatalogEntry> catalog,
        string[] requested,
        bool appUpdate = false,
        string[]? removing = null,
        string[]? fromFiles = null,
        string[]? damaged = null) =>
        ModulePlan.Create(installed, new HashSet<string>(damaged ?? []), new CatalogIndex(DateTime.UnixEpoch, [.. catalog]), requested, removing ?? [], fromFiles ?? [], appUpdate, App);

    // ---- Planning ----

    [Fact]
    public void A_requested_module_gets_the_newest_version_that_works_with_the_app()
    {
        var plan = Plan([], [Entry("a", "1.0.0"), Entry("a", "1.2.0"), Entry("a", "2.0.0", minApp: "0.2.0"), Entry("b", "1.0.0")], ["a"]);

        Assert.Equal(["a 1.2.0"], plan.Downloads.Select(d => $"{d.Id} {d.Version}"));
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void A_module_that_is_already_current_is_not_downloaded_again()
    {
        var plan = Plan([Record("a", "1.2.0")], [Entry("a", "1.2.0")], ["a"]);
        Assert.Empty(plan.Downloads);

        var damaged = Plan([Record("a", "1.2.0")], [Entry("a", "1.2.0")], ["a"], damaged: ["a"]);
        Assert.Single(damaged.Downloads);
    }

    [Fact]
    public void An_app_update_brings_every_official_module_along()
    {
        var installed = new[] { Record("a", "1.0.0"), Record("b", "1.0.0"), Record("mine", "1.0.0", "custom") };
        var catalog = new[] { Entry("a", "1.1.0"), Entry("b", "1.0.0"), Entry("mine", "9.0.0") };

        Assert.Empty(Plan(installed, catalog, []).Downloads);
        var plan = Plan(installed, catalog, [], appUpdate: true);

        // The custom module is the admin's own and is never swapped for a catalog one unasked.
        Assert.Equal(["a"], plan.Downloads.Select(d => d.Id));
    }

    [Fact]
    public void An_installed_module_with_no_version_for_the_new_app_is_a_warning_not_a_failure()
    {
        var plan = Plan([Record("a", "1.0.0")], [Entry("a", "1.0.0", maxApp: "0.0.9"), Entry("a", "3.0.0", minApp: "9.0.0")], [], appUpdate: true);

        Assert.Empty(plan.Downloads);
        Assert.Empty(plan.Problems);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("\"a\" (installed: 1.0.0) has no version for ScanRelay 0.1.0", warning);
        Assert.Contains("pushes to its printers are paused", warning);
    }

    [Fact]
    public void Asking_for_a_module_that_cannot_be_had_is_a_problem()
    {
        var plan = Plan([], [Entry("a", "3.0.0", minApp: "9.0.0")], ["a", "nowhere"]);

        Assert.Empty(plan.Downloads);
        Assert.Contains(plan.Problems, p => p.Contains("\"a\" has no version that works with ScanRelay 0.1.0"));
        Assert.Contains(plan.Problems, p => p.Contains("\"nowhere\" is not in the project's module list"));
    }

    [Fact]
    public void Modules_being_removed_or_installed_from_files_are_left_to_those_steps()
    {
        var plan = Plan([Record("a", "1.0.0"), Record("b", "1.0.0")], [Entry("a", "2.0.0"), Entry("b", "2.0.0"), Entry("c", "1.0.0")], ["c"], appUpdate: true, removing: ["a"], fromFiles: ["c"]);
        Assert.Equal(["b"], plan.Downloads.Select(d => d.Id));
    }

    [Fact]
    public void A_custom_module_built_for_another_sdk_or_app_gets_a_warning()
    {
        var otherSdk = ModulePackage.ReadData(Pack("in-house", sdk: 2));
        var otherApp = ModulePackage.ReadData(Pack("in-house", maxApp: "0.0.5"));
        var fine = ModulePackage.ReadData(Pack("in-house"));

        Assert.Contains("built for module SDK 2", ModulePlan.CustomWarning(otherSdk, App));
        Assert.Contains("does not work with ScanRelay 0.1.0", ModulePlan.CustomWarning(otherApp, App));
        Assert.Null(ModulePlan.CustomWarning(fine, App));
    }

    // ---- Installing from files ----

    [Fact]
    public void A_custom_module_is_only_installed_when_accepted()
    {
        var file = Pack("in-house");

        var refused = Assert.Throws<SetupException>(() => Installer.InstallCustomFiles([file], accepted: false));
        Assert.Contains("must be accepted first", refused.Message);
        Assert.Empty(Installer.Installed);

        Assert.Equal(["in-house"], Installer.InstallCustomFiles([file], accepted: true));
        var module = Assert.Single(Installer.Installed);
        Assert.Equal("custom", module.Status);
        Assert.True(File.Exists(Path.Combine(ModulesFolder, "in-house.srmod")));
    }

    [Fact]
    public void An_official_module_file_needs_a_good_signature_beside_it()
    {
        var file = Pack("official-one");

        var missing = Assert.Throws<SetupException>(() => Installer.InstallOfficialFiles([file]));
        Assert.Contains("needs its signature file", missing.Message);

        File.WriteAllText(file + ".sig", ModuleSigning.Sign(ModuleSigning.NewKeyPair().PrivatePem, File.ReadAllBytes(file)));
        var wrong = Assert.Throws<SetupException>(() => Installer.InstallOfficialFiles([file]));
        Assert.Contains("not signed by this project", wrong.Message);
        Assert.Empty(Installer.Installed);

        File.WriteAllText(file + ".sig", Sign(file));
        Assert.Equal(["official-one"], Installer.InstallOfficialFiles([file]));
        Assert.Equal("official", Assert.Single(Installer.Installed).Status);

        Assert.Throws<SetupException>(() => Installer.InstallOfficialFiles([Path.Combine(_work, "not-there.srmod")]));
    }

    [Fact]
    public void A_setup_built_without_the_signing_key_says_so()
    {
        var file = Pack("official-one");
        File.WriteAllText(file + ".sig", Sign(file));
        var keyless = new ModuleInstaller(ModulesFolder, new TrustedKeys([]), _log, TimeProvider.System);
        Assert.Contains("built without the project's signing key", Assert.Throws<SetupException>(() => keyless.InstallOfficialFiles([file])).Message);
    }

    [Fact]
    public void Removing_takes_the_module_out_and_tolerates_one_that_is_not_there()
    {
        Installer.InstallCustomFiles([Pack("in-house")], accepted: true);
        Installer.Remove(["in-house", "never-installed"]);
        Assert.Empty(Installer.Installed);
        Assert.False(File.Exists(Path.Combine(ModulesFolder, "in-house.srmod")));
    }

    [Fact]
    public void Share_rights_come_from_what_is_installed()
    {
        Assert.Equal(InboxRights.Strict, InboxRights.For(Installer.ShareRights()));

        Installer.InstallCustomFiles([Pack("plain"), Pack("needs-more", shareRights: "create, write, list, delete")], accepted: true);
        var rights = InboxRights.For(Installer.ShareRights());
        Assert.True(rights.Folders.HasFlag(NtfsRights.ReadDataOrListFolder));
        Assert.True(rights.Files.HasFlag(NtfsRights.Delete));
        Assert.False(rights.Files.HasFlag(NtfsRights.ReadDataOrListFolder));

        Installer.Remove(["needs-more"]);
        Assert.Equal(InboxRights.Strict, InboxRights.For(Installer.ShareRights()));
    }

    [Fact]
    public void Installed_custom_modules_for_another_sdk_are_warned_about()
    {
        Installer.InstallCustomFiles([Pack("old-sdk", sdk: 2), Pack("fine")], accepted: true);
        var warning = Assert.Single(Installer.CustomWarnings(App));
        Assert.Contains("\"old-sdk\"", warning);
    }

    // ---- The catalog ----

    /// <summary>Stands in for GitHub's release downloads.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Assets { get; } = [];
        public bool Unreachable { get; set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Unreachable) throw new HttpRequestException("No such host is known.");
            Assert.StartsWith("https://downloads.example.test/someone/fork/releases/latest/download/", request.RequestUri!.ToString());
            return Task.FromResult(Assets.TryGetValue(request.RequestUri.Segments[^1], out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private FakeGitHub Publish(params string[] packages)
    {
        var release = Path.Combine(_work, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(release);
        var github = new FakeGitHub();
        foreach (var package in packages)
        {
            var data = ModulePackage.ReadData(package);
            var name = $"{data.Id}-{data.Version}{ModulePackage.Extension}";
            File.Copy(package, Path.Combine(release, name));
            github.Assets[name] = File.ReadAllBytes(package);
            github.Assets[name + ".sig"] = Encoding.UTF8.GetBytes(Sign(package));
        }

        var index = Encoding.UTF8.GetBytes(ModuleCatalog.Build(release, DateTime.UnixEpoch));
        github.Assets[ModuleCatalog.IndexName] = index;
        github.Assets[ModuleCatalog.IndexName + ".sig"] = Encoding.UTF8.GetBytes(ModuleSigning.Sign(_projectKey.PrivatePem, index));
        return github;
    }

    private static InstallConfig Source()
    {
        var config = new InstallConfig { Repository = "someone/fork" };
        config.Cloud.GitHubDownload = "https://downloads.example.test";
        return config;
    }

    private Task FromCatalog(FakeGitHub github, string[] requested, bool appUpdate = false) =>
        Installer.InstallFromCatalogAsync(new HttpClient(github), Source(), requested, [], [], appUpdate, App, CancellationToken.None);

    [Fact]
    public async Task Requested_modules_are_downloaded_checked_and_installed()
    {
        var github = Publish(Pack("kyo"), Pack("bro", version: "1.1.0"));

        await FromCatalog(github, ["kyo"]);

        var module = Assert.Single(Installer.Installed);
        Assert.Equal(("kyo", "1.0.0", "official"), (module.Id, module.Version, module.Status));
        Assert.True(File.Exists(Path.Combine(ModulesFolder, "kyo.srmod.sig")));
    }

    [Fact]
    public async Task A_download_that_was_tampered_with_is_refused()
    {
        var github = Publish(Pack("kyo"));
        github.Assets["kyo-1.0.0.srmod"] = File.ReadAllBytes(Pack("kyo", shareRights: "create, write, read, list, delete"));

        var e = await Assert.ThrowsAsync<SetupException>(() => FromCatalog(github, ["kyo"]));
        Assert.Contains("does not match the module list", e.Message);
        Assert.Empty(Installer.Installed);
    }

    [Fact]
    public async Task GitHub_is_not_contacted_when_nothing_is_asked_of_it()
    {
        var github = new FakeGitHub { Unreachable = true };
        Installer.InstallCustomFiles([Pack("in-house")], accepted: true);

        await FromCatalog(github, []);
        await FromCatalog(github, [], appUpdate: true);

        Assert.Equal(0, github.Requests);
    }

    [Fact]
    public async Task Unreachable_GitHub_fails_clearly_when_a_module_was_asked_for()
    {
        var github = new FakeGitHub { Unreachable = true };

        var e = await Assert.ThrowsAsync<SetupException>(() => FromCatalog(github, ["kyo"]));

        Assert.Contains("could not be fetched from GitHub (someone/fork)", e.Message);
        Assert.Contains("kyo cannot be installed", e.Message);
        Assert.Contains("internet access", e.Message);
    }

    [Fact]
    public async Task Unreachable_GitHub_does_not_stop_a_repair_or_update_of_what_is_already_installed()
    {
        await FromCatalog(Publish(Pack("kyo")), ["kyo"]);
        var github = new FakeGitHub { Unreachable = true };

        await FromCatalog(github, ["kyo"]);
        Assert.Empty(_log.Warnings);

        await FromCatalog(github, ["kyo"], appUpdate: true);
        Assert.Contains("installed modules were left as they are", Assert.Single(_log.Warnings));
        Assert.Equal("1.0.0", Assert.Single(Installer.Installed).Version);
    }

    [Fact]
    public async Task An_app_update_moves_installed_modules_to_the_newest_matching_version()
    {
        await FromCatalog(Publish(Pack("kyo")), ["kyo"]);

        await FromCatalog(Publish(Pack("kyo", version: "1.1.0"), Pack("bro")), [], appUpdate: true);

        Assert.Equal(["kyo 1.1.0"], Installer.Installed.Select(m => $"{m.Id} {m.Version}"));
    }

    [Fact]
    public async Task A_missing_module_file_is_fetched_again()
    {
        var github = Publish(Pack("kyo"));
        await FromCatalog(github, ["kyo"]);
        File.Delete(Path.Combine(ModulesFolder, "kyo.srmod"));

        await FromCatalog(github, ["kyo"]);

        Assert.True(File.Exists(Path.Combine(ModulesFolder, "kyo.srmod")));
    }

    [Fact]
    public async Task The_update_preview_warns_without_changing_anything()
    {
        await FromCatalog(Publish(Pack("kyo")), ["kyo"]);
        Installer.InstallCustomFiles([Pack("old-sdk", sdk: 2)], accepted: true);
        var before = File.ReadAllText(Path.Combine(ModulesFolder, ModuleStore.ManifestName));
        var newRelease = Publish(Pack("kyo", version: "2.0.0", minApp: "0.2.0"));

        var warnings = await Installer.PreviewUpdateAsync(new HttpClient(newRelease), Source(), [], App, CancellationToken.None);

        Assert.Contains(warnings, w => w.Contains("\"kyo\" (installed: 1.0.0) has no version for ScanRelay 0.1.0"));
        Assert.Contains(warnings, w => w.Contains("\"old-sdk\" was built for module SDK 2"));
        Assert.Equal(before, File.ReadAllText(Path.Combine(ModulesFolder, ModuleStore.ManifestName)));

        var offline = await Installer.PreviewUpdateAsync(new HttpClient(new FakeGitHub { Unreachable = true }), Source(), [], App, CancellationToken.None);
        Assert.Contains(offline, w => w.Contains("could not be fetched from GitHub"));
    }
}
