using System.Net;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeOverseer.Server.Tests.TestInfrastructure;

public abstract class SqliteIntegrationTestBase : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"animeoverseer-tests-{Guid.NewGuid():N}.db");
    protected string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    protected AnimeDbContext CreateDb()
    {
        var db = new AnimeDbContext(new DbContextOptionsBuilder<AnimeDbContext>()
            .UseSqlite(ConnectionString).Options);
        db.Database.EnsureCreated();
        return db;
    }

    protected static async Task<Season> AddSeasonAsync(AnimeDbContext db)
    {
        var season = new Season { Name = "spring", Year = 2026 };
        db.Seasons.Add(season);
        await db.SaveChangesAsync();
        return season;
    }

    protected static AnimeCacheService CreateCache(AnimeDbContext db, HttpMessageHandler? handler = null, IMemoryCache? memoryCache = null)
    {
        var client = new HttpClient(handler ?? new FailingHandler());
        var aniList = new AniListApiService(client, NullLogger<AniListApiService>.Instance);
        var jikan = new JikanApiService(client, NullLogger<JikanApiService>.Instance);
        var imageCache = new ImageCacheService(new TestHttpClientFactory(client), new TestWebHostEnvironment());
        return new AnimeCacheService(db, aniList, jikan, imageCache, memoryCache ?? new MemoryCache(new MemoryCacheOptions()));
    }

    protected static Anime Incoming(int aniListId, string title, bool english, List<string>? aliases = null, List<SourceImage>? images = null)
        => new() { AniListId = aniListId, Title = title, HasEnglishTitle = english, AlternativeTitles = aliases ?? [], SourceImages = images ?? [] };

    protected static SourceImage Image(string source, string type, string url) => new() { Source = source, Type = type, Url = url };

    protected static PendingAnimeReview AddReview(int seasonId, Anime incoming, int[] candidates) => new()
    {
        SeasonId = seasonId, SourceAniListId = incoming.AniListId, SourceName = "test",
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new AnimeImportSnapshot
        {
            AniListId = incoming.AniListId, Title = incoming.Title, HasEnglishTitle = incoming.HasEnglishTitle,
            AlternativeTitles = incoming.AlternativeTitles, SourceImages = incoming.SourceImages
        }),
        CandidateAnimeIdsJson = System.Text.Json.JsonSerializer.Serialize(candidates)
    };

    public void Dispose()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), $"animeoverseer-images-{Guid.NewGuid():N}");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "AnimeOverseer.Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
