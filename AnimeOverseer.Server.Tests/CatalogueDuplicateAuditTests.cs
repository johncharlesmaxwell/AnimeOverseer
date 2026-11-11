using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class CatalogueDuplicateAuditTests : SqliteIntegrationTestBase
{
    [Fact]
    public void Rows_sharing_an_uncommon_word_are_paired_even_with_one_word_titles()
    {
        var rows = new[] { Row(1, "Toradora!"), Row(2, "Toradora! SOS"), Row(3, "Clannad") };

        var pairs = CatalogueDuplicateAudit.CandidatePairs(rows);

        Assert.Equal([(1, 2)], pairs);
    }

    [Fact]
    public void Romaji_spellings_and_aliases_pair_rows()
    {
        var alias = Row(3, "Attack on Titan");
        alias.TitleAliases.Add(new AnimeTitleAlias { Title = "Shingeki no Kyojin" });
        var rows = new[] { Row(1, "Yuuki Yuuna wa Yuusha de Aru"), Row(2, "Yuki Yuna wa Yusha de Aru"), alias, Row(4, "Shingeki no Kyojin") };

        var pairs = CatalogueDuplicateAudit.CandidatePairs(rows);

        Assert.Contains((1, 2), pairs);
        Assert.Contains((3, 4), pairs);
    }

    [Fact]
    public void A_word_on_too_many_rows_does_not_pair_them()
    {
        var rows = Enumerable.Range(1, 5).Select(id => Row(id, $"Gundam {(char)('A' + id)}{(char)('k' + id)}{(char)('q' + id)}")).ToList();

        Assert.Equal(10, CatalogueDuplicateAudit.CandidatePairs(rows).Count);
        Assert.Empty(CatalogueDuplicateAudit.CandidatePairs(rows, maxRowsPerKey: 4));
    }

    [Fact]
    public async Task Scan_finds_a_provider_split_duplicate_but_not_a_sequel_and_remembers_a_dismissal()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var aniListRow = Add(db, season, "Orbital Lantern", aniListId: 100, start: new DateTime(2020, 4, 3), episodes: 12);
        var malRow = Add(db, season, "Orbital Lantern", malId: 500, start: new DateTime(2020, 4, 3), episodes: 12);
        // A sequel shares the name but starts years later, a hard conflict.
        Add(db, season, "Orbital Lantern Season 2", aniListId: 101, start: new DateTime(2023, 1, 8), episodes: 12);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        var scan = await reviews.FindPossibleDuplicatesAsync();

        var pair = Assert.Single(scan.Pairs);
        Assert.Equal((malRow.Id, aniListRow.Id), (pair.Newer.Id, pair.Older.Id));
        Assert.Equal(100, pair.Match.Score);
        Assert.True(pair.CanDismiss);
        Assert.Equal(3, scan.RowsScanned);

        Assert.True(await reviews.DismissDuplicateAsync(pair.Newer.Id, pair.Older.Id));

        var decision = await db.AnimeMatchDecisions.AsNoTracking().SingleAsync();
        Assert.Equal((500, aniListRow.Id, false, "Audit"), (decision.SourceMalId!.Value, decision.AnimeId, decision.IsSameAnime, decision.Origin));
        Assert.Empty((await reviews.FindPossibleDuplicatesAsync()).Pairs);
    }

    [Fact]
    public async Task The_newer_rows_own_names_do_not_make_a_shared_title_look_common()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        // With a start date missing, an exact title is divided by the rows using it. Only the older
        // row would be in the catalogue when the newer one arrived, so it counts as unique.
        Add(db, season, "Quiet Harbor", aniListId: 7, start: new DateTime(2019, 1, 9), episodes: 12);
        Add(db, season, "Quiet Harbor", malId: 8, start: null, episodes: 12);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var pair = Assert.Single((await new AnimeReviewService(db, CreateCache(db)).FindPossibleDuplicatesAsync()).Pairs);

        var title = Assert.Single(pair.Match.Evidence, evidence => evidence.Signal == "Title");
        Assert.Equal(MatchScoring.Default.MainTitleExact, title.Weight);
        Assert.DoesNotContain("catalogue entries", title.Detail);
    }

    [Fact]
    public async Task A_pair_without_provider_ids_cannot_be_dismissed()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        Add(db, season, "Paper Comet", start: new DateTime(2018, 7, 2), episodes: 10);
        Add(db, season, "Paper Comet", start: new DateTime(2018, 7, 2), episodes: 10);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        var pair = Assert.Single((await reviews.FindPossibleDuplicatesAsync()).Pairs);

        Assert.False(pair.CanDismiss);
        Assert.False(await reviews.DismissDuplicateAsync(pair.Newer.Id, pair.Older.Id));
        Assert.False(await db.AnimeMatchDecisions.AnyAsync());
    }

    [Fact]
    public async Task Rows_sharing_a_provider_id_are_listed_first_whatever_their_names_and_cannot_be_dismissed()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var first = Add(db, season, "Wicked City", aniListId: 300, start: new DateTime(1987, 4, 25), episodes: 1);
        var second = Add(db, season, "Youjuu Toshi", aniListId: 300, start: new DateTime(1987, 4, 25), episodes: 1);
        Add(db, season, "Starlit Harbor", aniListId: 1, start: new DateTime(2020, 1, 9), episodes: 12);
        Add(db, season, "Starlit Harbor", malId: 2, start: new DateTime(2020, 1, 9), episodes: 12);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reviews = new AnimeReviewService(db, CreateCache(db));

        var pairs = (await reviews.FindPossibleDuplicatesAsync()).Pairs;

        Assert.Equal(2, pairs.Count);
        Assert.Equal((second.Id, first.Id, "AniList 300"), (pairs[0].Newer.Id, pairs[0].Older.Id, pairs[0].SharedProviderIds));
        Assert.False(pairs[0].CanDismiss);
        Assert.False(await reviews.DismissDuplicateAsync(second.Id, first.Id));
        Assert.Null(pairs[1].SharedProviderIds);
    }

    [Theory]
    [InlineData(2020, 1, 1, 2022, 1, 1)]   // 1 January may give only the year: a year-only overlap
    [InlineData(2020, 1, 1, 2021, 12, 31)]
    [InlineData(2020, 6, 15, 2022, 6, 16)]
    [InlineData(2019, 12, 31, 2022, 1, 1)]
    public void The_cheap_shortcut_only_drops_pairs_the_scorer_calls_conflicts(int y1, int m1, int d1, int y2, int m2, int d2)
    {
        var left = new Anime { Title = "Orbit", StartDate = new DateTime(y1, m1, d1) };
        var right = new Anime { Title = "Orbit", StartDate = new DateTime(y2, m2, d2) };

        if (CatalogueDuplicateAudit.RuledOutCheaply(left, right, MatchScoring.Default))
            Assert.True(MatchScorer.HasHardNegative(MatchScorer.Score(left, right)));
        Assert.True(CatalogueDuplicateAudit.RuledOutCheaply(new Anime { AniListId = 1 }, new Anime { AniListId = 2 }, MatchScoring.Default));
        Assert.False(CatalogueDuplicateAudit.RuledOutCheaply(left, right, MatchScoring.Default with { StartYearApartIsConflict = false }));
    }

    private static Anime Row(int id, string title) => new() { Id = id, Title = title };

    private static Anime Add(AnimeDbContext db, Season season, string title, int? aniListId = null, int? malId = null, DateTime? start = null, int? episodes = null)
    {
        var anime = new Anime { Title = title, AniListId = aniListId, MALId = malId, StartDate = start, Episodes = episodes, Type = "TV", Duration = 24, SeasonId = season.Id };
        db.Animes.Add(anime);
        return anime;
    }
}
