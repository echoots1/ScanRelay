using System.Text;
using Microsoft.Extensions.Time.Testing;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Health;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Updates;

namespace ScanRelay.Core.Tests;

public sealed class UpdateAndHealthTests : IDisposable
{
    private static readonly Actor Admin = new(Guid.NewGuid(), "Alex Admin");
    private readonly SampleModule _sample = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => _sample.Cleanup();

    private async Task<TestEnvironment> StartAsync(bool alerts = true)
    {
        var env = await TestEnvironment.StartAsync(_sample.Register, _time, i => i.ModulesFolder = _sample.ModulesFolder);
        await env.ConnectAsync();
        if (alerts)
        {
            var admin = env.State.AddUser("Alex Admin");
            var admins = env.State.AddGroup("Admins", admin);
            env.State.AddUser("Alerts", "alerts@example.com");
            await env.Settings.UpdateAsync(s => s with { AdminGroupId = admins.Id, AlertSender = "alerts@example.com" }, Admin);
        }

        return env;
    }

    private void PublishCatalog(TestEnvironment env, params (string Path, string Signature)[] packages)
    {
        var folder = Path.Combine(_sample.WorkFolder, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var (path, signature) in packages)
        {
            var data = ModulePackage.ReadData(path);
            var name = $"{data.Id}-{data.Version}{ModulePackage.Extension}";
            File.Copy(path, Path.Combine(folder, name));
            env.State.ReleaseAssets[name] = File.ReadAllBytes(path);
            env.State.ReleaseAssets[name + ".sig"] = Encoding.UTF8.GetBytes(signature);
        }

        var index = Encoding.UTF8.GetBytes(ModuleCatalog.Build(folder, DateTime.UtcNow));
        env.State.ReleaseAssets[ModuleCatalog.IndexName] = index;
        env.State.ReleaseAssets[ModuleCatalog.IndexName + ".sig"] = Encoding.UTF8.GetBytes(ModuleSigning.Sign(_sample.ProjectKey.PrivatePem, index));
    }

    [Fact]
    public async Task A_new_release_sends_one_email_then_one_reminder_after_fourteen_days()
    {
        await using var env = await StartAsync();
        PublishCatalog(env);
        env.State.Release = ("v9.1.0", "9.1.0", "Fixes upload of large files.\n\nSteps: none beyond the normal update.");
        var checker = env.Get<UpdateChecker>();

        var status = await checker.CheckAsync();
        await checker.CheckAsync();

        Assert.Equal("9.1.0", status.App!.Version);
        Assert.False(status.App.Security);
        var mail = Assert.Single(env.State.Outbox);
        Assert.Equal("ScanRelay: version 9.1.0 is available", mail.Subject);
        Assert.Contains($"Installed version: {ModuleHost.AppVersion}", mail.Body);
        Assert.Contains("Fixes upload of large files.", mail.Body);
        Assert.Contains("releases/tag/v9.1.0", mail.Body);

        _time.Advance(TimeSpan.FromDays(13));
        await checker.CheckAsync();
        Assert.Single(env.State.Outbox);

        _time.Advance(TimeSpan.FromDays(1));
        await checker.CheckAsync();
        await checker.CheckAsync();
        Assert.Equal(2, env.State.Outbox.Count);
        Assert.EndsWith("(reminder)", env.State.Outbox.Last().Subject);
        Assert.Equal("9.1.0", (await checker.GetStatusAsync()).App!.Version);
    }

    [Fact]
    public async Task A_release_with_an_installer_attached_offers_it_for_download()
    {
        await using var env = await StartAsync();
        PublishCatalog(env);
        env.State.ReleaseAssets["SHA256SUMS.txt"] = [1];
        env.State.Release = ("v9.1.0", "9.1.0", "Notes.");

        Assert.Null((await env.Get<UpdateChecker>().CheckAsync()).App!.InstallerUrl);

        // The installer is added to the same release afterwards. The next check picks it up without emailing again.
        env.State.ReleaseAssets["ScanRelaySetup-9.1.0.exe"] = [1, 2, 3];
        var status = await env.Get<UpdateChecker>().CheckAsync();

        Assert.EndsWith("/releases/download/v9.1.0/ScanRelaySetup-9.1.0.exe", status.App!.InstallerUrl);
        Assert.Single(env.State.Outbox);
        Assert.EndsWith("ScanRelaySetup-9.1.0.exe", (await env.Get<UpdateChecker>().GetStatusAsync()).App!.InstallerUrl);
    }

