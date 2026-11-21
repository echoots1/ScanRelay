using ScanRelay.Core;
using ScanRelay.Core.Alerts;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Data;
using ScanRelay.Core.Health;
using ScanRelay.Core.Jobs;
using ScanRelay.Core.Printers;
using ScanRelay.Core.Settings;
using ScanRelay.Core.Sync;
using ScanRelay.Core.Updates;

namespace ScanRelay.Service;

/// <summary>
/// Notices new files in Inbox and uploads them. A file watcher queues work the moment a
/// file appears; a sweep at start-up and every 60 seconds catches anything the watcher missed.
/// </summary>
public sealed class IntakeWorker(JobPipeline pipeline, DataFolder data, ILogger<IntakeWorker> logger) : BackgroundService
{
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(60);
    private const int ParallelUploads = 4;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _slots = new(ParallelUploads, ParallelUploads);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var watcher = TryWatch();
        try
        {
            // Scans made while the service was stopped, and jobs a restart interrupted.
            foreach (var id in await pipeline.FindUnfinishedAsync(stoppingToken)) Dispatch(id, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var id in await pipeline.ScanInboxAsync(stoppingToken)) Dispatch(id, stoppingToken);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    logger.LogError(e, "Looking through Inbox failed");
                }

                // While a file is still settling, look again soon.
                var wait = pipeline.WaitingInInbox > 0 ? TimeSpan.FromSeconds(1) : SweepEvery;
                await _signal.WaitAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Dispatch(Guid jobId, CancellationToken ct) => _ = Task.Run(
        async () =>
        {
            await _slots.WaitAsync(ct);
            try
            {
                await pipeline.ProcessAsync(jobId, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                logger.LogError(e, "Job {Job} stopped unexpectedly", jobId);
            }
            finally
            {
                _slots.Release();
            }
        },
        ct);

    private FileSystemWatcher? TryWatch()
    {
        try
        {
            var watcher = new FileSystemWatcher(data.Inbox) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            void Wake(object sender, EventArgs args)
            {
                try
                {
                    _signal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }

            watcher.Created += Wake;
            watcher.Changed += Wake;
            watcher.Renamed += Wake;
            watcher.Error += Wake;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            logger.LogWarning("The file watcher could not start, so new scans are found by the 60-second sweep only: {Reason}", e.Message);
            return null;
        }
    }
}

/// <summary>Runs everything that happens on a timetable: sync, retention, grouped alerts, update and certificate checks, drift check.</summary>
public sealed class ScheduleWorker(
    SettingsStore settings,
    DirectorySync sync,
    Retention retention,
    AlertService alerts,
    UpdateChecker updates,
    HealthCheck health,
    PrinterService printers,
    TimeProvider time,
    ILogger<ScheduleWorker> logger) : BackgroundService
{
    private DateTimeOffset _lastSync = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReachability = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDaily = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = time.GetUtcNow();
        _lastReachability = started;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
                var now = time.GetUtcNow();
                var s = await settings.GetAsync(stoppingToken);

                await TryAsync("Sending grouped alerts", () => alerts.FlushAsync(stoppingToken));

                if (now - _lastRetention >= TimeSpan.FromHours(6))
                {
                    _lastRetention = now;
                    await TryAsync("Retention", () => retention.RunAsync(stoppingToken));
                }

                // Nothing below can work until the Entra connection and the first sign-in are done.
                if (!s.SetupComplete) continue;

                var firstSync = _lastSync == DateTimeOffset.MinValue && now - started >= TimeSpan.FromMinutes(1);
                if (firstSync || (_lastSync != DateTimeOffset.MinValue && now - _lastSync >= TimeSpan.FromMinutes(s.SyncIntervalMinutes)))
                {
                    _lastSync = now;
                    await TryAsync("Sync", () => sync.RunAsync(RunKind.Sync, RunTrigger.Schedule, Actor.Service, stoppingToken));
                }

                if (now - _lastReachability >= TimeSpan.FromHours(1))
                {
                    _lastReachability = now;
                    await TryAsync("Printer check", () => printers.CheckReachabilityAsync(stoppingToken));
                }

                if (now - _lastDaily >= TimeSpan.FromHours(24) && now - started >= TimeSpan.FromMinutes(5))
                {
                    _lastDaily = now;
                    await TryAsync("Update check", () => updates.CheckAsync(stoppingToken));
                    await TryAsync("Certificate check", () => health.AlertOnExpiringCertificatesAsync(stoppingToken));
                }

                // Once a night, between 2 and 3 in the morning server time.
                var local = time.GetLocalNow();
                var today = local.ToString("yyyy-MM-dd");
                if (local.Hour == 2 && await settings.GetStateAsync("lastDriftCheck", stoppingToken) != today)
                {
                    await settings.SetStateAsync("lastDriftCheck", today, stoppingToken);
                    await TryAsync("Drift check", () => sync.DriftCheckAsync(stoppingToken));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TryAsync(string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // One failed task must not stop the timetable.
            logger.LogError(e, "{What} failed", what);
        }
    }
}
