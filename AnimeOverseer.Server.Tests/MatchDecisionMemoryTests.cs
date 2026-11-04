using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class MatchDecisionMemoryTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Creating_as_new_stops_later_imports_matching_that_candidate()
    {
        await using var db = CreateDb();
        var candidate = await AddAsync(db, "Orbit");
        await ImportAsync(db, Incoming(82, "Orbit", true));
        var review = await db.PendingAnimeReviews.AsNoTracking().SingleAsync();
        Assert.True(await CreateCache(db).ResolvePendingReviewAsync(review.Id, "Create"));

        var decision = await db.AnimeMatchDecisions.AsNoTracking().SingleAsync();
        Assert.False(decision.IsSameAnime);
        Assert.Equal(candidate.Id, decision.AnimeId);
        Assert.Equal(82, decision.SourceAniListId);

        // A later import of a rejected pair skips review and is created as new.
        var lantern = await AddAsync(db, "Lantern");
        db.AnimeMatchDecisions.Add(new AnimeMatchDecision { SourceAniListId = 90, AnimeId = lantern.Id, IsSameAnime = false, Origin = "Create" });
        await db.SaveChangesAsync();
        await ImportAsync(db, Incoming(90, "Lantern", true));

        Assert.False(await db.PendingAnimeReviews.AnyAsync(item => item.SourceAniListId == 90));
        Assert.Equal(2, await db.Animes.CountAsync(anime => anime.Title == "Lantern"));
    }

    [Fact]
    public async Task Merging_remembers_the_chosen_row_and_rejects_the_others()
    {
        await using var db = CreateDb();
        var chosen = await AddAsync(db, "Orbit");
        var other = await AddAsync(db, "Orbit");
        var review = AddReview(chosen.SeasonId, Incoming(82, "Orbit", true), [chosen.Id]);
        review.CandidateAnimeIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { chosen.Id, other.Id });
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();

        Assert.True(await CreateCache(db).ResolvePendingReviewAsync(review.Id, "Merge", chosen.Id));

        var decisions = await db.AnimeMatchDecisions.AsNoTracking().OrderBy(item => item.AnimeId).ToListAsync();
        Assert.Equal([(chosen.Id, true), (other.Id, false)], decisions.Select(item => (item.AnimeId, item.IsSameAnime)));
    }

    [Fact]
    public async Task Remembered_same_answer_routes_a_later_import_without_review()
    {
        await using var db = CreateDb();
        var chosen = await AddAsync(db, "Orbit");
        chosen.AniListId = 900;
        await db.SaveChangesAsync();
        db.AnimeMatchDecisions.Add(new AnimeMatchDecision { SourceMalId = 500, AnimeId = chosen.Id, IsSameAnime = true, Origin = "Merge" });
        await db.SaveChangesAsync();

        // Different AniList ID on the row would normally rule it out; the person said otherwise.
        var incoming = new Anime { MALId = 500, AniListId = 901, Title = "Orbit" };
        await ImportAsync(db, incoming);

        Assert.Empty(await db.PendingAnimeReviews.ToListAsync());
        Assert.Equal(500, (await db.Animes.AsNoTracking().SingleAsync()).MALId);
    }

    [Fact]
    public async Task Undoing_an_automatic_merge_remembers_the_pair_as_different()
    {
        await using var db = CreateDb();
        var existing = await AddAsync(db, "Orbit", type: "TV", episodes: 12);
        await MatchingSettings.SaveAsync(db, new MatchingSettings(AutoDecisionMode.Apply, 90));
        var incoming = Incoming(82, "Orbit", true);
        incoming.Type = "TV";
        incoming.Episodes = 12;
        incoming.StartDate = existing.StartDate;
        await ImportAsync(db, incoming);
        var review = await db.PendingAnimeReviews.AsNoTracking().SingleAsync();
        Assert.Equal("Merged", review.Status);

        await using var undoDb = CreateDb();
        Assert.Null(await CreateCache(undoDb).UndoAutomaticResolutionAsync(review.Id));

        var decision = await undoDb.AnimeMatchDecisions.AsNoTracking().SingleAsync();
        Assert.Equal((existing.Id, false, "Undo"), (decision.AnimeId, decision.IsSameAnime, decision.Origin));
        // With its only candidate rejected, a recheck would create it; the undo keeps it for the person.
        Assert.Equal(PendingReviewRecheckOutcome.Unchanged, await CreateCache(undoDb).RecheckPendingReviewAsync(review.Id));
        Assert.Equal("Pending", (await undoDb.PendingAnimeReviews.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Deferred_review_is_snoozed_and_then_reopened_by_a_later_import()
    {
        await using var db = CreateDb();
        await AddAsync(db, "Orbit");
        await MatchingSettings.SaveAsync(db, new MatchingSettings(AutoDecisionMode.Suggest, 90, 14));
        await ImportAsync(db, Incoming(82, "Orbit", true));
        var review = await db.PendingAnimeReviews.AsNoTracking().SingleAsync();
        Assert.True(await CreateCache(db).ResolvePendingReviewAsync(review.Id, "Defer"));

        var deferred = await db.PendingAnimeReviews.AsNoTracking().SingleAsync();
        Assert.Equal("Deferred", deferred.Status);
        Assert.InRange((deferred.DeferredUntil!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);
        Assert.Empty(await db.AnimeMatchDecisions.ToListAsync());

        await ImportAsync(db, Incoming(82, "Orbit", true));
        Assert.Equal("Deferred", (await db.PendingAnimeReviews.AsNoTracking().SingleAsync()).Status);

        await db.PendingAnimeReviews.ExecuteUpdateAsync(setter => setter.SetProperty(item => item.DeferredUntil, DateTime.UtcNow.AddMinutes(-1)));
        await using var later = CreateDb();
        await ImportAsync(later, Incoming(82, "Orbit", true));

        var reopened = await later.PendingAnimeReviews.AsNoTracking().SingleAsync();
        Assert.Equal((review.Id, "Pending"), (reopened.Id, reopened.Status));
        Assert.Null(reopened.DeferredUntil);
        Assert.Contains("reopened after being deferred", reopened.Reason);
    }

    [Fact]
    public async Task Forgetting_a_decision_lets_title_matching_ask_again()
    {
        await using var db = CreateDb();
        var candidate = await AddAsync(db, "Orbit");
        db.AnimeMatchDecisions.Add(new AnimeMatchDecision { SourceAniListId = 82, AnimeId = candidate.Id, IsSameAnime = false, Origin = "Create" });
        await db.SaveChangesAsync();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        var remembered = Assert.Single(await reviews.GetRememberedDecisionsAsync());
        Assert.True(await reviews.ForgetDecisionAsync(remembered.Decision.Id));
        await ImportAsync(db, Incoming(82, "Orbit", true));

        Assert.Single(await db.PendingAnimeReviews.ToListAsync());
    }

    private static async Task<Anime> AddAsync(AnimeDbContext db, string title, string? type = null, int? episodes = null)
    {
        var season = await db.Seasons.FirstOrDefaultAsync() ?? await AddSeasonAsync(db);
        var anime = new Anime { Title = title, Type = type, Episodes = episodes, StartDate = new DateTime(2026, 4, 1), SeasonId = season.Id };
        db.Animes.Add(anime);
        await db.SaveChangesAsync();
        return anime;
    }

    private static Task<List<int>> ImportAsync(AnimeDbContext db, Anime incoming)
        => CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));
}