    [Fact]
    public async Task A_security_release_says_so_in_the_subject()
    {
        await using var env = await StartAsync();
        PublishCatalog(env);
        env.State.Release = ("v9.2.0", "9.2.0 [security]", "Fixes a problem.");

        await env.Get<UpdateChecker>().CheckAsync();

        Assert.Equal("ScanRelay: security update 9.2.0 is available", Assert.Single(env.State.Outbox).Subject);
    }

    [Fact]
    public async Task The_installed_version_or_no_release_gives_no_notice()
    {
        await using var env = await StartAsync();
        PublishCatalog(env);
        Assert.Null((await env.Get<UpdateChecker>().CheckAsync()).App);

        env.State.Release = ($"v{ModuleHost.AppVersion}", ModuleHost.AppVersion.ToString(), "");
        var status = await env.Get<UpdateChecker>().CheckAsync();

        Assert.Null(status.App);
        Assert.Null(status.Problem);
        Assert.Empty(env.State.Outbox);
    }

    [Fact]
    public async Task With_the_update_check_off_github_is_never_contacted()
    {
        await using var env = await StartAsync();
        env.State.Release = ("v9.1.0", "9.1.0", "");
        await env.Settings.UpdateAsync(s => s with { UpdateCheck = false }, Admin);

        var status = await env.Get<UpdateChecker>().CheckAsync();

        Assert.Null(status.App);
        Assert.Null(status.CheckedUtc);
        Assert.Empty(env.State.Outbox);
    }

    [Fact]
    public async Task A_newer_module_on_github_is_reported_once_and_a_custom_module_is_ignored()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        PublishCatalog(env, _sample.Pack(y => y.Replace("version: 1.0.0", "version: 1.2.0")), _sample.Pack(y => y.Replace("id: scanrelay-sample", "id: other-module").Replace("version: 1.0.0", "version: 3.0.0")));

        var status = await env.Get<UpdateChecker>().CheckAsync();
        await env.Get<UpdateChecker>().CheckAsync();

