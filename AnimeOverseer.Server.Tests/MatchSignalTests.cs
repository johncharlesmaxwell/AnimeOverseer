using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public class MatchSignalTests
{
    private static readonly DateTime Spring2026 = new(2026, 4, 5);

    [Theory]
    [InlineData("Hunter × Hunter", "Hunter x Hunter")]
    [InlineData("Hunter×Hunter", "Hunter x Hunter")]
    [InlineData("Kaguya & Friends", "Kaguya and Friends")]
    public void Times_sign_and_ampersand_normalize_like_their_words(string left, string right)
        => Assert.Equal(TitleNormalizer.Compact(right), TitleNormalizer.Compact(left));

    [Theory]
    [InlineData("Yuuki", "Yūki")]
    [InlineData("Yuuki", "Yuki")]
    [InlineData("Toukyou Ghoul", "Tokyo Ghoul")]
    [InlineData("Naruto Shippuuden", "Naruto Shippūden")]
    public void Romaji_key_folds_long_vowels(string left, string right)
        => Assert.Equal(TitleNormalizer.RomajiKey(right), TitleNormalizer.RomajiKey(left));

    [Theory]
    [InlineData("TV SHORT", "TV")]
    [InlineData("tv_short", "TV")]
    [InlineData("movie", "Movie")]
    [InlineData("TV Special", "Special")]
    [InlineData("ona", "ONA")]
    [InlineData("Music Video", "Music")]
    public void Formats_fold_to_one_vocabulary(string format, string expected)
        => Assert.Equal(expected, TitleNormalizer.Format(format));

    [Theory]
    [InlineData("Currently Airing", true)]
    [InlineData("currently airing", true)]
    [InlineData("Not Yet Aired", true)]
    [InlineData("current", true)]
    [InlineData("Finished Airing", false)]
    [InlineData("Finished", false)]
    [InlineData(null, false)]
    public void Unfinished_status_is_recognised(string? status, bool expected)
        => Assert.Equal(expected, TitleNormalizer.IsUnfinished(status));

    [Fact]
    public void Tv_short_and_tv_are_the_same_format()
    {
        var match = MatchScorer.Score(Anime("Orbit", "TV SHORT", 12), Anime("Orbit", "TV", 12));

        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Format" && evidence.Weight == MatchScoring.Default.FormatSame);
    }

    [Fact]
    public void Matching_episode_lengths_add_points_and_short_vs_full_length_removes_them()
    {
        var same = MatchScorer.Score(Anime("Orbit", "TV", 12, duration: 24), Anime("Orbit", "TV", 12, duration: 23));
        var different = MatchScorer.Score(Anime("Orbit", "TV", 12, duration: 3), Anime("Orbit", "TV", 12, duration: 24));

        Assert.Contains(same.Evidence, evidence => evidence.Signal == "Length" && evidence.Weight == 5);
        Assert.Contains(different.Evidence, evidence => evidence.Signal == "Length" && evidence.Weight == -10);
    }

    [Theory]
    [InlineData(12, 14, null, MatchEvidenceKind.Neutral)]
    [InlineData(12, 30, null, MatchEvidenceKind.Negative)]
    [InlineData(12, 30, "Currently Airing", MatchEvidenceKind.Neutral)]
    public void Episode_counts_allow_a_tolerance_and_airing_shows(int source, int candidate, string? status, MatchEvidenceKind expected)
    {
        var incoming = Anime("Orbit", "TV", source);
        incoming.Status = status;

        var match = MatchScorer.Score(incoming, Anime("Orbit", "TV", candidate));

        Assert.Equal(expected, match.Evidence.Single(evidence => evidence.Signal == "Episodes").Kind);
    }

    [Fact]
    public void Airing_counts_are_penalised_when_the_setting_is_off()
    {
        var incoming = Anime("Orbit", "TV", 12);
        incoming.Status = "Currently Airing";

        var match = MatchScorer.Score(incoming, Anime("Orbit", "TV", 30), MatchScoring.Default with { IgnoreEpisodesWhileAiring = false });

        Assert.Equal(MatchEvidenceKind.Negative, match.Evidence.Single(evidence => evidence.Signal == "Episodes").Kind);
    }

    [Fact]
    public void Different_kitsu_ids_are_a_hard_conflict()
    {
        var source = Anime("Orbit", "TV", 12);
        source.KitsuId = 10;
        var candidate = Anime("Orbit", "TV", 12);
        candidate.KitsuId = 11;

        var match = MatchScorer.Score(source, candidate);

        Assert.Equal("Conflict", MatchScorer.Confidence(match));
        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Kitsu ID");
    }

    [Fact]
    public void A_shared_alias_counts_for_less()
    {
        var source = Anime("Journey's End", null, null);
        var candidate = Anime("Frieren", null, null);
        candidate.TitleAliases.Add(new AnimeTitleAlias { Title = "Journeys End" });
        var usage = new Dictionary<string, int> { [TitleNormalizer.Compact("Journeys End")] = 3 };

        var scaled = MatchScorer.Score(source, candidate, nameUsage: name => usage.GetValueOrDefault(name, 1));
        var unscaled = MatchScorer.Score(source, candidate, MatchScoring.Default with { ScaleSharedAliases = false }, name => usage.GetValueOrDefault(name, 1));

        Assert.Equal(10, scaled.Evidence.Single(evidence => evidence.Signal == "Title").Weight);
        Assert.Contains("shared by 3", scaled.Evidence.Single(evidence => evidence.Signal == "Title").Detail);
        Assert.Equal(30, unscaled.Evidence.Single(evidence => evidence.Signal == "Title").Weight);
    }

    [Fact]
    public void Romaji_spellings_match_as_titles_unless_switched_off()
    {
        var match = MatchScorer.Score(Anime("Yuuki Yuuna", "TV", 12), Anime("Yūki Yūna", "TV", 12));
        var off = MatchScorer.Score(Anime("Yuuki Yuuna", "TV", 12), Anime("Yūki Yūna", "TV", 12), MatchScoring.Default with { MatchRomajiSpellings = false });

        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Title" && evidence.Weight == MatchScoring.Default.MainTitleExact);
        Assert.DoesNotContain(off.Evidence, evidence => evidence.Signal == "Title" && evidence.Weight == MatchScoring.Default.MainTitleExact);
    }

    [Fact]
    public void Name_usage_counts_distinct_rows_per_name()
    {
        var usage = CatalogueNameUsage.Count([(1, "Gundam"), (1, "GUNDAM"), (2, "Gundam"), (3, "Other"), (3, null)]);

        Assert.Equal(2, usage["gundam"]);
        Assert.Equal(1, usage["other"]);
    }

    private static Anime Anime(string title, string? type, int? episodes, int? duration = null)
        => new() { Title = title, StartDate = Spring2026, Type = type, Episodes = episodes, Duration = duration };
}
