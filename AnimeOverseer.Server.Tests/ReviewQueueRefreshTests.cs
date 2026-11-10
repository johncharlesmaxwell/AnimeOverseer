using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class ReviewQueueRefreshTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Name_review_list_is_names_only_and_a_page_loads_the_full_records()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var anime = new Anime { AniListId = 1, Title = "Romaji Name", OriginalTitle = "Original Name", SeasonId = season.Id, ImageUrl = "https://example.test/a.jpg" };
        anime.TitleAliases.Add(new AnimeTitleAlias { Title = "Alias Name" });
        anime.SourceTitles.Add(new AnimeSourceTitle { Source = "AniList", Title = "English Name" });
        db.Animes.Add(anime);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        var item = Assert.Single(await reviews.GetNamesPendingReviewAsync());
        Assert.Equal(["Original Name", "Alias Name"], item.Names);
        Assert.Equal("English Name", Assert.Single(item.EnglishNames).Title);
        Assert.Null(item.Anime.Season);

        var page = Assert.Single(await reviews.LoadNameReviewPageAsync([item]));
        Assert.Equal((2026, "spring"), (page.Anime.Season.Year, page.Anime.Season.Name));
        Assert.Equal("https://example.test/a.jpg", page.Anime.DisplayImageUrl);
        Assert.Same(item.Names, page.Names);
    }

    [Fact]
    public async Task A_name_page_leaves_out_records_that_have_gone()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.AddRange(new Anime { Title = "Kept", OriginalTitle = "Kept Original", SeasonId = season.Id }, new Anime { Title = "Gone", OriginalTitle = "Gone Original", SeasonId = season.Id });
        await db.SaveChangesAsync();
        var reviews = new AnimeReviewService(db, CreateCache(db));
        var items = await reviews.GetNamesPendingReviewAsync();

        await db.Animes.Where(anime => anime.Title == "Gone").ExecuteDeleteAsync();

        Assert.Equal(["Kept"], (await reviews.LoadNameReviewPageAsync(items)).Select(item => item.Anime.Title));
    }

    [Fact]
    public async Task Rescoring_returns_only_the_named_reviews_that_are_still_pending()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var orbit = new Anime { Title = "Orbit", SeasonId = season.Id };
        var lantern = new Anime { Title = "Lantern", SeasonId = season.Id };
        db.Animes.AddRange(orbit, lantern);
        await db.SaveChangesAsync();
        var first = AddReview(season.Id, Incoming(10, "Orbit", true), [orbit.Id]);
        var second = AddReview(season.Id, Incoming(11, "Lantern", true), [lantern.Id]);
        var third = AddReview(season.Id, Incoming(12, "Orbit", true), [orbit.Id]);
        db.PendingAnimeReviews.AddRange(first, second, third);
        await db.SaveChangesAsync();
        var reviews = new AnimeReviewService(db, CreateCache(db));
        var queue = await reviews.GetPendingAsync();
        Assert.Equal(3, queue.Count);

        Assert.True(await reviews.ResolveOneAsync(first.Id, "Defer"));
        var fresh = await reviews.RescorePendingAsync([first.Id, third.Id]);

        var only = Assert.Single(fresh);
        Assert.Equal(third.Id, only.Review.Id);
        Assert.Equal(orbit.Id, Assert.Single(only.Candidates).Id);
        Assert.NotNull(only.BestMatch);
        Assert.Empty(await reviews.RescorePendingAsync([]));
    }

    [Fact]
    public async Task Queue_order_puts_the_best_match_first_and_reviews_without_a_candidate_last()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var orbit = new Anime { Title = "Orbit Station", SeasonId = season.Id };
        db.Animes.Add(orbit);
        await db.SaveChangesAsync();
        db.PendingAnimeReviews.AddRange(
            AddReview(season.Id, Incoming(20, "Orbit", true), [orbit.Id]),
            AddReview(season.Id, Incoming(21, "Orbit Station", true), [orbit.Id]),
            AddReview(season.Id, Incoming(22, "Nothing", true), []));
        await db.SaveChangesAsync();
        var queue = await new AnimeReviewService(db, CreateCache(db)).GetPendingAsync();

        Assert.Equal(queue, AnimeReviewService.InQueueOrder(queue.AsEnumerable().Reverse()));
        Assert.Equal("Orbit Station", queue[0].Incoming.Title);
        Assert.Null(queue[^1].BestMatch);
    }
}
