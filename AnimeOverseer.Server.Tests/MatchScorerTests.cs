using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public class MatchScorerTests
{
    private static readonly DateTime Spring2026 = new(2026, 4, 5);

    [Fact]
    public void Exact_title_with_agreeing_metadata_scores_high()
    {
        var match = MatchScorer.Score(
            Anime("Frieren", Spring2026, "TV", 28, 24),
            Anime("Frieren", Spring2026.AddDays(3), "TV", 28, 23));

        Assert.Equal(100, match.Score);
        Assert.Equal("High", MatchScorer.Confidence(match));
    }

    [Fact]
    public void Alias_match_with_unknown_metadata_scores_low_without_conflict()
    {
        var source = Anime("Journey's End", null, null, null);
        var candidate = Anime("Frieren", null, null, null);
        candidate.TitleAliases.Add(new AnimeTitleAlias { Title = "Journeys End" });

        var match = MatchScorer.Score(source, candidate);

        Assert.Equal(30, match.Score);
        Assert.Equal("Low", MatchScorer.Confidence(match));
        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Title" && evidence.Kind == MatchEvidenceKind.Positive);
    }

    [Theory]
    [InlineData("Saga of Tanya the Evil Season 2", "The Saga of Tanya the Evil")]
    [InlineData("Frieren 2", "Frieren")]
    [InlineData("Frieren 2nd Season", "Frieren")]
    public void Titles_naming_different_seasons_are_penalised(string sourceTitle, string candidateTitle)
    {
        var source = Anime(sourceTitle, Spring2026, "TV", 12);
        source.AlternativeTitles.Add(candidateTitle);

        var match = MatchScorer.Score(source, Anime(candidateTitle, Spring2026, "TV", 12));

        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Season" && evidence.Weight < 0);
    }

    [Fact]
    public void Movie_against_series_is_a_conflict()
    {
        var match = MatchScorer.Score(Anime("Orbit", Spring2026, "Movie", 1), Anime("Orbit", Spring2026, "TV", 12));

        Assert.True(match.Score <= 10);
        Assert.Equal("Conflict", MatchScorer.Confidence(match));
    }

    [Fact]
    public void Start_dates_a_year_apart_are_a_conflict()
    {
        var match = MatchScorer.Score(Anime("Orbit", Spring2026, "TV", 12), Anime("Orbit", Spring2026.AddYears(-2), "TV", 12));

        Assert.Equal("Conflict", MatchScorer.Confidence(match));
        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Start date" && evidence.Kind == MatchEvidenceKind.HardNegative);
    }

    [Fact]
    public void Conflicting_anilist_ids_are_a_conflict()
    {
        var source = Anime("Orbit", Spring2026, "TV", 12);
        source.AniListId = 1;
        var candidate = Anime("Orbit", Spring2026, "TV", 12);
        candidate.AniListId = 2;

        Assert.Equal("Conflict", MatchScorer.Confidence(MatchScorer.Score(source, candidate)));
    }

    [Fact]
    public void Candidate_listed_as_a_relation_is_a_conflict()
    {
        var source = Anime("Orbit", Spring2026, "TV", 12);
        source.ProviderRelations.Add(new ProviderRelation { RelationType = "SEQUEL", MalId = 77 });
        var candidate = Anime("Orbit", Spring2026, "TV", 12);
        candidate.MALId = 77;

        var match = MatchScorer.Score(source, candidate);

        Assert.True(MatchScorer.IsRelated(source, candidate));
        Assert.Contains(match.Evidence, evidence => evidence.Signal == "Relation" && evidence.Detail.EndsWith("sequel"));
        Assert.Equal("Conflict", MatchScorer.Confidence(match));
    }

    [Theory]
    [InlineData("Frieren", 1)]
    [InlineData("Frieren Season 2", 2)]
    [InlineData("Frieren 2nd Season", 2)]
    [InlineData("Frieren S2", 2)]
    [InlineData("The Saga of Tanya the Evil II", 2)]
    [InlineData("Mob Psycho 100", 1)]
    [InlineData("86", 1)]
    public void Season_number_reads_explicit_labels_only(string title, int expected) =>
        Assert.Equal(expected, TitleNormalizer.SeasonNumber(title));

    private static Anime Anime(string title, DateTime? start, string? type, int? episodes, int? duration = null)
        => new() { Title = title, StartDate = start, Type = type, Episodes = episodes, Duration = duration };
}
