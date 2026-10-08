using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services.Expressions;

public sealed record ExpressionFieldDefinition(
    string Name,
    string Label,
    ExpressionValueType Type,
    string Description);

public sealed record ExpressionFunctionDefinition(
    string Category,
    string Name,
    string Label,
    string Description,
    int MinimumArguments,
    int MaximumArguments,
    string Template,
    params ExpressionValueType[] FirstArgumentTypes)
{
    public bool UsesCurrentValue => Template.Contains("{value}", StringComparison.Ordinal);

    public bool CanApplyTo(ExpressionValueType? type) =>
        !UsesCurrentValue || type is not null &&
        (FirstArgumentTypes.Length == 0 || FirstArgumentTypes.Contains(type.Value) || type == ExpressionValueType.Null);

    public string ApplyTo(string currentValue) => Template.Replace("{value}", currentValue, StringComparison.Ordinal);
}

public sealed record ExpressionExampleDefinition(string Name, string Description, string Source);

/// <summary>Shared authoring and validation metadata for the filter expression language.</summary>
public static class FilterExpressionCatalog
{
    public static IReadOnlyList<ExpressionFieldDefinition> Fields { get; } =
    [
        Field("id", "Catalogue ID", ExpressionValueType.Number, "AnimeOverseer's catalogue identifier"),
        Field("title", "Title", ExpressionValueType.String, "Preferred display title"),
        Field("originalTitle", "Original title", ExpressionValueType.String, "Provider-supplied original title"),
        Field("synopsis", "Synopsis", ExpressionValueType.String, "Anime synopsis"),
        Field("type", "Type", ExpressionValueType.String, "TV, movie, OVA, and similar formats"),
        Field("status", "Status", ExpressionValueType.String, "Current airing status"),
        Field("season", "Season", ExpressionValueType.String, "Spring, summer, fall, or winter"),
        Field("year", "Year", ExpressionValueType.Number, "Release season year"),
        Field("episodes", "Episodes", ExpressionValueType.Number, "Episode count"),
        Field("duration", "Duration", ExpressionValueType.Number, "Minutes per episode"),
        Field("rating", "Rating", ExpressionValueType.Number, "Catalogue rating"),
        Field("malId", "MAL ID", ExpressionValueType.Number, "MyAnimeList identifier"),
        Field("aniListId", "AniList ID", ExpressionValueType.Number, "AniList identifier"),
        Field("kitsuId", "Kitsu ID", ExpressionValueType.Number, "Kitsu identifier"),
        Field("startDate", "Start date", ExpressionValueType.DateTime, "First airing date"),
        Field("endDate", "End date", ExpressionValueType.DateTime, "Final airing date"),
        Field("cachedAt", "Cached at", ExpressionValueType.DateTime, "Last provider cache date"),
        Field("createdAt", "Created at", ExpressionValueType.DateTime, "Catalogue creation date"),
        Field("modifiedAt", "Modified at", ExpressionValueType.DateTime, "Catalogue modification date"),
        Field("genres", "Genres", ExpressionValueType.StringCollection, "Genre names"),
        Field("themes", "Themes", ExpressionValueType.StringCollection, "Theme names"),
        Field("demographics", "Demographics", ExpressionValueType.StringCollection, "Demographic names"),
        Field("titleAliases", "Title aliases", ExpressionValueType.StringCollection, "Known alternative titles"),
        Field("imageCount", "Image count", ExpressionValueType.Number, "Number of stored artwork choices"),
        Field("hasLocalImage", "Has local image", ExpressionValueType.Boolean, "Whether any artwork is cached locally"),
        Field("hasEnglishTitle", "Has English title", ExpressionValueType.Boolean, "Whether the preferred title is explicitly English"),
        Field("namesReviewed", "Names reviewed", ExpressionValueType.Boolean, "Whether title choices have been reviewed"),
        Field("inLibrary", "In library", ExpressionValueType.Boolean, "Whether the anime is in a connected media library")
    ];

