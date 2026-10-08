using System.Globalization;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services.Expressions;

public static class FilterExpressionAdapter
{
    public static FilterExpression Left(FilterCondition condition) => condition.LeftExpression ?? new FilterExpression
    {
        Source = FieldName(condition.Field),
        Root = new FieldExpression { Name = FieldName(condition.Field) }
    };

    public static IReadOnlyList<FilterExpression> Rights(FilterCondition condition)
    {
        if (condition.RightExpressions.Count > 0) return condition.RightExpressions;
        return condition.EffectiveValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => LegacyRight(condition.Field, value.Trim()))
            .ToList();
    }

    public static bool TryRoot(FilterExpression expression, out ExpressionNode root)
    {
        if (expression.Root is not null) { root = expression.Root; return true; }
        var parsed = FilterExpressionParser.Parse(expression.Source);
        root = parsed.Root!;
        return parsed.IsValid;
    }

    public static string FieldName(FilterField field) => field switch
    {
        FilterField.OriginalTitle => "originalTitle",
        FilterField.MalId => "malId",
        FilterField.AniListId => "aniListId",
        FilterField.KitsuId => "kitsuId",
        FilterField.StartDate => "startDate",
        FilterField.EndDate => "endDate",
        FilterField.CachedAt => "cachedAt",
        FilterField.CreatedAt => "createdAt",
        FilterField.ModifiedAt => "modifiedAt",
        FilterField.InLibrary => "inLibrary",
        _ => char.ToLowerInvariant(field.ToString()[0]) + field.ToString()[1..]
    };

    private static FilterExpression LegacyRight(FilterField field, string source)
    {
        if (source.StartsWith("{{", StringComparison.Ordinal) && source.EndsWith("}}", StringComparison.Ordinal))
            return FilterExpressionParser.ParseOperand(source);

        ExpressionNode root;
        if (field is FilterField.Episodes or FilterField.Duration or FilterField.Rating or FilterField.Year or FilterField.MalId or FilterField.AniListId or FilterField.KitsuId
            && decimal.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            root = new LiteralExpression { ValueType = ExpressionValueType.Number, Value = source };
        else if (field == FilterField.InLibrary)
            root = new LiteralExpression { ValueType = ExpressionValueType.Boolean, Value = (source.Equals("yes", StringComparison.OrdinalIgnoreCase) || source.Equals("true", StringComparison.OrdinalIgnoreCase)).ToString() };
        else if (field is FilterField.StartDate or FilterField.EndDate or FilterField.CachedAt or FilterField.CreatedAt or FilterField.ModifiedAt
            && DateTime.TryParse(source, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            root = new LiteralExpression { ValueType = ExpressionValueType.DateTime, Value = date.ToString("O", CultureInfo.InvariantCulture) };
        else
            root = new LiteralExpression { ValueType = ExpressionValueType.String, Value = source };
        return new FilterExpression { Source = source, Root = root };
    }
}
