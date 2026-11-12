using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Xunit;

namespace AnimeOverseer.Server.Tests;

/// <summary>Subtitles, title years, shared main titles, end dates, genres, and why a review is left undecided.</summary>
public class MatchSignalExtraTests
{
    private static readonly DateTime Spring2026 = new(2026, 4, 1);

    [Theory]
    [InlineData("Re:Zero - Memory Snow", "rezero", "Memory Snow")]
    [InlineData("Mushoku Tensei: Jobless Reincarnation", "mushokutensei", "Jobless Reincarnation")]
    [InlineData("Orbit – Second Light (2020)", "orbit", "Second Light")]
    public void Subtitle_is_split_after_a_real_series_name(string title, string series, string subtitle)
        => Assert.Equal((series, subtitle), TitleNormalizer.SplitSubtitle(title));

    [Theory]
    [InlineData("Re:Zero")]
    [InlineData("Frieren")]
    [InlineData("Steins;Gate")]
    public void Title_without_a_subtitle_is_not_split(string title) => Assert.Null(TitleNormalizer.SplitSubtitle(title));

    [Fact]
    public void Same_series_with_different_subtitles_is_penalised()
    {
        var match = MatchScorer.Score(new Anime { Title = "Orbit: Memory Snow" }, new Anime { Title = "Orbit: Frozen Bond" });

        var subtitle = Assert.Single(match.Evidence, evidence => evidence.Signal == "Subtitle");
        Assert.Equal(-MatchScoring.Default.SubtitleMismatchPenalty, subtitle.Weight);
    }

    [Theory]
    [InlineData("Orbit: Memory Snow", "Orbit")]                         // only one side has a subtitle
    [InlineData("Orbit: Memory Snow Season 2", "Orbit: Memory Snow")]  // left to the season check
    [InlineData("Orbit: Memory Snow", "Orbit - Memory Snow")]          // same subtitle, different separator
    public void Subtitle_penalty_needs_two_different_real_subtitles(string sourceTitle, string candidateTitle)
    {
        var match = MatchScorer.Score(new Anime { Title = sourceTitle }, new Anime { Title = candidateTitle });

        Assert.DoesNotContain(match.Evidence, evidence => evidence.Signal == "Subtitle");
    }

    [Fact]
    public void Title_year_is_read_and_left_out_of_title_words()
    {
        Assert.Equal(2011, TitleNormalizer.TitleYear("Hunter x Hunter (2011)"));
        Assert.Equal(2019, TitleNormalizer.TitleYear("Fruits Basket [2019]"));
        Assert.Null(TitleNormalizer.TitleYear("Mob Psycho 100"));
        Assert.DoesNotContain("2011", TitleNormalizer.Tokens("Hunter x Hunter (2011)"));
    }

    [Fact]
    public void Title_year_far_from_the_other_start_date_is_a_conflict()
    {
        var remake = new Anime { Title = "Hunter x Hunter (2011)", OriginalTitle = "Hunter x Hunter" };
        var original = new Anime { Title = "Hunter x Hunter", OriginalTitle = "Hunter x Hunter", StartDate = new DateTime(1999, 10, 16) };

        var match = MatchScorer.Score(remake, original);

        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Title year" && evidence.Kind == MatchEvidenceKind.HardNegative);
        Assert.Equal("Conflict", MatchScorer.Confidence(match));
    }

    [Fact]
    public void Title_year_close_to_the_other_start_date_is_fine()
    {
        var match = MatchScorer.Score(new Anime { Title = "Orbit (2026)" }, new Anime { Title = "Orbit", StartDate = Spring2026 });

        Assert.DoesNotContain(match.Evidence, evidence => evidence.Signal == "Title year");
    }

