using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Xunit;

namespace AnimeOverseer.Server.Tests;

/// <summary>
/// Format conflicts, episode tolerance, imprecise dates, contained and respelled titles,
/// romanization, short aliases, corroborating titles, reverse relations and the replay sweep.
/// </summary>
public class MatchingImprovementTests
{
    private static Anime Anime(string title, string? type = null, int? episodes = null, DateTime? start = null)
        => new() { Title = title, Type = type, Episodes = episodes, StartDate = start };

    private static MatchEvidence Signal(CandidateMatch match, string signal) => match.Evidence.Single(evidence => evidence.Signal == signal);

    [Theory]
    [InlineData("Movie", "Special")]
    [InlineData("Movie", "OVA")]
    [InlineData("Movie", "ONA")]
    public void Movie_against_a_non_tv_format_is_only_a_format_penalty(string left, string right)
    {
        var match = MatchScorer.Score(Anime("Orbit", left), Anime("Orbit", right));

        var format = Signal(match, "Format");
        Assert.Equal(MatchEvidenceKind.Negative, format.Kind);
        Assert.Equal(-MatchScoring.Default.FormatDifferPenalty, format.Weight);
    }

    [Fact]
    public void Movie_against_tv_is_still_a_hard_conflict()
    {
        var match = MatchScorer.Score(Anime("Orbit", "Movie"), Anime("Orbit", "TV_SHORT"));

        Assert.Equal(MatchEvidenceKind.HardNegative, Signal(match, "Format").Kind);
    }

    [Theory]
    [InlineData(220, 224, MatchEvidenceKind.Neutral)]   // within 5% of a long show
    [InlineData(220, 240, MatchEvidenceKind.Negative)]  // beyond it
    [InlineData(12, 15, MatchEvidenceKind.Negative)]    // short shows keep the fixed tolerance
    [InlineData(12, 25, MatchEvidenceKind.Neutral)]     // about twice: a split cour
    [InlineData(4, 8, MatchEvidenceKind.Negative)]      // too short to call a cour
    public void Episode_tolerance_grows_with_long_shows_and_allows_split_cours(int source, int candidate, MatchEvidenceKind expected)
    {
        var match = MatchScorer.Score(Anime("Orbit", "TV", source), Anime("Orbit", "TV", candidate));

        Assert.Equal(expected, Signal(match, "Episodes").Kind);
    }

    [Fact]
    public void Month_only_date_is_compared_by_its_closest_day()
    {
        // "2011-10" parses as 1 October; the real premiere was 30 October, but MAL gave only the month.
        var match = MatchScorer.Score(Anime("Orbit", start: new DateTime(2011, 10, 1)), Anime("Orbit", start: new DateTime(2011, 11, 20)));

        var start = Signal(match, "Start date");
        Assert.Equal(MatchScoring.Default.StartWithinMonth, start.Weight);
        Assert.Contains("only the month", start.Detail);
    }

    [Fact]
    public void Year_only_date_earns_at_most_the_six_month_points()
    {
        var match = MatchScorer.Score(Anime("Orbit", start: new DateTime(2011, 1, 1)), Anime("Orbit", start: new DateTime(2011, 10, 2)));

        var start = Signal(match, "Start date");
        Assert.Equal(MatchScoring.Default.StartWithinSixMonths, start.Weight);
        Assert.Contains("only the year", start.Detail);
    }

    [Fact]
    public void Year_only_date_still_conflicts_with_a_much_later_start()
    {
        var match = MatchScorer.Score(Anime("Orbit", start: new DateTime(2011, 1, 1)), Anime("Orbit", start: new DateTime(2013, 4, 6)));

        Assert.Equal(MatchEvidenceKind.HardNegative, Signal(match, "Start date").Kind);
    }

    [Fact]
    public void Imprecise_dates_can_be_turned_off()
    {
        var scoring = MatchScoring.Default with { DatesMayBeImprecise = false };
        var match = MatchScorer.Score(Anime("Orbit", start: new DateTime(2011, 1, 1)), Anime("Orbit", start: new DateTime(2011, 10, 2)), scoring);

        Assert.Equal(-MatchScoring.Default.StartUnderYearPenalty, Signal(match, "Start date").Weight);
    }

    [Fact]
    public void Title_contained_in_another_earns_its_share()
    {
        var match = MatchScorer.Score(Anime("Orbit Blue"), Anime("Orbit Blue Recollection"));

        var title = Signal(match, "Title");
        Assert.Equal((int)Math.Round(MatchScoring.Default.SimilarTitleMax * 0.7), title.Weight);
        Assert.Contains("shorter title", title.Detail);
    }

