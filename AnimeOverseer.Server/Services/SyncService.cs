using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

public class SyncService(AnimeDbContext db, IServiceScopeFactory scopeFactory, AniListApiService aniList, MyAnimeListApiService myAnimeList, AnimeScheduleApiService animeSchedule, KitsuApiService kitsu, ILogger<SyncService> logger)
{
    public async Task RunTopUpcomingSyncAsync(SyncJob job, CancellationToken ct)
    {
        job.TotalCount = 9;
        job.ProcessedCount = 0;
        Log(job, "AniList quick-filter rankings refresh started — 9 ranked lists to refresh");
        await db.SaveChangesAsync(ct);
        using var scope = scopeFactory.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<AnimeCacheService>();
        var syncedIds = await cache.FetchAndCacheCurrentlyAiringAsync(async (label, count) =>
        {
            job.ProcessedCount++;
            job.Message = $"Refreshed {label} ({count:N0} anime) — {job.ProcessedCount} of {job.TotalCount} lists";
            Log(job, job.Message, "Success");
            await db.SaveChangesAsync(ct);
        }, ct);
        job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
        Log(job, $"AniList quick-filter rankings refresh complete — {syncedIds.Count:N0} anime updated", "Success");
    }

    public async Task RunJikanSyncAsync(SyncJob job, CancellationToken ct)
    {
        var (years, seasons) = ParseJikanParams(job.Parameters);
        var syncedIds = GetSyncedIds(job);
        // TotalCount is 0 because total anime across all pages is unknown upfront.
        job.TotalCount = 0;
        Log(job, $"AniList catalogue sync started — {seasons.Length} season(s) × {years.Length} year(s)");
        await db.SaveChangesAsync(ct);

        foreach (var year in years)
        {
            if (ct.IsCancellationRequested) break;

            Log(job, $"Fetching {string.Join(", ", seasons)} {year}…");
            await db.SaveChangesAsync(ct);

            // Callback fires after every page; persists the running anime count immediately.
            // Uses SyncService's own db context — AnimeCacheService has its own isolated context.
            async Task OnPageFetched(int page, int count)
            {
                ct.ThrowIfCancellationRequested();
                job.ProcessedCount += count;
                TrackJobProgress(job);
                await db.SaveChangesAsync(CancellationToken.None);
            }

            // Resolve AnimeCacheService in its own child scope so it gets a dedicated DbContext,
            // preventing ChangeTracker conflicts with this service's db instance.
            using var cacheScope = scopeFactory.CreateScope();
            var cache = cacheScope.ServiceProvider.GetRequiredService<AnimeCacheService>();
            syncedIds.UnionWith(await cache.FetchAndCacheSeasonsAsync(year, seasons, OnPageFetched, ct));
            job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);

            if (ct.IsCancellationRequested) break;

            // AnimeCacheService has already committed all anime data; clear any residual
            // tracked entities so only the job update and log entry are saved here.
            db.ChangeTracker.Clear();
            TrackJobProgress(job, includeSyncedIds: true);
            Log(job, $"Completed {year} — {job.ProcessedCount:N0} anime fetched so far", "Success");
            await db.SaveChangesAsync(ct);
        }

