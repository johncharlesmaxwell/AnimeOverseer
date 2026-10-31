using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class AutoDecisionTests : SqliteIntegrationTestBase
{
    private static readonly DateTime Spring2026 = new(2026, 4, 1);

    [Fact]
    public void Suggest_creates_when_every_candidate_has_a_hard_conflict()
    {
        var suggestion = MatchScorer.Suggest([Match(1, 10, hardNegative: true), Match(2, 5, hardNegative: true)], 90);

        Assert.Equal(new MatchSuggestion("Create", null, "every candidate has a hard conflict"), suggestion);
    }

    [Fact]
    public void Suggest_merges_the_only_conflict_free_candidate_at_the_threshold()
    {
        var suggestion = MatchScorer.Suggest([Match(1, 90), Match(2, 10, hardNegative: true)], 90);

        Assert.Equal("Merge", suggestion?.Decision);
        Assert.Equal(1, suggestion?.AnimeId);
    }

    [Theory]
    [InlineData(89, 0)]
    [InlineData(100, 95)]
    public void Suggest_leaves_low_or_ambiguous_matches_to_a_person(int first, int second)
    {
        var matches = second == 0 ? new[] { Match(1, first) } : [Match(1, first), Match(2, second)];

        Assert.Null(MatchScorer.Suggest(matches, 90));
    }

    [Theory]
    [InlineData(null, null, AutoDecisionMode.Suggest, 90)]
    [InlineData("apply", "75", AutoDecisionMode.Apply, 75)]
    [InlineData("Off", "10", AutoDecisionMode.Off, 50)]
    [InlineData("bogus", "x", AutoDecisionMode.Suggest, 90)]
    public void Settings_default_to_suggest_only_and_clamp_the_threshold(string? mode, string? threshold, AutoDecisionMode expectedMode, int expectedThreshold) =>
        Assert.Equal(new MatchingSettings(expectedMode, expectedThreshold), MatchingSettings.Read(mode, threshold));

    [Fact]
    public async Task Suggest_mode_queues_the_review_and_shows_the_suggestion()
    {
        await using var db = CreateDb();
        var existing = await AddOrbitAsync(db);

        await ImportAsync(db, IncomingOrbit());

        var item = Assert.Single(await new AnimeReviewService(db, CreateCache(db)).GetPendingAsync());
        Assert.Equal(new MatchSuggestion("Merge", existing.Id, "the only conflict-free candidate scored 100 (threshold 90)"), item.Suggestion);
        Assert.Equal(1, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Apply_mode_merges_a_high_scoring_match_and_records_it()
    {
        await using var db = CreateDb();
        var existing = await AddOrbitAsync(db);
        await MatchingSettings.SaveAsync(db, new MatchingSettings(AutoDecisionMode.Apply, 90));

        var synced = await ImportAsync(db, IncomingOrbit());

        var review = await db.PendingAnimeReviews.AsNoTracking().SingleAsync();
        Assert.Equal("Merged", review.Status);
        Assert.True(review.AutoResolved);
        Assert.Equal(existing.Id, review.ResolvedAnimeId);
        Assert.NotNull(review.PreMergeSnapshotJson);
        Assert.Contains(existing.Id, synced);
        Assert.Equal(82, (await db.Animes.AsNoTracking().SingleAsync()).AniListId);
    }

    [Fact]
    public async Task Apply_mode_creates_when_the_only_candidate_is_a_movie_for_a_series()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { Title = "Orbit", Type = "Movie", Episodes = 1, StartDate = Spring2026, SeasonId = season.Id });
        await db.SaveChangesAsync();
        await MatchingSettings.SaveAsync(db, new MatchingSettings(AutoDecisionMode.Apply, 90));

        await ImportAsync(db, IncomingOrbit());

        Assert.Equal("Created", (await db.PendingAnimeReviews.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Undoing_an_automatic_merge_restores_the_record_and_keeps_automation_off_it()
    {
        await using var db = CreateDb();
        var existing = await AddOrbitAsync(db);
        await MatchingSettings.SaveAsync(db, new MatchingSettings(AutoDecisionMode.Apply, 90));
        await ImportAsync(db, IncomingOrbit());
        var reviewId = (await db.PendingAnimeReviews.AsNoTracking().SingleAsync()).Id;

        await using var undoDb = CreateDb();
        Assert.Null(await CreateCache(undoDb).UndoAutomaticResolutionAsync(reviewId));

        await using var check = CreateDb();
        var restored = await check.Animes.Include(anime => anime.TitleAliases).Include(anime => anime.AnimeGenres).ThenInclude(link => link.Genre).SingleAsync();
        Assert.Equal(existing.Id, restored.Id);
        Assert.Null(restored.AniListId);
        Assert.Equal(["Orbit Original"], restored.TitleAliases.Select(alias => alias.Title));
        Assert.Equal(["Action"], restored.AnimeGenres.Select(link => link.Genre.Name));
        var review = await check.PendingAnimeReviews.SingleAsync();
        Assert.Equal("Pending", review.Status);
        Assert.True(review.AutoDecisionUndone);
        Assert.False(review.AutoResolved);

        Assert.Equal(PendingReviewRecheckOutcome.Unchanged, await CreateCache(check).RecheckPendingReviewAsync(reviewId));
        Assert.Equal("Pending", (await check.PendingAnimeReviews.AsNoTracking().SingleAsync()).Status);
        Assert.Null(Assert.Single(await new AnimeReviewService(check, CreateCache(check)).GetPendingAsync()).Suggestion);
    }

    [Fact]
    public async Task Undoing_an_automatic_create_deletes_the_record_unless_it_is_in_use()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { Title = "Orbit", Type = "Movie", SeasonId = season.Id, StartDate = Spring2026 });
        await db.SaveChangesAsync();
        await MatchingSettings.SaveAsync(db, new MatchingSettings(AutoDecisionMode.Apply, 90));
        await ImportAsync(db, IncomingOrbit());
        var review = await db.PendingAnimeReviews.AsNoTracking().SingleAsync();
        var collection = new AnimeCollection { Name = "Watching" };
        db.AnimeCollections.Add(collection);
        await db.SaveChangesAsync();
        db.AnimeCollectionItems.Add(new AnimeCollectionItem { CollectionId = collection.Id, AnimeId = review.ResolvedAnimeId!.Value });
        await db.SaveChangesAsync();

        Assert.NotNull(await CreateCache(db).UndoAutomaticResolutionAsync(review.Id));
        await db.AnimeCollectionItems.ExecuteDeleteAsync();
        await using var undoDb = CreateDb();
        Assert.Null(await CreateCache(undoDb).UndoAutomaticResolutionAsync(review.Id));

        await using var check = CreateDb();
        Assert.Equal(1, await check.Animes.CountAsync());
        Assert.Equal("Pending", (await check.PendingAnimeReviews.SingleAsync()).Status);
    }

    private static CandidateMatch Match(int animeId, int score, bool hardNegative = false) => new()
    {
        AnimeId = animeId, Score = score,
        Evidence = hardNegative ? [new MatchEvidence { Signal = "Format", Kind = MatchEvidenceKind.HardNegative }] : []
    };

    private static async Task<Anime> AddOrbitAsync(AnimeDbContext db)
    {
        var season = await AddSeasonAsync(db);
        var existing = new Anime
        {
            Title = "Orbit", Type = "TV", Episodes = 12, StartDate = Spring2026, SeasonId = season.Id,
            TitleAliases = [new() { Title = "Orbit Original" }],
            AnimeGenres = [new() { Genre = new Genre { Name = "Action" } }]
        };
        db.Animes.Add(existing);
        await db.SaveChangesAsync();
        return existing;
    }

    private static Anime IncomingOrbit()
    {
        var incoming = Incoming(82, "Orbit", true, aliases: ["Orubitto"]);
        incoming.Type = "TV";
        incoming.Episodes = 12;
        incoming.StartDate = Spring2026.AddDays(7);
        incoming.AnimeGenres.Add(new AnimeGenre { Genre = new Genre { Name = "Drama" } });
        return incoming;
    }

    private static Task<List<int>> ImportAsync(AnimeDbContext db, Anime incoming)
        => CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));
}