    [Fact]
    public void Word_breaks_do_not_hide_a_similar_title()
    {
        // The compact forms differ ("kai" vs "z kai"), so this is a spelling match, not an exact one.
        var match = MatchScorer.Score(Anime("Dragonball Z Kai"), Anime("Dragon Ball Kai Z"));

        var title = Signal(match, "Title");
        Assert.True(title.Weight >= 20, title.Detail);
    }

    [Theory]
    [InlineData("Shimbun", "Shinbun")]
    [InlineData("Tsubasa", "Tubasa")]
    [InlineData("Shoujo", "Syozyo")]
    [InlineData("Chikyuu", "Tikyu")]
    [InlineData("Kimi wo Aishiteru", "Kimi o Aishiteru")]
    [InlineData("Fushigi", "Husigi")]
    public void Romaji_key_folds_romanization_variants(string left, string right)
        => Assert.Equal(TitleNormalizer.RomajiKey(left), TitleNormalizer.RomajiKey(right));

    [Fact]
    public void Romanization_variants_count_as_an_exact_title()
    {
        var match = MatchScorer.Score(Anime("Fushigi Yuugi"), Anime("Husigi Yugi"));

        Assert.Equal(MatchScoring.Default.MainTitleExact, Signal(match, "Title").Weight);
    }

    [Fact]
    public void Short_alias_counts_half()
    {
        var source = Anime("Orbit Saga");
        source.AlternativeTitles.Add("OS");
        var candidate = Anime("Orbit: The Long Saga");
        candidate.AlternativeTitles.Add("OS");

        var title = Signal(match: MatchScorer.Score(source, candidate), "Title");
        Assert.Equal(MatchScoring.Default.AliasMatch / 2, title.Weight);
        Assert.Contains("counts half", title.Detail);
    }

    [Fact]
    public void Main_and_original_titles_both_matching_earn_a_bonus()
    {
        var source = new Anime { Title = "Orbit", OriginalTitle = "Kidou no Hoshi" };
        var candidate = new Anime { Title = "Orbit", OriginalTitle = "Kidō no Hoshi" };

        var match = MatchScorer.Score(source, candidate);

        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Title" && evidence.Detail == "Original titles match too" && evidence.Weight == MatchScoring.Default.TitleCorroborationBonus);
    }

    [Fact]
    public void Candidate_listing_the_incoming_record_as_related_is_a_hard_conflict()
    {
        var source = new Anime { Title = "Orbit", MALId = 200 };
        var candidate = new Anime { Title = "Orbit", MALId = 100 };
        candidate.ProviderRelations.Add(new ProviderRelation { MalId = 200, RelationType = "Sequel" });

        var match = MatchScorer.Score(source, candidate);

        var relation = Signal(match, "Relation");
        Assert.Equal(MatchEvidenceKind.HardNegative, relation.Kind);
        Assert.Contains("candidate lists", relation.Detail);
        Assert.True(MatchScorer.IsRelated(source, candidate));
    }

    [Fact]
    public void Replay_sweeps_thresholds_and_tallies_signals()
    {
        var settings = new MatchingSettings(AutoDecisionMode.Apply, 90);
        var source = Anime("Orbit", "TV", 12, new DateTime(2020, 4, 5));
        var right = Anime("Orbit", "TV", 12, new DateTime(2020, 4, 5)); right.Id = 1;
        var wrong = Anime("Orbit Blue", "TV", 13, new DateTime(2020, 4, 9)); wrong.Id = 2;
        var cases = new List<MatchReplayCase>
        {
            MatchReplay.Evaluate(new MatchReplayExample(1, source, [right], true, 1, new HashSet<string>()), settings, CatalogueNameStats.Empty),
            MatchReplay.Evaluate(new MatchReplayExample(2, source, [wrong], false, null, new HashSet<string>()), settings, CatalogueNameStats.Empty)
        };

        var report = new MatchReplayReport(cases, [], settings);

        Assert.Contains(report.Thresholds, row => row.IsCurrent && row.Threshold == 90);
        Assert.Equal(MatchingSettings.MinimumMergeThreshold, report.Thresholds[0].Threshold);
        // A low enough threshold merges the wrong row too.
        Assert.True(report.Thresholds[0].WrongMerges >= 1);
        var low = new MatchReplayReport(cases.Select(item => MatchReplay.Evaluate(
            new MatchReplayExample(item.ReviewId, item.Source, item.Candidates, item.PersonMerged, item.PersonAnimeId, new HashSet<string>()),
            settings with { MergeThreshold = 50 }, CatalogueNameStats.Empty)).ToList(), [], settings with { MergeThreshold = 50 });
        Assert.NotEmpty(low.PointsBehindWrongMerges);
    }
}