        if (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            TrackJobProgress(job, includeSyncedIds: true);
            Log(job, $"Sync complete — {job.ProcessedCount:N0} anime processed", "Success");
            await db.SaveChangesAsync(ct);
        }
    }

    private static int ParseSkipCount(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters)) return 0;
        try
        {
            var doc = JsonDocument.Parse(parameters);
            if (doc.RootElement.TryGetProperty("skipCount", out var el))
                return el.GetInt32();
        }
        catch { }
        return 0;
    }

    private static (int[] Years, string[] Seasons) ParseJikanParams(string? parameters)
    {
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            try
            {
                var doc = JsonDocument.Parse(parameters);
                var years = doc.RootElement.GetProperty("years")
                    .EnumerateArray().Select(e => e.GetInt32()).ToArray();
                var seasons = doc.RootElement.GetProperty("seasons")
                    .EnumerateArray().Select(e => e.GetString()!).ToArray();
                if (years.Length > 0 && seasons.Length > 0)
                    return (years, seasons);
            }
            catch { }
        }
        return ([DateTime.UtcNow.Year], ["spring", "summer", "fall", "winter"]);
    }

    public async Task RunAniListSyncAsync(SyncJob job, CancellationToken ct)
    {
        var syncedIds = GetSyncedIds(job);
        int skipCount = ParseSkipCount(job.Parameters);
        var animes = await db.Animes
            .Where(a => a.MALId != null && a.MALId > 0)
            .OrderBy(a => a.Id)
            .Skip(skipCount)
            .ToListAsync(ct);

        job.TotalCount = animes.Count + skipCount;
        job.ProcessedCount = skipCount;
        var resumeNote = skipCount > 0 ? $" (resuming from {skipCount:N0})" : "";
        Log(job, $"Starting AniList enrichment — {job.TotalCount:N0} anime with MAL IDs{resumeNote}");
        await db.SaveChangesAsync(ct);

        var failures = new ImportFailures("AniList", logger, db, job);
        foreach (var anime in animes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var data = await aniList.GetByMalIdAsync(anime.MALId!.Value);
                if (data != null)
                {
                    await EnrichFieldsAsync(anime, data, ct);
                    syncedIds.Add(anime.Id);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Record(anime, ex);
            }

            job.ProcessedCount++;

            if (job.ProcessedCount % 100 == 0)
                Log(job, $"Progress: {job.ProcessedCount:N0} / {job.TotalCount:N0}{failures.ProgressSuffix}");

            if (job.ProcessedCount % 20 == 0)
            {
                job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
                await db.SaveChangesAsync(ct);
            }

            // AniListApiService coordinates the global request rate and retries 429s.
        }

        job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
        await db.SaveChangesAsync(ct);

        Log(job, failures.Summary(job.ProcessedCount), failures.Count > 0 ? "Warning" : "Success");
        await db.SaveChangesAsync(ct);
    }

    public async Task RunKitsuSyncAsync(SyncJob job, CancellationToken ct)
    {
        var syncedIds = GetSyncedIds(job);
        int skipCount = ParseSkipCount(job.Parameters);
        var animes = await db.Animes
            .Where(a => (a.KitsuId == null || a.KitsuId == 0) && a.Title != null)
            .OrderBy(a => a.Id)
            .Skip(skipCount)
            .ToListAsync(ct);

        job.TotalCount = animes.Count + skipCount;
        job.ProcessedCount = skipCount;
        var resumeNote = skipCount > 0 ? $" (resuming from {skipCount:N0})" : "";
        Log(job, $"Starting Kitsu enrichment — {job.TotalCount:N0} anime without Kitsu data{resumeNote}");
        await db.SaveChangesAsync(ct);

        var failures = new ImportFailures("Kitsu", logger, db, job);
        foreach (var anime in animes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var data = await kitsu.GetByTitleAsync(anime.Title);
                if (data != null)
                {
                    await EnrichFieldsAsync(anime, data, ct);
                    syncedIds.Add(anime.Id);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Record(anime, ex);
            }

            job.ProcessedCount++;

            if (job.ProcessedCount % 100 == 0)
                Log(job, $"Progress: {job.ProcessedCount:N0} / {job.TotalCount:N0}{failures.ProgressSuffix}");

            if (job.ProcessedCount % 50 == 0)
            {
                job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
                await db.SaveChangesAsync(ct);
            }

            await Task.Delay(200, ct);
        }

        job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
        await db.SaveChangesAsync(ct);

        Log(job, failures.Summary(job.ProcessedCount), failures.Count > 0 ? "Warning" : "Success");
        await db.SaveChangesAsync(ct);
    }

    // Queues a log entry to be saved with the next SaveChangesAsync call.
    private void Log(SyncJob job, string message, string level = "Info")
    {
        db.SyncJobLogs.Add(new SyncJobLog
        {
            SyncJobId = job.Id,
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message
        });
    }

    private sealed class ImportFailures(string source, ILogger logger, AnimeDbContext db, SyncJob job)
    {
        private const int MaxRepresentativeFailures = 3;
        private const int ConsecutiveUnexpectedFailureLimit = 20;
        private const int FailureRateSampleSize = 100;
        private readonly List<string> representativeMessages = [];
        public int Count { get; private set; }
        public int UnexpectedCount { get; private set; }
        public int ConsecutiveUnexpectedCount { get; private set; }
        public string ProgressSuffix => Count == 0 ? "" : $" ({Count} errors)";

        public void RecordSuccess() => ConsecutiveUnexpectedCount = 0;

        public void Record(Anime anime, Exception exception)
        {
            var item = anime.MALId is > 0 ? $"MAL {anime.MALId}" : $"local {anime.Id}";
            if (!string.IsNullOrWhiteSpace(anime.Title)) item += $" ({anime.Title})";
            Record(item, exception);
        }

        public void Record(int localAnimeId, Exception exception)
            => Record($"local {localAnimeId}", exception);

        private void Record(string item, Exception exception)
        {
            Count++;
            if (IsUnexpected(exception))
            {
                UnexpectedCount++;
                ConsecutiveUnexpectedCount++;
            }
            else
            {
                // Expected bad-record failures do not contribute to an outage streak.
                ConsecutiveUnexpectedCount = 0;
            }

            var message = $"{source} item {item} failed ({exception.GetType().Name})";
            logger.LogError("{Source} sync item failed. JobId: {JobId}; Item: {Item}; ErrorType: {ErrorType}; Details: {Details}",
                source, job.Id, item, exception.GetType().Name, Redact(exception.ToString()));
            if (representativeMessages.Count < MaxRepresentativeFailures)
            {
                representativeMessages.Add(message);
                // Job logs are user-visible: retain only identity and exception type, never an external error body.
                db.SyncJobLogs.Add(new SyncJobLog { SyncJobId = job.Id, Timestamp = DateTime.UtcNow, Level = "Warning", Message = message });
            }
        }

        public string? CircuitBreakerReason(int processed)
        {
            if (ConsecutiveUnexpectedCount >= ConsecutiveUnexpectedFailureLimit)
                return $"Stopped after {ConsecutiveUnexpectedCount} consecutive unexpected failures";

            if (processed >= FailureRateSampleSize && UnexpectedCount * 2 > processed)
                return $"Stopped because {UnexpectedCount:N0} of {processed:N0} records had unexpected failures";

            return null;
        }

        public string Summary(int processed)
            => Count == 0
                ? $"Sync complete — {processed:N0} processed"
                : $"Sync complete — {processed:N0} processed, {Count} errors. {string.Join("; ", representativeMessages)}";

        private static string Redact(string value)
            => Regex.Replace(value,
                @"(?i)((?:access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|authorization|bearer)\s*(?:=|:|\s)\s*)[^\s,;&]+",
                "$1[REDACTED]");

        private static bool IsUnexpected(Exception exception)
            => exception is not ArgumentException &&
               exception is not HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound };
    }

    public Task RunMyAnimeListSyncAsync(SyncJob job, CancellationToken ct)
        => RunSelectedCatalogueSyncAsync(job, "MyAnimeList", myAnimeList.GetSeasonAsync, ct);

    public Task RunAnimeScheduleSyncAsync(SyncJob job, CancellationToken ct)
        => RunSelectedCatalogueSyncAsync(job, "AnimeSchedule", animeSchedule.GetSeasonAsync, ct);

    public async Task RunRecommendationsSyncAsync(SyncJob job, CancellationToken ct)
    {
        var (years, seasons) = ParseJikanParams(job.Parameters);
        var syncedIds = GetSyncedIds(job);
        var animeIds = await db.Animes.AsNoTracking()
            .Where(anime => anime.AniListId != null && anime.AniListId > 0 && years.Contains(anime.Season.Year) && seasons.Contains(anime.Season.Name))
            .OrderBy(anime => anime.Id).Select(anime => anime.Id).ToListAsync(ct);
        job.TotalCount = animeIds.Count;
        Log(job, $"AniList recommendation sync started — {job.TotalCount:N0} anime in {seasons.Length} season(s) × {years.Length} year(s)");
        await db.SaveChangesAsync(ct);

        var failures = new ImportFailures("AniList recommendation", logger, db, job);
        var checkpoint = new ProgressCheckpoint();
        try
        {
            foreach (var animeId in animeIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // A fresh scope ensures a failed record cannot leave tracked changes
                    // behind for the next one.
                    using var cacheScope = scopeFactory.CreateScope();
                    var cache = cacheScope.ServiceProvider.GetRequiredService<AnimeCacheService>();
                    await cache.FetchAndStoreRecommendationsAsync(animeId, ct);
                    syncedIds.Add(animeId);
                    failures.RecordSuccess();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Record(animeId, ex);
                }

                job.ProcessedCount++;
                if (job.ProcessedCount % 20 == 0 || job.ProcessedCount == job.TotalCount)
                    Log(job, $"Progress: {job.ProcessedCount:N0} / {job.TotalCount:N0}{failures.ProgressSuffix}");
                if (failures.CircuitBreakerReason(job.ProcessedCount) is { } circuitBreakerReason)
                {
                    Log(job, circuitBreakerReason, "Error");
                    job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException(circuitBreakerReason);
                }
                await SaveCheckpointAsync(job, syncedIds, checkpoint, ct);
                // AniListApiService coordinates the global request rate and retries 429s.
            }
        }
        finally
        {
            // The runner persists this with the job's final status, even on cancellation.
            job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
        }
        Log(job, failures.Summary(job.ProcessedCount), failures.Count > 0 ? "Warning" : "Success");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fills missing AniList/MAL IDs across the catalogue from AniList, then rechecks
    /// pending catalogue reviews so newly confirmed or ruled-out matches resolve.
    /// </summary>
    public async Task RunProviderIdsSyncAsync(SyncJob job, CancellationToken ct)
    {
        var syncedIds = GetSyncedIds(job);
        var animeIds = await db.Animes.AsNoTracking()
            .Where(anime => (anime.AniListId != null && anime.AniListId > 0 && (anime.MALId == null || anime.MALId <= 0)) ||
                            (anime.MALId != null && anime.MALId > 0 && (anime.AniListId == null || anime.AniListId <= 0)))
            .OrderBy(anime => anime.Id).Select(anime => anime.Id).ToListAsync(ct);
        var reviewIds = await db.PendingAnimeReviews.AsNoTracking()
            .Where(review => review.Status == "Pending")
            .OrderBy(review => review.Id).Select(review => review.Id).ToListAsync(ct);
        job.TotalCount = animeIds.Count + reviewIds.Count;
        Log(job, $"Provider ID check started — {animeIds.Count:N0} anime missing an AniList or MAL ID, {reviewIds.Count:N0} pending reviews");
        await db.SaveChangesAsync(ct);

        var failures = new ImportFailures("AniList provider ID", logger, db, job);
        var backfill = new Dictionary<ProviderIdBackfillOutcome, int>();
        var rechecked = new Dictionary<PendingReviewRecheckOutcome, int>();
        var checkpoint = new ProgressCheckpoint();
        try
        {
            foreach (var animeId in animeIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var cacheScope = scopeFactory.CreateScope();
                    var cache = cacheScope.ServiceProvider.GetRequiredService<AnimeCacheService>();
                    var result = await cache.BackfillProviderIdsAsync(animeId, ct);
                    backfill[result.Outcome] = backfill.GetValueOrDefault(result.Outcome) + 1;
                    if (result.Outcome == ProviderIdBackfillOutcome.Filled) syncedIds.Add(animeId);
                    if (result.DuplicateAnimeId is int duplicate)
                        Log(job, $"Anime #{animeId} and #{duplicate} resolve to the same provider anime; left unchanged as likely duplicates", "Warning");
                    failures.RecordSuccess();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Record(animeId, ex);
                }

                job.ProcessedCount++;
                if (job.ProcessedCount % 20 == 0) Log(job, $"Progress: {job.ProcessedCount:N0} / {job.TotalCount:N0}{failures.ProgressSuffix}");
                if (failures.CircuitBreakerReason(job.ProcessedCount) is { } circuitBreakerReason)
                {
                    Log(job, circuitBreakerReason, "Error");
                    job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException(circuitBreakerReason);
                }
                await SaveCheckpointAsync(job, syncedIds, checkpoint, ct);
            }
            if (animeIds.Count > 0)
                Log(job, $"Provider IDs filled for {backfill.GetValueOrDefault(ProviderIdBackfillOutcome.Filled):N0} anime; " +
                         $"{backfill.GetValueOrDefault(ProviderIdBackfillOutcome.NotFound):N0} not found on AniList; " +
                         $"{backfill.GetValueOrDefault(ProviderIdBackfillOutcome.Duplicate):N0} likely duplicates", "Success");

            foreach (var reviewId in reviewIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var cacheScope = scopeFactory.CreateScope();
                    var cache = cacheScope.ServiceProvider.GetRequiredService<AnimeCacheService>();
                    var outcome = await cache.RecheckPendingReviewAsync(reviewId, ct);
                    rechecked[outcome] = rechecked.GetValueOrDefault(outcome) + 1;
                    failures.RecordSuccess();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Provider ID recheck failed for pending review {ReviewId}", reviewId);
                    Log(job, $"Pending review #{reviewId} could not be rechecked: {ex.Message}", "Warning");
                }

                job.ProcessedCount++;
                if (job.ProcessedCount % 20 == 0) Log(job, $"Progress: {job.ProcessedCount:N0} / {job.TotalCount:N0}{failures.ProgressSuffix}");
                await SaveCheckpointAsync(job, syncedIds, checkpoint, ct);
            }
        }
        finally
        {
            job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
        }
        Log(job, $"Pending reviews rechecked — {rechecked.GetValueOrDefault(PendingReviewRecheckOutcome.Merged):N0} merged, " +
                 $"{rechecked.GetValueOrDefault(PendingReviewRecheckOutcome.Created):N0} created, " +
                 $"{rechecked.GetValueOrDefault(PendingReviewRecheckOutcome.Narrowed):N0} narrowed, " +
                 $"{rechecked.GetValueOrDefault(PendingReviewRecheckOutcome.Unchanged):N0} still need review", "Success");
        await db.SaveChangesAsync(ct);
    }

    public async Task RunRelationsSyncAsync(SyncJob job, CancellationToken ct)
    {
        var (years, seasons) = ParseJikanParams(job.Parameters);
        var syncedIds = GetSyncedIds(job);
        var animeIds = await db.Animes.AsNoTracking()
            .Where(anime => anime.AniListId != null && anime.AniListId > 0 && anime.MALId != null && anime.MALId > 0 && years.Contains(anime.Season.Year) && seasons.Contains(anime.Season.Name))
            .OrderBy(anime => anime.Id).Select(anime => anime.Id).ToListAsync(ct);
        job.TotalCount = animeIds.Count;
        Log(job, $"Related anime sync started — {job.TotalCount:N0} anime from AniList and Jikan");
        await db.SaveChangesAsync(ct);

        var failures = new ImportFailures("AniList relation", logger, db, job);
        var checkpoint = new ProgressCheckpoint();
        try
        {
            foreach (var animeId in animeIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // A fresh scope ensures a failed record cannot leave tracked changes
                    // behind for the next one.
                    using var cacheScope = scopeFactory.CreateScope();
                    var cache = cacheScope.ServiceProvider.GetRequiredService<AnimeCacheService>();
                    await cache.FetchAndStoreRelationsAsync(animeId, ct);
                    syncedIds.Add(animeId);
                    failures.RecordSuccess();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Record(animeId, ex);
                }

                job.ProcessedCount++;
                if (job.ProcessedCount % 20 == 0 || job.ProcessedCount == job.TotalCount)
                    Log(job, $"Progress: {job.ProcessedCount:N0} / {job.TotalCount:N0}{failures.ProgressSuffix}");
                if (failures.CircuitBreakerReason(job.ProcessedCount) is { } circuitBreakerReason)
                {
                    Log(job, circuitBreakerReason, "Error");
                    job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException(circuitBreakerReason);
                }
                await SaveCheckpointAsync(job, syncedIds, checkpoint, ct);
            }
        }
        finally
        {
            job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
        }
        Log(job, failures.Summary(job.ProcessedCount), failures.Count > 0 ? "Warning" : "Success");
        await db.SaveChangesAsync(ct);
    }

    private async Task RunSelectedCatalogueSyncAsync(
        SyncJob job,
        string sourceName,
        Func<int, string, CancellationToken, Task<List<Anime>>> fetch,
        CancellationToken ct)
    {
        var (years, seasons) = ParseJikanParams(job.Parameters);
        var syncedIds = GetSyncedIds(job);
        job.TotalCount = 0;
        Log(job, $"{sourceName} catalogue sync started — {seasons.Length} season(s) × {years.Length} year(s)");
        await db.SaveChangesAsync(ct);

        foreach (var year in years)
        {
            ct.ThrowIfCancellationRequested();
            using var cacheScope = scopeFactory.CreateScope();
            var cache = cacheScope.ServiceProvider.GetRequiredService<AnimeCacheService>();
            var ids = await cache.FetchAndCacheSeasonsFromAsync(
                year,
                seasons,
                fetch,
                async count =>
                {
                    job.ProcessedCount += count;
                    TrackJobProgress(job);
                    await db.SaveChangesAsync(CancellationToken.None);
                },
                ct);
            syncedIds.UnionWith(ids);
            job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
            db.ChangeTracker.Clear();
            TrackJobProgress(job, includeSyncedIds: true);
            Log(job, $"Completed {year} — {job.ProcessedCount:N0} anime fetched so far", "Success");
            await db.SaveChangesAsync(ct);
        }

        Log(job, $"{sourceName} sync complete — {job.ProcessedCount:N0} anime processed", "Success");
        await db.SaveChangesAsync(ct);
    }

    // Each save is its own write transaction, so per-record saves made long jobs compete
    // with page reads. Progress is persisted at most this often; the job page polls every 2s.
    private static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(2);

    private sealed class ProgressCheckpoint
    {
        private long _lastSave = Environment.TickCount64;

        public bool Due()
        {
            var now = Environment.TickCount64;
            if (now - _lastSave < ProgressSaveInterval.TotalMilliseconds) return false;
            _lastSave = now;
            return true;
        }
    }

    private async Task SaveCheckpointAsync(SyncJob job, HashSet<int> syncedIds, ProgressCheckpoint checkpoint, CancellationToken ct)
    {
        if (!checkpoint.Due()) return;
        job.SyncedAnimeIds = JsonSerializer.Serialize(syncedIds);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Tracks the job for a progress save, re-attaching it after ChangeTracker.Clear().
    /// Only the progress columns are marked, so a save does not rewrite the whole row
    /// (in particular the synced-ID list, which grows with every page).
    /// </summary>
    private void TrackJobProgress(SyncJob job, bool includeSyncedIds = false)
    {
        var entry = db.Entry(job);
        if (entry.State == EntityState.Detached) db.SyncJobs.Attach(job);
        entry.Property(j => j.ProcessedCount).IsModified = true;
        entry.Property(j => j.TotalCount).IsModified = true;
        entry.Property(j => j.Message).IsModified = true;
        if (includeSyncedIds) entry.Property(j => j.SyncedAnimeIds).IsModified = true;
    }

    private static HashSet<int> GetSyncedIds(SyncJob job)
    {
        if (string.IsNullOrWhiteSpace(job.SyncedAnimeIds)) return [];
        try { return JsonSerializer.Deserialize<HashSet<int>>(job.SyncedAnimeIds) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task EnrichFieldsAsync(Anime target, Anime source, CancellationToken ct)
    {
        EnrichFields(target, source);
        await SourceTitleStore.RecordAsync(db, target, source, ct);
    }

    private static void EnrichFields(Anime target, Anime source)
    {
        if (!target.NamesReviewed && !target.HasEnglishTitle && source.HasEnglishTitle)
        {
            target.Title = source.Title;
            target.HasEnglishTitle = true;
        }
        if (string.IsNullOrWhiteSpace(target.Synopsis) && !string.IsNullOrWhiteSpace(source.Synopsis))
            target.Synopsis = source.Synopsis;
        if (target.Episodes == null && source.Episodes != null)
            target.Episodes = source.Episodes;
        if (target.Rating == null && source.Rating != null)
            target.Rating = source.Rating;
        if (string.IsNullOrWhiteSpace(target.OriginalTitle) && !string.IsNullOrWhiteSpace(source.OriginalTitle))
            target.OriginalTitle = source.OriginalTitle;
        if ((target.AniListId == null || target.AniListId == 0) && source.AniListId is > 0)
            target.AniListId = source.AniListId;
        if ((target.KitsuId == null || target.KitsuId == 0) && source.KitsuId is > 0)
            target.KitsuId = source.KitsuId;
    }
}
