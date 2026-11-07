using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class MatchingRulesTests : SqliteIntegrationTestBase
{
    private static readonly DateTime Spring2026 = new(2026, 4, 5);
    private static readonly MatchingSettings Apply = new(AutoDecisionMode.Apply, 90);

    [Fact]
    public void Margin_merge_is_off_by_default()
        => Assert.Null(MatchScorer.Suggest([Match(1, 95), Match(2, 40)], Apply));

    [Fact]
    public void Margin_merge_takes_a_clear_leader_above_the_threshold()
    {
        var settings = Apply with { MarginMergeEnabled = true, MergeMargin = 25 };

        var suggestion = MatchScorer.Suggest([Match(2, 40), Match(1, 95), Match(3, 5, hardNegative: true)], settings);

        Assert.Equal("Merge", suggestion?.Decision);
        Assert.Equal(1, suggestion?.AnimeId);
    }

    [Theory]
    [InlineData(95, 75)] // leader too close to the runner-up
    [InlineData(85, 20)] // leader below the threshold
    public void Margin_merge_leaves_close_or_weak_leaders_to_a_person(int best, int next)
        => Assert.Null(MatchScorer.Suggest([Match(1, best), Match(2, next)], Apply with { MarginMergeEnabled = true, MergeMargin = 25 }));

    [Fact]
    public void Low_score_create_creates_when_nothing_scores_well()
    {
        var settings = Apply with { LowScoreCreateEnabled = true, CreateBelowScore = 20 };

        Assert.Equal("Create", MatchScorer.Suggest([Match(1, 15), Match(2, 10)], settings)?.Decision);
        Assert.Null(MatchScorer.Suggest([Match(1, 20)], settings));
        Assert.Null(MatchScorer.Suggest([Match(1, 15)], Apply));
    }

    [Fact]
    public void Score_uses_configured_weights()
    {
        var scoring = MatchScoring.Default with { MainTitleExact = 20, FormatSame = 0 };

        var match = MatchScorer.Score(Anime("Frieren", Spring2026, "TV", 28), Anime("Frieren", Spring2026, "TV", 28), scoring);

        Assert.Equal(20 + 25 + 0 + 15, match.Score);
    }

    [Fact]
    public void Turning_off_the_year_apart_conflict_applies_a_penalty_instead()
    {
        var scoring = MatchScoring.Default with { StartYearApartIsConflict = false, StartYearApartPenalty = 30 };

        var match = MatchScorer.Score(Anime("Orbit", Spring2026, "TV", 12), Anime("Orbit", Spring2026.AddYears(-2), "TV", 12), scoring);

        Assert.Equal(45 - 30 + 15 + 15, match.Score);
        Assert.Equal("Low", MatchScorer.Confidence(match, scoring));
    }

    [Fact]
    public void Turning_off_the_movie_conflict_treats_it_as_a_format_difference()
    {
        var scoring = MatchScoring.Default with { MovieVsSeriesIsConflict = false };

        var match = MatchScorer.Score(Anime("Orbit", Spring2026, "Movie", 1), Anime("Orbit", Spring2026, "TV", 1), scoring);

        Assert.DoesNotContain(match.Evidence, evidence => evidence.Kind == MatchEvidenceKind.HardNegative);
        Assert.Equal(45 + 25 - 5 + 15, match.Score);
    }

    [Fact]
    public void Confidence_bands_follow_the_settings()
    {
        var scoring = MatchScoring.Default with { HighConfidence = 60, MediumConfidence = 30 };

        Assert.Equal("High", MatchScorer.Confidence(Match(1, 60), scoring));
        Assert.Equal("Medium", MatchScorer.Confidence(Match(1, 30), scoring));
        Assert.Equal("Low", MatchScorer.Confidence(Match(1, 29), scoring));
    }

    [Fact]
    public void Normalizing_clamps_out_of_range_values()
    {
        var settings = (Apply with
        {
            MergeMargin = 0,
            CreateBelowScore = 80,
            Scoring = MatchScoring.Default with { MainTitleExact = 500, HighConfidence = 40, MediumConfidence = 70 }
        }).Normalized();

        Assert.Equal(1, settings.MergeMargin);
        Assert.Equal(MatchingSettings.MinimumMergeThreshold, settings.CreateBelowScore);
        Assert.Equal(100, settings.Scoring.MainTitleExact);
        Assert.Equal(40, settings.Scoring.MediumConfidence);
    }

    [Fact]
    public async Task All_settings_round_trip_through_storage()
    {
        await using var db = CreateDb();
        var settings = new MatchingSettings(AutoDecisionMode.Apply, 85, 14)
        {
            MarginMergeEnabled = true,
            MergeMargin = 30,
            LowScoreCreateEnabled = true,
            CreateBelowScore = 15,
            Scoring = MatchScoring.Default with { AliasMatch = 35, MovieVsSeriesIsConflict = false, HighConfidence = 75 }
        };

        await MatchingSettings.SaveAsync(db, settings);

        await using var check = CreateDb();
        Assert.Equal(settings, await MatchingSettings.LoadAsync(check));
    }

    [Fact]
    public async Task Missing_or_corrupt_scoring_falls_back_to_defaults()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = MatchingSettings.ScoringKey, Value = "{not json" });
        await db.SaveChangesAsync();

        Assert.Equal(MatchScoring.Default, (await MatchingSettings.LoadAsync(db)).Scoring);
    }

    private static CandidateMatch Match(int animeId, int score, bool hardNegative = false) => new()
    {
        AnimeId = animeId,
        Score = score,
        Evidence = hardNegative ? [new MatchEvidence { Signal = "Format", Kind = MatchEvidenceKind.HardNegative }] : []
    };

    private static Anime Anime(string title, DateTime? start, string? type, int? episodes)
        => new() { Title = title, StartDate = start, Type = type, Episodes = episodes };
}
