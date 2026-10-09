using System.Text.Json;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Services.Expressions;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class FilterExpressionTests
{
    [Fact]
    public void Parses_and_round_trips_nested_typed_expression()
    {
        var parsed = FilterExpressionParser.Parse("{{div(mul(episodes, duration), 60)}}");

        Assert.True(parsed.IsValid);
        var operand = new FilterExpression { Source = "{{div(mul(episodes, duration), 60)}}", Root = parsed.Root };
        var json = JsonSerializer.Serialize(operand);
        var restored = JsonSerializer.Deserialize<FilterExpression>(json);

        Assert.IsType<FunctionExpression>(restored!.Root);
        Assert.Equal("div(mul(episodes, duration), 60)", FilterExpressionParser.Format(restored.Root!));
    }

    [Fact]
    public void Evaluates_nested_math_and_collection_functions()
    {
        var anime = new Anime
        {
            Title = "Example", Episodes = 12, Duration = 24,
            AnimeGenres =
            [
                new AnimeGenre { Genre = new Genre { Name = "Action" } },
                new AnimeGenre { Genre = new Genre { Name = "Adventure" } },
                new AnimeGenre { Genre = new Genre { Name = "Comedy" } }
            ]
        };

        var hours = Evaluate("div(mul(episodes, duration), 60)", anime);
        var overlap = Evaluate("length(intersection(genres, createArray(\"Action\", \"Adventure\")))", anime);

        Assert.Equal(4.8m, hours.Value);
        Assert.Equal(2m, overlap.Value);
    }

    [Fact]
    public void Evaluates_dates_against_one_captured_clock()
    {
        var anime = new Anime { Title = "Example", StartDate = new DateTime(2026, 1, 1) };
        var parsed = FilterExpressionParser.Parse("dateDifference(startDate, addDays(utcNow(), 1))");

        var value = FilterExpressionEvaluator.Evaluate(parsed.Root!, anime, new ExpressionEvaluationContext(new DateTime(2026, 1, 10)));

        Assert.Equal(10m, value.Value);
    }

    [Fact]
    public void Computed_left_operand_filters_anime()
    {
        var matching = new Anime
        {
            Title = "Match",
            AnimeGenres = [new AnimeGenre { Genre = new Genre { Name = "Action" } }, new AnimeGenre { Genre = new Genre { Name = "Drama" } }]
        };
        var other = new Anime { Title = "Other", AnimeGenres = [new AnimeGenre { Genre = new Genre { Name = "Action" } }] };
        var condition = new FilterCondition
        {
            LeftExpression = FilterExpressionParser.ParseOperand("length(genres)"),
            Operator = FilterOperator.GreaterThanOrEqual,
            Value = "2"
        };
        var state = new FilterState { Groups = [new FilterGroup { Conditions = [condition] }] };

        var result = FilterEngine.Apply([matching, other], state);

        Assert.Equal("Match", Assert.Single(result).Title);
        Assert.False(CollectionFilterExpressionResolver.RequiresAnimeContext(state));
    }

    [Fact]
    public void Invalid_expression_fails_closed_and_is_reported()
    {
        var condition = new FilterCondition
        {
            LeftExpression = new FilterExpression { Source = "unknown(title)" },
            Operator = FilterOperator.Equals,
            Value = "Example"
        };
        var state = new FilterState { Groups = [new FilterGroup { Conditions = [condition] }] };

        Assert.Empty(FilterEngine.Apply([new Anime { Title = "Example" }], state));
        Assert.Contains("Unknown function", Assert.Single(FilterExpressionValidator.Validate(state)));
    }

    [Theory]
    [InlineData("utcNow()")]
    [InlineData("coalesce(endDate, startDate)")]
    [InlineData("contains(genres, \"Action\")")]
    [InlineData("startOfMonth(startDate)")]
    public void Validates_supported_functions(string source)
    {
        var parsed = FilterExpressionParser.Parse(source);
        Assert.True(parsed.IsValid);
        Assert.Empty(FilterExpressionValidator.Validate(parsed.Root!));
    }

    [Fact]
    public void Formula_catalog_templates_produce_valid_typed_expressions()
    {
        foreach (var function in FilterExpressionCatalog.Functions)
        {
            var inputType = function.FirstArgumentTypes.FirstOrDefault();
            var input = inputType switch
            {
                ExpressionValueType.Number => "episodes",
                ExpressionValueType.Boolean => "hasLocalImage",
                ExpressionValueType.DateTime => "startDate",
                ExpressionValueType.StringCollection => "genres",
                _ => "title"
            };
            var parsed = FilterExpressionParser.Parse(function.ApplyTo(input));

            Assert.True(parsed.IsValid, $"Catalog template for {function.Name} did not parse.");
            Assert.Empty(FilterExpressionValidator.Validate(parsed.Root!));
        }
    }

    [Fact]
    public void Formula_catalog_examples_are_valid()
    {
        foreach (var example in FilterExpressionCatalog.Examples)
        {
            var parsed = FilterExpressionParser.Parse(example.Source);

            Assert.True(parsed.IsValid, $"Catalog example '{example.Name}' did not parse.");
            Assert.Empty(FilterExpressionValidator.Validate(parsed.Root!));
        }
    }

    [Fact]
    public void Formula_tree_editor_replaces_a_nested_argument_without_mutating_the_source_tree()
    {
        var original = FilterExpressionParser.Parse("div(mul(episodes, duration), 60)").Root!;

        var replaced = FilterExpressionTreeEditor.TryReplace(
            original, "0/1", new FieldExpression { Name = "rating" }, out var updated);

        Assert.True(replaced);
        Assert.Equal("div(mul(episodes, duration), 60)", FilterExpressionParser.Format(original));
        Assert.Equal("div(mul(episodes, rating), 60)", FilterExpressionParser.Format(updated));
    }

    [Fact]
    public void Formula_tree_editor_adds_removes_and_reorders_collection_items()
    {
        var original = FilterExpressionParser.Parse("createArray(\"Action\", \"Adventure\")").Root!;

        Assert.True(FilterExpressionTreeEditor.TryAppendArgument(
            original, "", new LiteralExpression { ValueType = ExpressionValueType.String, Value = "Comedy" }, out var added));
        Assert.True(FilterExpressionTreeEditor.TryMoveArgument(added, "2", -1, out var moved, out var movedPath));
        Assert.True(FilterExpressionTreeEditor.TryRemoveArgument(moved, movedPath, out var removed));

        Assert.Equal("createArray(\"Action\", \"Adventure\", \"Comedy\")", FilterExpressionParser.Format(added));
        Assert.Equal("1", movedPath);
        Assert.Equal("createArray(\"Action\", \"Comedy\", \"Adventure\")", FilterExpressionParser.Format(moved));
        Assert.Equal("createArray(\"Action\", \"Adventure\")", FilterExpressionParser.Format(removed));
    }

    [Fact]
    public void Rejects_excessive_expression_depth()
    {
        var source = "title";
        for (var index = 0; index < FilterExpressionParser.MaximumDepth + 2; index++) source = $"trim({source})";

        var parsed = FilterExpressionParser.Parse(source);

        Assert.False(parsed.IsValid);
        Assert.Contains("nesting", parsed.Diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reports_function_argument_type_errors()
    {
        var parsed = FilterExpressionParser.Parse("add(title, genres)");

        var diagnostics = FilterExpressionValidator.Validate(parsed.Root!);

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, diagnostic => Assert.Contains("must be a number", diagnostic.Message));
    }

    [Fact]
    public void Evaluates_nested_logical_functions_and_lazy_if_branches()
    {
        var anime = new Anime
        {
            Title = "Example", Episodes = 24,
            AnimeGenres = [new AnimeGenre { Genre = new Genre { Name = "Action" } }]
        };

        var result = Evaluate("if(and(greater(episodes, 20), contains(genres, \"Action\")), \"long\", string(div(1, 0)))", anime);

        Assert.Equal("long", result.Value);
    }

    [Fact]
    public void Reports_operators_and_formula_values_incompatible_with_the_left_type()
    {
        var numericState = new FilterState
        {
            Groups = [new FilterGroup { Conditions = [new FilterCondition
            {
                LeftExpression = FilterExpressionParser.ParseOperand("length(genres)"),
                Operator = FilterOperator.Contains,
                Value = "2"
            }] }]
        };
        var textState = new FilterState
        {
            Groups = [new FilterGroup { Conditions = [new FilterCondition
            {
                LeftExpression = FilterExpressionParser.ParseOperand("title"),
                Operator = FilterOperator.Equals,
                Value = "{{episodes}}"
            }] }]
        };

        Assert.Contains(FilterExpressionValidator.Validate(numericState), error => error.Contains("cannot be used", StringComparison.Ordinal));
        Assert.Contains(FilterExpressionValidator.Validate(textState), error => error.Contains("cannot compare", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_filter_json_defaults_to_schema_version_one()
    {
        const string json = """{"Groups":[{"Conditions":[{"Field":0,"Operator":0,"Value":"Example"}]}]}""";

        var state = CollectionService.Deserialize(json);

        Assert.Equal(1, state.SchemaVersion);
        Assert.Equal("Example", Assert.Single(Assert.Single(state.Groups).Conditions).Value);
    }

    private static ExpressionValue Evaluate(string source, Anime anime)
    {
        var parsed = FilterExpressionParser.Parse(source);
        Assert.True(parsed.IsValid);
        Assert.Empty(FilterExpressionValidator.Validate(parsed.Root!));
        return FilterExpressionEvaluator.Evaluate(parsed.Root!, anime, new ExpressionEvaluationContext(new DateTime(2026, 1, 1)));
    }
}

public sealed class FilterExpressionQueryTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Saving_an_automatic_collection_versions_the_filter_schema()
    {
        await using var db = CreateDb();
        var service = new CollectionService(db);
        var state = State("length(genres)", FilterOperator.GreaterThan, "1");

        var collection = await service.CreateAsync("Computed", null, CollectionType.Automatic, state);
        var saved = CollectionService.Deserialize(collection.FilterJson);

        Assert.Equal(2, saved.SchemaVersion);
        Assert.Equal(2, state.SchemaVersion);
        Assert.NotNull(Assert.Single(Assert.Single(saved.Groups).Conditions).LeftExpression);
    }

    [Fact]
    public async Task Saving_an_invalid_automatic_collection_is_rejected()
    {
        await using var db = CreateDb();
        var service = new CollectionService(db);
        var state = State("unknown(title)", FilterOperator.Equals, "Example");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync("Invalid", null, CollectionType.Automatic, state));

        Assert.Contains("Unknown function", exception.Message);
        Assert.Empty(db.AnimeCollections);
    }

    [Fact]
    public async Task Computed_collection_length_executes_in_sqlite()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var action = new Genre { Name = "Action" };
        var drama = new Genre { Name = "Drama" };
        db.Animes.AddRange(
            new Anime { Title = "Match", SeasonId = season.Id, AnimeGenres = [new() { Genre = action }, new() { Genre = drama }] },
            new Anime { Title = "Other", SeasonId = season.Id, AnimeGenres = [new() { Genre = action }] });
        await db.SaveChangesAsync();
        var state = State("length(genres)", FilterOperator.GreaterThanOrEqual, "2");

        var query = FilterQueryBuilder.Apply(db.Animes.AsNoTracking(), state).Select(anime => anime.Title);
        var matches = await query.ToListAsync();

        Assert.True(matches.SequenceEqual(["Match"]), query.ToQueryString());
        Assert.False(CollectionFilterExpressionResolver.RequiresAnimeContext(state));
    }

    [Fact]
    public async Task Computed_runtime_math_matches_between_sqlite_and_memory()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.AddRange(
            new Anime { Title = "Short", SeasonId = season.Id, Episodes = 12, Duration = 24 },
            new Anime { Title = "Long", SeasonId = season.Id, Episodes = 50, Duration = 24 });
        await db.SaveChangesAsync();
        var state = State("div(mul(episodes, duration), 60)", FilterOperator.LessThan, "10");

        var fromDatabase = await FilterQueryBuilder.Apply(db.Animes.AsNoTracking(), state).Select(anime => anime.Title).ToListAsync();
        var loaded = await db.Animes.AsNoTracking().ToListAsync();
        var fromMemory = FilterEngine.Apply(loaded, state).Select(anime => anime.Title).ToList();

        Assert.Equal(["Short"], fromDatabase);
        Assert.Equal(fromDatabase, fromMemory);
    }

    [Fact]
    public async Task Collection_intersection_executes_in_sqlite()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var action = new Genre { Name = "Action" };
        var adventure = new Genre { Name = "Adventure" };
        var drama = new Genre { Name = "Drama" };
        db.Animes.AddRange(
            new Anime { Title = "Match", SeasonId = season.Id, AnimeGenres = [new() { Genre = action }, new() { Genre = adventure }] },
            new Anime { Title = "Other", SeasonId = season.Id, AnimeGenres = [new() { Genre = drama }] });
        await db.SaveChangesAsync();
        var state = State("length(intersection(genres, createArray(\"Action\", \"Adventure\")))", FilterOperator.Equals, "2");

        var query = FilterQueryBuilder.Apply(db.Animes.AsNoTracking(), state).Select(anime => anime.Title);
        var matches = await query.ToListAsync();

        Assert.True(matches.SequenceEqual(["Match"]), query.ToQueryString());
        Assert.False(CollectionFilterExpressionResolver.RequiresAnimeContext(state));
    }

    [Fact]
    public async Task Nested_logical_functions_execute_in_sqlite()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var action = new Genre { Name = "Action" };
        db.Animes.AddRange(
            new Anime { Title = "Match", SeasonId = season.Id, Episodes = 24, AnimeGenres = [new() { Genre = action }] },
            new Anime { Title = "Other", SeasonId = season.Id, Episodes = 12 });
        await db.SaveChangesAsync();
        var state = State("if(and(greater(episodes, 20), contains(genres, \"Action\")), 1, 0)", FilterOperator.Equals, "1");

        var query = FilterQueryBuilder.Apply(db.Animes.AsNoTracking(), state).Select(anime => anime.Title);
        var matches = await query.ToListAsync();

        Assert.True(matches.SequenceEqual(["Match"]), query.ToQueryString());
        Assert.False(CollectionFilterExpressionResolver.RequiresAnimeContext(state));
    }

    [Fact]
    public async Task New_alias_image_and_review_fields_filter_in_sqlite()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.AddRange(
            new Anime
            {
                Title = "Match", SeasonId = season.Id, HasEnglishTitle = true, NamesReviewed = true,
                TitleAliases = [new() { Title = "Hidden Hero" }],
                Images = [new() { Source = "AniList", Type = "Poster", ImageUrl = "https://images.test/poster.jpg" }]
            },
            new Anime { Title = "Other", SeasonId = season.Id });
        await db.SaveChangesAsync();
        var state = new FilterState
        {
            Groups = [new FilterGroup { Conditions =
            [
                new FilterCondition { Field = FilterField.TitleAliases, Operator = FilterOperator.Contains, Value = "Hero" },
                new FilterCondition { Field = FilterField.ImageCount, Operator = FilterOperator.GreaterThan, Value = "0" },
                new FilterCondition { Field = FilterField.HasEnglishTitle, Operator = FilterOperator.Equals, Value = "Yes" },
                new FilterCondition { Field = FilterField.NamesReviewed, Operator = FilterOperator.Equals, Value = "Yes" }
            ] }]
        };

        var matches = await FilterQueryBuilder.Apply(db.Animes.AsNoTracking(), state).Select(anime => anime.Title).ToListAsync();

        Assert.Equal(["Match"], matches);
    }

    private static FilterState State(string left, FilterOperator op, string right) => new()
    {
        Groups = [new FilterGroup { Conditions = [new FilterCondition { LeftExpression = FilterExpressionParser.ParseOperand(left), Operator = op, Value = right }] }]
    };
}
