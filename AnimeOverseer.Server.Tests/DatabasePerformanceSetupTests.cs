using AnimeOverseer.Server.BackgroundServices;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
}
