using AnimeOverseer.Server.Models;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AnimeOverseer.Server.Services.Expressions;

namespace AnimeOverseer.Server.Services;

/// <summary>Resolves relative values stored in automatic collection filters at evaluation time.</summary>
public static partial class CollectionFilterExpressionResolver
{
    [GeneratedRegex("^currentYear\\s*([+-])\\s*(\\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentYearOffset();

    [GeneratedRegex("^(today|currentDate)\\s*([+-])\\s*(\\d+)d$", RegexOptions.IgnoreCase)]
    private static partial Regex DateOffset();

    public static FilterState Resolve(FilterState source, DateTime? now = null, Anime? anime = null)
    {
        var date = (now ?? DateTime.UtcNow).Date;
        return new FilterState
        {
            SchemaVersion = source.SchemaVersion,
            TopLevelLogic = source.TopLevelLogic,
            Groups = source.Groups.Select(group => ResolveGroup(group, date, anime)).ToList()
        };
    }

    private static FilterGroup ResolveGroup(FilterGroup group, DateTime date, Anime? anime) => new()
    {
        Id = group.Id,
        Logic = group.Logic,
        Conditions = group.Conditions.Select(condition => new FilterCondition
        {
            Id = condition.Id, Field = condition.Field, Operator = condition.Operator,
            Value = ResolveValue(condition.Field, condition.Value, date, anime),
            Values = condition.Values.Select(value => ResolveValue(condition.Field, value, date, anime)).ToList(),
            ValueLogic = condition.ValueLogic,
            LeftExpression = condition.LeftExpression,
            RightExpressions = condition.RightExpressions
        }).ToList(),
        Subgroups = group.Subgroups.Select(subgroup => ResolveGroup(subgroup, date, anime)).ToList()
    };

    public static bool RequiresAnimeContext(FilterState state)
    {
        var conditions = state.Groups.SelectMany(AllConditions).ToList();
        if (conditions.Any(condition => condition.EffectiveValues.Any(value =>
                value.Contains("{{", StringComparison.Ordinal) && !value.TrimStart().StartsWith("{{", StringComparison.Ordinal))))
            return true;
        return conditions.Any(FilterExpressionQueryTranslator.UsesExpressions) && !FilterExpressionQueryTranslator.CanTranslate(state);
    }

    private static IEnumerable<FilterCondition> AllConditions(FilterGroup group) =>
        group.Conditions.Concat(group.Subgroups.SelectMany(AllConditions));

    private static string ResolveValue(FilterField field, string value, DateTime today, Anime? anime)
    {
        var expression = value.Trim();
        if (expression.Length == 0) return value;

        if (field == FilterField.Year)
        {
            if (expression.Equals("currentYear", StringComparison.OrdinalIgnoreCase)) return today.Year.ToString(CultureInfo.InvariantCulture);
            if (expression.Equals("nextYear", StringComparison.OrdinalIgnoreCase)) return (today.Year + 1).ToString(CultureInfo.InvariantCulture);
            if (expression.Equals("previousYear", StringComparison.OrdinalIgnoreCase)) return (today.Year - 1).ToString(CultureInfo.InvariantCulture);
            // The next anime season is winter after the autumn quarter, so only
            // then does its matching calendar year advance.
            if (expression.Equals("nextSeasonYear", StringComparison.OrdinalIgnoreCase))
                return (today.Year + (today.Month >= 10 ? 1 : 0)).ToString(CultureInfo.InvariantCulture);
            var offset = CurrentYearOffset().Match(expression);
            if (offset.Success)
            {
                var magnitude = int.Parse(offset.Groups[2].Value, CultureInfo.InvariantCulture);
                return (today.Year + (offset.Groups[1].Value == "+" ? magnitude : -magnitude)).ToString(CultureInfo.InvariantCulture);
            }
        }

        if (field == FilterField.Season)
        {
            var seasons = new[] { "winter", "spring", "summer", "fall" };
            var current = (today.Month - 1) / 3;
            var index = expression.ToLowerInvariant() switch
            {
                "currentseason" => current,
                "nextseason" => (current + 1) % seasons.Length,
                "previousseason" => (current + seasons.Length - 1) % seasons.Length,
                _ => -1
            };
            if (index >= 0) return seasons[index];
            return ResolveStringTemplates(value, anime);
        }

        if (IsStringField(field)) return ResolveStringTemplates(value, anime);

        if (field is FilterField.StartDate or FilterField.EndDate or FilterField.CachedAt or FilterField.CreatedAt or FilterField.ModifiedAt)
        {
            if (expression.Equals("today", StringComparison.OrdinalIgnoreCase) || expression.Equals("currentDate", StringComparison.OrdinalIgnoreCase)) return today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var offset = DateOffset().Match(expression);
            if (offset.Success)
            {
                var days = int.Parse(offset.Groups[3].Value, CultureInfo.InvariantCulture) * (offset.Groups[2].Value == "+" ? 1 : -1);
                return today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        return value;
    }

    private static bool IsStringField(FilterField field) => field is
        FilterField.Title or FilterField.OriginalTitle or FilterField.Synopsis or FilterField.Type or FilterField.Status or
        FilterField.Season or FilterField.Genres or FilterField.Themes or FilterField.Demographics or FilterField.TitleAliases;

    /// <summary>Resolves explicit {{function(...)}} fragments without changing ordinary filter text.</summary>
    private static string ResolveStringTemplates(string value, Anime? anime)
    {
        var result = new StringBuilder();
        var position = 0;
        while (position < value.Length)
        {
            var start = value.IndexOf("{{", position, StringComparison.Ordinal);
            if (start < 0) return result.Append(value, position, value.Length - position).ToString();
            result.Append(value, position, start - position);
            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0) return result.Append(value, start, value.Length - start).ToString();

            var source = value[(start + 2)..end];
            result.Append(TryEvaluateStringExpression(source, anime, out var resolved) || TryEvaluateTypedExpression(source, anime, out resolved)
                ? resolved : value[start..(end + 2)]);
            position = end + 2;
        }
        return result.ToString();
    }

    private static bool TryEvaluateStringExpression(string source, Anime? anime, out string result)
    {
        result = string.Empty;
        if (anime is not null && TryGetAnimeField(anime, source.Trim(), out result)) return true;
        var open = source.IndexOf('(');
        if (open <= 0 || !source.TrimEnd().EndsWith(')')) return false;
        var name = source[..open].Trim().ToLowerInvariant();
        var arguments = SplitArguments(source[(open + 1)..source.LastIndexOf(')')]);
        if (arguments is null) return false;
        var values = arguments.Select(argument => ResolveArgument(argument, anime)).ToArray();

        switch (name)
        {
            case "lower" when values.Length == 1: result = values[0].ToLowerInvariant(); return true;
            case "upper" when values.Length == 1: result = values[0].ToUpperInvariant(); return true;
            case "trim" when values.Length == 1: result = values[0].Trim(); return true;
            case "concat" when values.Length >= 1: result = string.Concat(values); return true;
            case "replace" when values.Length == 3: result = values[0].Replace(values[1], values[2], StringComparison.Ordinal); return true;
            case "substring" when (values.Length is 2 or 3) && int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start):
                if (start < 0 || start > values[0].Length) return false;
                if (values.Length == 2) { result = values[0][start..]; return true; }
                if (!int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0 || start + length > values[0].Length) return false;
                result = values[0].Substring(start, length); return true;
            default: return false;
        }
    }

    private static bool TryEvaluateTypedExpression(string source, Anime? anime, out string result)
    {
        result = string.Empty;
        if (anime is null) return false;
        var parsed = FilterExpressionParser.Parse(source);
        if (!parsed.IsValid || FilterExpressionValidator.Validate(parsed.Root!).Count > 0 ||
            !FilterExpressionEvaluator.TryEvaluate(parsed.Root!, anime, ExpressionEvaluationContext.Create(), out var value)) return false;
        result = FilterExpressionEvaluator.AsString(value);
        return true;
    }

    private static List<string>? SplitArguments(string source)
    {
        var result = new List<string>();
        var start = 0;
        char quote = '\0';
        var depth = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (quote != '\0')
            {
                if (character == quote && (index == 0 || source[index - 1] != '\\')) quote = '\0';
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character == '(') { depth++; continue; }
            if (character == ')') { if (--depth < 0) return null; continue; }
            if (character == ',' && depth == 0) { result.Add(source[start..index]); start = index + 1; }
        }
        if (quote != '\0' || depth != 0) return null;
        result.Add(source[start..]);
        return result;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '\'' or '"')
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\'", "'");
        return trimmed;
    }

