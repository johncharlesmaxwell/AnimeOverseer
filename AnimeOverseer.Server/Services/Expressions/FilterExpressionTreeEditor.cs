using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services.Expressions;

/// <summary>Immutable-style editing helpers for paths in a parsed filter expression tree.</summary>
public static class FilterExpressionTreeEditor
{
    public static string ChildPath(string parentPath, int index) =>
        string.IsNullOrEmpty(parentPath) ? index.ToString() : $"{parentPath}/{index}";

    public static bool TryGet(ExpressionNode root, string path, out ExpressionNode node)
    {
        node = root;
        foreach (var index in ParsePath(path))
        {
            if (node is not FunctionExpression function || index < 0 || index >= function.Arguments.Count)
                return false;
            node = function.Arguments[index];
        }
        return true;
    }

    public static bool TryReplace(ExpressionNode root, string path, ExpressionNode replacement, out ExpressionNode updated)
    {
        if (string.IsNullOrEmpty(path))
        {
            updated = Clone(replacement);
            return true;
        }

        updated = Clone(root);
        if (!TryGetParent(updated, path, out var parent, out var index)) return false;
        parent.Arguments[index] = Clone(replacement);
        return true;
    }

    public static bool TryAppendArgument(ExpressionNode root, string functionPath, ExpressionNode argument, out ExpressionNode updated)
    {
        updated = Clone(root);
        if (!TryGet(updated, functionPath, out var selected) || selected is not FunctionExpression function) return false;
        function.Arguments.Add(Clone(argument));
        return true;
    }

    public static bool TryRemoveArgument(ExpressionNode root, string path, out ExpressionNode updated)
    {
        updated = Clone(root);
        if (!TryGetParent(updated, path, out var parent, out var index)) return false;
        parent.Arguments.RemoveAt(index);
        return true;
    }

    public static bool TryMoveArgument(ExpressionNode root, string path, int offset, out ExpressionNode updated, out string newPath)
    {
        updated = Clone(root);
        newPath = path;
        if (!TryGetParent(updated, path, out var parent, out var index)) return false;
        var destination = index + offset;
        if (destination < 0 || destination >= parent.Arguments.Count) return false;
        (parent.Arguments[index], parent.Arguments[destination]) = (parent.Arguments[destination], parent.Arguments[index]);
        var separator = path.LastIndexOf('/');
        newPath = separator < 0 ? destination.ToString() : $"{path[..separator]}/{destination}";
        return true;
    }

    public static ExpressionNode Clone(ExpressionNode node) => node switch
    {
        FieldExpression field => new FieldExpression { Name = field.Name },
        LiteralExpression literal => new LiteralExpression { ValueType = literal.ValueType, Value = literal.Value },
        FunctionExpression function => new FunctionExpression
        {
            Name = function.Name,
            Arguments = function.Arguments.Select(Clone).ToList()
        },
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown expression node type.")
    };

    public static bool TryGetParent(ExpressionNode root, string path, out FunctionExpression parent, out int childIndex)
    {
        parent = null!;
        childIndex = -1;
        var indexes = ParsePath(path);
        if (indexes.Count == 0) return false;
        childIndex = indexes[^1];
        ExpressionNode current = root;
        foreach (var index in indexes.Take(indexes.Count - 1))
        {
            if (current is not FunctionExpression function || index < 0 || index >= function.Arguments.Count) return false;
            current = function.Arguments[index];
        }
        if (current is not FunctionExpression result || childIndex < 0 || childIndex >= result.Arguments.Count) return false;
        parent = result;
        return true;
    }

    private static IReadOnlyList<int> ParsePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        var indexes = new List<int>();
        foreach (var segment in path.Split('/'))
        {
            if (!int.TryParse(segment, out var index)) return [-1];
            indexes.Add(index);
        }
        return indexes;
    }
}
