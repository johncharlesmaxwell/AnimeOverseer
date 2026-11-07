using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public class SeasonLabelTests
{
    private static readonly DateTime Spring2026 = new(2026, 4, 5);

    [Theory]
    [InlineData("Frieren", 1, 1, false)]
    [InlineData("Frieren Season 2", 2, 1, false)]
    [InlineData("Frieren Season II", 2, 1, false)]
    [InlineData("Frieren 2nd Season", 2, 1, false)]
    [InlineData("Frieren Second Season", 2, 1, false)]
    [InlineData("Frieren S2", 2, 1, false)]
    [InlineData("Attack on Titan Season 3 Part 2", 3, 2, false)]
    [InlineData("Re:Zero 2nd Season Part 2", 2, 2, false)]
    [InlineData("Dr. Stone Cour 2", 1, 2, false)]
    [InlineData("Dr. Stone 2nd Cour", 1, 2, false)]
    [InlineData("Attack on Titan: The Final Season", 0, 1, true)]
    [InlineData("Attack on Titan: The Final Season Part 2", 0, 2, true)]
    [InlineData("Zoku Natsume Yuujinchou", 2, 1, false)]
    [InlineData("Naruto: Shippuuden", 2, 1, false)]
    [InlineData("進撃の巨人 第2期", 2, 1, false)]
    [InlineData("The Saga of Tanya the Evil II", 2, 1, false)]
    [InlineData("Mob Psycho 100", 1, 1, false)]
    [InlineData("86", 1, 1, false)]
    [InlineData("Season of the Witch", 1, 1, false)]
    [InlineData("Mr. S Rising", 1, 1, false)]
    public void Installment_reads_season_and_part_labels(string title, int season, int part, bool final)
        => Assert.Equal(new Installment(season, part, final), TitleNormalizer.InstallmentOf(title));

    [Fact]
    public void Installment_reads_the_original_title_when_the_main_title_names_none()
    {
        Assert.Equal(new Installment(2, 1), TitleNormalizer.InstallmentOf("Frieren: Beyond Journey's End", "Sousou no Frieren 2nd Season"));
        Assert.Equal(new Installment(3, 1), TitleNormalizer.InstallmentOf("Orbit Season 3", "Orbit 2nd Season"));
        Assert.Equal(new Installment(3, 2), TitleNormalizer.InstallmentOf("Orbit Season 3", "Orbit Season 3 Part 2"));
    }

    [Theory]
    [InlineData("Season 2", "Season 1", true)]
    [InlineData("Season 2", "Season 2 Part 2", true)]
    [InlineData("Final Season", "Season 4", false)]
    [InlineData("Final Season", "Season 1", true)]
    [InlineData("Final Season", "Final Season Part 2", true)]
    public void Installments_conflict_on_season_or_part(string left, string right, bool expected)
        => Assert.Equal(expected, TitleNormalizer.InstallmentOf("Orbit " + left).ConflictsWith(TitleNormalizer.InstallmentOf("Orbit " + right)));

    [Theory]
    [InlineData("Frieren 2nd Season", "Frieren Season 2")]
    [InlineData("Frieren S2", "Frieren Second Season")]
    [InlineData("The Saga of Tanya the Evil II", "The Saga of Tanya the Evil Season 2")]
    [InlineData("Frieren Season 1", "Frieren")]
    [InlineData("Attack on Titan: The Final Season", "Attack on Titan Final Season")]
    [InlineData("Zoku Natsume Yuujinchou", "Natsume Yuujinchou Season 2")]
    [InlineData("Dr. Stone Cour 2", "Dr. Stone Part 2")]
    public void Season_canonical_form_agrees_across_spellings(string left, string right)
        => Assert.Equal(TitleNormalizer.SeasonCanonical(right), TitleNormalizer.SeasonCanonical(left));

    [Fact]
    public void Different_season_in_the_romaji_title_is_penalised()
    {
        var source = Anime("Frieren: Beyond Journey's End", "Sousou no Frieren 2nd Season");
        var candidate = Anime("Frieren: Beyond Journey's End", "Sousou no Frieren");

        var match = MatchScorer.Score(source, candidate);

        var season = Assert.Single(match.Evidence, evidence => evidence.Signal == "Season");
        Assert.Equal(-MatchScoring.Default.SeasonMismatchPenalty, season.Weight);
        Assert.Contains("Season 2 vs Season 1", season.Detail);
    }

    [Fact]
    public void Different_parts_are_penalised_and_matching_ones_are_not()
    {
        var part2 = MatchScorer.Score(Anime("Orbit Season 3 Part 2", null), Anime("Orbit Season 3", null));
        var same = MatchScorer.Score(Anime("Orbit 3rd Season Part 2", null), Anime("Orbit Season 3 Cour 2", null));

        Assert.Contains(part2.Evidence, evidence => evidence.Signal == "Season");
        Assert.DoesNotContain(same.Evidence, evidence => evidence.Signal == "Season");
    }

    [Theory]
    [InlineData("Frieren", "Frieren Second Season")]
    [InlineData("Attack on Titan", "Attack on Titan Season 3 Part 2")]
    [InlineData("Attack on Titan", "Attack on Titan: The Final Season")]
    [InlineData("Dr. Stone", "Dr. Stone 2nd Cour")]
    [InlineData("Frieren Season 2", "Frieren 2nd Season")]
    [InlineData("Frieren", "Frieren Season II")]
    public void Library_matches_more_season_label_forms(string library, string anime)
        => Assert.True(MediaLibraryService.TitlesMatch(library, anime));

    [Theory]
    [InlineData("Frieren Season 2", "Frieren Season 3")]
    [InlineData("Frieren", "Frieren II")]
    [InlineData("Naruto", "Naruto Shippuden")]
    [InlineData("Natsume Yuujinchou", "Zoku Natsume Yuujinchou")]
    public void Library_still_rejects_other_installments_and_bare_numbers(string library, string anime)
        => Assert.False(MediaLibraryService.TitlesMatch(library, anime));

    [Fact]
    public void Library_index_agrees_with_title_matching()
    {
        var library = new MediaLibraryService.LibraryTitles(["Attack on Titan", "Frieren Season 2", "Blue Lock"]);

        Assert.True(library.Matches(Anime("Shingeki no Kyojin", "Attack on Titan: The Final Season Part 2")));
        Assert.True(library.Matches(Anime("Frieren 2nd Season", null)));
        Assert.False(library.Matches(Anime("Frieren Season 3", null)));
        Assert.False(library.Matches(Anime("Blue Lock: Episode Nagi", null)));
        Assert.True(library.ContainsExact(" blue lock "));
    }

    private static Anime Anime(string title, string? original)
        => new() { Title = title, OriginalTitle = original, StartDate = Spring2026, Type = "TV", Episodes = 12 };
}
