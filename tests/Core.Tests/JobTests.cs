using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Data;
using ScanRelay.Core.Jobs;
using ScanRelay.MockCloud;

namespace ScanRelay.Core.Tests;

public class JobTests
{
    private static readonly Actor Admin = new(Guid.NewGuid(), "Alex Admin");

    private sealed class Rig : IAsyncDisposable
    {
        public required TestEnvironment Env { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public JobPipeline Pipeline => Env.Get<JobPipeline>();

        public static async Task<Rig> StartAsync()
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            time.SetLocalTimeZone(TimeZoneInfo.Utc);
            var env = await TestEnvironment.StartAsync(time: time);
            await env.ConnectAsync();
            return new Rig { Env = env, Time = time };
        }

        public async Task<MockUser> AddPersonAsync(string name, bool hasDrive = true)
        {
            var user = Env.State.AddUser(name, hasDrive: hasDrive);
            await using var db = await Env.Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync();
            db.People.Add(new Person
            {
                Id = user.Id,
                DisplayName = user.DisplayName,
                UserPrincipalName = user.Upn,
                FirstSeenUtc = Time.GetUtcNow().UtcDateTime,
                LastSeenUtc = Time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
            Directory.CreateDirectory(Path.Combine(Env.Data.Inbox, user.Id.ToString()));
            return user;
        }

        public string Drop(string folder, string name, string content = "%PDF-test")
        {
            Directory.CreateDirectory(Path.Combine(Env.Data.Inbox, folder));
            var path = Path.Combine(Env.Data.Inbox, folder, name);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>Lets the file settle, then scans and processes.</summary>
        public async Task<int> SettleAndRunAsync()
        {
            await Pipeline.ScanInboxAsync();
            Time.Advance(JobPipeline.QuietTime);
            return await Pipeline.RunOnceAsync();
        }

        /// <summary>Runs a task that waits on the fake clock, moving the clock on until it finishes.</summary>
        public async Task DriveAsync(Task task)
        {
            for (var i = 0; i < 2000 && !task.IsCompleted; i++)
            {
                Time.Advance(TimeSpan.FromSeconds(5));
                await Task.Delay(5);
            }

            await task;
        }

        public async Task<List<Job>> JobsAsync()
        {
            await using var db = await Env.Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync();
            return await db.Jobs.OrderBy(j => j.DetectedUtc).ToListAsync();
        }

        public ValueTask DisposeAsync() => Env.DisposeAsync();
    }

    [Fact]
    public async Task A_finished_scan_reaches_the_owners_onedrive_and_moves_to_store()
    {
        await using var rig = await Rig.StartAsync();
        var sam = await rig.AddPersonAsync("Sam Scanner");
        var path = rig.Drop(sam.Id.ToString(), "SCN_0001.pdf", "%PDF-hello");
        var detected = rig.Time.GetUtcNow().UtcDateTime;

        Assert.Equal(1, await rig.SettleAndRunAsync());

        var file = Assert.Single(rig.Env.State.Files(sam, "Dokumente/Scans"));
        Assert.Equal($"Scan_{detected:yyyy-MM-dd_HHmmss}.pdf", file.Name);
        Assert.Equal("%PDF-hello", System.Text.Encoding.UTF8.GetString(file.Content));
        var job = Assert.Single(await rig.JobsAsync());
        Assert.Equal(JobStatus.Uploaded, job.Status);
        Assert.Equal(sam.Id, job.PersonId);
        Assert.Equal(10, job.SizeBytes);
        Assert.Equal(file.Name, job.UploadedFileName);
        Assert.Equal(1, job.Attempts);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(rig.Pipeline.StorePath(job)));
        Assert.Empty(rig.Env.State.Outbox);
    }

    [Fact]
    public async Task A_file_still_being_written_is_left_alone()
    {
        await using var rig = await Rig.StartAsync();
        var sam = await rig.AddPersonAsync("Sam Scanner");
        var path = rig.Drop(sam.Id.ToString(), "scan.pdf", "part");

        await rig.Pipeline.RunOnceAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        File.AppendAllText(path, "-more");
        await rig.Pipeline.RunOnceAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, await rig.Pipeline.RunOnceAsync());
        Assert.Equal(1, rig.Pipeline.WaitingInInbox);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await rig.Pipeline.RunOnceAsync());
        Assert.Equal("part-more", System.Text.Encoding.UTF8.GetString(Assert.Single(rig.Env.State.Files(sam, "Dokumente/Scans")).Content));
    }

