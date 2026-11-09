using System.Text.Json;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class MatchReplayTests : SqliteIntegrationTestBase
{
    private static readonly DateTime Spring2026 = new(2026, 4, 1);

    [Fact]
    public async Task Replay_compares_the_settings_decision_with_the_persons()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var orbit = await AddAsync(db, season, "Orbit");
        var lantern = await AddAsync(db, season, "Lantern");
        var cache = CreateCache(db);

        // Agreed: the person merged a clean match.
        var merged = await AddReviewAsync(db, season, Snapshot(1, "Orbit"), orbit.Id);
        Assert.True(await cache.ResolvePendingReviewAsync(merged.Id, "Merge", orbit.Id));
        // Wrong merge: the person knew a same-named show was different.
        var created = await AddReviewAsync(db, season, Snapshot(2, "Lantern"), lantern.Id);
        Assert.True(await cache.ResolvePendingReviewAsync(created.Id, "Create"));
        // Agreed: movie vs series is a hard conflict, and the person created it too.
        var movie = Snapshot(3, "Lantern");
        movie.Type = "Movie";
        var createdMovie = await AddReviewAsync(db, season, movie, lantern.Id);
        Assert.True(await cache.ResolvePendingReviewAsync(createdMovie.Id, "Create"));
        // Automatic decisions are not the person's, so they are not tested against.
        var automatic = await AddReviewAsync(db, season, Snapshot(4, "Orbit"), orbit.Id);
        automatic.Status = "Merged";
        automatic.AutoResolved = true;
        automatic.ResolvedAnimeId = orbit.Id;
        // An old merge with no pre-merge copy cannot be rebuilt fairly.
        var old = await AddReviewAsync(db, season, Snapshot(5, "Orbit"), orbit.Id);
        old.Status = "Merged";
        old.ResolvedAnimeId = orbit.Id;
        await db.SaveChangesAsync();

        var report = await new AnimeReviewService(db, cache).ReplayDecisionsAsync(MatchingSettings.Default);

        Assert.Equal(3, report.Total);
        Assert.Equal([merged.Id, createdMovie.Id], report.Agreed.Select(item => item.ReviewId).Order());
        var wrong = Assert.Single(report.WrongMerges);
        Assert.Equal((created.Id, lantern.Id), (wrong.ReviewId, wrong.Suggestion!.AnimeId));
        Assert.Equal(2.0 / 3, report.Precision!.Value, 6);
        Assert.Equal(1, report.PersonMerges);
        Assert.Empty(report.PersonChoiceNotTop);
        Assert.Equal(1, Assert.Single(report.Skipped).Value);
    }

    [Fact]
    public async Task Merge_target_is_scored_as_it_was_before_the_merge()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var frieren = await AddAsync(db, season, "Frieren");
        var cache = CreateCache(db);
        var review = await AddReviewAsync(db, season, Snapshot(1, "Journey's End"), frieren.Id);
        Assert.True(await cache.ResolvePendingReviewAsync(review.Id, "Merge", frieren.Id));
        // The merge gave the row the incoming title, so scored as it is now it would match exactly.
        Assert.Equal("Journey's End", (await db.Animes.AsNoTracking().SingleAsync(item => item.Id == frieren.Id)).Title);

        var report = await new AnimeReviewService(db, cache).ReplayDecisionsAsync(MatchingSettings.Default);

        var item = Assert.Single(report.Cases);
        Assert.Equal(MatchReplayOutcome.Abstained, item.Outcome);
        Assert.Equal("Frieren", item.CandidateById(frieren.Id)!.Title);
        Assert.True(item.PersonChoiceScore < 90);
        Assert.True(item.PersonChoiceIsTop);
    }

    [Fact]
    public void Names_the_decision_added_do_not_count_as_shared()
    {
        var source = new Anime { Title = "Orbit Zero", AlternativeTitles = ["Lantern Saga"] };
        var candidate = new Anime { Id = 7, Title = "Orbit", TitleAliases = [new AnimeTitleAlias { Title = "Lantern Saga" }] };
        // The created row also carries "Lantern Saga", so the catalogue counts it twice.
        var stats = new CatalogueNameStats(CatalogueNameUsage.Count([(7, "Lantern Saga"), (8, "Lantern Saga")]), [], 2);
        var example = new MatchReplayExample(1, source, [candidate], PersonMerged: false, PersonAnimeId: null, NamesAddedByDecision: new HashSet<string> { "lantern saga".Replace(" ", "") });

        var item = MatchReplay.Evaluate(example, MatchingSettings.Default, stats);

        Assert.Equal(MatchingSettings.Default.Scoring.AliasMatch, item.Matches[7].Evidence.Single(evidence => evidence.Signal == "Title").Weight);
    }

    private static async Task<Anime> AddAsync(AnimeDbContext db, Season season, string title)
    {
        var anime = new Anime { Title = title, Type = "TV", Episodes = 12, StartDate = Spring2026, SeasonId = season.Id };
        db.Animes.Add(anime);
        await db.SaveChangesAsync();
        return anime;
    }

    private static AnimeImportSnapshot Snapshot(int aniListId, string title)
        => new() { AniListId = aniListId, Title = title, HasEnglishTitle = true, Type = "TV", Episodes = 12, StartDate = Spring2026 };

    private static async Task<PendingAnimeReview> AddReviewAsync(AnimeDbContext db, Season season, AnimeImportSnapshot snapshot, params int[] candidates)
    {
        var review = new PendingAnimeReview
        {
            SeasonId = season.Id, SourceAniListId = snapshot.AniListId, SourceName = "test",
            PayloadJson = JsonSerializer.Serialize(snapshot), CandidateAnimeIdsJson = JsonSerializer.Serialize(candidates)
        };
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();
        return review;
    }
}
