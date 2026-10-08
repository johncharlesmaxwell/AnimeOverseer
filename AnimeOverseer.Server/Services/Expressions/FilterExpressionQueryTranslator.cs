using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services.Expressions;

/// <summary>Translates the deterministic expression subset supported by EF/SQLite.</summary>
public static class FilterExpressionQueryTranslator
{
    public static bool CanTranslate(FilterState state)
    {
        var parameter = Expression.Parameter(typeof(Anime), "anime");
        return state.Groups.SelectMany(AllConditions).Where(FilterState.IsActiveCondition)
            .All(condition => !UsesExpressions(condition) || TryBuildCondition(condition, parameter, DateTime.UtcNow, out _));
    }

    public static bool TryBuildCondition(FilterCondition condition, ParameterExpression anime, DateTime utcNow, out Expression expression)
    {
        expression = Expression.Constant(false);
        if (!FilterExpressionAdapter.TryRoot(FilterExpressionAdapter.Left(condition), out var leftRoot) ||
            !TryTranslate(leftRoot, anime, utcNow, out var left)) return false;

        if (condition.Operator is FilterOperator.ContainsData or FilterOperator.DoesNotContainData)
        {
            expression = condition.Operator == FilterOperator.ContainsData ? left.HasValue : Expression.Not(left.HasValue);
            return true;
        }

        var comparisons = new List<Expression>();
        foreach (var rightOperand in FilterExpressionAdapter.Rights(condition))
        {
            if (!FilterExpressionAdapter.TryRoot(rightOperand, out var rightRoot) || !TryTranslate(rightRoot, anime, utcNow, out var right) ||
                !TryCompare(left, condition.Operator, right, out var comparison)) return false;
            comparisons.Add(comparison);
        }
        if (comparisons.Count == 0) return false;
        expression = comparisons.Count == 1 ? comparisons[0] : comparisons.Aggregate(condition.ValueLogic == ValueLogic.All
            ? (Func<Expression, Expression, Expression>)Expression.AndAlso : Expression.OrElse);
        return true;
    }

    public static bool UsesExpressions(FilterCondition condition) => condition.LeftExpression is not null || condition.RightExpressions.Count > 0 ||
        condition.EffectiveValues.Any(value => value.TrimStart().StartsWith("{{", StringComparison.Ordinal));

    private static bool TryTranslate(ExpressionNode node, ParameterExpression anime, DateTime utcNow, out QueryValue result)
    {
        result = default;
        switch (node)
        {
            case LiteralExpression literal:
                return TryLiteral(literal, out result);
            case FieldExpression field:
                return TryField(field.Name, anime, out result);
            case FunctionExpression function:
                return TryFunction(function, anime, utcNow, out result);
            default:
                return false;
        }
    }

