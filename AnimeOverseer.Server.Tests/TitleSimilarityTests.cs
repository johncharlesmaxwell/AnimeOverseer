using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public class TitleSimilarityTests
{
    [Fact]
    public void One_word_title_is_partly_similar_to_a_longer_title_containing_it()
    {
        var match = MatchScorer.Score(new Anime { Title = "Toradora!" }, new Anime { Title = "Toradora! SOS" });

        var title = Assert.Single(match.Evidence, evidence => evidence.Signal == "Title");
        Assert.Equal(MatchEvidenceKind.Positive, title.Kind);
        // Every word of the shorter title is in the longer: 70% of the 30 similar-title points.
        Assert.Equal(21, title.Weight);
    }

    [Fact]
    public void Romaji_spellings_are_folded_word_by_word_in_similar_titles()
    {
        // Neither whole title matches, even after folding, since the season labels are spelled differently.
        var source = new Anime { Title = "Haikyuu!! Second Season Recap" };
        var candidate = new Anime { Title = "Haikyu!! 2nd Season Recap" };

        var folded = MatchScorer.Score(source, candidate);
        var plain = MatchScorer.Score(source, candidate, MatchScoring.Default with { MatchRomajiSpellings = false });

        Assert.Equal(30, TitleWeight(folded));
        Assert.True(TitleWeight(plain) < 30);
    }

    [Fact]
    public void Sharing_a_common_word_counts_for_less_than_sharing_a_rare_one()
    {
        var stats = Stats(("Kimi no Na wa", 1), ("Boku no Hero Academia", 2), ("Ore no Imouto", 3), ("Tonari no Totoro", 4), ("Sora no Woto", 5));
        var source = new Anime { Title = "Kimi no Sora" };

        var common = MatchScorer.Score(source, new Anime { Title = "Boku no Hero" }, null, stats);
        var unweighted = MatchScorer.Score(source, new Anime { Title = "Boku no Hero" }, MatchScoring.Default with { WeightRareWords = false }, stats);
        var rare = MatchScorer.Score(source, new Anime { Title = "Kimi to Sora" }, null, stats);

        Assert.True(TitleWeight(common) < TitleWeight(unweighted));
        Assert.True(TitleWeight(rare) > TitleWeight(common));
    }

    [Fact]
    public void Weighted_score_is_plain_jaccard_when_every_word_weighs_the_same()
    {
        HashSet<string> left = ["a", "b", "c"], right = ["b", "c", "d"];

        Assert.Equal(TitleNormalizer.TokenScore(left, right), TitleNormalizer.WeightedTokenScore(left, right, _ => 1), 6);
        Assert.Equal(1, CatalogueNameStats.Empty.TokenWeight("anything"));
    }

    [Fact]
    public void Token_counts_include_plain_and_folded_forms_once_per_row()
    {
        var counts = CatalogueNameUsage.CountTokens([(1, "Yuuki Yuuna"), (1, "Yūki Yūna"), (2, "Yuki")]);

        Assert.Equal(1, counts["yuuki"]);
        Assert.Equal(2, counts["yuki"]); // "Yūki" loses its macron, so rows 1 and 2 both use it
        Assert.Equal(2, counts["~yuki"]);
    }

    private static CatalogueNameStats Stats(params (string Title, int Id)[] rows)
    {
        var names = rows.Select(row => (row.Id, (string?)row.Title)).ToList();
        return new CatalogueNameStats(CatalogueNameUsage.Count(names), CatalogueNameUsage.CountTokens(names), rows.Length);
    }

    private static int TitleWeight(CandidateMatch match) => match.Evidence.Single(evidence => evidence.Signal == "Title").Weight;
}
