using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace AnimeOverseer.Server.BackgroundServices;

public class SyncJobRunnerService(
    IServiceScopeFactory scopeFactory,
    SyncJobTrigger syncJobTrigger,
    ILogger<SyncJobRunnerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStaleJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Jobs always run serially. Drain the queue before waiting so a
                // manual trigger starts all currently queued jobs without delay.
                while (await ProcessNextJobAsync(stoppingToken)) { }

                // Keep the timed wake-up as a safeguard for jobs inserted outside
                // SyncJobService, while allowing queue requests to wake us at once.
                await syncJobTrigger.WaitAsync(TimeSpan.FromMinutes(3), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Sync job runner error"); }
        }
    }

    // Mark any jobs left in Running state from a previous run as Failed.
    private async Task ResetStaleJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnimeDbContext>();

        var stale = await db.SyncJobs
            .Where(j => j.Status == "Running")
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            job.Status = job.CancellationRequested ? "Cancelled" : "Failed";
            job.Message = job.CancellationRequested ? "Cancelled" : "Interrupted by server restart";
            job.FinishedAt = DateTime.UtcNow;
        }

        if (stale.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private async Task<bool> ProcessNextJobAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnimeDbContext>();

        var job = await db.SyncJobs
            .Where(j => j.Status == "Queued")
            .OrderBy(j => j.QueuedAt)
            .FirstOrDefaultAsync(ct);

        if (job == null) return false;

        job.Status = "Running";
        job.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Starting sync job {Id} ({Type})", job.Id, job.JobType);

        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var monitorCts = new CancellationTokenSource();
        var jobCancellationRequested = false;
        var cancellationMonitor = MonitorCancellationAsync(
            job.Id,
            jobCts,
            monitorCts.Token,
            () => jobCancellationRequested = true);

        try
        {
            await (job.JobType switch
            {
                "Jikan"   => sync.RunJikanSyncAsync(job, jobCts.Token), // legacy queued jobs
                "AniList" => sync.RunJikanSyncAsync(job, jobCts.Token),
                "MyAnimeList" => sync.RunMyAnimeListSyncAsync(job, jobCts.Token),
                "AnimeSchedule" => sync.RunAnimeScheduleSyncAsync(job, jobCts.Token),
                "TopUpcoming" => sync.RunTopUpcomingSyncAsync(job, jobCts.Token),
                "Recommendations" => sync.RunRecommendationsSyncAsync(job, jobCts.Token),
                "Relations" => sync.RunRelationsSyncAsync(job, jobCts.Token),
                "ProviderIds" => sync.RunProviderIdsSyncAsync(job, jobCts.Token),
                "Kitsu"   => sync.RunKitsuSyncAsync(job, jobCts.Token),
                _         => Task.CompletedTask
            });

            job.Status = jobCancellationRequested ? "Cancelled" : ct.IsCancellationRequested ? "Failed" : "Completed";
            // Catalogue pagination does not provide a total before the fetch starts.
            // Once a job completes, the number processed is its actual total.
            if (job.Status == "Completed" && job.TotalCount == 0)
                job.TotalCount = job.ProcessedCount;
            job.Message = jobCancellationRequested
                ? "Cancelled"
                : ct.IsCancellationRequested
                ? "Cancelled"
                : $"Processed {job.ProcessedCount} of {job.TotalCount}";
        }
        catch (OperationCanceledException) when (jobCancellationRequested)
        {
            job.Status = "Cancelled";
            job.Message = "Cancelled";
        }
        catch (Exception ex)
        {
            var error = FormatError(ex);
            job.Status = "Failed";
            job.Message = error;
            db.SyncJobLogs.Add(new SyncJobLog
            {
                SyncJobId = job.Id,
                Timestamp = DateTime.UtcNow,
                Level = "Error",
                Message = $"Sync failed — {error}"
            });
            logger.LogError("Sync job {Id} ({Type}) failed. ErrorType: {ErrorType}; Details: {Details}",
                job.Id, job.JobType, ex.GetType().Name, Redact(ex.ToString()));
        }
        finally
        {
            await monitorCts.CancelAsync();
            try { await cancellationMonitor; }
            catch (OperationCanceledException) { }
            job.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None); // always persist final status
        }

        logger.LogInformation("Sync job {Id} ({Type}) finished with status {Status}", job.Id, job.JobType, job.Status);
        if (job.Status == "Completed") await RunEnglishNamePrecheckAsync(scope.ServiceProvider, job, db, ct);
        // Ranged jobs intentionally advance only after one successful season. This keeps
        // the queue serial and preserves each API client's throttling.
        await scope.ServiceProvider.GetRequiredService<SyncJobService>().QueueNextPartAsync(job);
        return true;
    }

    /// <summary>Settles English names the sources agree on, so they skip name review.</summary>
    private async Task RunEnglishNamePrecheckAsync(IServiceProvider services, SyncJob job, AnimeDbContext db, CancellationToken ct)
    {
        try
        {
            var settings = await EnglishNameSettings.LoadAsync(db, ct);
            if (!settings.Enabled) return;
            var result = await services.GetRequiredService<AnimeReviewService>().ApplyEnglishNamePrecheckAsync(settings, ct);
            if (result.Total + result.ReturnedToReview == 0) return;
            db.SyncJobLogs.Add(new SyncJobLog
            {
                SyncJobId = job.Id,
                Timestamp = DateTime.UtcNow,
                Level = "Info",
                Message = $"English names from sources: {result.Renamed:N0} renamed, {result.Confirmed:N0} confirmed, {result.ReturnedToReview:N0} back to name review"
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "English-name pre-check after sync job {Id} failed", job.Id);
        }
    }

    private static string FormatError(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message, StringComparer.Ordinal))
                messages.Add(current.Message);
        }
        return messages.Count == 0 ? exception.GetType().Name : Redact(string.Join(" → ", messages));
    }

    private static string Redact(string value)
        => Regex.Replace(value,
            @"(?i)((?:access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|authorization|bearer)\s*(?:=|:|\s)\s*)[^\s,;&]+",
            "$1[REDACTED]");

    private async Task MonitorCancellationAsync(int jobId, CancellationTokenSource jobCts, CancellationToken ct, Action onCancellationRequested)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnimeDbContext>();

            while (!ct.IsCancellationRequested)
            {
                var cancellationRequested = await db.SyncJobs
                    .AsNoTracking()
                    .Where(j => j.Id == jobId)
                    .Select(j => j.CancellationRequested)
                    .SingleOrDefaultAsync(ct);

                if (cancellationRequested)
                {
                    onCancellationRequested();
                    await jobCts.CancelAsync();
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