    private static string ResolveArgument(string value, Anime? anime)
    {
        var trimmed = value.Trim();
        var quoted = trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '\'' or '"';
        var literal = Unquote(trimmed);
        if (quoted) return literal;
        if (anime is not null && TryEvaluateStringExpression(literal, anime, out var resolved)) return resolved;
        return literal;
    }

    private static bool TryGetAnimeField(Anime anime, string name, out string value)
    {
        value = name.ToLowerInvariant() switch
        {
            "id" => anime.Id.ToString(CultureInfo.InvariantCulture),
            "title" => anime.Title,
            "originaltitle" => anime.OriginalTitle ?? string.Empty,
            "synopsis" => anime.Synopsis ?? string.Empty,
            "type" => anime.Type ?? string.Empty,
            "status" => anime.Status ?? string.Empty,
            "season" => anime.Season?.Name ?? string.Empty,
            "year" => anime.Season?.Year.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "episodes" => anime.Episodes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "duration" => anime.Duration?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "rating" => anime.Rating?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "malid" => anime.MALId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "anilistid" => anime.AniListId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "kitsuid" => anime.KitsuId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "startdate" => anime.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            "enddate" => anime.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            "cachedat" => anime.CachedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            "createdat" => anime.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "modifiedat" => anime.ModifiedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "genres" => string.Join(", ", anime.AnimeGenres.Select(link => link.Genre?.Name).Where(name => !string.IsNullOrWhiteSpace(name))),
            "themes" => string.Join(", ", anime.AnimeThemes.Select(link => link.Theme?.Name).Where(name => !string.IsNullOrWhiteSpace(name))),
            "demographics" => string.Join(", ", anime.AnimeDemographics.Select(link => link.Demographic?.Name).Where(name => !string.IsNullOrWhiteSpace(name))),
            _ => string.Empty
        };
        return name.ToLowerInvariant() is "id" or "title" or "originaltitle" or "synopsis" or "type" or "status" or "season" or "year" or "episodes" or "duration" or "rating" or "malid" or "anilistid" or "kitsuid" or "startdate" or "enddate" or "cachedat" or "createdat" or "modifiedat" or "genres" or "themes" or "demographics";
    }
}
