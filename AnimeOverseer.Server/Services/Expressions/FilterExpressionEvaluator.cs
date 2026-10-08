using System.Globalization;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services.Expressions;

public sealed record ExpressionEvaluationContext(DateTime UtcNow, Func<Anime, bool>? IsInLibrary = null)
{
    public static ExpressionEvaluationContext Create(Func<Anime, bool>? isInLibrary = null) => new(DateTime.UtcNow, isInLibrary);
}

public static class FilterExpressionEvaluator
{
    public static bool TryEvaluate(ExpressionNode expression, Anime anime, ExpressionEvaluationContext context, out ExpressionValue value)
    {
        try
        {
            value = Evaluate(expression, anime, context);
            return true;
        }
        catch (Exception)
        {
            value = ExpressionValue.Null;
            return false;
        }
    }

    public static ExpressionValue Evaluate(ExpressionNode expression, Anime anime, ExpressionEvaluationContext context) => expression switch
    {
        LiteralExpression literal => EvaluateLiteral(literal),
        FieldExpression field => EvaluateField(field.Name, anime, context),
        FunctionExpression function => EvaluateFunction(function, anime, context),
        _ => throw new ExpressionEvaluationException("Unknown expression node.")
    };

    private static ExpressionValue EvaluateLiteral(LiteralExpression literal) => literal.ValueType switch
    {
        ExpressionValueType.Null => ExpressionValue.Null,
        ExpressionValueType.String => new(ExpressionValueType.String, literal.Value ?? string.Empty),
        ExpressionValueType.Number when decimal.TryParse(literal.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => new(ExpressionValueType.Number, number),
        ExpressionValueType.Boolean when bool.TryParse(literal.Value, out var boolean) => new(ExpressionValueType.Boolean, boolean),
        ExpressionValueType.DateTime when System.DateTime.TryParse(literal.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) => new(ExpressionValueType.DateTime, date),
        _ => throw new ExpressionEvaluationException("Invalid literal.")
    };

    private static ExpressionValue EvaluateField(string name, Anime anime, ExpressionEvaluationContext context) => name.ToLowerInvariant() switch
    {
        "id" => Number(anime.Id),
        "title" => String(anime.Title),
        "originaltitle" => String(anime.OriginalTitle),
        "synopsis" => String(anime.Synopsis),
        "type" => String(anime.Type),
        "status" => String(anime.Status),
        "season" => String(anime.Season?.Name),
        "year" => NullableNumber(anime.Season?.Year),
        "episodes" => NullableNumber(anime.Episodes),
        "duration" => NullableNumber(anime.Duration),
        "rating" => Number(anime.Rating),
        "malid" => NullableNumber(anime.MALId),
        "anilistid" => NullableNumber(anime.AniListId),
        "kitsuid" => NullableNumber(anime.KitsuId),
        "startdate" => Date(anime.StartDate),
        "enddate" => Date(anime.EndDate),
        "cachedat" => Date(anime.CachedAt),
        "createdat" => Date(anime.CreatedAt),
        "modifiedat" => Date(anime.ModifiedAt),
        "genres" => Strings(anime.AnimeGenres.Select(link => link.Genre?.Name)),
        "themes" => Strings(anime.AnimeThemes.Select(link => link.Theme?.Name)),
        "demographics" => Strings(anime.AnimeDemographics.Select(link => link.Demographic?.Name)),
        "titlealiases" => Strings(anime.TitleAliases.Select(alias => alias.Title)),
        "imagecount" => Number(anime.Images.Count),
        "haslocalimage" => Boolean(!string.IsNullOrWhiteSpace(anime.LocalImagePath) || anime.Images.Any(image => !string.IsNullOrWhiteSpace(image.LocalImagePath))),
        "hasenglishtitle" => Boolean(anime.HasEnglishTitle),
        "namesreviewed" => Boolean(anime.NamesReviewed),
        "inlibrary" => Boolean(context.IsInLibrary?.Invoke(anime) ?? false),
        _ => throw new ExpressionEvaluationException($"Unknown anime field '{name}'.")
    };

    private static ExpressionValue EvaluateFunction(FunctionExpression function, Anime anime, ExpressionEvaluationContext context)
    {
        var name = CanonicalName(function.Name);
        if (name == "if")
        {
            var condition = Evaluate(function.Arguments[0], anime, context);
            return Evaluate(function.Arguments[RequireBoolean([condition], 0, name) ? 1 : 2], anime, context);
        }
        if (name == "coalesce")
        {
            foreach (var argument in function.Arguments)
            {
                var candidate = Evaluate(argument, anime, context);
                if (!candidate.IsNull && !IsEmpty(candidate)) return candidate;
            }
            return ExpressionValue.Null;
        }
        var args = function.Arguments.Select(argument => Evaluate(argument, anime, context)).ToArray();
        return name switch
        {
            "tolower" => String(RequireString(args, 0, name).ToLowerInvariant()),
            "toupper" => String(RequireString(args, 0, name).ToUpperInvariant()),
            "trim" => String(RequireString(args, 0, name).Trim()),
            "concat" => String(string.Concat(args.Select(AsString))),
            "replace" => String(RequireString(args, 0, name).Replace(RequireString(args, 1, name), RequireString(args, 2, name), StringComparison.Ordinal)),
            "substring" => Substring(args),
            "slice" => Slice(args),
            "indexof" => Number(RequireString(args, 0, name).IndexOf(RequireString(args, 1, name), StringComparison.OrdinalIgnoreCase)),
            "lastindexof" => Number(RequireString(args, 0, name).LastIndexOf(RequireString(args, 1, name), StringComparison.OrdinalIgnoreCase)),
            "length" => Length(args),
            "empty" => Boolean(IsEmpty(RequireArg(args, 0, name))),
            "equals" => Boolean(Compare(args, 0) == 0),
            "greater" => Boolean(Compare(args, 0) > 0),
            "greaterorequals" => Boolean(Compare(args, 0) >= 0),
            "less" => Boolean(Compare(args, 0) < 0),
            "lessorequals" => Boolean(Compare(args, 0) <= 0),
            "and" => Boolean(args.Select((_, index) => RequireBoolean(args, index, name)).All(value => value)),
            "or" => Boolean(args.Select((_, index) => RequireBoolean(args, index, name)).Any(value => value)),
            "not" => Boolean(!RequireBoolean(args, 0, name)),
            "startswith" => Boolean(RequireString(args, 0, name).StartsWith(RequireString(args, 1, name), StringComparison.OrdinalIgnoreCase)),
            "endswith" => Boolean(RequireString(args, 0, name).EndsWith(RequireString(args, 1, name), StringComparison.OrdinalIgnoreCase)),
            "string" => String(AsString(RequireArg(args, 0, name))),
            "int" or "float" or "decimal" => Number(RequireNumber(args, 0, name)),
            "bool" => Boolean(AsBoolean(RequireArg(args, 0, name))),
            "add" => Number(RequireNumber(args, 0, name) + RequireNumber(args, 1, name)),
            "sub" => Number(RequireNumber(args, 0, name) - RequireNumber(args, 1, name)),
            "mul" => Number(RequireNumber(args, 0, name) * RequireNumber(args, 1, name)),
            "div" => Divide(args),
            "mod" => Number(RequireNumber(args, 0, name) % NonZero(args, 1, name)),
            "min" => Number(args.Select((_, index) => RequireNumber(args, index, name)).Min()),
            "max" => Number(args.Select((_, index) => RequireNumber(args, index, name)).Max()),
            "utcnow" => Date(context.UtcNow),
            "adddays" => Date(RequireDate(args, 0, name).AddDays((double)RequireNumber(args, 1, name))),
            "addtotime" => AddToTime(args, 1),
            "subtractfromtime" => AddToTime(args, -1),
            "datedifference" => Number((decimal)(RequireDate(args, 1, name) - RequireDate(args, 0, name)).TotalDays),
            "dayofmonth" => Number(RequireDate(args, 0, name).Day),
            "dayofweek" => Number((int)RequireDate(args, 0, name).DayOfWeek),
            "dayofyear" => Number(RequireDate(args, 0, name).DayOfYear),
            "startofday" => Date(RequireDate(args, 0, name).Date),
            "startofmonth" => StartOfMonth(args),
            "formatdatetime" => String(RequireDate(args, 0, name).ToString(RequireString(args, 1, name), CultureInfo.InvariantCulture)),
            "parsedatetime" => ParseDate(args),
            "array" or "createarray" => Strings(args.Select(AsString)),
            "contains" => Boolean(Contains(args)),
            "intersection" => Intersection(args),
            "union" => Union(args),
            "first" => FirstOrLast(args, false),
            "last" => FirstOrLast(args, true),
            "join" => String(string.Join(RequireString(args, 1, name), RequireStrings(args, 0, name))),
            "split" => Strings(RequireString(args, 0, name).Split(RequireString(args, 1, name), StringSplitOptions.None)),
            "sort" => Strings(RequireStrings(args, 0, name).Order(StringComparer.OrdinalIgnoreCase)),
            _ => throw new ExpressionEvaluationException($"Unknown function '{function.Name}'.")
        };
    }

    private static string CanonicalName(string name) => name.ToLowerInvariant() switch { "lower" => "tolower", "upper" => "toupper", _ => name.ToLowerInvariant() };
    private static ExpressionValue Substring(ExpressionValue[] args)
    {
        var text = RequireString(args, 0, "substring");
        var start = checked((int)RequireNumber(args, 1, "substring"));
        if (start < 0 || start > text.Length) throw new ExpressionEvaluationException("substring start is outside the string.");
        if (args.Length == 2) return String(text[start..]);
        var length = checked((int)RequireNumber(args, 2, "substring"));
        if (length < 0 || start + length > text.Length) throw new ExpressionEvaluationException("substring length is outside the string.");
        return String(text.Substring(start, length));
    }
    private static ExpressionValue Slice(ExpressionValue[] args)
    {
        var text = RequireString(args, 0, "slice");
        var start = checked((int)RequireNumber(args, 1, "slice"));
        var end = args.Length > 2 ? checked((int)RequireNumber(args, 2, "slice")) : text.Length;
        if (start < 0) start = text.Length + start;
        if (end < 0) end = text.Length + end;
        start = Math.Clamp(start, 0, text.Length); end = Math.Clamp(end, start, text.Length);
        return String(text[start..end]);
    }
    private static ExpressionValue Length(ExpressionValue[] args)
    {
        var value = RequireArg(args, 0, "length");
        return Number(value.Type switch { ExpressionValueType.String => ((string)value.Value!).Length, ExpressionValueType.StringCollection => ((IReadOnlyList<string>)value.Value!).Count, ExpressionValueType.Null => 0, _ => throw new ExpressionEvaluationException("length requires text or a collection.") });
    }
    private static ExpressionValue Divide(ExpressionValue[] args) => Number(RequireNumber(args, 0, "div") / NonZero(args, 1, "div"));
    private static int Compare(ExpressionValue[] args, int start)
    {
        var left = RequireArg(args, start, "comparison");
        var right = RequireArg(args, start + 1, "comparison");
        if (left.IsNull || right.IsNull) return left.IsNull == right.IsNull ? 0 : left.IsNull ? -1 : 1;
        if (left.Type != right.Type) throw new ExpressionEvaluationException("Comparison arguments must have the same type.");
        return left.Type switch
        {
            ExpressionValueType.Number => ((decimal)left.Value!).CompareTo((decimal)right.Value!),
            ExpressionValueType.DateTime => ((DateTime)left.Value!).CompareTo((DateTime)right.Value!),
            ExpressionValueType.Boolean => ((bool)left.Value!).CompareTo((bool)right.Value!),
            ExpressionValueType.String => StringComparer.OrdinalIgnoreCase.Compare((string)left.Value!, (string)right.Value!),
            ExpressionValueType.StringCollection => ((IReadOnlyList<string>)left.Value!).SequenceEqual((IReadOnlyList<string>)right.Value!, StringComparer.OrdinalIgnoreCase) ? 0 : -1,
            _ => 0
        };
    }
    private static decimal NonZero(ExpressionValue[] args, int index, string name)
    {
        var value = RequireNumber(args, index, name);
        if (value == 0) throw new ExpressionEvaluationException($"{name} cannot divide by zero.");
        return value;
    }
    private static ExpressionValue AddToTime(ExpressionValue[] args, int sign)
    {
        var date = RequireDate(args, 0, "addToTime");
        var amount = (double)RequireNumber(args, 1, "addToTime") * sign;
        var unit = RequireString(args, 2, "addToTime").ToLowerInvariant();
        return Date(unit switch { "day" or "days" => date.AddDays(amount), "week" or "weeks" => date.AddDays(amount * 7), "month" or "months" => date.AddMonths(checked((int)amount)), "year" or "years" => date.AddYears(checked((int)amount)), "hour" or "hours" => date.AddHours(amount), "minute" or "minutes" => date.AddMinutes(amount), _ => throw new ExpressionEvaluationException("Unsupported time unit.") });
    }
    private static ExpressionValue StartOfMonth(ExpressionValue[] args) { var date = RequireDate(args, 0, "startOfMonth"); return Date(new DateTime(date.Year, date.Month, 1, 0, 0, 0, date.Kind)); }
    private static ExpressionValue ParseDate(ExpressionValue[] args) => System.DateTime.TryParse(RequireString(args, 0, "parseDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? Date(date) : throw new ExpressionEvaluationException("Invalid date.");
    private static bool Contains(ExpressionValue[] args)
    {
        var source = RequireArg(args, 0, "contains"); var search = RequireArg(args, 1, "contains");
        return source.Type switch { ExpressionValueType.String => ((string)source.Value!).Contains(AsString(search), StringComparison.OrdinalIgnoreCase), ExpressionValueType.StringCollection => ((IReadOnlyList<string>)source.Value!).Contains(AsString(search), StringComparer.OrdinalIgnoreCase), _ => false };
    }
    private static ExpressionValue Intersection(ExpressionValue[] args)
    {
        if (args.Length < 2) throw new ExpressionEvaluationException("intersection requires at least two collections.");
        IEnumerable<string> result = RequireStrings(args, 0, "intersection");
        for (var index = 1; index < args.Length; index++) result = result.Intersect(RequireStrings(args, index, "intersection"), StringComparer.OrdinalIgnoreCase);
        return Strings(result);
    }
    private static ExpressionValue Union(ExpressionValue[] args) => Strings(args.SelectMany((_, index) => RequireStrings(args, index, "union")).Distinct(StringComparer.OrdinalIgnoreCase));
    private static ExpressionValue FirstOrLast(ExpressionValue[] args, bool last)
    {
        var value = RequireArg(args, 0, last ? "last" : "first");
        if (value.Type == ExpressionValueType.String) { var text = (string)value.Value!; return text.Length == 0 ? ExpressionValue.Null : String((last ? text[^1] : text[0]).ToString()); }
        var list = RequireStrings(args, 0, last ? "last" : "first");
        return list.Count == 0 ? ExpressionValue.Null : String(last ? list[^1] : list[0]);
    }
    private static bool IsEmpty(ExpressionValue value) => value.IsNull || value.Type switch { ExpressionValueType.String => string.IsNullOrEmpty((string?)value.Value), ExpressionValueType.StringCollection => ((IReadOnlyList<string>)value.Value!).Count == 0, _ => false };
    private static ExpressionValue RequireArg(ExpressionValue[] args, int index, string name) => index < args.Length ? args[index] : throw new ExpressionEvaluationException($"{name} is missing argument {index + 1}.");
    private static string RequireString(ExpressionValue[] args, int index, string name) { var value = RequireArg(args, index, name); return value.IsNull ? string.Empty : value.Type == ExpressionValueType.String ? (string)value.Value! : throw new ExpressionEvaluationException($"{name} argument {index + 1} must be text."); }
    private static decimal RequireNumber(ExpressionValue[] args, int index, string name) { var value = RequireArg(args, index, name); if (value.Type == ExpressionValueType.Number) return (decimal)value.Value!; if (decimal.TryParse(AsString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number; throw new ExpressionEvaluationException($"{name} argument {index + 1} must be a number."); }
    private static bool RequireBoolean(ExpressionValue[] args, int index, string name) { var value = RequireArg(args, index, name); return value.Type == ExpressionValueType.Boolean ? (bool)value.Value! : throw new ExpressionEvaluationException($"{name} argument {index + 1} must be true or false."); }
    private static DateTime RequireDate(ExpressionValue[] args, int index, string name) { var value = RequireArg(args, index, name); if (value.Type == ExpressionValueType.DateTime) return (DateTime)value.Value!; if (System.DateTime.TryParse(AsString(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date; throw new ExpressionEvaluationException($"{name} argument {index + 1} must be a date."); }
    private static IReadOnlyList<string> RequireStrings(ExpressionValue[] args, int index, string name) { var value = RequireArg(args, index, name); return value.Type == ExpressionValueType.StringCollection ? (IReadOnlyList<string>)value.Value! : throw new ExpressionEvaluationException($"{name} argument {index + 1} must be a collection."); }
    private static bool AsBoolean(ExpressionValue value) => value.Type switch { ExpressionValueType.Boolean => (bool)value.Value!, ExpressionValueType.Number => (decimal)value.Value! != 0, ExpressionValueType.String when bool.TryParse((string)value.Value!, out var result) => result, _ => !IsEmpty(value) };
    public static string AsString(ExpressionValue value) => value.IsNull ? string.Empty : value.Type switch { ExpressionValueType.DateTime => ((DateTime)value.Value!).ToString("O", CultureInfo.InvariantCulture), ExpressionValueType.Number => ((decimal)value.Value!).ToString(CultureInfo.InvariantCulture), ExpressionValueType.Boolean => ((bool)value.Value!).ToString(CultureInfo.InvariantCulture), ExpressionValueType.StringCollection => string.Join(", ", (IReadOnlyList<string>)value.Value!), _ => value.Value?.ToString() ?? string.Empty };
    private static ExpressionValue String(string? value) => value is null ? ExpressionValue.Null : new(ExpressionValueType.String, value);
    private static ExpressionValue NullableNumber(int? value) => value.HasValue ? Number(value.Value) : ExpressionValue.Null;
    private static ExpressionValue Number(decimal? value) => value.HasValue ? Number(value.Value) : ExpressionValue.Null;
    private static ExpressionValue Number(decimal value) => new(ExpressionValueType.Number, value);
    private static ExpressionValue Date(DateTime? value) => value.HasValue ? new(ExpressionValueType.DateTime, value.Value) : ExpressionValue.Null;
    private static ExpressionValue Boolean(bool value) => new(ExpressionValueType.Boolean, value);
    private static ExpressionValue Strings(IEnumerable<string?> values) => new(ExpressionValueType.StringCollection, values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList());
}

public sealed class ExpressionEvaluationException(string message) : Exception(message);
