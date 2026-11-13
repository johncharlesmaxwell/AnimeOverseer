using System.Text.Json;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.BackgroundServices;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

public class SyncJobService(AnimeDbContext db, SyncJobTrigger syncJobTrigger)
{
    public async Task<SyncJob?> QueueAsync(string jobType, string? parameters = null)
    {
        var alreadyActive = await db.SyncJobs.AnyAsync(j =>
            j.JobType == jobType && (j.Status == "Queued" || j.Status == "Running"));
        if (alreadyActive) return null;

        var job = new SyncJob { JobType = jobType, QueuedAt = DateTime.UtcNow, Parameters = parameters };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        syncJobTrigger.Signal();
        return job;
    }

    public async Task<SyncJob?> ContinueAsync(int failedJobId)
    {
        var failed = await db.SyncJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == failedJobId);
        if (failed == null || failed.Status != "Failed" || failed.ProcessedCount <= 0) return null;

        var alreadyActive = await db.SyncJobs.AnyAsync(j =>
            j.JobType == failed.JobType && (j.Status == "Queued" || j.Status == "Running"));
        if (alreadyActive) return null;

        // Catalogue imports are idempotent when re-run with the same season parameters.
        // Legacy enrichment jobs query the local DB ordered by Id, so they can skip records.
        var parameters = failed.JobType is "Jikan" or "AniList" or "MyAnimeList" or "AnimeSchedule"
            ? failed.Parameters
            : JsonSerializer.Serialize(new { skipCount = failed.ProcessedCount });

