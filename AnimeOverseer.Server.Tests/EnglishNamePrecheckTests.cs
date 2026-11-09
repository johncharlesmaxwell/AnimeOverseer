using System.Text.Json;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class EnglishNamePrecheckTests : SqliteIntegrationTestBase
{
    private static readonly EnglishNameSettings Defaults = EnglishNameSettings.Default;

    [Fact]
    public void Sources_that_differ_only_in_punctuation_agree_and_the_first_source_spells_it()
    {
        var verdict = EnglishNamePrecheck.Evaluate(
            [("Kitsu", "frieren - beyond journeys end"), ("AniList", "Frieren: Beyond Journey's End"), ("MyAnimeList", "Frieren: Beyond Journey’s End")],
            Defaults);

        Assert.Equal(EnglishNameOutcome.Agreed, verdict.Outcome);
        Assert.Equal("Frieren: Beyond Journey's End", verdict.Title);
        Assert.Equal(["AniList", "MyAnimeList", "Kitsu"], verdict.Options.Single().Sources);
    }

    [Fact]
    public void Disagreeing_sources_need_a_person_unless_the_tie_break_is_on()
    {
        (string, string)[] titles = [("AniList", "The Apothecary Diaries"), ("MyAnimeList", "The Apothecary Diaries"), ("Kitsu", "Kusuriya no Hitorigoto")];

        var asked = EnglishNamePrecheck.Evaluate(titles, Defaults);
        Assert.Equal(EnglishNameOutcome.NeedsPerson, asked.Outcome);
        Assert.Null(asked.Title);
        // Most-supported name first, so the review page lists it first.
        Assert.Equal("The Apothecary Diaries", asked.Options[0].Title);

        var broken = EnglishNamePrecheck.Evaluate(titles, Defaults with { TieBreak = true });
        Assert.Equal(EnglishNameOutcome.TieBroken, broken.Outcome);
        Assert.Equal("The Apothecary Diaries", broken.Title);
    }

    [Fact]
    public void Tie_break_falls_back_to_source_order_when_counts_are_level()
    {
        (string, string)[] titles = [("AniList", "Name A"), ("Kitsu", "Name B")];

        Assert.Equal("Name A", EnglishNamePrecheck.Evaluate(titles, Defaults with { TieBreak = true }).Title);
        Assert.Equal("Name B", EnglishNamePrecheck.Evaluate(titles, Defaults with { TieBreak = true, Priority = ["Kitsu", "AniList", "MyAnimeList", "AnimeSchedule"] }).Title);
    }

    [Fact]
    public void Too_few_sources_or_none_leave_the_name_to_a_person()
    {
        Assert.Equal(EnglishNameOutcome.NeedsPerson, EnglishNamePrecheck.Evaluate([("AniList", "Orbit")], Defaults with { MinSources = 2 }).Outcome);
        Assert.Equal(EnglishNameOutcome.None, EnglishNamePrecheck.Evaluate([("AniList", "  ")], Defaults).Outcome);
    }

    [Fact]
    public void Stored_source_order_drops_unknown_sources_and_keeps_missing_ones_last()
    {
        var settings = EnglishNameSettings.Read(new Dictionary<string, string?>
        {
            [EnglishNameSettings.PriorityKey] = "Kitsu, Nope ,AniList,Kitsu",
            [EnglishNameSettings.MinSourcesKey] = "9"
        });

        Assert.Equal(["Kitsu", "AniList", "MyAnimeList", "AnimeSchedule"], settings.Priority);
        Assert.Equal(EnglishNameSettings.MaximumMinSources, settings.MinSources);
        Assert.True(settings.Enabled);
    }

    [Fact]
    public void Kitsu_romaji_is_not_treated_as_english()
    {
        using var document = JsonDocument.Parse("""{"titles":{"en_jp":"Sousou no Frieren","ja_jp":"葬送のフリーレン"}}""");
        Assert.Null(KitsuApiService.GetEnglishTitle(document.RootElement));

        using var english = JsonDocument.Parse("""{"titles":{"en":"Frieren","en_jp":"Sousou no Frieren"}}""");
        Assert.Equal("Frieren", KitsuApiService.GetEnglishTitle(english.RootElement));
    }

    [Fact]
    public async Task Imports_keep_one_row_per_source_with_its_latest_english_title()
    {
        await using var db = CreateDb();
        await ImportAsync(db, FromAniList(7, "Orbit Patrol"));
        await ImportAsync(db, FromAniList(7, "Orbit Patrol!"));

        var row = await db.AnimeSourceTitles.AsNoTracking().SingleAsync();
        Assert.Equal(("AniList", "Orbit Patrol!"), (row.Source, row.Title));
    }

    [Fact]
    public async Task Precheck_names_records_whose_sources_agree_and_keeps_the_old_name()
    {
        await using var db = CreateDb();
        var anime = await AddAsync(db, "Sousou no Frieren", ("AniList", "Frieren: Beyond Journey's End"), ("MyAnimeList", "Frieren - Beyond Journeys End"));
        var unsettled = await AddAsync(db, "Kusuriya", ("AniList", "The Apothecary Diaries"), ("Kitsu", "Drug Store Diaries"));

        var result = await Reviews(db).ApplyEnglishNamePrecheckAsync();

        Assert.Equal(new EnglishNamePrecheckResult(1, 0, 0), result);
        var named = await db.Animes.AsNoTracking().Include(item => item.TitleAliases).SingleAsync(item => item.Id == anime.Id);
        Assert.Equal("Frieren: Beyond Journey's End", named.Title);
        Assert.True(named.NamesReviewed && named.NameFromSources && named.HasEnglishTitle);
        Assert.Contains(named.TitleAliases, alias => alias.Title == "Sousou no Frieren");

        // The disagreement stays for a person, English names listed first with their sources.
        var queue = await Reviews(db).GetNamesPendingReviewAsync();
        var item = Assert.Single(queue);
        Assert.Equal(unsettled.Id, item.Anime.Id);
        Assert.Equal(["The Apothecary Diaries", "Drug Store Diaries"], item.EnglishNames.Select(option => option.Title));
        Assert.Equal(["AniList"], item.EnglishNames[0].Sources);
    }

    [Fact]
    public async Task A_name_a_person_chose_survives_later_syncs_and_prechecks()
    {
        await using var db = CreateDb();
        await ImportAsync(db, FromAniList(7, "Orbit Patrol"));
        var anime = await db.Animes.AsNoTracking().SingleAsync();
        Assert.True(await Reviews(db).SetMainEnglishTitleAsync(anime.Id, "Orbit Patrol (Mine)", allowCustom: true));

        await ImportAsync(db, FromAniList(7, "Orbit Patrol Renamed"));
        await Reviews(db).ApplyEnglishNamePrecheckAsync();

        var stored = await db.Animes.AsNoTracking().SingleAsync();
        Assert.Equal("Orbit Patrol (Mine)", stored.Title);
        Assert.False(stored.NameFromSources);
    }

    [Fact]
    public async Task A_precheck_name_goes_back_to_review_when_sources_stop_agreeing()
    {
        await using var db = CreateDb();
        var anime = await AddAsync(db, "Orbit", ("AniList", "Orbit Patrol"));
        await Reviews(db).ApplyEnglishNamePrecheckAsync();
        Assert.True((await db.Animes.AsNoTracking().SingleAsync()).NameFromSources);

        db.AnimeSourceTitles.Add(new AnimeSourceTitle { AnimeId = anime.Id, Source = "MyAnimeList", Title = "Space Patrol Orbit" });
        await db.SaveChangesAsync();
        var result = await Reviews(db).ApplyEnglishNamePrecheckAsync();

        Assert.Equal(1, result.ReturnedToReview);
        var stored = await db.Animes.AsNoTracking().SingleAsync();
        Assert.Equal("Orbit Patrol", stored.Title);
        Assert.False(stored.NamesReviewed || stored.NameFromSources);
    }

    [Fact]
    public async Task Migrations_create_the_source_title_table_and_match_the_model()
    {
        await using var db = new AnimeDbContext(new DbContextOptionsBuilder<AnimeDbContext>().UseSqlite(ConnectionString).Options);
        await db.Database.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(0, await db.AnimeSourceTitles.CountAsync());
        Assert.Equal(0, await db.Animes.CountAsync(item => item.NameFromSources));
    }

    private static AnimeReviewService Reviews(AnimeDbContext db) => new(db, CreateCache(db));

    private static Anime FromAniList(int id, string english) => new()
    {
        AniListId = id, Title = english, HasEnglishTitle = true, DataSource = "AniList", SourceEnglishTitle = english, AlternativeTitles = [english]
    };

    private static async Task<Anime> AddAsync(AnimeDbContext db, string title, params (string Source, string Title)[] sourceTitles)
    {
        var season = await db.Seasons.FirstOrDefaultAsync() ?? await AddSeasonAsync(db);
        var anime = new Anime
        {
            Title = title, SeasonId = season.Id,
            SourceTitles = sourceTitles.Select(item => new AnimeSourceTitle { Source = item.Source, Title = item.Title }).ToList()
        };
        db.Animes.Add(anime);
        await db.SaveChangesAsync();
        return anime;
    }

    private static Task<List<int>> ImportAsync(AnimeDbContext db, Anime incoming)
        => CreateCache(db).FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));
}
