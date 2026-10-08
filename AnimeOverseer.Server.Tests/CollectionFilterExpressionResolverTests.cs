using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class CollectionFilterExpressionResolverTests
{
    [Fact]
    public void Resolves_string_functions_only_inside_explicit_template_delimiters()
    {
        var state = new FilterState
        {
            Groups = [new FilterGroup { Conditions = [
                new FilterCondition { Field = FilterField.Title, Value = "{{replace(\"My Anime\", \" \", \"-\")}}" },
                new FilterCondition { Field = FilterField.Genres, Value = "{{upper(\"action\")}}" },
                new FilterCondition { Field = FilterField.Synopsis, Value = "- 10" }
            ] }]
        };

        var resolved = CollectionFilterExpressionResolver.Resolve(state);

        Assert.Equal("My-Anime", resolved.Groups[0].Conditions[0].Value);
        Assert.Equal("ACTION", resolved.Groups[0].Conditions[1].Value);
        Assert.Equal("- 10", resolved.Groups[0].Conditions[2].Value);
    }

    [Fact]
    public void Resolves_anime_fields_inside_string_templates()
    {
        var anime = new Anime
        {
            Id = 42, Title = "My Anime", OriginalTitle = "Watashi no Anime", Episodes = 12,
            Season = new Season { Name = "spring", Year = 2026 }
        };
        var state = new FilterState
        {
            Groups = [new FilterGroup { Conditions = [new FilterCondition
            {
                Field = FilterField.Title, Value = "{{concat(lower(title), \"-\", year, \"-\", episodes)}}"
            }] }]
        };

        var resolved = CollectionFilterExpressionResolver.Resolve(state, anime: anime);

        Assert.Equal("my anime-2026-12", resolved.Groups[0].Conditions[0].Value);
        Assert.True(CollectionFilterExpressionResolver.RequiresAnimeContext(state));
    }

    [Fact]
    public void Resolves_new_typed_functions_inside_embedded_templates()
    {
        var anime = new Anime
        {
            Title = "Example",
            AnimeGenres = [new AnimeGenre { Genre = new Genre { Name = "Action" } }, new AnimeGenre { Genre = new Genre { Name = "Drama" } }]
        };
        var state = new FilterState
        {
            Groups = [new FilterGroup { Conditions = [new FilterCondition { Field = FilterField.Title, Value = "Genres: {{join(genres, \"|\")}}" }] }]
        };

        var resolved = CollectionFilterExpressionResolver.Resolve(state, anime: anime);

        Assert.Equal("Genres: Action|Drama", resolved.Groups[0].Conditions[0].Value);
        Assert.True(CollectionFilterExpressionResolver.RequiresAnimeContext(state));
    }

    [Fact]
    public void Resolves_every_value_in_a_multi_value_condition()
    {
        var state = new FilterState
        {
            Groups = [new FilterGroup { Conditions = [new FilterCondition
            {
                Field = FilterField.Genres,
                Values = ["{{lower(\"ACTION\")}}", "{{upper(\"drama\")}}"],
                ValueLogic = ValueLogic.All
            }] }]
        };

        var resolved = CollectionFilterExpressionResolver.Resolve(state);

        Assert.Equal(["action", "DRAMA"], resolved.Groups[0].Conditions[0].Values);
        Assert.Equal(ValueLogic.All, resolved.Groups[0].Conditions[0].ValueLogic);
    }

    [Theory]
    [InlineData("2026-08-01", "2026")]
    [InlineData("2026-10-01", "2027")]
    public void Next_season_year_matches_the_calendar_year_of_next_season(string dateText, string expected)
    {
        var state = new FilterState { Groups = [new FilterGroup { Conditions = [new FilterCondition { Field = FilterField.Year, Value = "nextSeasonYear" }] }] };

        var resolved = CollectionFilterExpressionResolver.Resolve(state, DateTime.Parse(dateText));

        Assert.Equal(expected, resolved.Groups[0].Conditions[0].Value);
    }

    [Theory]
    [InlineData("2026-08-01", "fall", "2026")]
    [InlineData("2026-10-01", "winter", "2027")]
    public void Next_season_and_its_year_resolve_as_a_matching_pair(string dateText, string expectedSeason, string expectedYear)
    {
        var state = new FilterState { Groups = [new FilterGroup { Conditions = [
            new FilterCondition { Field = FilterField.Season, Value = "nextSeason" },
            new FilterCondition { Field = FilterField.Year, Value = "nextSeasonYear" }
        ] }] };

        var resolved = CollectionFilterExpressionResolver.Resolve(state, DateTime.Parse(dateText));

        Assert.Equal(expectedSeason, resolved.Groups[0].Conditions[0].Value);
        Assert.Equal(expectedYear, resolved.Groups[0].Conditions[1].Value);
    }
}
