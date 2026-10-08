using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services.Expressions;

namespace AnimeOverseer.Server.Services;

public static class FilterEngine
{
    public static List<Anime> Apply(List<Anime> animes, FilterState state, Func<Anime, bool>? isInLibrary = null)
    {
        var activeGroups = state.Groups.Where(FilterState.IsActiveGroup).ToList();
        if (activeGroups.Count == 0) return animes;
        var context = ExpressionEvaluationContext.Create(isInLibrary);
        return state.TopLevelLogic == GroupLogic.And
            ? animes.Where(anime => activeGroups.All(group => MatchGroup(anime, group, context))).ToList()
            : animes.Where(anime => activeGroups.Any(group => MatchGroup(anime, group, context))).ToList();
    }

    private static bool MatchGroup(Anime anime, FilterGroup group, ExpressionEvaluationContext context)
    {
        var matches = group.Conditions.Where(FilterState.IsActiveCondition).Select(condition => MatchCondition(anime, condition, context))
            .Concat(group.Subgroups.Where(FilterState.IsActiveGroup).Select(subgroup => MatchGroup(anime, subgroup, context))).ToList();
        return matches.Count == 0 || (group.Logic == GroupLogic.And ? matches.All(match => match) : matches.Any(match => match));
    }

    private static bool MatchCondition(Anime anime, FilterCondition condition, ExpressionEvaluationContext context)
    {
        if (!FilterExpressionAdapter.TryRoot(FilterExpressionAdapter.Left(condition), out var leftRoot) ||
            !FilterExpressionEvaluator.TryEvaluate(leftRoot, anime, context, out var left))
            return false;

        if (condition.Operator is FilterOperator.ContainsData or FilterOperator.DoesNotContainData)
        {
            var hasData = HasData(left);
            return condition.Operator == FilterOperator.ContainsData ? hasData : !hasData;
        }

        var comparisons = new List<bool>();
        foreach (var rightExpression in FilterExpressionAdapter.Rights(condition))
        {
            if (!FilterExpressionAdapter.TryRoot(rightExpression, out var rightRoot) ||
                !FilterExpressionEvaluator.TryEvaluate(rightRoot, anime, context, out var right))
                comparisons.Add(false);
            else comparisons.Add(Compare(left, condition.Operator, right));
        }

        if (comparisons.Count == 0) return false;
        return condition.ValueLogic == ValueLogic.All ? comparisons.All(value => value) : comparisons.Any(value => value);
    }

    private static bool Compare(ExpressionValue left, FilterOperator op, ExpressionValue right)
    {
        if (left.Type == ExpressionValueType.StringCollection)
            return CompareCollection((IReadOnlyList<string>)left.Value!, op, FilterExpressionEvaluator.AsString(right));
        if ((left.IsNull && right.Type == ExpressionValueType.String || right.IsNull && left.Type == ExpressionValueType.String) &&
            op is FilterOperator.Contains or FilterOperator.NotContains or FilterOperator.StartsWith or FilterOperator.EndsWith or FilterOperator.Equals or FilterOperator.NotEquals)
            return CompareText(FilterExpressionEvaluator.AsString(left), op, FilterExpressionEvaluator.AsString(right));
        if (left.IsNull || right.IsNull)
            return op == FilterOperator.NotEquals && left.IsNull != right.IsNull;

        if (left.Type == ExpressionValueType.Number && TryNumber(right, out var rightNumber))
            return CompareComparable((decimal)left.Value!, op, rightNumber);
        if (left.Type == ExpressionValueType.DateTime && TryDate(right, out var rightDate))
            return CompareComparable(((DateTime)left.Value!).Date, op, rightDate.Date);
        if (left.Type == ExpressionValueType.Boolean && TryBoolean(right, out var rightBoolean))
            return op switch { FilterOperator.Equals => (bool)left.Value! == rightBoolean, FilterOperator.NotEquals => (bool)left.Value! != rightBoolean, _ => false };

        var leftText = FilterExpressionEvaluator.AsString(left);
        var rightText = FilterExpressionEvaluator.AsString(right);
        return CompareText(leftText, op, rightText);
    }

    private static bool CompareText(string leftText, FilterOperator op, string rightText) => op switch
        {
            FilterOperator.Contains => leftText.Contains(rightText, StringComparison.OrdinalIgnoreCase),
            FilterOperator.NotContains => !leftText.Contains(rightText, StringComparison.OrdinalIgnoreCase),
            FilterOperator.StartsWith => leftText.StartsWith(rightText, StringComparison.OrdinalIgnoreCase),
            FilterOperator.EndsWith => leftText.EndsWith(rightText, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Equals => leftText.Equals(rightText, StringComparison.OrdinalIgnoreCase),
            FilterOperator.NotEquals => !leftText.Equals(rightText, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool CompareCollection(IReadOnlyList<string> values, FilterOperator op, string right) => op switch
    {
        FilterOperator.Contains => values.Any(value => value.Contains(right, StringComparison.OrdinalIgnoreCase)),
        FilterOperator.NotContains => values.All(value => !value.Contains(right, StringComparison.OrdinalIgnoreCase)),
        FilterOperator.StartsWith => values.Any(value => value.StartsWith(right, StringComparison.OrdinalIgnoreCase)),
        FilterOperator.EndsWith => values.Any(value => value.EndsWith(right, StringComparison.OrdinalIgnoreCase)),
        FilterOperator.Equals => values.Any(value => value.Equals(right, StringComparison.OrdinalIgnoreCase)),
        FilterOperator.NotEquals => values.All(value => !value.Equals(right, StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    private static bool CompareComparable<T>(T left, FilterOperator op, T right) where T : IComparable<T> => op switch
    {
        FilterOperator.Equals => left.CompareTo(right) == 0,
        FilterOperator.NotEquals => left.CompareTo(right) != 0,
        FilterOperator.GreaterThan => left.CompareTo(right) > 0,
        FilterOperator.LessThan => left.CompareTo(right) < 0,
        FilterOperator.GreaterThanOrEqual => left.CompareTo(right) >= 0,
        FilterOperator.LessThanOrEqual => left.CompareTo(right) <= 0,
        _ => false
    };

    private static bool HasData(ExpressionValue value) => !value.IsNull && value.Type switch
    {
        ExpressionValueType.String => !string.IsNullOrEmpty((string)value.Value!),
        ExpressionValueType.StringCollection => ((IReadOnlyList<string>)value.Value!).Count > 0,
        _ => true
    };

    private static bool TryNumber(ExpressionValue value, out decimal number)
    {
        if (value.Type == ExpressionValueType.Number) { number = (decimal)value.Value!; return true; }
        return decimal.TryParse(FilterExpressionEvaluator.AsString(value), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private static bool TryDate(ExpressionValue value, out DateTime date)
    {
        if (value.Type == ExpressionValueType.DateTime) { date = (DateTime)value.Value!; return true; }
        return DateTime.TryParse(FilterExpressionEvaluator.AsString(value), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out date);
    }

    private static bool TryBoolean(ExpressionValue value, out bool boolean)
    {
        if (value.Type == ExpressionValueType.Boolean) { boolean = (bool)value.Value!; return true; }
        var text = FilterExpressionEvaluator.AsString(value);
        if (text.Equals("yes", StringComparison.OrdinalIgnoreCase)) { boolean = true; return true; }
        if (text.Equals("no", StringComparison.OrdinalIgnoreCase)) { boolean = false; return true; }
        return bool.TryParse(text, out boolean);
    }
}
