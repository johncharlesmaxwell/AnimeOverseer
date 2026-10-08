namespace AnimeOverseer.Server.Models;

public class FilterState
{
    public int SchemaVersion { get; set; } = 1;
    public GroupLogic TopLevelLogic { get; set; } = GroupLogic.And;
    public List<FilterGroup> Groups { get; set; } = [new FilterGroup()];

    public bool IsActive => Groups.Any(IsActiveGroup);
    public int ActiveCount => Groups.Sum(ActiveConditionCount);

    public static bool IsActiveGroup(FilterGroup group) =>
        group.Conditions.Any(IsActiveCondition) || group.Subgroups.Any(IsActiveGroup);

    public static int ActiveConditionCount(FilterGroup group) =>
        group.Conditions.Count(IsActiveCondition) + group.Subgroups.Sum(ActiveConditionCount);

    public static bool IsActiveCondition(FilterCondition condition) =>
        condition.Operator is FilterOperator.ContainsData or FilterOperator.DoesNotContainData ||
        condition.RightExpressions.Any(expression => !string.IsNullOrWhiteSpace(expression.Source)) ||
        condition.EffectiveValues.Any(value => !string.IsNullOrWhiteSpace(value));
}

public class FilterGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public GroupLogic Logic { get; set; } = GroupLogic.And;
    public List<FilterCondition> Conditions { get; set; } = [new FilterCondition()];
    public List<FilterGroup> Subgroups { get; set; } = [];
}

public class FilterCondition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public FilterField Field { get; set; } = FilterField.Title;
    public FilterOperator Operator { get; set; } = FilterOperator.Contains;
    public string Value { get; set; } = string.Empty;
    // Value remains for compatibility with existing collection JSON and dashboard URLs.
    // New filters keep each entry independently so values can contain formulae too.
    public List<string> Values { get; set; } = [];
    public ValueLogic ValueLogic { get; set; } = ValueLogic.Any;

    // Version 2 operands. Null/empty values are adapted from Field/Value/Values,
    // which remain readable for existing saved collections and dashboard URLs.
    public FilterExpression? LeftExpression { get; set; }
    public List<FilterExpression> RightExpressions { get; set; } = [];

    public IEnumerable<string> EffectiveValues => Values.Count > 0 ? Values : [Value];
}

public enum GroupLogic { And, Or }
public enum ValueLogic { Any, All }

public enum FilterField
{
    Title,
    OriginalTitle,
    Type,
    Status,
    Season,
    Episodes,
    Rating,
    Genres,
    Themes,
    Demographics,
    Year,
    Synopsis,
    MalId,
    AniListId,
    KitsuId,
    Duration,
    StartDate,
    EndDate,
    CachedAt,
    CreatedAt,
    ModifiedAt,
    InLibrary,
    TitleAliases,
    ImageCount,
    HasLocalImage,
    HasEnglishTitle,
    NamesReviewed
}

public enum FilterOperator
{
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    ContainsData,
    DoesNotContainData
}
