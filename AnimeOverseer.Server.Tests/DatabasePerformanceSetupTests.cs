using AnimeOverseer.Server.BackgroundServices;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public class DatabasePerformanceSetupTests : SqliteIntegrationTestBase
{
    [Fact]
    public void Normalize_TurnsOffSharedCacheAndAddsBusyTimeout()
    {
        var builder = new SqliteConnectionStringBuilder(
            SqliteConnectionSetup.Normalize("Data Source=test.db;Cache=Shared"));

        Assert.Equal("test.db", builder.DataSource);
        Assert.NotEqual(SqliteCacheMode.Shared, builder.Cache);
        Assert.Equal(30, builder.DefaultTimeout);
    }

    [Fact]
    public void Normalize_KeepsConfiguredTimeoutAndFallsBackToDefaultDatabase()
    {
        Assert.Equal(5, new SqliteConnectionStringBuilder(SqliteConnectionSetup.Normalize("Data Source=a.db;Default Timeout=5")).DefaultTimeout);
        Assert.Equal("animeoverseer.db", new SqliteConnectionStringBuilder(SqliteConnectionSetup.Normalize(null)).DataSource);
    }

    [Fact]
    public async Task PragmaInterceptor_SetsSynchronousNormalOnEachConnection()
    {
        await using var db = new AnimeDbContext(new DbContextOptionsBuilder<AnimeDbContext>()
            .UseSqlite(ConnectionString).AddInterceptors(new SqlitePragmaInterceptor()).Options);
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA synchronous;";

        // 1 = NORMAL (FULL is 2).
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CancelAsync_SignalsTheRunningJobImmediately()
    {
        await using var db = CreateDb();
        var job = new SyncJob { JobType = "Relations", Status = "Running" };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        var trigger = new SyncJobTrigger();
        var cancelled = false;
        using (trigger.RegisterRunningJob(job.Id, () => cancelled = true))
        {
            Assert.True(await new SyncJobService(db, trigger).CancelAsync(job.Id));
        }

        Assert.True(cancelled);
        Assert.False(trigger.RequestCancellation(job.Id)); // unregistered once the job ends
    }

    [Fact]
    public async Task JobQueries_LeaveOutTheSyncedIdListUntilAsked()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var anime = new Anime { Title = "Synced", SeasonId = season.Id, ImageUrl = "https://example.test/a.jpg" };
        db.Animes.Add(anime);
        await db.SaveChangesAsync();
        var job = new SyncJob { JobType = "Relations", Status = "Completed", SyncedAnimeIds = $"[{anime.Id}]" };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        db.SyncJobLogs.Add(new SyncJobLog { SyncJobId = job.Id, Message = "first" });
        db.SyncJobLogs.Add(new SyncJobLog { SyncJobId = job.Id, Message = "second" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var jobs = new SyncJobService(db, new SyncJobTrigger());

        Assert.Null((await jobs.GetByIdAsync(job.Id))!.SyncedAnimeIds);
        Assert.All(await jobs.GetRecentAsync(), item => Assert.Null(item.SyncedAnimeIds));
        var synced = Assert.Single(await jobs.GetSyncedAnimesAsync(job.Id));
        Assert.Equal(("Synced", "https://example.test/a.jpg"), (synced.Title, synced.ImageUrl));

        var logs = await jobs.GetLogsAsync(job.Id);
        Assert.Equal(["first", "second"], logs.Select(log => log.Message));
        Assert.Equal(["second"], (await jobs.GetLogsAsync(job.Id, logs[0].Id)).Select(log => log.Message));
    }

    // The catalogue name snapshot is shared through the memory cache; each test passes
    // one cache to every service so later imports reuse the snapshot the first one built.

    [Fact]
    public async Task Shared_name_snapshot_matches_a_row_added_after_it_was_built()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var shared = new MemoryCache(new MemoryCacheOptions());
        var first = new Anime { Title = "Alpha Harbor Chronicle", SeasonId = season.Id };
        db.Animes.Add(first);
        await db.SaveChangesAsync();
        await ImportAsync(db, shared, Incoming(1, "Alpha Harbor Chronicle", false));
        Assert.Equal($"[{first.Id}]", (await ReviewForAsync(db, 1)).CandidateAnimeIdsJson);

        var added = new Anime { Title = "Beta Harbor Chronicle", SeasonId = season.Id };
        db.Animes.Add(added);
        await db.SaveChangesAsync();
        await ImportAsync(db, shared, Incoming(2, "Beta Harbor Chronicle", false));

        Assert.Equal($"[{added.Id}]", (await ReviewForAsync(db, 2)).CandidateAnimeIdsJson);
    }

    [Fact]
    public async Task Shared_name_snapshot_matches_a_row_by_its_new_name_after_a_rename()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var shared = new MemoryCache(new MemoryCacheOptions());
        var row = new Anime { Title = "Original Lantern Story", SeasonId = season.Id };
        db.Animes.Add(row);
        await db.SaveChangesAsync();
        await ImportAsync(db, shared, Incoming(20, "Completely Unrelated Program", false));

        // The import cleared this context's change tracker, so reload before renaming.
        (await db.Animes.SingleAsync(anime => anime.Id == row.Id)).Title = "Epsilon Lantern Story";
        await db.SaveChangesAsync();
        await ImportAsync(db, shared, Incoming(21, "Epsilon Lantern Story", false));

        Assert.Equal($"[{row.Id}]", (await ReviewForAsync(db, 21)).CandidateAnimeIdsJson);
    }

    [Fact]
    public async Task Shared_name_snapshot_drops_a_deleted_row()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var shared = new MemoryCache(new MemoryCacheOptions());
        var doomed = new Anime { Title = "Gamma Lighthouse Tale", SeasonId = season.Id };
        db.Animes.Add(doomed);
        await db.SaveChangesAsync();
        await ImportAsync(db, shared, Incoming(10, "Delta Unconnected Series", false));

        db.Animes.Remove(doomed);
        await db.SaveChangesAsync();
        await ImportAsync(db, shared, Incoming(11, "Gamma Lighthouse Tale", false));

        Assert.False(await db.PendingAnimeReviews.AnyAsync(review => review.SourceAniListId == 11));
        Assert.True(await db.Animes.AnyAsync(anime => anime.AniListId == 11));
    }

    [Fact]
    public async Task English_name_precheck_can_be_limited_to_given_anime()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        Anime Agreeing(string title) => new()
        {
            Title = title, SeasonId = season.Id,
            SourceTitles =
            [
                new AnimeSourceTitle { Source = "AniList", Title = "Frieren: Beyond Journey's End" },
                new AnimeSourceTitle { Source = "MyAnimeList", Title = "Frieren - Beyond Journeys End" }
            ]
        };
        var included = Agreeing("Sousou no Frieren");
        var skipped = Agreeing("Sousou no Frieren (copy)");
        db.Animes.AddRange(included, skipped);
        await db.SaveChangesAsync();

        var result = await new AnimeReviewService(db, CreateCache(db)).ApplyEnglishNamePrecheckAsync(animeIds: [included.Id]);

        Assert.Equal(1, result.Renamed);
        db.ChangeTracker.Clear();
        Assert.True((await db.Animes.SingleAsync(anime => anime.Id == included.Id)).NamesReviewed);
        Assert.False((await db.Animes.SingleAsync(anime => anime.Id == skipped.Id)).NamesReviewed);
    }

    [Fact]
    public async Task Log_pruning_removes_only_logs_of_jobs_finished_before_the_cutoff()
    {
        await using var db = CreateDb();
        var old = new SyncJob { JobType = "Relations", Status = "Completed", FinishedAt = DateTime.UtcNow.AddDays(-120) };
        var recent = new SyncJob { JobType = "Relations", Status = "Completed", FinishedAt = DateTime.UtcNow.AddDays(-5) };
        var running = new SyncJob { JobType = "Relations", Status = "Running" };
        db.SyncJobs.AddRange(old, recent, running);
        await db.SaveChangesAsync();
        db.SyncJobLogs.AddRange(new[] { old, recent, running }.Select(job => new SyncJobLog { SyncJobId = job.Id, Message = job.Status }));
        await db.SaveChangesAsync();

        var pruned = await DatabaseMaintenanceService.PruneJobLogsAsync(db, DateTime.UtcNow - DatabaseMaintenanceService.LogRetention);

        Assert.Equal(1, pruned);
        Assert.Equal(new[] { recent.Id, running.Id }.Order(), (await db.SyncJobLogs.Select(log => log.SyncJobId).ToListAsync()).Order());
    }

    private static Task ImportAsync(AnimeDbContext db, IMemoryCache shared, Anime incoming)
        => CreateCache(db, memoryCache: shared).FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));

    private static Task<PendingAnimeReview> ReviewForAsync(AnimeDbContext db, int aniListId)
        => db.PendingAnimeReviews.AsNoTracking().SingleAsync(review => review.SourceAniListId == aniListId);
}
