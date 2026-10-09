using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services.Expressions;

public static class FilterExpressionValidator
{
    public static IReadOnlyList<ExpressionDiagnostic> Validate(ExpressionNode node)
    {
        var diagnostics = new List<ExpressionDiagnostic>();
        InferType(node, diagnostics);
        return diagnostics;
    }

    public static IReadOnlyList<string> Validate(FilterState state)
    {
        var errors = new List<string>();
        foreach (var condition in state.Groups.SelectMany(AllConditions))
        {
            if (condition.LeftExpression is not null) ValidateSource(condition.LeftExpression.Source, condition.Id, "left", errors);
            foreach (var right in condition.RightExpressions) ValidateSource(right.Source, condition.Id, "right", errors);
            foreach (var value in condition.EffectiveValues)
                foreach (var source in FormulaSources(value)) ValidateSource(source, condition.Id, "value", errors);
            ValidateConditionTypes(condition, errors);
        }
        return errors;
    }

    private static void ValidateConditionTypes(FilterCondition condition, List<string> errors)
    {
        if (!TryType(FilterExpressionAdapter.Left(condition), out var leftType)) return;
        if (!SupportsOperator(leftType, condition.Operator))
            errors.Add($"Condition {condition.Id}: {condition.Operator} cannot be used with {TypeLabel(leftType)}.");
        if (condition.Operator is FilterOperator.ContainsData or FilterOperator.DoesNotContainData) return;

        var rightOperands = condition.RightExpressions.Count > 0
            ? condition.RightExpressions
            : condition.EffectiveValues.Where(IsWholeFormula).Select(FilterExpressionParser.ParseOperand).ToList();
        foreach (var right in rightOperands)
        {
            if (!TryType(right, out var rightType) || Compatible(leftType, rightType)) continue;
            errors.Add($"Condition {condition.Id}: cannot compare {TypeLabel(leftType)} with {TypeLabel(rightType)}.");
        }
    }

    private static bool TryType(FilterExpression expression, out ExpressionValueType type)
    {
        type = ExpressionValueType.Null;
        var parsed = FilterExpressionParser.Parse(expression.Source);
        if (!parsed.IsValid) return false;
        var diagnostics = new List<ExpressionDiagnostic>();
        type = InferType(parsed.Root!, diagnostics);
        return diagnostics.Count == 0;
    }

