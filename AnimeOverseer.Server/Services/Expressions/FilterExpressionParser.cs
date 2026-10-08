using System.Globalization;
using System.Text;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services.Expressions;

public static class FilterExpressionParser
{
    public const int MaximumLength = 4096;
    public const int MaximumDepth = 16;
    public const int MaximumArguments = 32;

    public static ExpressionParseResult Parse(string source)
    {
        source = Unwrap(source.Trim());
        if (source.Length == 0)
            return new(null, [new(0, "Expression is empty.")]);
        if (source.Length > MaximumLength)
            return new(null, [new(MaximumLength, $"Expression exceeds {MaximumLength} characters.")]);

        var parser = new Parser(source);
        var root = parser.ParseExpression(0);
        parser.SkipWhiteSpace();
        if (root is not null && !parser.AtEnd)
            parser.Error("Unexpected text.");
        return new(parser.Diagnostics.Count == 0 ? root : null, parser.Diagnostics);
    }

    public static FilterExpression ParseOperand(string source)
    {
        var result = Parse(source);
        return new FilterExpression { Source = source, Root = result.Root };
    }

    public static string Format(ExpressionNode node) => node switch
    {
        FieldExpression field => field.Name,
        LiteralExpression { ValueType: ExpressionValueType.Null } => "null",
        LiteralExpression { ValueType: ExpressionValueType.Boolean } literal => literal.Value?.ToLowerInvariant() ?? "false",
        LiteralExpression { ValueType: ExpressionValueType.Number } literal => literal.Value ?? "0",
        LiteralExpression literal => $"\"{Escape(literal.Value ?? string.Empty)}\"",
        FunctionExpression function => $"{function.Name}({string.Join(", ", function.Arguments.Select(Format))})",
        _ => string.Empty
    };

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Unwrap(string source) =>
        source.StartsWith("{{", StringComparison.Ordinal) && source.EndsWith("}}", StringComparison.Ordinal) && source.Length >= 4
            ? source[2..^2].Trim()
            : source;

    private sealed class Parser(string source)
    {
        private int position;
        public List<ExpressionDiagnostic> Diagnostics { get; } = [];
        public bool AtEnd => position >= source.Length;

        public ExpressionNode? ParseExpression(int depth)
        {
            if (depth > MaximumDepth)
            {
                Error($"Expression nesting exceeds {MaximumDepth} levels.");
                return null;
            }

            SkipWhiteSpace();
            if (AtEnd) { Error("Expected an expression."); return null; }
            if (source[position] is '\'' or '"') return ParseString();
            if (char.IsDigit(source[position]) || source[position] == '-' && position + 1 < source.Length && char.IsDigit(source[position + 1]))
                return ParseNumber();
            if (!IsIdentifierStart(source[position])) { Error("Expected a field, literal, or function."); return null; }

            var identifier = ParseIdentifier();
            SkipWhiteSpace();
            if (!AtEnd && source[position] == '(')
                return ParseFunction(identifier, depth);
            if (identifier.Equals("null", StringComparison.OrdinalIgnoreCase))
                return new LiteralExpression { ValueType = ExpressionValueType.Null };
            if (bool.TryParse(identifier, out var boolean))
                return new LiteralExpression { ValueType = ExpressionValueType.Boolean, Value = boolean.ToString(CultureInfo.InvariantCulture) };
            return new FieldExpression { Name = identifier };
        }

        private ExpressionNode? ParseFunction(string name, int depth)
        {
            position++;
            var arguments = new List<ExpressionNode>();
            SkipWhiteSpace();
            if (!AtEnd && source[position] == ')')
            {
                position++;
                return new FunctionExpression { Name = name, Arguments = arguments };
            }

            while (!AtEnd)
            {
                if (arguments.Count >= MaximumArguments)
                {
                    Error($"A function cannot have more than {MaximumArguments} arguments.");
                    return null;
                }
                var argument = ParseExpression(depth + 1);
                if (argument is null) return null;
                arguments.Add(argument);
                SkipWhiteSpace();
                if (!AtEnd && source[position] == ',') { position++; continue; }
                if (!AtEnd && source[position] == ')')
                {
                    position++;
                    return new FunctionExpression { Name = name, Arguments = arguments };
                }
                Error("Expected ',' or ')'.");
                return null;
            }
            Error("Expected ')'.");
            return null;
        }

        private LiteralExpression ParseString()
        {
            var quote = source[position++];
            var value = new StringBuilder();
            while (!AtEnd)
            {
                var character = source[position++];
                if (character == quote)
                    return new LiteralExpression { ValueType = ExpressionValueType.String, Value = value.ToString() };
                if (character == '\\' && !AtEnd)
                {
                    var escaped = source[position++];
                    value.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped });
                }
                else value.Append(character);
            }
            Error("Unterminated string literal.");
            return new LiteralExpression { ValueType = ExpressionValueType.String, Value = value.ToString() };
        }

        private LiteralExpression ParseNumber()
        {
            var start = position++;
            while (!AtEnd && (char.IsDigit(source[position]) || source[position] is '.' or 'e' or 'E' or '+' or '-')) position++;
            var text = source[start..position];
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) Error("Invalid number.");
            return new LiteralExpression { ValueType = ExpressionValueType.Number, Value = text };
        }

        private string ParseIdentifier()
        {
            var start = position++;
            while (!AtEnd && (char.IsLetterOrDigit(source[position]) || source[position] == '_')) position++;
            return source[start..position];
        }

        public void SkipWhiteSpace()
        {
            while (!AtEnd && char.IsWhiteSpace(source[position])) position++;
        }

        public void Error(string message)
        {
            if (Diagnostics.Count == 0) Diagnostics.Add(new(position, message));
        }

        private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';
    }
}
