using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScanRelay.Core.Data;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Jobs;

/// <summary>Deletes processed files after the retention period, and job records after the history period.</summary>
public sealed class Retention(
    IDbContextFactory<ScanRelayDbContext> factory,
    DataFolder data,
    SettingsStore settings,
    JobPipeline pipeline,
    TimeProvider time,
    ILogger<Retention> logger)
{
    public sealed record Result(int FilesDeleted, int JobsDeleted, int RunsDeleted);

    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var fileCutoff = now.AddDays(-s.FileRetentionDays);
        var historyCutoff = now.AddDays(-s.JobHistoryDays);
        var files = 0;

        await using var db = await factory.CreateDbContextAsync(ct);
        var expired = await db.Jobs
            .Where(j => j.FileDeletedUtc == null && j.FinishedUtc != null && j.FinishedUtc < fileCutoff)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            try
            {
                File.Delete(pipeline.StorePath(job));
                job.FileDeletedUtc = now;
                files++;
            }
            catch (IOException e)
            {
                logger.LogWarning("Could not delete the stored file for job {Job}: {Reason}", job.Id, e.Message);
            }
        }

        await db.SaveChangesAsync(ct);

        // Files in Store that belong to no job, such as after a restored backup.
        var known = (await db.Jobs.Where(j => j.FileDeletedUtc == null).Select(j => j.Id).ToListAsync(ct)).Select(id => id.ToString("N")).ToHashSet();
        if (Directory.Exists(data.Store))
        {
            foreach (var path in Directory.EnumerateFiles(data.Store))
            {
                if (known.Contains(Path.GetFileNameWithoutExtension(path)) || File.GetLastWriteTimeUtc(path) > fileCutoff) continue;
                try
                {
                    File.Delete(path);
                    files++;
                }
                catch (IOException)
                {
                }
            }
        }

        var jobs = await db.Jobs.Where(j => j.FinishedUtc != null && j.FinishedUtc < historyCutoff).ExecuteDeleteAsync(ct);
        var runs = await db.Runs.Where(r => r.FinishedUtc != null && r.FinishedUtc < historyCutoff).ExecuteDeleteAsync(ct);
        await db.AuditLog.Where(a => a.AtUtc < now.AddDays(-Math.Max(365, s.JobHistoryDays))).ExecuteDeleteAsync(ct);
        if (files + jobs + runs > 0)
        {
            logger.LogInformation("Retention removed {Files} files, {Jobs} job records and {Runs} run records", files, jobs, runs);
        }

        return new Result(files, jobs, runs);
    }
}
