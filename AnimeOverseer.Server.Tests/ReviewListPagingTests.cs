using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class ReviewListPagingTests : SqliteIntegrationTestBase
{
    private static ReviewListQuery Query(string? search = null, string? show = null, string? show2 = null, string? sort = null, int page = 1, int pageSize = 24)
        => new(search, show, show2, sort, page, pageSize);

    [Fact]
    public async Task Auto_resolved_page_searches_filters_and_sorts_in_sql()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var orbit = await AddAnimeAsync(db, season, "Orbit Catalogue");
        AddAuto(db, season, "Lantern", "Created", minutesAgo: 30);
        AddAuto(db, season, "orbit station", "Merged", minutesAgo: 20, resolvedId: orbit.Id);
        AddAuto(db, season, "Bramble", "Merged", minutesAgo: 10);
        db.PendingAnimeReviews.Add(new PendingAnimeReview { SeasonId = season.Id, Status = "Merged", SourceTitle = "Made by a person", PayloadJson = "{}" });
        await db.SaveChangesAsync();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        var newest = await reviews.GetAutoResolvedPageAsync(Query());
        Assert.Equal(["Bramble", "orbit station", "Lantern"], newest.Items.Select(item => item.Incoming.Title));
        Assert.Equal(3, newest.Matching);
        // The undo snapshot is never read for the list.
        Assert.All(newest.Items, item => Assert.Null(item.Review.PreMergeSnapshotJson));

        var byTitle = await reviews.GetAutoResolvedPageAsync(Query(sort: "title"));
        Assert.Equal(["Bramble", "Lantern", "orbit station"], byTitle.Items.Select(item => item.Review.SourceTitle));

        var merged = await reviews.GetAutoResolvedPageAsync(Query(show: "merged"));
        Assert.Equal(["Bramble", "orbit station"], merged.Items.Select(item => item.Review.SourceTitle));

        // Searches the resolved catalogue title as well as the incoming one, case-insensitively.
        var catalogue = await reviews.GetAutoResolvedPageAsync(Query(search: "CATALOGUE"));
        Assert.Equal("orbit station", Assert.Single(catalogue.Items).Review.SourceTitle);
        Assert.Equal(orbit.Id, catalogue.Items[0].Resolved?.Id);
    }

    [Fact]
    public async Task Pages_are_clamped_to_the_matching_rows()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        for (var index = 0; index < 5; index++) AddAuto(db, season, $"Title {index}", "Created", minutesAgo: index);
        await db.SaveChangesAsync();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        var second = await reviews.GetAutoResolvedPageAsync(Query(page: 2, pageSize: 2));
        Assert.Equal((2, 5), (second.Page, second.Matching));
        Assert.Equal(["Title 2", "Title 3"], second.Items.Select(item => item.Review.SourceTitle));

        var pastEnd = await reviews.GetAutoResolvedPageAsync(Query(page: 9, pageSize: 2));
        Assert.Equal(3, pastEnd.Page);
        Assert.Equal("Title 4", Assert.Single(pastEnd.Items).Review.SourceTitle);
    }

    [Fact]
    public async Task Search_words_must_all_match_and_wildcards_are_literal()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        AddAuto(db, season, "Orbit 100% Season", "Created", minutesAgo: 1);
        AddAuto(db, season, "Orbit 1000 Nights", "Created", minutesAgo: 2);
        AddAuto(db, season, "Lantern_Two", "Created", minutesAgo: 3);
        AddAuto(db, season, "LanternXTwo", "Created", minutesAgo: 4);
        await db.SaveChangesAsync();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        Assert.Equal("Orbit 100% Season", Assert.Single((await reviews.GetAutoResolvedPageAsync(Query(search: "100%"))).Items).Review.SourceTitle);
        Assert.Equal("Lantern_Two", Assert.Single((await reviews.GetAutoResolvedPageAsync(Query(search: "n_t"))).Items).Review.SourceTitle);
        Assert.Equal("Orbit 1000 Nights", Assert.Single((await reviews.GetAutoResolvedPageAsync(Query(search: "nights orbit"))).Items).Review.SourceTitle);
    }

    [Fact]
    public async Task Remembered_page_searches_both_titles_and_filters_by_answer_and_origin()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var orbit = await AddAnimeAsync(db, season, "Orbit");
        var lantern = await AddAnimeAsync(db, season, "Lantern");
        db.AnimeMatchDecisions.AddRange(
            new AnimeMatchDecision { SourceAniListId = 1, AnimeId = orbit.Id, IsSameAnime = true, Origin = "Merge", SourceTitle = "Orbit (TV)", DecidedAt = DateTime.UtcNow.AddMinutes(-3) },
            new AnimeMatchDecision { SourceAniListId = 2, AnimeId = orbit.Id, IsSameAnime = false, Origin = "Create", SourceTitle = "Orbit Movie", DecidedAt = DateTime.UtcNow.AddMinutes(-2) },
            new AnimeMatchDecision { SourceAniListId = 3, AnimeId = lantern.Id, IsSameAnime = false, Origin = "Undo", SourceTitle = "Bramble", DecidedAt = DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        Assert.Equal(["Bramble", "Orbit Movie", "Orbit (TV)"], (await reviews.GetRememberedDecisionsPageAsync(Query())).Items.Select(item => item.SourceTitle));
        Assert.Equal(["Lantern", "Orbit", "Orbit"], (await reviews.GetRememberedDecisionsPageAsync(Query(sort: "title"))).Items.Select(item => item.AnimeTitle));
        Assert.Equal("Orbit (TV)", Assert.Single((await reviews.GetRememberedDecisionsPageAsync(Query(show: "same"))).Items).SourceTitle);
        Assert.Equal("Orbit Movie", Assert.Single((await reviews.GetRememberedDecisionsPageAsync(Query(show: "different", show2: "create"))).Items).SourceTitle);
        // "lantern" is only the catalogue title, "bramble" only the source title.
        Assert.Single((await reviews.GetRememberedDecisionsPageAsync(Query(search: "lantern"))).Items);
        Assert.Single((await reviews.GetRememberedDecisionsPageAsync(Query(search: "bramble"))).Items);
    }

    [Fact]
    public async Task Queued_reviews_and_remembered_answers_keep_the_source_title()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        await AddAnimeAsync(db, season, "Orbit");
        await CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { Incoming(82, "Orbit", true) }));
        var review = await db.PendingAnimeReviews.AsNoTracking().SingleAsync();
        Assert.Equal("Orbit", review.SourceTitle);

        Assert.True(await CreateCache(db).ResolvePendingReviewAsync(review.Id, "Create"));
        Assert.Equal("Orbit", (await db.AnimeMatchDecisions.AsNoTracking().SingleAsync()).SourceTitle);
    }

    private static void AddAuto(AnimeDbContext db, Season season, string title, string status, int minutesAgo, int? resolvedId = null)
    {
        var review = new PendingAnimeReview
        {
            SeasonId = season.Id, Status = status, AutoResolved = true, ResolvedAnimeId = resolvedId, Reason = "test",
            ResolvedAt = DateTime.UtcNow.AddMinutes(-minutesAgo), PreMergeSnapshotJson = "{\"large\":true}"
        };
        review.SetPayload(new AnimeImportSnapshot { Title = title });
        db.PendingAnimeReviews.Add(review);
    }

    private static async Task<Anime> AddAnimeAsync(AnimeDbContext db, Season season, string title)
    {
        var anime = new Anime { Title = title, SeasonId = season.Id, StartDate = new DateTime(2026, 4, 1) };
        db.Animes.Add(anime);
        await db.SaveChangesAsync();
        return anime;
    }
}
