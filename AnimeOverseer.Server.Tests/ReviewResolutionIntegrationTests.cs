using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class ReviewResolutionIntegrationTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Selecting_a_name_removes_it_from_the_name_review_queue()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var anime = new Anime { AniListId = 1, Title = "Romaji Name", OriginalTitle = "English Name", SeasonId = season.Id };
        db.Animes.Add(anime);
        await db.SaveChangesAsync();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        Assert.Equal(["English Name"], (await reviews.GetNamesPendingReviewAsync()).Single().Names);
        Assert.True(await reviews.SetMainEnglishTitleAsync(anime.Id, "English Name"));
        Assert.Empty(await reviews.GetNamesPendingReviewAsync());
    }

    [Fact]
    public async Task Merge_review_keeps_english_primary_title_and_collects_incoming_aliases_and_images()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var existing = new Anime { AniListId = 10, Title = "English Primary", HasEnglishTitle = true, SeasonId = season.Id, TitleAliases = [new() { Title = "Old Alias" }] };
        db.Animes.Add(existing);
        await db.SaveChangesAsync();
        var review = AddReview(season.Id, Incoming(20, "Romaji Name", false, aliases: ["Japanese Name"], images: [Image("MAL", "Poster", "https://images.test/merge.jpg")]), [existing.Id]);
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();
        Assert.True(await CreateCache(db).ResolvePendingReviewAsync(review.Id, "Merge", existing.Id));
        var merged = await db.Animes.Include(x => x.TitleAliases).Include(x => x.Images).SingleAsync();
        Assert.Equal("English Primary", merged.Title);
        Assert.Equal(10, merged.AniListId);
        Assert.Contains(merged.TitleAliases, x => x.Title == "Romaji Name");
        Assert.Contains(merged.TitleAliases, x => x.Title == "Japanese Name");
        Assert.Single(merged.Images);
    }

    [Fact]
    public async Task Create_review_persists_a_new_anime_with_its_aliases_and_images()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var review = AddReview(season.Id, Incoming(30, "New English", true, aliases: ["New Native"], images: [Image("AniList", "Banner", "https://images.test/new.jpg")]), []);
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();
        Assert.True(await CreateCache(db).ResolvePendingReviewAsync(review.Id, "Create"));
        var created = await db.Animes.Include(x => x.TitleAliases).Include(x => x.Images).SingleAsync();
        Assert.Equal("New English", created.Title);
        Assert.Equal(30, created.AniListId);
        Assert.Contains(created.TitleAliases, x => x.Title == "New Native");
        Assert.Single(created.Images);
    }

    [Fact]
    public async Task Review_cannot_merge_an_unlisted_candidate_and_cannot_be_resolved_twice()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var candidate = new Anime { AniListId = 91, Title = "Candidate", SeasonId = season.Id };
        var other = new Anime { AniListId = 92, Title = "Other", SeasonId = season.Id };
        db.Animes.AddRange(candidate, other);
        await db.SaveChangesAsync();
        var review = AddReview(season.Id, Incoming(93, "Incoming", false), [candidate.Id]);
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();
        var cache = CreateCache(db);
        Assert.False(await cache.ResolvePendingReviewAsync(review.Id, "Merge", other.Id));
        Assert.True(await cache.ResolvePendingReviewAsync(review.Id, "Defer"));
        Assert.False(await cache.ResolvePendingReviewAsync(review.Id, "Create"));
        Assert.Equal("Deferred", (await db.PendingAnimeReviews.SingleAsync()).Status);
    }

    [Fact]
    public async Task Merge_keeps_the_selected_preferred_image_when_new_artwork_is_added()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var existing = new Anime { AniListId = 101, Title = "Primary", SeasonId = season.Id };
        db.Animes.Add(existing);
        await db.SaveChangesAsync();
        var preferred = new AnimeImage { AnimeId = existing.Id, Source = "AniList", Type = "Poster", ImageUrl = "https://images.test/old.jpg" };
        db.AnimeImages.Add(preferred);
        await db.SaveChangesAsync();
        existing.PreferredImageId = preferred.Id;
        await db.SaveChangesAsync();
        var review = AddReview(season.Id, Incoming(102, "Alternate", false, images: [Image("MAL", "Poster", "https://images.test/new.jpg")]), [existing.Id]);
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();
        Assert.True(await CreateCache(db).ResolvePendingReviewAsync(review.Id, "Merge", existing.Id));
        var merged = await db.Animes.Include(x => x.Images).SingleAsync();
        Assert.Equal(preferred.Id, merged.PreferredImageId);
        Assert.Equal(2, merged.Images.Count);
    }

    [Fact]
    public async Task Pending_reviews_without_stored_scores_are_scored_on_read_and_sorted_by_confidence()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var strong = new Anime { Title = "Orbit", Type = "TV", SeasonId = season.Id };
        var weak = new Anime { Title = "Lantern", Type = "Movie", SeasonId = season.Id };
        db.Animes.AddRange(strong, weak);
        await db.SaveChangesAsync();
        var weakReview = AddReview(season.Id, Incoming(11, "Lantern", true), [weak.Id]);
        weakReview.CreatedAt = DateTime.UtcNow;
        var strongReview = AddReview(season.Id, Incoming(12, "Orbit", true), [strong.Id]);
        strongReview.CreatedAt = DateTime.UtcNow.AddDays(-1);
        db.PendingAnimeReviews.AddRange(weakReview, strongReview);
        await db.SaveChangesAsync();
        // Snapshots built by AddReview carry no format, so make the weak one disagree.
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<AnimeImportSnapshot>(weakReview.PayloadJson)!;
        snapshot.Type = "TV";
        weakReview.PayloadJson = System.Text.Json.JsonSerializer.Serialize(snapshot);
        await db.SaveChangesAsync();

        var items = await new AnimeReviewService(db, CreateCache(db)).GetPendingAsync();

        Assert.Equal([strongReview.Id, weakReview.Id], items.Select(item => item.Review.Id));
        Assert.Equal("Conflict", MatchScorer.Confidence(items[1].BestMatch!));
        Assert.Null((await db.PendingAnimeReviews.FindAsync(strongReview.Id))!.MatchScore);
    }
}