        Assert.Equal([new ModuleUpdate("scanrelay-sample", "1.0.0", "1.2.0")], status.Modules);
        var mail = Assert.Single(env.State.Outbox);
        Assert.Equal("ScanRelay: a module update is available", mail.Subject);
        Assert.Contains("scanrelay-sample: 1.0.0 → 1.2.0", mail.Body);
    }

    [Fact]
    public async Task A_release_without_a_module_list_is_not_reported_as_a_problem()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        env.State.Release = ("v9.1.0", "9.1.0", "Notes.");

        var status = await env.Get<UpdateChecker>().CheckAsync();

        Assert.Equal("9.1.0", status.App!.Version);
        Assert.Null(status.Problem);
        Assert.Empty(status.Modules);
        var error = await Assert.ThrowsAsync<ModuleException>(() => env.Get<ModuleCatalog>().FetchAsync());
        Assert.Contains("added from a file", error.Message);
    }

    [Fact]
    public async Task A_module_list_that_is_not_signed_by_the_project_is_ignored()
    {
        _sample.InstallOfficial();
        await using var env = await StartAsync();
        PublishCatalog(env, _sample.Pack(y => y.Replace("version: 1.0.0", "version: 1.2.0")));
        var index = env.State.ReleaseAssets[ModuleCatalog.IndexName];
        env.State.ReleaseAssets[ModuleCatalog.IndexName + ".sig"] = Encoding.UTF8.GetBytes(ModuleSigning.Sign(ModuleSigning.NewKeyPair().PrivatePem, index));

        var status = await env.Get<UpdateChecker>().CheckAsync();

        Assert.Empty(status.Modules);
        Assert.Contains("not signed by this project", status.Problem);
    }

    [Fact]
    public async Task The_installer_can_download_a_module_from_the_catalog_and_a_swapped_file_is_refused()
    {
        await using var env = await StartAsync();
        PublishCatalog(env, _sample.Pack());
        var catalog = env.Get<ModuleCatalog>();
        var entry = Assert.Single((await catalog.FetchAsync()).Modules);
        Assert.True(entry.Covers("ScanRelay MockPrinter 1000"));
        Assert.True(entry.WorksWithApp(ModuleHost.AppVersion));

        var (path, signature) = await catalog.DownloadPackageAsync(entry, Path.Combine(_sample.WorkFolder, "dl"));
        var installed = new ModuleStore(_sample.ModulesFolder, _sample.Keys).Install(path, signature, allowCustom: false, DateTime.UtcNow);
        Assert.Equal("official", installed.Status);

        env.State.ReleaseAssets[entry.File] = File.ReadAllBytes(_sample.Pack(y => y.Replace("vendor: ScanRelay", "vendor: Someone")).Path);
        var error = await Assert.ThrowsAsync<ModuleException>(() => catalog.DownloadPackageAsync(entry, Path.Combine(_sample.WorkFolder, "dl2")));
        Assert.Contains("does not match the module list", error.Message);
    }

    [Fact]
    public async Task The_health_check_says_exactly_what_is_missing_then_goes_green()
    {
        await using var env = await TestEnvironment.StartAsync(_sample.Register, _time, i => i.ModulesFolder = _sample.ModulesFolder);
        var health = env.Get<HealthCheck>();

        var before = await health.RunAsync();
        Assert.Equal(HealthState.Ok, before.Single(i => i.Name == "Database").State);
        Assert.Equal(HealthState.Ok, before.Single(i => i.Name == "Data folder").State);
        Assert.Equal(HealthState.Problem, before.Single(i => i.Name == "Entra connection").State);
        Assert.Equal(HealthState.Warning, before.Single(i => i.Name == "Printer modules").State);

        var app = await env.ConnectAsync(consent: false);
        var unapproved = (await health.RunAsync()).Single(i => i.Name == "Permissions");
        Assert.Equal(HealthState.Problem, unapproved.State);
        Assert.Contains("Files.ReadWrite.All", unapproved.Detail);

        app.ConsentGranted = true;
        env.Get<Cloud.AppTokenProvider>().Reset();
        var noGroups = await health.RunAsync();
        Assert.Equal(HealthState.Problem, noGroups.Single(i => i.Name == "Admin group").State);
        Assert.Equal(HealthState.Warning, noGroups.Single(i => i.Name == "Alert emails").State);

        var admins = env.State.AddGroup("Admins", env.State.AddUser("Alex Admin"));
        var staff = env.State.AddGroup("Staff", env.State.AddUser("Sam Scanner"));
        await env.Settings.UpdateAsync(s => s with { AdminGroupId = admins.Id, DefaultSyncGroupId = staff.Id, AlertSender = "alerts@example.com" }, Admin);
        var after = await health.RunAsync();
        Assert.All(after.Where(i => i.Name != "Printer modules"), i => Assert.Equal(HealthState.Ok, i.State));
        Assert.Contains("Admins, 1 member", after.Single(i => i.Name == "Admin group").Detail);
    }

    [Fact]
    public async Task An_expiring_certificate_emails_the_admins_at_thirty_days_and_again_at_seven()
    {
        await using var env = await StartAsync();
        var health = env.Get<HealthCheck>();
        var expires = env.Get<Cloud.GraphCertificate>().Get().NotAfter.ToUniversalTime();

        await health.AlertOnExpiringCertificatesAsync();
        Assert.Empty(env.State.Outbox);

        _time.SetUtcNow(expires.AddDays(-29));
        await health.AlertOnExpiringCertificatesAsync();
        await health.AlertOnExpiringCertificatesAsync();
        Assert.Equal("ScanRelay: A certificate expires soon", Assert.Single(env.State.Outbox).Subject);
        Assert.Equal(HealthState.Warning, (await health.RunAsync()).Single(i => i.Name == "Graph certificate").State);

        _time.SetUtcNow(expires.AddDays(-6));
        await health.AlertOnExpiringCertificatesAsync();
        Assert.Equal(2, env.State.Outbox.Count);
    }
}