    public static IReadOnlyList<ExpressionFunctionDefinition> Functions { get; } =
    [
        Function("Text", "toLower", "Lowercase", "Convert text to lowercase", 1, 1, "toLower({value})", ExpressionValueType.String),
        Function("Text", "toUpper", "Uppercase", "Convert text to uppercase", 1, 1, "toUpper({value})", ExpressionValueType.String),
        Function("Text", "trim", "Trim", "Remove surrounding whitespace", 1, 1, "trim({value})", ExpressionValueType.String),
        Function("Text", "concat", "Combine text", "Combine values as text", 1, 32, "concat({value}, \"\")"),
        Function("Text", "replace", "Replace text", "Replace matching text", 3, 3, "replace({value}, \"\", \"\")", ExpressionValueType.String),
        Function("Text", "substring", "Substring", "Take text from a position and length", 2, 3, "substring({value}, 0, 1)", ExpressionValueType.String),
        Function("Text", "slice", "Slice", "Take text between two positions", 2, 3, "slice({value}, 0, 1)", ExpressionValueType.String),
        Function("Text", "indexOf", "Find position", "Find the first text position", 2, 2, "indexOf({value}, \"\")", ExpressionValueType.String),
        Function("Text", "lastIndexOf", "Find last position", "Find the last text position", 2, 2, "lastIndexOf({value}, \"\")", ExpressionValueType.String),
        Function("Text", "length", "Length", "Count characters or collection items", 1, 1, "length({value})", ExpressionValueType.String, ExpressionValueType.StringCollection),
        Function("Text", "startsWith", "Starts with", "Check the start of text", 2, 2, "startsWith({value}, \"\")", ExpressionValueType.String),
        Function("Text", "endsWith", "Ends with", "Check the end of text", 2, 2, "endsWith({value}, \"\")", ExpressionValueType.String),

        Function("Logic", "empty", "Is empty", "Check for null, empty text, or an empty collection", 1, 1, "empty({value})"),
        Function("Logic", "coalesce", "Fallback", "Return the first non-empty value", 1, 32, "coalesce({value}, {value})"),
        Function("Logic", "if", "If", "Choose a value conditionally", 3, 3, "if(true, {value}, {value})"),
        Function("Logic", "equals", "Equals", "Compare two values", 2, 2, "equals({value}, {value})"),
        Function("Logic", "greater", "Greater than", "Check whether a number or date is greater", 2, 2, "greater({value}, {value})", ExpressionValueType.Number, ExpressionValueType.DateTime),
        Function("Logic", "greaterOrEquals", "At least", "Check whether a number or date is greater or equal", 2, 2, "greaterOrEquals({value}, {value})", ExpressionValueType.Number, ExpressionValueType.DateTime),
        Function("Logic", "less", "Less than", "Check whether a number or date is lower", 2, 2, "less({value}, {value})", ExpressionValueType.Number, ExpressionValueType.DateTime),
        Function("Logic", "lessOrEquals", "At most", "Check whether a number or date is lower or equal", 2, 2, "lessOrEquals({value}, {value})", ExpressionValueType.Number, ExpressionValueType.DateTime),
        Function("Logic", "and", "All are true", "Require every condition", 2, 32, "and({value}, true)", ExpressionValueType.Boolean),
        Function("Logic", "or", "Any are true", "Require any condition", 2, 32, "or({value}, true)", ExpressionValueType.Boolean),
        Function("Logic", "not", "Not", "Invert a condition", 1, 1, "not({value})", ExpressionValueType.Boolean),

        Function("Conversion", "string", "To text", "Convert a value to text", 1, 1, "string({value})"),
        Function("Conversion", "int", "To integer", "Convert a value to a whole number", 1, 1, "int({value})"),
        Function("Conversion", "float", "To number", "Convert a value to a number", 1, 1, "float({value})"),
        Function("Conversion", "decimal", "To decimal", "Convert a value to a decimal number", 1, 1, "decimal({value})"),
        Function("Conversion", "bool", "To true/false", "Convert a value to true or false", 1, 1, "bool({value})"),

        Function("Math", "add", "Add", "Add numbers", 2, 2, "add({value}, 1)", ExpressionValueType.Number),
        Function("Math", "sub", "Subtract", "Subtract numbers", 2, 2, "sub({value}, 1)", ExpressionValueType.Number),
        Function("Math", "mul", "Multiply", "Multiply numbers", 2, 2, "mul({value}, 1)", ExpressionValueType.Number),
        Function("Math", "div", "Divide", "Divide numbers", 2, 2, "div({value}, 1)", ExpressionValueType.Number),
        Function("Math", "mod", "Remainder", "Get a division remainder", 2, 2, "mod({value}, 1)", ExpressionValueType.Number),
        Function("Math", "min", "Minimum", "Get the lowest number", 1, 32, "min({value}, 0)", ExpressionValueType.Number),
        Function("Math", "max", "Maximum", "Get the highest number", 1, 32, "max({value}, 0)", ExpressionValueType.Number),

        Function("Date and time", "utcNow", "Current time", "Current UTC date and time", 0, 0, "utcNow()"),
        Function("Date and time", "addDays", "Add days", "Add days to a date", 2, 2, "addDays({value}, 1)", ExpressionValueType.DateTime),
        Function("Date and time", "addToTime", "Add interval", "Add a time interval", 3, 3, "addToTime({value}, 1, \"days\")", ExpressionValueType.DateTime),
        Function("Date and time", "subtractFromTime", "Subtract interval", "Subtract a time interval", 3, 3, "subtractFromTime({value}, 1, \"days\")", ExpressionValueType.DateTime),
        Function("Date and time", "dateDifference", "Days between", "Count days between dates", 2, 2, "dateDifference({value}, utcNow())", ExpressionValueType.DateTime),
        Function("Date and time", "dayOfMonth", "Day of month", "Get the day of the month", 1, 1, "dayOfMonth({value})", ExpressionValueType.DateTime),
        Function("Date and time", "dayOfWeek", "Day of week", "Get the day of the week", 1, 1, "dayOfWeek({value})", ExpressionValueType.DateTime),
        Function("Date and time", "dayOfYear", "Day of year", "Get the day of the year", 1, 1, "dayOfYear({value})", ExpressionValueType.DateTime),
        Function("Date and time", "startOfDay", "Start of day", "Get the start of a day", 1, 1, "startOfDay({value})", ExpressionValueType.DateTime),
        Function("Date and time", "startOfMonth", "Start of month", "Get the start of a month", 1, 1, "startOfMonth({value})", ExpressionValueType.DateTime),
        Function("Date and time", "formatDateTime", "Format date", "Format a date as text", 2, 2, "formatDateTime({value}, \"yyyy-MM-dd\")", ExpressionValueType.DateTime),
        Function("Date and time", "parseDateTime", "Parse date", "Parse text as a date", 1, 1, "parseDateTime({value})", ExpressionValueType.String),

        Function("Collections", "array", "Create list", "Create a text collection", 0, 32, "array({value}, \"\")", ExpressionValueType.String),
        Function("Collections", "createArray", "Create list", "Create a text collection", 0, 32, "createArray({value}, \"\")", ExpressionValueType.String),
        Function("Collections", "contains", "Contains", "Check text or a collection", 2, 2, "contains({value}, \"\")", ExpressionValueType.String, ExpressionValueType.StringCollection),
        Function("Collections", "intersection", "Items in common", "Find common collection items", 2, 32, "intersection({value}, createArray(\"\"))", ExpressionValueType.StringCollection),
        Function("Collections", "union", "Combine lists", "Combine unique collection items", 2, 32, "union({value}, createArray(\"\"))", ExpressionValueType.StringCollection),
        Function("Collections", "first", "First item", "Get the first character or collection item", 1, 1, "first({value})", ExpressionValueType.String, ExpressionValueType.StringCollection),
        Function("Collections", "last", "Last item", "Get the last character or collection item", 1, 1, "last({value})", ExpressionValueType.String, ExpressionValueType.StringCollection),
        Function("Collections", "join", "Join list", "Join collection items as text", 2, 2, "join({value}, \", \")", ExpressionValueType.StringCollection),
        Function("Collections", "split", "Split text", "Split text into a collection", 2, 2, "split({value}, \",\")", ExpressionValueType.String),
        Function("Collections", "sort", "Sort list", "Sort collection items", 1, 1, "sort({value})", ExpressionValueType.StringCollection)
    ];