    private static bool TryLiteral(LiteralExpression literal, out QueryValue result)
    {
        result = default;
        switch (literal.ValueType)
        {
            case ExpressionValueType.Null:
                result = new(ExpressionValueType.Null, Expression.Constant(string.Empty), Expression.Constant(false)); return true;
            case ExpressionValueType.String:
                result = Scalar(ExpressionValueType.String, Expression.Constant(literal.Value ?? string.Empty)); return true;
            case ExpressionValueType.Number when decimal.TryParse(literal.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                result = Scalar(ExpressionValueType.Number, Expression.Constant(number)); return true;
            case ExpressionValueType.Boolean when bool.TryParse(literal.Value, out var boolean):
                result = Scalar(ExpressionValueType.Boolean, Expression.Constant(boolean)); return true;
            case ExpressionValueType.DateTime when DateTime.TryParse(literal.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date):
                result = Scalar(ExpressionValueType.DateTime, Expression.Constant(date)); return true;
            default: return false;
        }
    }

    private static bool TryField(string name, ParameterExpression anime, out QueryValue result)
    {
        result = default;
        var normalized = name.ToLowerInvariant();
        if (normalized is "genres" or "themes" or "demographics" or "titlealiases")
        {
            var (collectionName, navigationName) = normalized switch
            {
                "genres" => ("AnimeGenres", "Genre"), "themes" => ("AnimeThemes", "Theme"),
                "demographics" => ("AnimeDemographics", "Demographic"), _ => ("TitleAliases", string.Empty)
            };
            var collection = Expression.Property(anime, collectionName);
            var itemType = collection.Type.GetGenericArguments()[0];
            var item = Expression.Parameter(itemType, "item");
            var nameExpression = normalized == "titlealiases"
                ? Expression.Property(item, "Title")
                : Expression.Property(Expression.Property(item, navigationName), "Name");
            result = new(ExpressionValueType.StringCollection, collection, Any(collection), new CollectionValue(collection, item, nameExpression));
            return true;
        }

        var property = normalized switch
        {
            "id" => "Id", "title" => "Title", "originaltitle" => "OriginalTitle", "synopsis" => "Synopsis", "type" => "Type", "status" => "Status",
            "episodes" => "Episodes", "duration" => "Duration", "rating" => "Rating", "malid" => "MALId", "anilistid" => "AniListId", "kitsuid" => "KitsuId",
            "startdate" => "StartDate", "enddate" => "EndDate", "cachedat" => "CachedAt", "createdat" => "CreatedAt", "modifiedat" => "ModifiedAt",
            "hasenglishtitle" => "HasEnglishTitle", "namesreviewed" => "NamesReviewed", _ => string.Empty
        };
        if (normalized is "season" or "year")
        {
            var season = Expression.Property(anime, "Season");
            var value = Expression.Property(season, normalized == "season" ? "Name" : "Year");
            result = normalized == "season" ? StringValue(value) : NumberValue(value);
            return true;
        }
        if (normalized == "imagecount")
        {
            var images = Expression.Property(anime, "Images"); result = NumberValue(Count(images)); return true;
        }
        if (property.Length == 0 || normalized is "haslocalimage" or "inlibrary") return false;
        var expression = Expression.Property(anime, property);
        if (expression.Type == typeof(string)) result = StringValue(expression);
        else if (expression.Type == typeof(int) || expression.Type == typeof(int?) || expression.Type == typeof(decimal) || expression.Type == typeof(decimal?)) result = NumberValue(expression);
        else if (expression.Type == typeof(DateTime) || expression.Type == typeof(DateTime?)) result = DateValue(expression);
        else if (expression.Type == typeof(bool)) result = Scalar(ExpressionValueType.Boolean, expression);
        else return false;
        return true;
    }

    private static bool TryFunction(FunctionExpression function, ParameterExpression anime, DateTime utcNow, out QueryValue result)
    {
        result = default;
        var name = function.Name.ToLowerInvariant() switch { "lower" => "tolower", "upper" => "toupper", var value => value };
        if (name == "utcnow" && function.Arguments.Count == 0) { result = Scalar(ExpressionValueType.DateTime, Expression.Constant(utcNow)); return true; }
        if (name == "length" && function.Arguments.Count == 1 && TryIntersectionCount(function.Arguments[0], anime, utcNow, out result)) return true;

        var arguments = new List<QueryValue>();
        foreach (var argument in function.Arguments)
        {
            if (!TryTranslate(argument, anime, utcNow, out var translated)) return false;
            arguments.Add(translated);
        }

        if (name == "length" && function.Arguments.Count == 1)
        {
            var argument = arguments[0];
            if (argument.Type == ExpressionValueType.StringCollection) { result = NumberValue(Count(argument.Collection!.Value.Collection)); return true; }
            if (argument.Type == ExpressionValueType.String) { result = NumberValue(Expression.Property(SafeString(argument), nameof(string.Length)), argument.HasValue); return true; }
            return false;
        }
        if (name == "empty" && arguments.Count == 1) { result = Scalar(ExpressionValueType.Boolean, Expression.Not(arguments[0].HasValue)); return true; }
        if (name is "and" or "or" && arguments.Count >= 2 && arguments.All(value => value.Type == ExpressionValueType.Boolean))
        {
            result = Scalar(ExpressionValueType.Boolean, arguments.Select(value => value.Value).Aggregate(name == "and"
                ? (Func<Expression, Expression, Expression>)Expression.AndAlso : Expression.OrElse)); return true;
        }
        if (name == "not" && arguments.Count == 1 && arguments[0].Type == ExpressionValueType.Boolean)
        {
            result = Scalar(ExpressionValueType.Boolean, Expression.Not(arguments[0].Value)); return true;
        }
        if (name is "startswith" or "endswith" && arguments.Count == 2 && arguments.All(value => value.Type == ExpressionValueType.String))
        {
            var method = name == "startswith" ? nameof(string.StartsWith) : nameof(string.EndsWith);
            result = Scalar(ExpressionValueType.Boolean, Expression.Call(Expression.Call(SafeString(arguments[0]), nameof(string.ToLower), Type.EmptyTypes), method, Type.EmptyTypes,
                Expression.Call(SafeString(arguments[1]), nameof(string.ToLower), Type.EmptyTypes))); return true;
        }
        if (name is "equals" or "greater" or "greaterorequals" or "less" or "lessorequals" && arguments.Count == 2)
        {
            var op = name switch
            {
                "equals" => FilterOperator.Equals, "greater" => FilterOperator.GreaterThan,
                "greaterorequals" => FilterOperator.GreaterThanOrEqual, "less" => FilterOperator.LessThan,
                _ => FilterOperator.LessThanOrEqual
            };
            Expression comparison;
            if (arguments[0].Type == ExpressionValueType.DateTime && arguments[1].Type == ExpressionValueType.DateTime)
                comparison = WithNulls(arguments[0], arguments[1], op, Compare(arguments[0].Value, op, arguments[1].Value));
            else if (!TryCompare(arguments[0], op, arguments[1], out comparison)) return false;
            result = Scalar(ExpressionValueType.Boolean, comparison); return true;
        }
        if (name is "tolower" or "toupper" or "trim" && arguments.Count == 1 && arguments[0].Type == ExpressionValueType.String)
        {
            var method = name switch { "tolower" => nameof(string.ToLower), "toupper" => nameof(string.ToUpper), _ => nameof(string.Trim) };
            result = new(ExpressionValueType.String, Expression.Call(SafeString(arguments[0]), method, Type.EmptyTypes), arguments[0].HasValue); return true;
        }
        if (name == "replace" && arguments.Count == 3 && arguments.All(value => value.Type == ExpressionValueType.String))
        {
            result = new(ExpressionValueType.String, Expression.Call(SafeString(arguments[0]), nameof(string.Replace), Type.EmptyTypes, SafeString(arguments[1]), SafeString(arguments[2])), arguments[0].HasValue); return true;
        }
        if (name is "add" or "sub" or "mul" or "div" or "mod" && arguments.Count == 2 && arguments.All(value => CanNumber(value)))
        {
            var left = AsNumber(arguments[0]); var right = AsNumber(arguments[1]);
            var value = name switch { "add" => Expression.Add(left, right), "sub" => Expression.Subtract(left, right), "mul" => Expression.Multiply(left, right), "div" => Expression.Divide(left, right), _ => Expression.Modulo(left, right) };
            var hasValue = Expression.AndAlso(arguments[0].HasValue, arguments[1].HasValue);
            if (name is "div" or "mod") hasValue = Expression.AndAlso(hasValue, Expression.NotEqual(right, Expression.Constant(0m)));
            result = new(ExpressionValueType.Number, value, hasValue); return true;
        }
        if (name == "coalesce" && arguments.Count >= 1 && arguments.Select(value => value.Type).Distinct().Count() == 1)
        {
            var value = arguments[^1].Value;
            for (var index = arguments.Count - 2; index >= 0; index--) value = Expression.Condition(arguments[index].HasValue, arguments[index].Value, value);
            result = new(arguments[0].Type, value, arguments.Select(argument => argument.HasValue).Aggregate(Expression.OrElse)); return true;
        }
        if (name == "if" && arguments.Count == 3 && arguments[0].Type == ExpressionValueType.Boolean && arguments[1].Type == arguments[2].Type)
        {
            result = new(arguments[1].Type, Expression.Condition(arguments[0].Value, arguments[1].Value, arguments[2].Value),
                Expression.Condition(arguments[0].Value, arguments[1].HasValue, arguments[2].HasValue)); return true;
        }
        if (name == "contains" && arguments.Count == 2 && TryContains(arguments[0], arguments[1], out var contains))
        {
            result = Scalar(ExpressionValueType.Boolean, contains); return true;
        }
        if (name == "adddays" && arguments.Count == 2 && arguments[0].Type == ExpressionValueType.DateTime && CanNumber(arguments[1]))
        {
            result = new(ExpressionValueType.DateTime, Expression.Call(arguments[0].Value, nameof(DateTime.AddDays), Type.EmptyTypes, Expression.Convert(AsNumber(arguments[1]), typeof(double))),
                Expression.AndAlso(arguments[0].HasValue, arguments[1].HasValue)); return true;
        }
        if (name is "dayofmonth" or "dayofweek" or "dayofyear" && arguments.Count == 1 && arguments[0].Type == ExpressionValueType.DateTime)
        {
            var property = name switch { "dayofmonth" => nameof(DateTime.Day), "dayofweek" => nameof(DateTime.DayOfWeek), _ => nameof(DateTime.DayOfYear) };
            Expression value = Expression.Property(arguments[0].Value, property);
            if (value.Type.IsEnum) value = Expression.Convert(value, typeof(int));
            result = NumberValue(value, arguments[0].HasValue); return true;
        }
        if (name == "startofday" && arguments.Count == 1 && arguments[0].Type == ExpressionValueType.DateTime)
        {
            result = new(ExpressionValueType.DateTime, Expression.Property(arguments[0].Value, nameof(DateTime.Date)), arguments[0].HasValue); return true;
        }
        return false;
    }

    private static bool TryIntersectionCount(ExpressionNode node, ParameterExpression anime, DateTime utcNow, out QueryValue result)
    {
        result = default;
        if (node is not FunctionExpression { Name: var name, Arguments.Count: >= 2 } intersection || !name.Equals("intersection", StringComparison.OrdinalIgnoreCase) ||
            !TryTranslate(intersection.Arguments[0], anime, utcNow, out var collection) || collection.Type != ExpressionValueType.StringCollection) return false;
        var requested = new List<string>();
        foreach (var argument in intersection.Arguments.Skip(1))
        {
            if (argument is not FunctionExpression array || !(array.Name.Equals("array", StringComparison.OrdinalIgnoreCase) || array.Name.Equals("createArray", StringComparison.OrdinalIgnoreCase))) return false;
            foreach (var item in array.Arguments)
            {
                if (item is not LiteralExpression { ValueType: ExpressionValueType.String } literal) return false;
                requested.Add((literal.Value ?? string.Empty).ToLowerInvariant());
            }
        }
        var metadata = collection.Collection!.Value;
        var lowerName = Expression.Call(metadata.Name, nameof(string.ToLower), Type.EmptyTypes);
        var contains = Expression.Call(Expression.Constant(requested), typeof(List<string>).GetMethod(nameof(List<string>.Contains), [typeof(string)])!, lowerName);
        result = NumberValue(Count(metadata.Collection, Expression.Lambda(contains, metadata.Item)));
        return true;
    }

    private static bool TryContains(QueryValue source, QueryValue search, out Expression expression)
    {
        expression = Expression.Constant(false);
        if (search.Type != ExpressionValueType.String) return false;
        if (source.Type == ExpressionValueType.String)
        {
            expression = Expression.Call(Expression.Call(SafeString(source), nameof(string.ToLower), Type.EmptyTypes), nameof(string.Contains), Type.EmptyTypes,
                Expression.Call(SafeString(search), nameof(string.ToLower), Type.EmptyTypes)); return true;
        }
        if (source.Type != ExpressionValueType.StringCollection) return false;
        var metadata = source.Collection!.Value;
        var match = Expression.Equal(Expression.Call(metadata.Name, nameof(string.ToLower), Type.EmptyTypes), Expression.Call(SafeString(search), nameof(string.ToLower), Type.EmptyTypes));
        expression = Any(metadata.Collection, Expression.Lambda(match, metadata.Item)); return true;
    }

    private static bool TryCompare(QueryValue left, FilterOperator op, QueryValue right, out Expression expression)
    {
        expression = Expression.Constant(false);
        if (left.Type == ExpressionValueType.StringCollection)
        {
            if (right.Type != ExpressionValueType.String) return false;
            var metadata = left.Collection!.Value;
            var name = Expression.Call(metadata.Name, nameof(string.ToLower), Type.EmptyTypes);
            var search = Expression.Call(SafeString(right), nameof(string.ToLower), Type.EmptyTypes);
            Expression match = op switch
            {
                FilterOperator.Contains or FilterOperator.NotContains => Expression.Call(name, nameof(string.Contains), Type.EmptyTypes, search),
                FilterOperator.StartsWith => Expression.Call(name, nameof(string.StartsWith), Type.EmptyTypes, search),
                FilterOperator.EndsWith => Expression.Call(name, nameof(string.EndsWith), Type.EmptyTypes, search),
                _ => Expression.Equal(name, search)
            };
            var any = Any(metadata.Collection, Expression.Lambda(match, metadata.Item));
            expression = op is FilterOperator.NotContains or FilterOperator.NotEquals ? Expression.Not(any) : any;
            return true;
        }
        if (left.Type == ExpressionValueType.Number && TryAlignNumbers(left, right, out var leftNumber, out var rightNumber))
        {
            expression = WithNulls(left, right, op, Compare(leftNumber, op, rightNumber)); return true;
        }
        if (left.Type == ExpressionValueType.DateTime && TryAsDate(right, out var rightDate))
        {
            expression = WithNulls(left, right, op, Compare(Expression.Property(left.Value, nameof(DateTime.Date)), op, Expression.Property(rightDate, nameof(DateTime.Date)))); return true;
        }
        if (left.Type == ExpressionValueType.Boolean && TryAsBoolean(right, out var rightBoolean))
        {
            expression = WithNulls(left, right, op, op == FilterOperator.NotEquals ? Expression.NotEqual(left.Value, rightBoolean) : Expression.Equal(left.Value, rightBoolean)); return op is FilterOperator.Equals or FilterOperator.NotEquals;
        }
        if (left.Type == ExpressionValueType.String)
        {
            if (right.Type != ExpressionValueType.String) return false;
            var leftText = Expression.Call(SafeString(left), nameof(string.ToLower), Type.EmptyTypes);
            var rightText = Expression.Call(SafeString(right), nameof(string.ToLower), Type.EmptyTypes);
            Expression? comparison = op switch
            {
                FilterOperator.Contains or FilterOperator.NotContains => Expression.Call(leftText, nameof(string.Contains), Type.EmptyTypes, rightText),
                FilterOperator.StartsWith => Expression.Call(leftText, nameof(string.StartsWith), Type.EmptyTypes, rightText),
                FilterOperator.EndsWith => Expression.Call(leftText, nameof(string.EndsWith), Type.EmptyTypes, rightText),
                FilterOperator.Equals => Expression.Equal(leftText, rightText), FilterOperator.NotEquals => Expression.NotEqual(leftText, rightText), _ => null
            };
            if (comparison is null) return false;
            expression = op == FilterOperator.NotContains ? Expression.Not(comparison) : comparison; return true;
        }
        return false;
    }

    private static Expression WithNulls(QueryValue left, QueryValue right, FilterOperator op, Expression comparison)
    {
        var both = Expression.AndAlso(left.HasValue, right.HasValue);
        return op == FilterOperator.NotEquals
            ? Expression.OrElse(Expression.ExclusiveOr(left.HasValue, right.HasValue), Expression.AndAlso(both, comparison))
            : Expression.AndAlso(both, comparison);
    }
    private static Expression Compare(Expression left, FilterOperator op, Expression right) => op switch
    {
        FilterOperator.Equals => Expression.Equal(left, right), FilterOperator.NotEquals => Expression.NotEqual(left, right),
        FilterOperator.GreaterThan => Expression.GreaterThan(left, right), FilterOperator.LessThan => Expression.LessThan(left, right),
        FilterOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(left, right), FilterOperator.LessThanOrEqual => Expression.LessThanOrEqual(left, right),
        _ => Expression.Constant(false)
    };
    private static bool TryAsNumber(QueryValue value, out Expression expression)
    {
        if (CanNumber(value)) { expression = AsNumber(value); return true; }
        if (value.Type == ExpressionValueType.String && value.Value is ConstantExpression { Value: string text } && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { expression = Expression.Constant(number); return true; }
        expression = Expression.Constant(0m); return false;
    }
    private static bool TryAlignNumbers(QueryValue left, QueryValue right, out Expression leftExpression, out Expression rightExpression)
    {
        leftExpression = left.Value;
        rightExpression = Expression.Constant(0);
        if (left.Value.Type == typeof(int) && right.Type == ExpressionValueType.String && right.Value is ConstantExpression { Value: string text }
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            rightExpression = Expression.Constant(integer);
            return true;
        }
        if (!TryAsNumber(right, out rightExpression)) return false;
        leftExpression = AsNumber(left);
        if (rightExpression.Type != typeof(decimal)) rightExpression = Expression.Convert(rightExpression, typeof(decimal));
        return true;
    }
    private static bool TryAsDate(QueryValue value, out Expression expression)
    {
        if (value.Type == ExpressionValueType.DateTime) { expression = value.Value; return true; }
        if (value.Type == ExpressionValueType.String && value.Value is ConstantExpression { Value: string text } && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) { expression = Expression.Constant(date); return true; }
        expression = Expression.Constant(default(DateTime)); return false;
    }
    private static bool TryAsBoolean(QueryValue value, out Expression expression)
    {
        if (value.Type == ExpressionValueType.Boolean) { expression = value.Value; return true; }
        if (value.Type == ExpressionValueType.String && value.Value is ConstantExpression { Value: string text })
        {
            if (text.Equals("yes", StringComparison.OrdinalIgnoreCase)) { expression = Expression.Constant(true); return true; }
            if (text.Equals("no", StringComparison.OrdinalIgnoreCase)) { expression = Expression.Constant(false); return true; }
            if (bool.TryParse(text, out var boolean)) { expression = Expression.Constant(boolean); return true; }
        }
        expression = Expression.Constant(false); return false;
    }
    private static bool CanNumber(QueryValue value) => value.Type == ExpressionValueType.Number;
    private static Expression AsNumber(QueryValue value) => value.Value.Type == typeof(decimal) ? value.Value : Expression.Convert(value.Value, typeof(decimal));
    private static Expression SafeString(QueryValue value) => value.Value.Type == typeof(string)
        ? Expression.Coalesce(value.Value, Expression.Constant(string.Empty)) : Expression.Constant(string.Empty);
    private static QueryValue StringValue(Expression expression) => new(ExpressionValueType.String, expression,
        Expression.AndAlso(Expression.NotEqual(expression, Expression.Constant(null, typeof(string))), Expression.NotEqual(expression, Expression.Constant(string.Empty))));
    private static QueryValue NumberValue(Expression expression, Expression? hasValue = null)
    {
        if (Nullable.GetUnderlyingType(expression.Type) is Type underlying)
            return new(ExpressionValueType.Number, Expression.Property(expression, "Value"), Expression.Property(expression, "HasValue"));
        return new(ExpressionValueType.Number, expression, hasValue ?? Expression.Constant(true));
    }
    private static QueryValue DateValue(Expression expression)
    {
        if (Nullable.GetUnderlyingType(expression.Type) is not null)
            return new(ExpressionValueType.DateTime, Expression.Property(expression, "Value"), Expression.Property(expression, "HasValue"));
        return Scalar(ExpressionValueType.DateTime, expression);
    }
    private static QueryValue Scalar(ExpressionValueType type, Expression expression) => new(type, expression, Expression.Constant(true));
    private static Expression Any(Expression collection) => CallEnumerable(nameof(Enumerable.Any), collection);
    private static Expression Any(Expression collection, LambdaExpression predicate) => CallEnumerable(nameof(Enumerable.Any), collection, predicate);
    private static Expression Count(Expression collection) => CallEnumerable(nameof(Enumerable.Count), collection);
    private static Expression Count(Expression collection, LambdaExpression predicate) => CallEnumerable(nameof(Enumerable.Count), collection, predicate);
    private static MethodCallExpression CallEnumerable(string methodName, Expression collection, LambdaExpression? predicate = null)
    {
        var parameterCount = predicate is null ? 1 : 2;
        var method = typeof(Enumerable).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .First(candidate => candidate.Name == methodName && candidate.GetParameters().Length == parameterCount).MakeGenericMethod(collection.Type.GetGenericArguments()[0]);
        return predicate is null ? Expression.Call(method, collection) : Expression.Call(method, collection, predicate);
    }
    private static IEnumerable<FilterCondition> AllConditions(FilterGroup group) => group.Conditions.Concat(group.Subgroups.SelectMany(AllConditions));
    private readonly record struct CollectionValue(Expression Collection, ParameterExpression Item, Expression Name);
    private readonly record struct QueryValue(ExpressionValueType Type, Expression Value, Expression HasValue, CollectionValue? Collection = null);
}