    [Fact]
    public async Task A_file_the_printer_still_holds_open_is_left_alone()
    {
        await using var rig = await Rig.StartAsync();
        var sam = await rig.AddPersonAsync("Sam Scanner");
        var path = rig.Drop(sam.Id.ToString(), "scan.pdf");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, await rig.SettleAndRunAsync());
        }

        Assert.Equal(1, await rig.Pipeline.RunOnceAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("half")]
    public async Task A_file_that_never_finishes_or_stays_empty_fails_after_ten_minutes(string content)
    {
        await using var rig = await Rig.StartAsync();
        var sam = await rig.AddPersonAsync("Sam Scanner");
        var path = rig.Drop(sam.Id.ToString(), "scan.pdf", content);
        using var held = content.Length > 0 ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
        if (held is not null && !OperatingSystem.IsWindows())
        {
            // Other systems do not enforce the lock, so keep the size changing instead.
            held.Dispose();
        }

        await rig.Pipeline.RunOnceAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(9));
        if (content.Length > 0) File.AppendAllText(path, "x");
        await rig.Pipeline.RunOnceAsync();
        Assert.Empty(await rig.JobsAsync());

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        if (content.Length > 0) File.AppendAllText(path, "x");
        await rig.Pipeline.RunOnceAsync();

        var job = Assert.Single(await rig.JobsAsync());
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains(content.Length == 0 ? "empty" : "never finished", job.Error);
        Assert.False(File.Exists(path));
        Assert.Empty(rig.Env.State.Files(sam, "Dokumente/Scans"));
    }

    [Fact]
    public async Task A_file_type_that_is_not_allowed_fails()
    {
        await using var rig = await Rig.StartAsync();
        var sam = await rig.AddPersonAsync("Sam Scanner");
        rig.Drop(sam.Id.ToString(), "scan.tif");

        await rig.SettleAndRunAsync();

        var job = Assert.Single(await rig.JobsAsync());
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("File type not allowed", job.Error);
        Assert.True(File.Exists(rig.Pipeline.StorePath(job)));
    }

    [Theory]
    [InlineData("not-a-user-id")]
    [InlineData("3f2b8a5e-0000-4000-8000-000000000000")]
    public async Task A_folder_that_matches_no_known_person_fails(string folder)
    {
        await using var rig = await Rig.StartAsync();
        rig.Drop(folder, "scan.pdf");

        await rig.SettleAndRunAsync();

        var job = Assert.Single(await rig.JobsAsync());
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("matches no known person", job.Error);
        Assert.Equal(folder, job.FolderName);
    }

    [Fact]
    public async Task An_owner_without_onedrive_fails_at_once()
    {
        await using var rig = await Rig.StartAsync();
        var nora = await rig.AddPersonAsync("Nora NoDrive", hasDrive: false);
        rig.Drop(nora.Id.ToString(), "scan.pdf");

        await rig.SettleAndRunAsync();

        var job = Assert.Single(await rig.JobsAsync());
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("no OneDrive", job.Error);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task A_graph_error_is_retried_three_times_with_a_growing_delay()
    {
        await using var rig = await Rig.StartAsync();
        var sam = await rig.AddPersonAsync("Sam Scanner");
        rig.Drop(sam.Id.ToString(), "scan.pdf");
        await rig.Pipeline.ScanInboxAsync();
        rig.Time.Advance(JobPipeline.QuietTime);
        var id = Assert.Single(await rig.Pipeline.ScanInboxAsync());
        rig.Env.State.FailStatus = 500;
        rig.Env.State.FailNextGraphRequests = 3;

        await rig.DriveAsync(rig.Pipeline.ProcessAsync(id));

        var job = Assert.Single(await rig.JobsAsync());
        Assert.Equal(JobStatus.Uploaded, job.Status);
        Assert.Equal(4, job.Attempts);
        Assert.Null(job.Error);
        Assert.Single(rig.Env.State.Files(sam, "Dokumente/Scans"));
        Assert.True(JobPipeline.RetryDelays.Zip(JobPipeline.RetryDelays.Skip(1)).All(p => p.First < p.Second));
    }

    [Fact]
    public async Task After_the_retries_the_job_fails_and_can_be_rerun_from_the_jobs_page()
    {
        await using var rig = await Rig.StartAsync();
        var admin = rig.Env.State.AddUser("Alex Admin");
        var group = rig.Env.State.AddGroup("Admins", admin);
        rig.Env.State.AddUser("Alerts", "alerts@example.com");
        await rig.Env.Settings.UpdateAsync(s => s with { AdminGroupId = group.Id, AlertSender = "alerts@example.com" }, Admin);
        var sam = await rig.AddPersonAsync("Sam Scanner");
        rig.Drop(sam.Id.ToString(), "scan.pdf");
        await rig.Pipeline.ScanInboxAsync();
        rig.Time.Advance(JobPipeline.QuietTime);
        var id = Assert.Single(await rig.Pipeline.ScanInboxAsync());
        rig.Env.State.FailStatus = 500;
        rig.Env.State.FailNextGraphRequests = 4;

        await rig.DriveAsync(rig.Pipeline.ProcessAsync(id));

        var failed = Assert.Single(await rig.JobsAsync());
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(4, failed.Attempts);
        Assert.Empty(rig.Env.State.Files(sam, "Dokumente/Scans"));
        var mail = Assert.Single(rig.Env.State.Outbox);
        Assert.Equal("ScanRelay: A scan failed", mail.Subject);
        Assert.Equal([admin.Upn], mail.To);
        Assert.Contains("Sam Scanner", mail.Body);

        Assert.Null(await rig.Pipeline.RerunAsync(id, Admin));

        var done = Assert.Single(await rig.JobsAsync());
        Assert.Equal(JobStatus.Uploaded, done.Status);
        Assert.Single(rig.Env.State.Files(sam, "Dokumente/Scans"));
        Assert.NotNull(await rig.Pipeline.RerunAsync(id, Admin));
    }

    [Fact]
    public async Task Rerunning_a_job_for_someone_added_since_then_delivers_it()
    {
        await using var rig = await Rig.StartAsync();
        var later = rig.Env.State.AddUser("Late Joiner");
        rig.Drop(later.Id.ToString(), "scan.pdf");
        await rig.SettleAndRunAsync();
        var id = Assert.Single(await rig.JobsAsync()).Id;
        await using (var db = await rig.Env.Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync())
        {
            db.People.Add(new Person { Id = later.Id, DisplayName = "Late Joiner", UserPrincipalName = later.Upn });
            await db.SaveChangesAsync();
        }

        Assert.Null(await rig.Pipeline.RerunAsync(id, Admin));

        Assert.Single(rig.Env.State.Files(later, "Dokumente/Scans"));
    }

    [Fact]
    public async Task Many_failures_in_a_row_send_one_email_then_one_grouped_email()
    {
        await using var rig = await Rig.StartAsync();
        var admin = rig.Env.State.AddUser("Alex Admin");
        var group = rig.Env.State.AddGroup("Admins", admin);
        rig.Env.State.AddUser("Alerts", "alerts@example.com");
        await rig.Env.Settings.UpdateAsync(s => s with { AdminGroupId = group.Id, AlertSender = "alerts@example.com" }, Admin);
        for (var i = 0; i < 6; i++) rig.Drop("unknown", $"scan{i}.pdf");

        await rig.SettleAndRunAsync();
        await rig.Env.Get<Alerts.AlertService>().FlushAsync();
        Assert.Single(rig.Env.State.Outbox);

        rig.Time.Advance(Alerts.AlertService.GroupWindow);
        await rig.Env.Get<Alerts.AlertService>().FlushAsync();

        Assert.Equal(2, rig.Env.State.Outbox.Count);
        Assert.Equal("ScanRelay: 5 scans failed", rig.Env.State.Outbox.Last().Subject);
    }

    [Fact]
    public async Task Scans_made_while_the_service_was_stopped_are_picked_up_on_start()
    {
        await using var rig = await Rig.StartAsync();
        var sam = await rig.AddPersonAsync("Sam Scanner");
        rig.Drop(sam.Id.ToString(), "one.pdf");
        rig.Drop(sam.Id.ToString(), "two.pdf");
        await rig.Pipeline.ScanInboxAsync();
        rig.Time.Advance(JobPipeline.QuietTime);
        var claimed = await rig.Pipeline.ScanInboxAsync();
        Assert.Equal(2, claimed.Count);

        // The service stops here, before uploading. A new pipeline finds the claimed jobs.
        var unfinished = await rig.Pipeline.FindUnfinishedAsync();
        Assert.Equal(claimed.Order(), unfinished.Order());
        foreach (var id in unfinished) await rig.Pipeline.ProcessAsync(id);

        Assert.Equal(2, rig.Env.State.Files(sam, "Dokumente/Scans").Count);
        Assert.Equal(0, await rig.Pipeline.RunOnceAsync());
    }

    [Fact]
    public async Task Retention_deletes_files_after_seven_days_and_records_after_ninety()
    {
        await using var rig = await Rig.StartAsync();
        var sam = await rig.AddPersonAsync("Sam Scanner");
        rig.Drop(sam.Id.ToString(), "good.pdf");
        rig.Drop("unknown", "bad.pdf");
        await rig.SettleAndRunAsync();
        var jobs = await rig.JobsAsync();
        var failed = jobs.Single(j => j.Status == JobStatus.Failed);
        var retention = rig.Env.Get<Retention>();

        rig.Time.Advance(TimeSpan.FromDays(6));
        Assert.Equal(new Retention.Result(0, 0, 0), await retention.RunAsync());
        Assert.All(jobs, j => Assert.True(File.Exists(rig.Pipeline.StorePath(j))));

        rig.Time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(2, (await retention.RunAsync()).FilesDeleted);
        Assert.All(jobs, j => Assert.False(File.Exists(rig.Pipeline.StorePath(j))));
        Assert.Equal(2, (await rig.JobsAsync()).Count(j => j.FileDeletedUtc is not null));
        Assert.Contains("no longer on the server", await rig.Pipeline.RerunAsync(failed.Id, Admin));

        rig.Time.Advance(TimeSpan.FromDays(83));
        Assert.Equal(2, (await retention.RunAsync()).JobsDeleted);
        Assert.Empty(await rig.JobsAsync());
    }
}