    public static IReadOnlyList<ExpressionExampleDefinition> Examples { get; } =
    [
        new("Runtime in hours", "Multiply episodes by minutes, then convert to hours.", "div(mul(episodes, duration), 60)"),
        new("Preferred title fallback", "Use the original title when present, otherwise the display title.", "coalesce(originalTitle, title)"),
        new("Matching genre count", "Count Action and Adventure genres attached to an anime.", "length(intersection(genres, createArray(\"Action\", \"Adventure\")))"),
        new("URL-friendly title", "Lowercase the title and replace spaces with hyphens.", "toLower(replace(title, \" \", \"-\"))")
    ];

    private static readonly Dictionary<string, ExpressionFieldDefinition> FieldsByName =
        Fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ExpressionFunctionDefinition> FunctionsByName = BuildFunctionLookup();

    public static bool TryGetField(string name, out ExpressionFieldDefinition definition) =>
        FieldsByName.TryGetValue(name, out definition!);

    public static bool TryGetFunction(string name, out ExpressionFunctionDefinition definition) =>
        FunctionsByName.TryGetValue(name, out definition!);

    public static string TypeLabel(ExpressionValueType type) => type switch
    {
        ExpressionValueType.String => "Text",
        ExpressionValueType.Number => "Number",
        ExpressionValueType.Boolean => "True/false",
        ExpressionValueType.DateTime => "Date",
        ExpressionValueType.StringCollection => "Collection",
        _ => "Empty"
    };

