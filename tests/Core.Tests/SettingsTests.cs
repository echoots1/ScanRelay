using Microsoft.EntityFrameworkCore;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Data;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Tests;

public class SettingsTests
{
    private static readonly Actor Admin = new(Guid.NewGuid(), "Alex Admin");

    [Fact]
    public async Task Defaults_match_the_spec()
    {
        await using var env = await TestEnvironment.StartAsync();

        var s = await env.Settings.GetAsync();

        Assert.Equal(60, s.SyncIntervalMinutes);
        Assert.Equal("Documents/Scans", s.OneDriveFolder);
        Assert.Equal("Scan_{yyyy-MM-dd_HHmmss}", s.FileNameTemplate);
        Assert.Equal(["pdf"], s.AllowedExtensions());
        Assert.Equal(7, s.FileRetentionDays);
        Assert.Equal(90, s.JobHistoryDays);
        Assert.Equal(5, s.ChangeGuardLimit);
        Assert.True(s.UpdateCheck);
        Assert.Null(s.AdminGroupId);
        Assert.False(s.SetupComplete);
        Assert.Empty(s.Validate());
    }

    [Fact]
    public async Task Saving_survives_a_restart_and_audits_only_what_changed()
    {
        await using var env = await TestEnvironment.StartAsync();
        var group = Guid.NewGuid();

        await env.Settings.UpdateAsync(s => s with { SyncIntervalMinutes = 30, AdminGroupId = group, AdminGroupName = "IT" }, Admin);

        var fresh = new SettingsStore(
            env.Get<IDbContextFactory<ScanRelayDbContext>>(), env.Get<Security.SecretProtector>(), env.Get<AuditLog>(), TimeProvider.System);
        var s = await fresh.GetAsync();
        Assert.Equal(30, s.SyncIntervalMinutes);
        Assert.Equal(group, s.AdminGroupId);
        await using var db = await env.Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync();
        var entries = await db.AuditLog.OrderBy(a => a.Target).ToListAsync();
        Assert.Equal(["AdminGroupId", "AdminGroupName", "SyncIntervalMinutes"], entries.Select(e => e.Target));
        Assert.All(entries, e => Assert.Equal("Alex Admin", e.ActorName));
        Assert.Equal("60 → 30", entries[2].Detail);
    }

    [Theory]
    [InlineData(0, 7, 90, "pdf", "Documents/Scans", "Scan_{yyyy}")]
    [InlineData(60, 0, 90, "pdf", "Documents/Scans", "Scan_{yyyy}")]
    [InlineData(60, 30, 7, "pdf", "Documents/Scans", "Scan_{yyyy}")]
    [InlineData(60, 7, 90, "", "Documents/Scans", "Scan_{yyyy}")]
    [InlineData(60, 7, 90, "p*f", "Documents/Scans", "Scan_{yyyy}")]
    [InlineData(60, 7, 90, "pdf", "", "Scan_{yyyy}")]
    [InlineData(60, 7, 90, "pdf", "Documents/Sc:ans", "Scan_{yyyy}")]
    [InlineData(60, 7, 90, "pdf", "Documents/../Other", "Scan_{yyyy}")]
    [InlineData(60, 7, 90, "pdf", "Documents/Scans", "Scan_{yyyy")]
    [InlineData(60, 7, 90, "pdf", "Documents/Scans", "Scan_{HH:mm}")]
    [InlineData(60, 7, 90, "pdf", "Documents/Scans", "")]
    public async Task Bad_values_are_refused_and_nothing_is_saved(int interval, int retention, int history, string types, string folder, string template)
    {
        await using var env = await TestEnvironment.StartAsync();

        await Assert.ThrowsAsync<SettingsException>(() => env.Settings.UpdateAsync(
            s => s with
            {
                SyncIntervalMinutes = interval,
                FileRetentionDays = retention,
                JobHistoryDays = history,
                AllowedFileTypes = types,
                OneDriveFolder = folder,
                FileNameTemplate = template,
            },
            Admin));

        Assert.Equal(new AppSettings(), await env.Settings.GetAsync());
    }

    [Fact]
    public async Task Secrets_are_stored_encrypted_and_can_be_read_back_by_the_service()
    {
        await using var env = await TestEnvironment.StartAsync();

        await env.Settings.SetSecretAsync("printer:1", "hunter2", Admin);

        Assert.Equal("hunter2", await env.Settings.GetSecretAsync("printer:1"));
        await using var db = await env.Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync();
        var row = await db.Settings.SingleAsync(s => s.IsSecret);
        Assert.DoesNotContain("hunter2", row.Value);
        Assert.DoesNotContain("hunter2", string.Join(" ", (await db.AuditLog.ToListAsync()).Select(a => a.Detail + a.Target)));
    }

    [Fact]
    public async Task Export_leaves_out_secrets_and_import_restores_the_rest()
    {
        await using var source = await TestEnvironment.StartAsync();
        await source.Settings.UpdateAsync(s => s with { FileRetentionDays = 14, AlertSender = "alerts@example.com", SetupComplete = true }, Admin);
        await source.Settings.SetSecretAsync("printer:1", "hunter2", Admin);
        await source.Settings.SetStateAsync("lastUpdateCheck", "x");

        var json = await source.Settings.ExportAsync();

        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("printer:1", json);
        Assert.DoesNotContain("lastUpdateCheck", json);
        Assert.DoesNotContain("SetupComplete", json);

        await using var target = await TestEnvironment.StartAsync();
        await target.Settings.ImportAsync(json, Admin);
        var imported = await target.Settings.GetAsync();
        Assert.Equal(14, imported.FileRetentionDays);
        Assert.Equal("alerts@example.com", imported.AlertSender);
        Assert.False(imported.SetupComplete);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"Format":"ScanRelay settings","Version":1,"Settings":{"SyncIntervalMinutes":"soon"}}""")]
    [InlineData("""{"Format":"ScanRelay settings","Version":1,"Settings":{"SyncIntervalMinutes":"1"}}""")]
    public async Task A_bad_import_file_changes_nothing(string json)
    {
        await using var env = await TestEnvironment.StartAsync();

        await Assert.ThrowsAsync<SettingsException>(() => env.Settings.ImportAsync(json, Admin));

        Assert.Equal(new AppSettings(), await env.Settings.GetAsync());
    }

    [Theory]
    [InlineData("Scan_{yyyy-MM-dd_HHmmss}", "Scan_2026-11-20_143205")]
    [InlineData("{yyyyMMdd} scan", "20261120 scan")]
    [InlineData("plain", "plain")]
    public void File_names_come_from_the_template(string template, string expected)
    {
        Assert.Equal(expected, FileNaming.Render(template, new DateTime(2026, 11, 20, 14, 32, 5)));
    }

    [Fact]
    public void A_name_clash_gets_a_numbered_suffix()
    {
        Assert.Equal("Scan.pdf", FileNaming.WithSuffix("Scan", "pdf", 1));
        Assert.Equal("Scan (2).pdf", FileNaming.WithSuffix("Scan", "pdf", 2));
    }
}
