using System.Text.Json.Serialization;

namespace AnimeOverseer.Server.Models;

public enum ExpressionValueType
{
    Null,
    String,
    Number,
    Boolean,
    DateTime,
    StringCollection
}

/// <summary>A versioned, serializable filter-expression operand.</summary>
public sealed class FilterExpression
{
    public string Source { get; set; } = string.Empty;
    public ExpressionNode? Root { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(LiteralExpression), "literal")]
[JsonDerivedType(typeof(FieldExpression), "field")]
[JsonDerivedType(typeof(FunctionExpression), "function")]
public abstract class ExpressionNode;

public sealed class LiteralExpression : ExpressionNode
{
    public ExpressionValueType ValueType { get; set; }
    public string? Value { get; set; }
}

public sealed class FieldExpression : ExpressionNode
{
    public string Name { get; set; } = string.Empty;
}

public sealed class FunctionExpression : ExpressionNode
{
    public string Name { get; set; } = string.Empty;
    public List<ExpressionNode> Arguments { get; set; } = [];
}

public readonly record struct ExpressionValue(ExpressionValueType Type, object? Value)
{
    public static ExpressionValue Null => new(ExpressionValueType.Null, null);
    public bool IsNull => Type == ExpressionValueType.Null || Value is null;
}

public sealed record ExpressionDiagnostic(int Position, string Message);

public sealed record ExpressionParseResult(ExpressionNode? Root, IReadOnlyList<ExpressionDiagnostic> Diagnostics)
{
    public bool IsValid => Root is not null && Diagnostics.Count == 0;
}