    private static bool IsWholeFormula(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal);
    }

    private static bool SupportsOperator(ExpressionValueType type, FilterOperator op) => type switch
    {
        ExpressionValueType.String or ExpressionValueType.StringCollection => op is FilterOperator.Contains or FilterOperator.NotContains or
            FilterOperator.StartsWith or FilterOperator.EndsWith or FilterOperator.Equals or FilterOperator.NotEquals or
            FilterOperator.ContainsData or FilterOperator.DoesNotContainData,
        ExpressionValueType.Number or ExpressionValueType.DateTime => op is FilterOperator.Equals or FilterOperator.NotEquals or
            FilterOperator.GreaterThan or FilterOperator.LessThan or FilterOperator.GreaterThanOrEqual or FilterOperator.LessThanOrEqual or
            FilterOperator.ContainsData or FilterOperator.DoesNotContainData,
        ExpressionValueType.Boolean => op is FilterOperator.Equals or FilterOperator.NotEquals or FilterOperator.ContainsData or FilterOperator.DoesNotContainData,
        _ => op is FilterOperator.Equals or FilterOperator.NotEquals or FilterOperator.ContainsData or FilterOperator.DoesNotContainData
    };

    private static bool Compatible(ExpressionValueType left, ExpressionValueType right)
    {
        if (right == ExpressionValueType.Null) return true;
        if (left == ExpressionValueType.StringCollection) return right == ExpressionValueType.String;
        return left == right || left is ExpressionValueType.Number or ExpressionValueType.DateTime or ExpressionValueType.Boolean && right == ExpressionValueType.String;
    }

    private static void ValidateSource(string source, Guid conditionId, string side, List<string> errors)
    {
        var parsed = FilterExpressionParser.Parse(source);
        if (!parsed.IsValid)
        {
            errors.Add($"Condition {conditionId}: invalid {side} expression: {parsed.Diagnostics[0].Message}");
            return;
        }
        var validation = Validate(parsed.Root!);
        if (validation.Count > 0) errors.Add($"Condition {conditionId}: invalid {side} expression: {validation[0].Message}");
    }

    private static IEnumerable<FilterCondition> AllConditions(FilterGroup group) => group.Conditions.Concat(group.Subgroups.SelectMany(AllConditions));

    private static IEnumerable<string> FormulaSources(string value)
    {
        var position = 0;
        while (position < value.Length)
        {
            var start = value.IndexOf("{{", position, StringComparison.Ordinal);
            if (start < 0) yield break;
            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0) { yield return value[start..]; yield break; }
            yield return value[start..(end + 2)];
            position = end + 2;
        }
    }

    public static ExpressionValueType? InferType(ExpressionNode node)
    {
        var diagnostics = new List<ExpressionDiagnostic>();
        var type = InferType(node, diagnostics);
        return diagnostics.Count == 0 ? type : null;
    }

    private static ExpressionValueType InferType(ExpressionNode node, List<ExpressionDiagnostic> diagnostics)
    {
        switch (node)
        {
            case LiteralExpression literal:
                return literal.ValueType;
            case FieldExpression field:
                if (FilterExpressionCatalog.TryGetField(field.Name, out var fieldDefinition)) return fieldDefinition.Type;
                diagnostics.Add(new(0, $"Unknown anime field '{field.Name}'."));
                return ExpressionValueType.Null;
            case FunctionExpression function:
                if (!FilterExpressionCatalog.TryGetFunction(function.Name, out var functionDefinition))
                {
                    diagnostics.Add(new(0, $"Unknown function '{function.Name}'."));
                    foreach (var argument in function.Arguments) InferType(argument, diagnostics);
                    return ExpressionValueType.Null;
                }
                else if (function.Arguments.Count < functionDefinition.MinimumArguments || function.Arguments.Count > functionDefinition.MaximumArguments)
                    diagnostics.Add(new(0, $"{function.Name} expects {(functionDefinition.MinimumArguments == functionDefinition.MaximumArguments ? functionDefinition.MinimumArguments.ToString() : $"{functionDefinition.MinimumArguments}-{functionDefinition.MaximumArguments}")} arguments."));
                var argumentTypes = function.Arguments.Select(argument => InferType(argument, diagnostics)).ToArray();
                ValidateLiteralFormat(function, argumentTypes, diagnostics);
                return ValidateFunctionTypes(function.Name, argumentTypes, diagnostics);
            default:
                diagnostics.Add(new(0, "Unknown expression node."));
                return ExpressionValueType.Null;
        }
    }

    private static ExpressionValueType ValidateFunctionTypes(string sourceName, ExpressionValueType[] args, List<ExpressionDiagnostic> diagnostics)
    {
        var name = sourceName.ToLowerInvariant() switch { "lower" => "tolower", "upper" => "toupper", var value => value };
        bool Require(int index, params ExpressionValueType[] accepted)
        {
            if (index >= args.Length) return false;
            if (accepted.Contains(args[index]) || args[index] == ExpressionValueType.Null) return true;
            diagnostics.Add(new(0, $"{sourceName} argument {index + 1} must be {string.Join(" or ", accepted.Select(TypeLabel))}."));
            return false;
        }

        if (name is "tolower" or "toupper" or "trim") Require(0, ExpressionValueType.String);
        else if (name == "replace") { Require(0, ExpressionValueType.String); Require(1, ExpressionValueType.String); Require(2, ExpressionValueType.String); }
        else if (name is "substring" or "slice") { Require(0, ExpressionValueType.String); Require(1, ExpressionValueType.Number); if (args.Length > 2) Require(2, ExpressionValueType.Number); }
        else if (name is "indexof" or "lastindexof") { Require(0, ExpressionValueType.String); Require(1, ExpressionValueType.String); }
        else if (name == "length") Require(0, ExpressionValueType.String, ExpressionValueType.StringCollection);
        else if (name == "if")
        {
            Require(0, ExpressionValueType.Boolean);
            if (args.Length == 3 && args[1] != args[2] && args[1] != ExpressionValueType.Null && args[2] != ExpressionValueType.Null)
                diagnostics.Add(new(0, "if true and false results must have the same type."));
        }
        else if (name == "coalesce" && args.Where(type => type != ExpressionValueType.Null).Distinct().Count() > 1)
            diagnostics.Add(new(0, "coalesce arguments must have the same type."));
        else if (name is "and" or "or") for (var index = 0; index < args.Length; index++) Require(index, ExpressionValueType.Boolean);
        else if (name == "not") Require(0, ExpressionValueType.Boolean);
        else if (name is "startswith" or "endswith") { Require(0, ExpressionValueType.String); Require(1, ExpressionValueType.String); }
        else if (name == "equals" && args.Length == 2 && args[0] != args[1] && args[0] != ExpressionValueType.Null && args[1] != ExpressionValueType.Null)
            diagnostics.Add(new(0, "equals arguments must have the same type."));
        else if (name is "greater" or "greaterorequals" or "less" or "lessorequals")
        {
            Require(0, ExpressionValueType.Number, ExpressionValueType.DateTime);
            Require(1, ExpressionValueType.Number, ExpressionValueType.DateTime);
            if (args.Length == 2 && args[0] != args[1]) diagnostics.Add(new(0, $"{sourceName} arguments must have the same type."));
        }
        else if (name is "add" or "sub" or "mul" or "div" or "mod" or "min" or "max")
            for (var index = 0; index < args.Length; index++) Require(index, ExpressionValueType.Number);
        else if (name == "adddays") { Require(0, ExpressionValueType.DateTime); Require(1, ExpressionValueType.Number); }
        else if (name is "addtotime" or "subtractfromtime") { Require(0, ExpressionValueType.DateTime); Require(1, ExpressionValueType.Number); Require(2, ExpressionValueType.String); }
        else if (name == "datedifference") { Require(0, ExpressionValueType.DateTime); Require(1, ExpressionValueType.DateTime); }
        else if (name is "dayofmonth" or "dayofweek" or "dayofyear" or "startofday" or "startofmonth") Require(0, ExpressionValueType.DateTime);
        else if (name == "formatdatetime") { Require(0, ExpressionValueType.DateTime); Require(1, ExpressionValueType.String); }
        else if (name == "parsedatetime") Require(0, ExpressionValueType.String);
        else if (name == "string" && args.Length > 1) Require(1, ExpressionValueType.String);
        else if (name == "contains") { Require(0, ExpressionValueType.String, ExpressionValueType.StringCollection); Require(1, ExpressionValueType.String); }
        else if (name is "intersection" or "union" or "sort") for (var index = 0; index < args.Length; index++) Require(index, ExpressionValueType.StringCollection);
        else if (name is "first" or "last") Require(0, ExpressionValueType.String, ExpressionValueType.StringCollection);
        else if (name == "join") { Require(0, ExpressionValueType.StringCollection); Require(1, ExpressionValueType.String); }
        else if (name == "split") { Require(0, ExpressionValueType.String); Require(1, ExpressionValueType.String); }

        return name switch
        {
            "empty" or "contains" or "bool" or "equals" or "greater" or "greaterorequals" or "less" or "lessorequals" or
                "and" or "or" or "not" or "startswith" or "endswith" => ExpressionValueType.Boolean,
            "length" or "indexof" or "lastindexof" or "int" or "float" or "decimal" or "add" or "sub" or "mul" or "div" or "mod" or "min" or "max" or
                "datedifference" or "dayofmonth" or "dayofweek" or "dayofyear" => ExpressionValueType.Number,
            "utcnow" or "adddays" or "addtotime" or "subtractfromtime" or "startofday" or "startofmonth" or "parsedatetime" => ExpressionValueType.DateTime,
            "array" or "createarray" or "intersection" or "union" or "split" or "sort" => ExpressionValueType.StringCollection,
            "coalesce" => args.FirstOrDefault(type => type != ExpressionValueType.Null),
            "if" => args.Length > 1 ? args[1] : ExpressionValueType.Null,
            "first" or "last" or "tolower" or "toupper" or "trim" or "concat" or "replace" or "substring" or "slice" or "string" or "formatdatetime" or "join" => ExpressionValueType.String,
            _ => ExpressionValueType.Null
        };
    }

    private static void ValidateLiteralFormat(FunctionExpression function, ExpressionValueType[] argumentTypes, List<ExpressionDiagnostic> diagnostics)
    {
        if (!function.Name.Equals("string", StringComparison.OrdinalIgnoreCase) || function.Arguments.Count != 2 ||
            function.Arguments[1] is not LiteralExpression { ValueType: ExpressionValueType.String } literal ||
            argumentTypes.Length == 0 || string.IsNullOrEmpty(literal.Value)) return;
        try
        {
            _ = argumentTypes[0] switch
            {
                ExpressionValueType.Number => 0m.ToString(literal.Value, System.Globalization.CultureInfo.InvariantCulture),
                ExpressionValueType.DateTime => DateTime.UnixEpoch.ToString(literal.Value, System.Globalization.CultureInfo.InvariantCulture),
                _ => string.Empty
            };
        }
        catch (FormatException)
        {
            diagnostics.Add(new(0, $"'{literal.Value}' is not a valid format for {TypeLabel(argumentTypes[0])}."));
        }
    }

    private static string TypeLabel(ExpressionValueType type) => type switch
    {
        ExpressionValueType.String => "text", ExpressionValueType.Number => "a number", ExpressionValueType.Boolean => "true or false",
        ExpressionValueType.DateTime => "a date", ExpressionValueType.StringCollection => "a collection", _ => "null"
    };
}
