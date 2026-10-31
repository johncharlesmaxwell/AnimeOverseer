using System.Net;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class ImportIntegrationTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Catalogue_card_query_loads_only_the_preferred_artwork()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var anime = new Anime { Title = "Orbit", SeasonId = season.Id, StartDate = DateTime.UtcNow };
        db.Animes.Add(anime);
        await db.SaveChangesAsync();
        var preferred = new AnimeImage { AnimeId = anime.Id, Source = "AniList", Type = "Poster", ImageUrl = "https://images.test/preferred.jpg" };
        db.AnimeImages.AddRange(preferred, new AnimeImage { AnimeId = anime.Id, Source = "AniList", Type = "Banner", ImageUrl = "https://images.test/banner.jpg" });
        await db.SaveChangesAsync();
        anime.PreferredImageId = preferred.Id;
        await db.SaveChangesAsync();

        var cards = await CreateCache(db).GetRecentAsync(0, 10);

        var card = Assert.Single(cards);
        Assert.Single(card.Images);
        Assert.Equal(preferred.Id, card.Images[0].Id);
    }

    [Fact]
    public async Task Import_reuses_provider_identity_and_deduplicates_aliases_and_images()
    {
        await using var db = CreateDb();
        var cache = CreateCache(db);
        var source = Incoming(101, "Orbit", true, aliases: ["Orbit!"], images: [Image("AniList", "Poster", "https://images.test/orbit.jpg")]);
        await cache.FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { source }));
        source.AlternativeTitles.Add("Orbit");
        await cache.FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { source }));
        Assert.Equal(1, await db.Animes.CountAsync());
        Assert.Equal(1, await db.AnimeImages.CountAsync());
        Assert.Equal(2, await db.AnimeTitleAliases.CountAsync());
    }

    [Fact]
    public async Task Ambiguous_alias_match_creates_one_pending_review_without_a_duplicate_anime()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        // Rows without provider IDs cannot be confirmed or ruled out by ID.
        db.Animes.AddRange(new Anime { Title = "First", SeasonId = season.Id, TitleAliases = [new() { Title = "Shared Name" }] }, new Anime { Title = "Second", SeasonId = season.Id, TitleAliases = [new() { Title = "Shared Name" }] });
        await db.SaveChangesAsync();
        var incoming = Incoming(999, "Shared Name", false);
        await CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["summer"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));
        await CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["summer"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));
        var review = await db.PendingAnimeReviews.SingleAsync();
        Assert.Equal("Pending", review.Status);
        Assert.Equal(2, await db.Animes.CountAsync());
        Assert.Contains("title/alias", review.Reason);
    }

    [Fact]
    public async Task Normalized_single_title_match_is_staged_for_review_with_its_only_candidate()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var existing = new Anime { Title = "Café: The Show II", SeasonId = season.Id };
        db.Animes.Add(existing);
        await db.SaveChangesAsync();
        await CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["fall"], (_, _, _) => Task.FromResult(new List<Anime> { Incoming(82, "Cafe The Show 2", false) }));
        var review = await db.PendingAnimeReviews.SingleAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(new[] { existing.Id }), review.CandidateAnimeIdsJson);
        Assert.Equal(1, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Staged_review_stores_the_match_score_and_evidence()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { Title = "Orbit", Type = "TV", Episodes = 12, StartDate = new DateTime(2026, 4, 1), SeasonId = season.Id });
        await db.SaveChangesAsync();
        var incoming = Incoming(82, "Orbit", true);
        incoming.Type = "TV";
        incoming.Episodes = 12;
        incoming.StartDate = new DateTime(2026, 4, 8);

        await CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));

        var review = await db.PendingAnimeReviews.SingleAsync();
        Assert.NotNull(review.MatchScore);
        var match = Assert.Single(System.Text.Json.JsonSerializer.Deserialize<List<AnimeOverseer.Server.Services.CandidateMatch>>(review.MatchEvidenceJson!)!);
        Assert.Equal(review.MatchScore, match.Score);
        Assert.Equal(100, match.Score);
        Assert.Equal("High", AnimeOverseer.Server.Services.MatchScorer.Confidence(match));
        Assert.Contains("\"Positive\"", review.MatchEvidenceJson);
    }

    [Fact]
    public async Task Title_match_with_a_conflicting_mal_id_is_not_staged_for_review()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { AniListId = 81, MALId = 1001, Title = "Same Title", SeasonId = season.Id });
        await db.SaveChangesAsync();
        var incoming = Incoming(82, "Same Title", false);
        incoming.MALId = 2002;

        await CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["fall"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));

        Assert.Empty(await db.PendingAnimeReviews.ToListAsync());
        Assert.Equal(2, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Reimport_replaces_tags_without_duplicate_join_rows()
    {
        await using var db = CreateDb();
        var cache = CreateCache(db);
        var first = Incoming(111, "Tagged", true);
        first.AnimeGenres = [new AnimeGenre { Genre = new Genre { Name = "Action" } }];
        first.AnimeThemes = [new AnimeTheme { Theme = new Theme { Name = "School" } }];
        var replacement = Incoming(111, "Tagged", true);
        replacement.AnimeGenres = [new AnimeGenre { Genre = new Genre { Name = "Drama" } }];
        replacement.AnimeThemes = [new AnimeTheme { Theme = new Theme { Name = "School" } }];
        await cache.FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { first }));
        await cache.FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { replacement }));
        var anime = await db.Animes.Include(x => x.AnimeGenres).ThenInclude(x => x.Genre).Include(x => x.AnimeThemes).ThenInclude(x => x.Theme).SingleAsync();
        Assert.Equal(["Drama"], anime.AnimeGenres.Select(x => x.Genre.Name));
        Assert.Equal(["School"], anime.AnimeThemes.Select(x => x.Theme.Name));
    }

    [Fact]
    public async Task Failed_later_season_leaves_already_persisted_anime_intact_without_duplicates()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { AniListId = 77, Title = "Existing English", HasEnglishTitle = true, SeasonId = season.Id });
        await db.SaveChangesAsync();
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["spring", "summer"], (_, _, _) =>
        {
            calls++;
            return calls == 1 ? Task.FromResult(new List<Anime> { Incoming(77, "Romaji Replacement", false) }) : Task.FromException<List<Anime>>(new HttpRequestException("second season failed"));
        }));
        var saved = await db.Animes.SingleAsync();
        Assert.Equal("Existing English", saved.Title);
        Assert.Equal(1, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Failed_image_download_keeps_catalogue_and_image_metadata()
    {
        await using var db = CreateDb();
        await CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { Incoming(121, "Artwork Failure", true, images: [Image("AniList", "Poster", "https://images.test/unavailable.jpg")]) }));
        var image = await db.AnimeImages.SingleAsync();
        Assert.Equal("https://images.test/unavailable.jpg", image.ImageUrl);
        Assert.Null(image.LocalImagePath);
        Assert.Equal(1, await db.Animes.CountAsync());
    }
}