    [Fact]
    public void Shared_main_title_counts_for_less_only_when_a_start_date_is_missing()
    {
        static int Usage(string name) => name == "orbit" ? 2 : 1;
        var undated = MatchScorer.Score(new Anime { Title = "Orbit" }, new Anime { Title = "Orbit", StartDate = Spring2026 }, nameUsage: Usage);
        var dated = MatchScorer.Score(new Anime { Title = "Orbit", StartDate = Spring2026 }, new Anime { Title = "Orbit", StartDate = Spring2026 }, nameUsage: Usage);
        var off = MatchScorer.Score(new Anime { Title = "Orbit" }, new Anime { Title = "Orbit", StartDate = Spring2026 },
            MatchScoring.Default with { ScaleSharedMainTitles = false }, Usage);

        Assert.Equal(22, TitleWeight(undated)); // 45 / 2, rounded to even like shared aliases
        Assert.Equal(45, TitleWeight(dated));
        Assert.Equal(45, TitleWeight(off));
    }

    [Fact]
    public void Starting_after_the_other_ended_is_penalised()
    {
        var firstCour = new Anime { Title = "Orbit", StartDate = new DateTime(2026, 1, 5), EndDate = new DateTime(2026, 3, 30) };
        var secondCour = new Anime { Title = "Orbit", StartDate = new DateTime(2026, 7, 6) };
        var sameShow = new Anime { Title = "Orbit", StartDate = new DateTime(2026, 1, 5) };

        var later = MatchScorer.Score(secondCour, firstCour);
        var reversed = MatchScorer.Score(firstCour, secondCour);
        var same = MatchScorer.Score(sameShow, firstCour);

        Assert.Equal(-MatchScoring.Default.StartsAfterEndPenalty, Assert.Single(later.Evidence, evidence => evidence.Signal == "End date").Weight);
        Assert.Contains(reversed.Evidence, evidence => evidence.Signal == "End date");
        Assert.DoesNotContain(same.Evidence, evidence => evidence.Signal == "End date");
    }

    [Fact]
    public void Records_with_no_genres_in_common_are_penalised()
    {
        var match = MatchScorer.Score(Tagged("Orbit", "Action", "Sci-Fi"), Tagged("Orbit", "Romance", "Slice of Life"));
        var overlap = MatchScorer.Score(Tagged("Orbit", "Action", "Sci-Fi"), Tagged("Orbit", "sci fi", "Drama"));
        var sparse = MatchScorer.Score(Tagged("Orbit", "Action"), Tagged("Orbit", "Romance", "Drama"));

        Assert.Equal(-MatchScoring.Default.GenresDisjointPenalty, Assert.Single(match.Evidence, evidence => evidence.Signal == "Genres").Weight);
        Assert.DoesNotContain(overlap.Evidence, evidence => evidence.Signal == "Genres");
        Assert.DoesNotContain(sparse.Evidence, evidence => evidence.Signal == "Genres");
    }

    [Fact]
    public void Undecided_review_names_the_rule_that_came_closest()
    {
        var settings = MatchingSettings.Default;
        CandidateMatch Match(int id, int score) => new() { AnimeId = id, Score = score };

        Assert.Equal("Left for you: the only conflict-free candidate scored 72, 18 short of the merge threshold (90).",
            MatchScorer.WhyUndecided([Match(1, 72)], settings));
        Assert.Equal("Left for you: 2 candidates are conflict-free; the best reaches the threshold and leads by 30, but Merge a clear leader is off.",
            MatchScorer.WhyUndecided([Match(1, 95), Match(2, 65)], settings));
        Assert.Equal("Left for you: 2 candidates are conflict-free and the best leads the next by 5, less than the margin (25).",
            MatchScorer.WhyUndecided([Match(1, 95), Match(2, 90)], settings with { MarginMergeEnabled = true }));
        Assert.Null(MatchScorer.WhyUndecided([Match(1, 95)], settings));
    }

    private static Anime Tagged(string title, params string[] genres)
        => new() { Title = title, AnimeGenres = genres.Select(name => new AnimeGenre { Genre = new Genre { Name = name } }).ToList() };

    private static int TitleWeight(CandidateMatch match) => match.Evidence.Single(evidence => evidence.Signal == "Title").Weight;
}
