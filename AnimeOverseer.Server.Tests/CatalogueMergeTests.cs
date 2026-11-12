using System.Text.Json;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class CatalogueMergeTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Merging_moves_everything_on_the_removed_row_to_the_kept_one_and_deletes_it()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var keep = Add(db, season, "Orbital Lantern", aniListId: 100);
        keep.Synopsis = "Kept synopsis";
        var remove = Add(db, season, "Kidou Lantern", malId: 500);
        remove.KitsuId = 9;
        remove.OriginalTitle = "軌道ランタン";
        remove.Synopsis = "Removed synopsis";
        remove.Episodes = 12;
        remove.StartDate = new DateTime(2020, 4, 3);
        var other = Add(db, season, "Unrelated Show", aniListId: 200);
        var collectionBoth = new AnimeCollection { Name = "Both" };
        var collectionRemoved = new AnimeCollection { Name = "Removed only" };
        db.AnimeCollections.AddRange(collectionBoth, collectionRemoved);
        var genreShared = new Genre { Name = "Sci-Fi" };
        var genreRemoved = new Genre { Name = "Drama" };
        var theme = new Theme { Name = "Space" };
        var demographic = new Demographic { Name = "Seinen" };
        db.AddRange(genreShared, genreRemoved, theme, demographic);
        await db.SaveChangesAsync();

        remove.Requests.Add(new Request { Title = "Please add Kidou Lantern" });
        keep.Requests.Add(new Request { Title = "Please add Orbital Lantern" });
        var addedEarly = new DateTime(2025, 1, 1);
        db.AnimeCollectionItems.AddRange(
            new AnimeCollectionItem { CollectionId = collectionBoth.Id, AnimeId = keep.Id, AddedAt = new DateTime(2026, 1, 1) },
            new AnimeCollectionItem { CollectionId = collectionBoth.Id, AnimeId = remove.Id, AddedAt = addedEarly },
            new AnimeCollectionItem { CollectionId = collectionRemoved.Id, AnimeId = remove.Id });
        db.AnimeImages.Add(new AnimeImage { AnimeId = keep.Id, Source = "AniList", Type = "Poster", ImageUrl = "https://img/shared.jpg" });
        db.AnimeImages.AddRange(
            new AnimeImage { AnimeId = remove.Id, Source = "AniList", Type = "Poster", ImageUrl = "https://img/shared.jpg" },
            new AnimeImage { AnimeId = remove.Id, Source = "MyAnimeList", Type = "Poster", ImageUrl = "https://img/mal.jpg", LocalImagePath = "/images/mal.jpg" });
        db.AnimeTitleAliases.AddRange(
            new AnimeTitleAlias { AnimeId = keep.Id, Title = "Orbital Lantern" },
            new AnimeTitleAlias { AnimeId = remove.Id, Title = "OL" },
            new AnimeTitleAlias { AnimeId = remove.Id, Title = "orbital lantern" });
        db.AnimeSourceTitles.AddRange(
            new AnimeSourceTitle { AnimeId = keep.Id, Source = "AniList", Title = "Orbital Lantern" },
            new AnimeSourceTitle { AnimeId = remove.Id, Source = "AniList", Title = "Lantern in Orbit" },
            new AnimeSourceTitle { AnimeId = remove.Id, Source = "MyAnimeList", Title = "Orbital Lantern (MAL)" });
        db.AnimeGenres.AddRange(new AnimeGenre { AnimeId = keep.Id, GenreId = genreShared.Id }, new AnimeGenre { AnimeId = remove.Id, GenreId = genreShared.Id }, new AnimeGenre { AnimeId = remove.Id, GenreId = genreRemoved.Id });
        db.AnimeThemes.Add(new AnimeTheme { AnimeId = remove.Id, ThemeId = theme.Id });
        db.AnimeDemographics.Add(new AnimeDemographic { AnimeId = remove.Id, DemographicId = demographic.Id });
        db.AnimeRecommendations.AddRange(
            new AnimeRecommendation { SourceAnimeId = remove.Id, RecommendedAnimeId = other.Id, Rating = 5 },
            new AnimeRecommendation { SourceAnimeId = other.Id, RecommendedAnimeId = remove.Id, Rating = 7 },
            new AnimeRecommendation { SourceAnimeId = remove.Id, RecommendedAnimeId = keep.Id, Rating = 3 });
        db.AnimeMatchDecisions.Add(new AnimeMatchDecision { SourceMalId = 777, AnimeId = remove.Id, IsSameAnime = true, Origin = "Merge" });
        await db.SaveChangesAsync();
        var movedImageId = await db.AnimeImages.Where(image => image.AnimeId == remove.Id && image.Source == "MyAnimeList").Select(image => image.Id).SingleAsync();
        remove.PreferredImageId = movedImageId;
        var pending = new PendingAnimeReview
        {
            SeasonId = season.Id, SourceName = "test", SourceAniListId = 999, PayloadJson = "{}", Status = "Pending",
            CandidateAnimeIdsJson = JsonSerializer.Serialize(new[] { remove.Id, other.Id }),
            MatchEvidenceJson = JsonSerializer.Serialize(new[] { new CandidateMatch { AnimeId = remove.Id, Score = 70 }, new CandidateMatch { AnimeId = other.Id, Score = 40 } })
        };
        var mergedIntoRemoved = new PendingAnimeReview
        {
            SeasonId = season.Id, SourceName = "test", SourceMalId = 777, PayloadJson = "{}", Status = "Merged", ResolvedAnimeId = remove.Id,
            CandidateAnimeIdsJson = JsonSerializer.Serialize(new[] { remove.Id }), PreMergeSnapshotJson = "{}"
        };
        db.PendingAnimeReviews.AddRange(pending, mergedIntoRemoved);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new AnimeReviewService(db, CreateCache(db)).MergeDuplicateAsync(keep.Id, remove.Id);

        Assert.True(result.Merged, result.Failure);
        Assert.Empty(result.DroppedIds);
        db.ChangeTracker.Clear();
        Assert.False(await db.Animes.AnyAsync(anime => anime.Id == remove.Id));
        var kept = await db.Animes.Include(anime => anime.TitleAliases).Include(anime => anime.Images).Include(anime => anime.SourceTitles)
            .Include(anime => anime.AnimeGenres).Include(anime => anime.AnimeThemes).Include(anime => anime.AnimeDemographics)
            .AsSplitQuery().SingleAsync(anime => anime.Id == keep.Id);

        // Provider IDs fill in; the kept row's own values win and the removed row fills blanks.
        Assert.Equal((100, 500, 9), (kept.AniListId, kept.MALId, kept.KitsuId));
        Assert.Equal(("Orbital Lantern", "Kept synopsis", "軌道ランタン", 12), (kept.Title, kept.Synopsis, kept.OriginalTitle, kept.Episodes));
        Assert.Equal(new DateTime(2020, 4, 3), kept.StartDate);
        // The removed row's names become aliases, without case-only duplicates.
        Assert.Equal(["Kidou Lantern", "OL", "Orbital Lantern", "軌道ランタン"], kept.TitleAliases.Select(alias => alias.Title).Order(StringComparer.Ordinal));
        // Images move without duplicating one the kept row already has, keeping their IDs and the preferred image.
        Assert.Equal(2, kept.Images.Count);
        Assert.Contains(kept.Images, image => image.Id == movedImageId && image.LocalImagePath == "/images/mal.jpg");
        Assert.Equal(movedImageId, kept.PreferredImageId);
        // A source's title the kept row already has stays; a missing source moves across.
        Assert.Equal(new Dictionary<string, string> { ["AniList"] = "Orbital Lantern", ["MyAnimeList"] = "Orbital Lantern (MAL)" },
            kept.SourceTitles.ToDictionary(title => title.Source, title => title.Title));
        Assert.Equal([genreShared.Id, genreRemoved.Id], kept.AnimeGenres.Select(tag => tag.GenreId).Order());
        Assert.Equal(theme.Id, Assert.Single(kept.AnimeThemes).ThemeId);
        Assert.Equal(demographic.Id, Assert.Single(kept.AnimeDemographics).DemographicId);

        Assert.Equal(2, await db.Requests.CountAsync(request => EF.Property<int?>(request, "AnimeId") == keep.Id));
        var items = await db.AnimeCollectionItems.Where(item => item.AnimeId == keep.Id).OrderBy(item => item.CollectionId).ToListAsync();
        Assert.Equal([collectionBoth.Id, collectionRemoved.Id], items.Select(item => item.CollectionId));
        Assert.Equal(addedEarly, items[0].AddedAt);
        Assert.False(await db.AnimeCollectionItems.AnyAsync(item => item.AnimeId == remove.Id));

        var recommendations = await db.AnimeRecommendations.Select(item => new { item.SourceAnimeId, item.RecommendedAnimeId }).ToListAsync();
        Assert.Equal(2, recommendations.Count);
        Assert.Contains(new { SourceAnimeId = keep.Id, RecommendedAnimeId = other.Id }, recommendations);
        Assert.Contains(new { SourceAnimeId = other.Id, RecommendedAnimeId = keep.Id }, recommendations);

        var decision = await db.AnimeMatchDecisions.SingleAsync();
        Assert.Equal((777, keep.Id, true), (decision.SourceMalId, decision.AnimeId, decision.IsSameAnime));

        var reviews = await db.PendingAnimeReviews.OrderBy(review => review.Id).ToListAsync();
        Assert.Equal([keep.Id, other.Id], JsonSerializer.Deserialize<int[]>(reviews[0].CandidateAnimeIdsJson));
        Assert.Equal([keep.Id, other.Id], JsonSerializer.Deserialize<List<CandidateMatch>>(reviews[0].MatchEvidenceJson!)!.Select(match => match.AnimeId));
        Assert.Equal(keep.Id, reviews[1].ResolvedAnimeId);
        Assert.Null(reviews[1].PreMergeSnapshotJson);

        var record = await db.AnimeMergeRecords.SingleAsync();
        Assert.Equal((keep.Id, remove.Id, "Kidou Lantern", (int?)500), (record.KeptAnimeId, record.RemovedAnimeId, record.RemovedTitle, record.RemovedMalId));
        Assert.Contains("1 request", result.Moved);
        Assert.Contains("2 collection entries", result.Moved);
    }

    [Fact]
    public async Task A_pair_sharing_a_provider_id_can_be_merged_from_the_scan_dropping_the_removed_rows_conflicting_ids()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var older = Add(db, season, "Wicked City", aniListId: 300, malId: 500);
        var newer = Add(db, season, "Youjuu Toshi", aniListId: 300, malId: 501);
        await db.SaveChangesAsync();
        // Franchise links are stored by MAL ID, so links to the dropped ID move to the kept one.
        db.CachedAnimeRelations.AddRange(
            new CachedAnimeRelation { RootMalId = 501, RelatedMalId = 900, RelationType = "Sequel", Name = "Sequel" },
            new CachedAnimeRelation { RootMalId = 500, RelatedMalId = 900, RelationType = "Sequel", Name = "Sequel" },
            new CachedAnimeRelation { RootMalId = 901, RelatedMalId = 501, RelationType = "Prequel", Name = "Wicked City" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reviews = new AnimeReviewService(db, CreateCache(db));
        var pair = Assert.Single((await reviews.FindPossibleDuplicatesAsync()).Pairs);
        Assert.NotNull(pair.SharedProviderIds);

        var result = await reviews.MergeDuplicateAsync(pair.Older.Id, pair.Newer.Id);

        Assert.True(result.Merged, result.Failure);
        Assert.Equal(["MAL 501"], result.DroppedIds);
        db.ChangeTracker.Clear();
        var kept = await db.Animes.Include(anime => anime.TitleAliases).SingleAsync();
        Assert.Equal((older.Id, 300, 500), (kept.Id, kept.AniListId, kept.MALId));
        Assert.Contains(kept.TitleAliases, alias => alias.Title == "Youjuu Toshi");
        var relations = await db.CachedAnimeRelations.OrderBy(item => item.RootMalId).Select(item => new { item.RootMalId, item.RelatedMalId }).ToListAsync();
        Assert.Equal([new { RootMalId = 500, RelatedMalId = 900 }, new { RootMalId = 901, RelatedMalId = 500 }], relations);
        Assert.Empty((await reviews.FindPossibleDuplicatesAsync()).Pairs);
        Assert.Equal(newer.Id, (await db.AnimeMergeRecords.SingleAsync()).RemovedAnimeId);
    }

    [Fact]
    public async Task An_english_title_replaces_a_romaji_one_and_a_contradicted_decision_is_dropped()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var keep = Add(db, season, "Hoshi no Umi", aniListId: 10);
        var remove = Add(db, season, "Sea of Stars", malId: 20);
        remove.HasEnglishTitle = true;
        await db.SaveChangesAsync();
        db.AnimeMatchDecisions.AddRange(
            // Said when an import of MAL 20 was merged into the other row; the kept row now is MAL 20.
            new AnimeMatchDecision { SourceMalId = 20, AnimeId = keep.Id, IsSameAnime = false, Origin = "Merge", DecidedAt = new DateTime(2026, 1, 1) },
            // Both rows have an answer for AniList 30; the newer one wins.
            new AnimeMatchDecision { SourceAniListId = 30, AnimeId = keep.Id, IsSameAnime = false, Origin = "Create", DecidedAt = new DateTime(2026, 2, 1) },
            new AnimeMatchDecision { SourceAniListId = 30, AnimeId = remove.Id, IsSameAnime = true, Origin = "Merge", DecidedAt = new DateTime(2026, 3, 1) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True((await new AnimeReviewService(db, CreateCache(db)).MergeDuplicateAsync(keep.Id, remove.Id)).Merged);

        db.ChangeTracker.Clear();
        var kept = await db.Animes.Include(anime => anime.TitleAliases).SingleAsync();
        Assert.Equal(("Sea of Stars", true), (kept.Title, kept.HasEnglishTitle));
        Assert.Contains(kept.TitleAliases, alias => alias.Title == "Hoshi no Umi");
        var decision = await db.AnimeMatchDecisions.SingleAsync();
        Assert.Equal((30, keep.Id, true), (decision.SourceAniListId, decision.AnimeId, decision.IsSameAnime));
    }

    [Fact]
    public async Task A_person_chosen_title_on_the_kept_row_is_not_replaced()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var keep = Add(db, season, "My Chosen Name", aniListId: 10);
        keep.NamesReviewed = true;
        var remove = Add(db, season, "Provider English Name", malId: 20);
        remove.HasEnglishTitle = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True((await new AnimeReviewService(db, CreateCache(db)).MergeDuplicateAsync(keep.Id, remove.Id)).Merged);

        db.ChangeTracker.Clear();
        Assert.Equal("My Chosen Name", (await db.Animes.SingleAsync()).Title);
    }

    [Fact]
    public async Task An_automatic_decision_on_either_row_before_the_merge_can_no_longer_be_undone()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var keep = Add(db, season, "Paper Comet", aniListId: 10);
        var created = Add(db, season, "Paper Comet", malId: 20);
        await db.SaveChangesAsync();
        var review = new PendingAnimeReview
        {
            SeasonId = season.Id, SourceName = "test", SourceMalId = 20, PayloadJson = "{}", CandidateAnimeIdsJson = "[]",
            Status = "Created", AutoResolved = true, ResolvedAnimeId = created.Id, ResolvedAt = DateTime.UtcNow.AddDays(-1)
        };
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var cache = CreateCache(db);

        Assert.True((await new AnimeReviewService(db, cache).MergeDuplicateAsync(keep.Id, created.Id)).Merged);
        db.ChangeTracker.Clear();
        var failure = await cache.UndoAutomaticResolutionAsync(review.Id);

        // Undoing the create would otherwise delete the kept row, which now holds both records.
        Assert.NotNull(failure);
        Assert.Contains("merged with a duplicate", failure);
        db.ChangeTracker.Clear();
        Assert.True(await db.Animes.AnyAsync(anime => anime.Id == keep.Id));
        Assert.Equal("Created", (await db.PendingAnimeReviews.SingleAsync()).Status);
    }

    [Fact]
    public async Task Merging_a_row_into_itself_or_a_missing_row_changes_nothing()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var only = Add(db, season, "Lonely Planet", aniListId: 1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        Assert.False((await reviews.MergeDuplicateAsync(only.Id, only.Id)).Merged);
        Assert.False((await reviews.MergeDuplicateAsync(only.Id, only.Id + 1)).Merged);
        Assert.False((await reviews.MergeDuplicateAsync(only.Id + 1, only.Id)).Merged);
        Assert.True(await db.Animes.AnyAsync(anime => anime.Id == only.Id));
        Assert.False(await db.AnimeMergeRecords.AnyAsync());
    }

    [Fact]
    public async Task Usage_counts_requests_and_collections_per_row()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var used = Add(db, season, "Used", aniListId: 1);
        var unused = Add(db, season, "Unused", malId: 2);
        var collection = new AnimeCollection { Name = "Favourites" };
        db.AnimeCollections.Add(collection);
        used.Requests.Add(new Request { Title = "one" });
        used.Requests.Add(new Request { Title = "two" });
        await db.SaveChangesAsync();
        db.AnimeCollectionItems.Add(new AnimeCollectionItem { CollectionId = collection.Id, AnimeId = used.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var usage = await new AnimeReviewService(db, CreateCache(db)).GetDuplicateUsageAsync([used.Id, unused.Id]);

        Assert.Equal(new DuplicateRowUsage(2, 1), usage[used.Id]);
        Assert.Equal(new DuplicateRowUsage(0, 0), usage[unused.Id]);
    }

    private static Anime Add(AnimeDbContext db, Season season, string title, int? aniListId = null, int? malId = null)
    {
        var anime = new Anime { Title = title, AniListId = aniListId, MALId = malId, Type = "TV", SeasonId = season.Id };
        db.Animes.Add(anime);
        return anime;
    }
}
