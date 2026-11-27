using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScanRelay.Core.Alerts;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Data;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Jobs;

/// <summary>
/// Turns files that printers write into jobs and uploads them.
/// A file is claimed by moving it from Inbox to Store under its job id, so a
/// crash or restart can never process the same file twice.
/// </summary>
public sealed class JobPipeline(
    IDbContextFactory<ScanRelayDbContext> factory,
    DataFolder data,
    SettingsStore settings,
    ICloud cloud,
    AlertService alerts,
    AuditLog audit,
    TimeProvider time,
    ILogger<JobPipeline> logger)
{
    /// <summary>A file counts as finished when its size has not changed for this long and it opens exclusively.</summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(3);

    /// <summary>A file that never finishes fails after this long. One that stays empty is cleared away after it.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

    /// <summary>Waits before the three retries of a Graph or network error.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>What a scan made with a printer's test entry is recorded as. It is not sent anywhere.</summary>
    public const string TestScanMessage = "Test scan received. The printer can reach the share. The file was not uploaded.";

    private readonly ConcurrentDictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, byte> _inProgress = new();

    /// <summary>Raised whenever a job changes, so open pages can refresh.</summary>
    public event Action? Changed;

    /// <summary>How many files are in Inbox but not yet finished. The caller looks again soon while this is above zero.</summary>
    public int WaitingInInbox => _tracked.Count;

    /// <summary>Looks through Inbox once and claims every finished file. Returns the new job ids, ready to process.</summary>
    public async Task<IReadOnlyList<Guid>> ScanInboxAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var claimed = new List<Guid>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(data.Inbox)) return claimed;

        foreach (var path in Directory.EnumerateFiles(data.Inbox, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                continue;
            }

            seen.Add(path);
            var tracked = _tracked.AddOrUpdate(
                path,
                _ => new Tracked(now, size, now),
                (_, old) => old.Size == size ? old : old with { Size = size, LastChange = now });

            var quiet = now - tracked.LastChange >= QuietTime;
            if (size > 0 && quiet && OpensExclusively(path))
            {
                if (await ClaimAsync(path, tracked, null, ct) is { } id) claimed.Add(id);
            }
            else if (now - tracked.FirstSeen >= GiveUpAfter)
            {
                if (size == 0) RemoveEmpty(path);
                else await ClaimAsync(path, tracked, "The printer never finished writing the file.", ct);
            }
        }

        // Files that went away on their own, such as a printer's temporary file.
        foreach (var path in _tracked.Keys.Where(p => !seen.Contains(p))) _tracked.TryRemove(path, out _);
        return claimed;
    }

    /// <summary>
    /// An empty file is not a scan. Some scanners write one to see that they can reach the folder and
    /// then cannot take it away again, because the printer account may only create and write. So an
    /// empty file that has stayed empty is cleared away quietly: no failed job, no alert.
    /// </summary>
    private void RemoveEmpty(string path)
    {
        try
        {
            // Looked at again at the last moment, in case it started to fill.
            if (new FileInfo(path).Length != 0) return;
            File.Delete(path);
            _tracked.TryRemove(path, out _);
            logger.LogInformation("Removed {File} from Inbox: it stayed empty for {Minutes} minutes, so it was a scanner's test file and not a scan", Path.GetRelativePath(data.Inbox, path), GiveUpAfter.TotalMinutes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still held by something. It is looked at again at the next pass.
            logger.LogDebug("Could not remove the empty file {File}: {Reason}", Path.GetRelativePath(data.Inbox, path), e.Message);
        }
    }

    /// <summary>Jobs left unfinished by a restart, ready to process.</summary>
    public async Task<IReadOnlyList<Guid>> FindUnfinishedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Jobs.Where(j => j.Status == JobStatus.Waiting || j.Status == JobStatus.Uploading)
            .OrderBy(j => j.DetectedUtc).Select(j => j.Id).ToListAsync(ct);
    }

    /// <summary>Scans and processes everything that is ready, one after another. Returns how many jobs it handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var ids = await ScanInboxAsync(ct);
        foreach (var id in ids) await ProcessAsync(id, ct);
        return ids.Count;
    }

    /// <summary>Uploads one job's file, retrying Graph and network errors, and records how it ended.</summary>
    public async Task ProcessAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!_inProgress.TryAdd(jobId, 0)) return;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var job = await db.Jobs.FindAsync([jobId], ct);
            if (job is null || job.Status is JobStatus.Uploaded or JobStatus.Failed) return;

            var s = await settings.GetAsync(ct);
            var extension = Path.GetExtension(job.OriginalFileName).TrimStart('.').ToLowerInvariant();
            var file = StorePath(job);

            string? refusal =
                !File.Exists(file) ? "The file is no longer on the server."
                : !s.AllowedExtensions().Contains(extension) ? $"File type not allowed: .{extension}"
                : job.FolderName.Equals(Printers.PrinterService.TestEntryId.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? TestScanMessage
                : job.PersonId is null ? "The folder matches no known person."
                : null;
            if (refusal is not null)
            {
                await FinishAsync(db, job, null, refusal, ct);
                return;
            }

            OneDrivePath.TryParse(s.OneDriveFolder, out var folder, out _);
            job.UploadedFileName ??= FileNaming.Render(s.FileNameTemplate, TimeZoneInfo.ConvertTimeFromUtc(job.DetectedUtc, time.LocalTimeZone));
            job.Status = JobStatus.Uploading;
            await db.SaveChangesAsync(ct);
            var target = new OneDriveTarget(folder!.UnderDocuments, folder.Segments, job.UploadedFileName, extension);

            for (var attempt = 0; ; attempt++)
            {
                job.Attempts++;
                try
                {
                    var item = await cloud.UploadAsync(job.PersonId!.Value, target, file, ct);
                    await FinishAsync(db, job, item, null, ct);
                    return;
                }
                catch (CloudException e) when (e.IsTransient && attempt < RetryDelays.Length)
                {
                    logger.LogWarning("Upload of job {Job} failed and will be retried: {Reason}", job.Id, e.Message);
                    job.Error = e.Message;
                    await db.SaveChangesAsync(ct);
                    Changed?.Invoke();
                    await Task.Delay(RetryDelays[attempt], time, ct);
                }
                catch (CloudException e)
                {
                    var reason = e.Status == 404 ? "The owner has no OneDrive, or it could not be found. " + e.Message : e.Message;
                    await FinishAsync(db, job, null, reason, ct);
                    return;
                }
            }
        }
        finally
        {
            _inProgress.TryRemove(jobId, out _);
        }
    }

    /// <summary>Tries a failed job again. Available while its file is still in Store.</summary>
    public async Task<string?> RerunAsync(Guid jobId, Actor actor, CancellationToken ct = default)
    {
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var job = await db.Jobs.FindAsync([jobId], ct);
            if (job is null) return "That job no longer exists.";
            if (job.Status != JobStatus.Failed) return "Only a failed job can be re-run.";
            if (job.FileDeletedUtc is not null || !File.Exists(StorePath(job))) return "The file is no longer on the server, so the job cannot be re-run.";

            // The person may have been added since the scan was made.
            if (job.PersonId is null && Guid.TryParse(job.FolderName, out var folderId) && await db.People.AnyAsync(p => p.Id == folderId, ct))
            {
                job.PersonId = folderId;
            }

            // An earlier attempt may have reached OneDrive even though we never heard back.
            if (job.DriveItemId?.Split('|') is [var driveId, var itemId] && await SafeExistsAsync(driveId, itemId, ct))
            {
                await FinishAsync(db, job, new UploadedItem(driveId, itemId, job.UploadedFileName ?? ""), null, ct);
                await audit.WriteAsync(actor, "Job re-run", job.Id.ToString(), "Already in OneDrive", ct);
                return null;
            }

            job.Status = JobStatus.Waiting;
            job.Error = null;
            job.FinishedUtc = null;
            job.UploadedFileName = null;
            await db.SaveChangesAsync(ct);
        }

        await audit.WriteAsync(actor, "Job re-run", jobId.ToString(), ct: ct);
        await ProcessAsync(jobId, ct);
        return null;
    }

    public string StorePath(Job job) => Path.Combine(data.Store, job.Id.ToString("N") + Path.GetExtension(job.OriginalFileName).ToLowerInvariant());

    private async Task<bool> SafeExistsAsync(string driveId, string itemId, CancellationToken ct)
    {
        try
        {
            return await cloud.ItemExistsAsync(driveId, itemId, ct);
        }
        catch (CloudException)
        {
            return false;
        }
    }

    private async Task<Guid?> ClaimAsync(string path, Tracked tracked, string? failure, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(data.Inbox, path);
        var folderName = Path.GetDirectoryName(relative) ?? "";
        var job = new Job
        {
            Id = Guid.NewGuid(),
            FolderName = folderName,
            OriginalFileName = Path.GetFileName(path),
            SizeBytes = tracked.Size,
            Status = JobStatus.Waiting,
            DetectedUtc = tracked.FirstSeen.UtcDateTime,
        };

        try
        {
            File.Move(path, StorePath(job));
        }
        catch (IOException e)
        {
            // Still locked, or already gone. Leave it for the next look.
            logger.LogDebug("Could not claim {File}: {Reason}", relative, e.Message);
            if (!File.Exists(path)) _tracked.TryRemove(path, out _);
            return null;
        }

        _tracked.TryRemove(path, out _);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (Guid.TryParse(folderName, out var personId) && await db.People.AnyAsync(p => p.Id == personId, ct))
        {
            job.PersonId = personId;
        }

        db.Jobs.Add(job);
        if (failure is not null)
        {
            await FinishAsync(db, job, null, failure, ct);
            return null;
        }

        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return job.Id;
    }

    private async Task FinishAsync(ScanRelayDbContext db, Job job, UploadedItem? item, string? error, CancellationToken ct)
    {
        job.FinishedUtc = time.GetUtcNow().UtcDateTime;
        if (item is not null)
        {
            job.Status = JobStatus.Uploaded;
            job.Error = null;
            job.DriveItemId = item.DriveId + "|" + item.ItemId;
            if (item.Name.Length > 0) job.UploadedFileName = item.Name;
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.Error = error;
        }

        await db.SaveChangesAsync(ct);
        Changed?.Invoke();

        // Logs record who, when, size and result. Never file contents.
        if (item is not null)
        {
            logger.LogInformation("Job {Job} uploaded for {Person}, {Size} bytes", job.Id, job.PersonId, job.SizeBytes);
        }
        else
        {
            logger.LogWarning("Job {Job} failed for folder {Folder}, {Size} bytes: {Reason}", job.Id, job.FolderName, job.SizeBytes, error);
            if (error == TestScanMessage) return;
            var who = job.PersonId is { } id ? (await db.People.FindAsync([id], ct))?.DisplayName ?? id.ToString() : $"folder \"{job.FolderName}\"";
            await alerts.RaiseAsync(AlertKind.ScanFailed, $"Scan for {who} at {job.DetectedUtc:u}: {error}", ct);
        }
    }

    private static bool OpensExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Tracked(DateTimeOffset FirstSeen, long Size, DateTimeOffset LastChange);
}