        var job = new SyncJob { JobType = failed.JobType, QueuedAt = DateTime.UtcNow, Parameters = parameters };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        syncJobTrigger.Signal();
        return job;
    }

    /// <summary>Queues the next unit of a job configured to process its range serially.</summary>
    public async Task QueueNextPartAsync(SyncJob completedJob)
    {
        if (completedJob.Status != "Completed" || string.IsNullOrWhiteSpace(completedJob.Parameters)) return;
        try
        {
            using var document = JsonDocument.Parse(completedJob.Parameters);
            var legacyInitialImport = document.RootElement.TryGetProperty("initialImport", out var initial) && initial.GetBoolean();
            var processOneAtATime = document.RootElement.TryGetProperty("processOneSeasonAtATime", out var option) && option.GetBoolean();
            if (!legacyInitialImport && !processOneAtATime) return;
            var year = document.RootElement.GetProperty("years").EnumerateArray().Single().GetInt32();
            var season = document.RootElement.GetProperty("seasons").EnumerateArray().Single().GetString();
            var seasons = document.RootElement.TryGetProperty("batchSeasons", out var batchSeasons)
                ? batchSeasons.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().ToArray()
                : document.RootElement.TryGetProperty("initialImportSeasons", out var configuredSeasons)
                ? configuredSeasons.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().ToArray()
                : [];
            var years = document.RootElement.TryGetProperty("batchYears", out var batchYears)
                ? batchYears.EnumerateArray().Select(x => x.GetInt32()).ToArray()
                : legacyInitialImport && document.RootElement.TryGetProperty("initialImportEndYear", out var configuredEndYear)
                    ? Enumerable.Range(year, configuredEndYear.GetInt32() - year + 1).ToArray()
                    : [];
            if (seasons.Length == 0 || years.Length == 0) return;
            var seasonIndex = Array.IndexOf(seasons, season);
            var yearIndex = Array.IndexOf(years, year);
            if (seasonIndex < 0 || yearIndex < 0) return;
            var nextYearIndex = seasonIndex == seasons.Length - 1 ? yearIndex + 1 : yearIndex;
            if (nextYearIndex >= years.Length) return;
            var nextSeason = seasons[(seasonIndex + 1) % seasons.Length];
            await QueuePartAsync(completedJob.JobType, years[nextYearIndex], nextSeason, years, seasons);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    public async Task<SyncJob> QueuePartAsync(string jobType, int year, string season, int[] years, string[] seasons)
    {
        var job = new SyncJob
        {
            JobType = jobType,
            QueuedAt = DateTime.UtcNow,
            Parameters = JsonSerializer.Serialize(new
            {
                processOneSeasonAtATime = true,
                batchYears = years,
                batchSeasons = seasons,
                years = new[] { year },
                seasons = new[] { season }
            })
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        syncJobTrigger.Signal();
        return job;
    }

    public async Task<bool> CancelAsync(int jobId)
    {
        var job = await db.SyncJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null || job.Status is not ("Queued" or "Running")) return false;

        if (job.Status == "Queued")
        {
            job.Status = "Cancelled";
            job.Message = "Cancelled before starting";
            job.FinishedAt = DateTime.UtcNow;
        }
        else
        {
            job.CancellationRequested = true;
            job.Message = "Cancellation requested";
        }

        await db.SaveChangesAsync();
        // The flag is saved first so the runner records the job as Cancelled.
        if (job.CancellationRequested) syncJobTrigger.RequestCancellation(jobId);
        return true;
    }

    // Job lists and the job page poll while a job runs. SyncedAnimeIds can hold thousands
    // of IDs, so polled reads leave it out; only the synced-anime list reads it.
    private IQueryable<SyncJob> JobSummaries() => db.SyncJobs.AsNoTracking().Select(j => new SyncJob
    {
        Id = j.Id,
        JobType = j.JobType,
        Status = j.Status,
        QueuedAt = j.QueuedAt,
        StartedAt = j.StartedAt,
        FinishedAt = j.FinishedAt,
        ProcessedCount = j.ProcessedCount,
        TotalCount = j.TotalCount,
        CancellationRequested = j.CancellationRequested,
        Message = j.Message,
        Parameters = j.Parameters
    });

    public async Task<List<SyncJob>> GetRecentAsync(int skip = 0, int count = 30)
        => await JobSummaries()
            .OrderByDescending(j => j.QueuedAt)
            .Skip(skip)
            .Take(count)
            .ToListAsync();

    public Task<int> GetCountAsync()
        => db.SyncJobs.CountAsync();

    /// <summary>The job without its synced-ID list; use <see cref="GetSyncedAnimesAsync"/> for that.</summary>
    public async Task<SyncJob?> GetByIdAsync(int id)
        => await JobSummaries().FirstOrDefaultAsync(j => j.Id == id);

    /// <summary>Logs for a job, optionally only those added after <paramref name="afterLogId"/>.</summary>
    public async Task<List<SyncJobLog>> GetLogsAsync(int jobId, int afterLogId = 0)
        => await db.SyncJobLogs
            .AsNoTracking()
            .Where(l => l.SyncJobId == jobId && l.Id > afterLogId)
            .OrderBy(l => l.Id)
            .ToListAsync();

    /// <summary>The anime a job handled, with only the fields a synced-anime card shows.</summary>
    public async Task<List<Anime>> GetSyncedAnimesAsync(int jobId)
    {
        var json = await db.SyncJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.SyncedAnimeIds).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(json)) return [];

        List<int>? ids;
        try { ids = JsonSerializer.Deserialize<List<int>>(json); }
        catch (JsonException) { return []; }
        if (ids is not { Count: > 0 }) return [];

        var order = ids.Select((id, index) => new { id, index })
            .GroupBy(x => x.id)
            .ToDictionary(group => group.Key, group => group.First().index);
        var animes = await db.Animes.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new Anime { Id = a.Id, Title = a.Title, ImageUrl = a.ImageUrl, LocalImagePath = a.LocalImagePath })
            .ToListAsync();

        // Dictionary lookups are not translatable to SQL, so preserve the job's
        // recorded sync order only after EF has fetched the matching rows.
        return animes.OrderBy(a => order[a.Id]).ToList();
    }
}