    public static string TypeCssClass(ExpressionValueType? type) => type switch
    {
        ExpressionValueType.String => "expression-type--text",
        ExpressionValueType.Number => "expression-type--number",
        ExpressionValueType.Boolean => "expression-type--boolean",
        ExpressionValueType.DateTime => "expression-type--date",
        ExpressionValueType.StringCollection => "expression-type--collection",
        _ => "expression-type--empty"
    };

    public static string TypeIcon(ExpressionValueType type) => type switch
    {
        ExpressionValueType.String => "T",
        ExpressionValueType.Number => "#",
        ExpressionValueType.Boolean => "✓",
        ExpressionValueType.DateTime => "◷",
        ExpressionValueType.StringCollection => "≡",
        _ => "?"
    };

    private static Dictionary<string, ExpressionFunctionDefinition> BuildFunctionLookup()
    {
        var lookup = Functions.ToDictionary(function => function.Name, StringComparer.OrdinalIgnoreCase);
        lookup["lower"] = lookup["toLower"];
        lookup["upper"] = lookup["toUpper"];
        return lookup;
    }

    private static ExpressionFieldDefinition Field(string name, string label, ExpressionValueType type, string description) =>
        new(name, label, type, description);

    private static ExpressionFunctionDefinition Function(
        string category,
        string name,
        string label,
        string description,
        int minimumArguments,
        int maximumArguments,
        string template,
        params ExpressionValueType[] firstArgumentTypes) =>
        new(category, name, label, description, minimumArguments, maximumArguments, template, firstArgumentTypes);
}
