using System.Globalization;
using System.Text;

namespace Blazor.AI.Decision;

/// <summary>
/// A small, safe business-rule expression language, parsed once into an AST and evaluated against a fact dictionary.
/// <para>
/// Supports numbers, 'strings' or "strings", true/false/null, variables (dotted names allowed), arithmetic
/// (+ - * / %), comparisons (= == != &lt;&gt; &lt; &lt;= &gt; &gt;=), and/or/not (also &amp;&amp; || !), <c>x in ('a','b')</c>,
/// <c>contains</c>, <c>startsWith</c>, and functions: min, max, abs, round, floor, ceil, len, lower, upper, if, coalesce, between.
/// </para>
/// No reflection or code execution is involved, so expressions authored by business users are safe to run.
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    private Expression(string source, Node root)
    {
        Source = source;
        _root = root;
    }

    public string Source { get; }

    /// <summary>Variables referenced by the expression.</summary>
    public IReadOnlySet<string> Variables => _root.Variables();

    public static Expression Parse(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var parser = new Parser(Tokenize(source));
        var root = parser.ParseOr();
        parser.ExpectEnd();
        return new Expression(source, root);
    }

    public static bool TryParse(string source, out Expression? expression, out string? error)
    {
        try
        {
            expression = Parse(source);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }

    public object? Evaluate(IReadOnlyDictionary<string, object?> facts) => _root.Eval(facts);

    public bool EvaluateBool(IReadOnlyDictionary<string, object?> facts) => Truthy(Evaluate(facts));

    public override string ToString() => Source;

    // ------------------------------------------------------------------ values

    internal static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    internal static double? AsNumber(object? value) => value switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        IConvertible c when value is not string => System.Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => null,
    };

    internal static string AsString(object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Normalises CLR values (int, decimal, long...) to double so comparisons are consistent.</summary>
    internal static object? Normalize(object? value) => value switch
    {
        null or string or bool or double => value,
        System.Text.Json.JsonElement e => e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => e.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => e.ToString(),
        },
        IConvertible c and not DateTime => System.Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => value,
    };

    internal static bool ValuesEqual(object? a, object? b)
    {
        a = Normalize(a);
        b = Normalize(b);
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (AsNumber(a) is { } x && AsNumber(b) is { } y && (a is double || b is double))
        {
            return Math.Abs(x - y) < 1e-9;
        }

        return string.Equals(AsString(a), AsString(b), StringComparison.OrdinalIgnoreCase);
    }

    internal static int Compare(object? a, object? b)
    {
        if (AsNumber(Normalize(a)) is { } x && AsNumber(Normalize(b)) is { } y)
        {
            return x.CompareTo(y);
        }

        return string.Compare(AsString(a), AsString(b), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ tokenizer

    private enum T
    {
        Number,
        String,
        Ident,
        Op,
        LParen,
        RParen,
        Comma,
        LBracket,
        RBracket,
        End,
    }

    private readonly record struct Token(T Kind, string Text, int Position);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
            {
                while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.' || s[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(T.Number, s[start..i].Replace("_", string.Empty, StringComparison.Ordinal), start));
            }
            else if (c is '\'' or '"')
            {
                i++;
                var builder = new StringBuilder();
                while (i < s.Length && s[i] != c)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                    }

                    builder.Append(s[i++]);
                }

                if (i >= s.Length)
                {
                    throw new FormatException($"Unterminated string starting at position {start + 1}.");
                }

                i++;
                tokens.Add(new Token(T.String, builder.ToString(), start));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.'))
                {
                    i++;
                }

                tokens.Add(new Token(T.Ident, s[start..i], start));
            }
            else
            {
                var two = i + 1 < s.Length ? s.Substring(i, 2) : null;
                if (two is "==" or "!=" or "<=" or ">=" or "<>" or "&&" or "||")
                {
                    tokens.Add(new Token(T.Op, two, start));
                    i += 2;
                    continue;
                }

                i++;
                tokens.Add(c switch
                {
                    '(' => new Token(T.LParen, "(", start),
                    ')' => new Token(T.RParen, ")", start),
                    '[' => new Token(T.LBracket, "[", start),
                    ']' => new Token(T.RBracket, "]", start),
                    ',' => new Token(T.Comma, ",", start),
                    '+' or '-' or '*' or '/' or '%' or '<' or '>' or '=' or '!' => new Token(T.Op, c.ToString(), start),
                    _ => throw new FormatException($"Unexpected character '{c}' at position {start + 1}."),
                });
            }
        }

        tokens.Add(new Token(T.End, string.Empty, s.Length));
        return tokens;
    }

    // ------------------------------------------------------------------ parser

    private sealed class Parser(List<Token> tokens)
    {
        private int _i;

        private Token Peek => tokens[_i];

        public void ExpectEnd()
        {
            if (Peek.Kind != T.End)
            {
                throw new FormatException($"Unexpected '{Peek.Text}' at position {Peek.Position + 1}.");
            }
        }

        private bool IsWord(string word) => Peek.Kind == T.Ident && Peek.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

        private bool IsOp(string op) => Peek.Kind == T.Op && Peek.Text == op;

        private Token Next() => tokens[_i++];

        private void Expect(T kind, string what)
        {
            if (Peek.Kind != kind)
            {
                throw new FormatException($"Expected {what} at position {Peek.Position + 1}.");
            }

            _i++;
        }

        public Node ParseOr()
        {
            var left = ParseAnd();
            while (IsWord("or") || IsOp("||"))
            {
                Next();
                left = new Logical(left, ParseAnd(), false);
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (IsWord("and") || IsOp("&&"))
            {
                Next();
                left = new Logical(left, ParseNot(), true);
            }

            return left;
        }

        private Node ParseNot()
        {
            if (IsWord("not") || IsOp("!"))
            {
                Next();
                return new Not(ParseNot());
            }

            return ParseComparison();
        }

        private Node ParseComparison()
        {
            var left = ParseAdditive();
            if (Peek.Kind == T.Op && Peek.Text is "=" or "==" or "!=" or "<>" or "<" or "<=" or ">" or ">=")
            {
                var op = Next().Text;
                return new Comparison(left, ParseAdditive(), op);
            }

            var negate = false;
            if (IsWord("not"))
            {
                Next();
                negate = true;
            }

            if (IsWord("in"))
            {
                Next();
                var close = Peek.Kind == T.LBracket ? T.RBracket : T.RParen;
                if (Peek.Kind is not (T.LParen or T.LBracket))
                {
                    throw new FormatException($"Expected '(' after 'in' at position {Peek.Position + 1}.");
                }

                Next();
                var items = new List<Node> { ParseAdditive() };
                while (Peek.Kind == T.Comma)
                {
                    Next();
                    items.Add(ParseAdditive());
                }

                Expect(close, close == T.RBracket ? "']'" : "')'");
                Node node = new InList(left, items);
                return negate ? new Not(node) : node;
            }

            foreach (var word in new[] { "contains", "startswith", "endswith" })
            {
                if (IsWord(word))
                {
                    Next();
                    Node node = new StringTest(left, ParseAdditive(), word);
                    return negate ? new Not(node) : node;
                }
            }

            if (negate)
            {
                throw new FormatException($"Expected 'in' or 'contains' after 'not' at position {Peek.Position + 1}.");
            }

            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (IsOp("+") || IsOp("-"))
            {
                var op = Next().Text[0];
                left = new Binary(left, ParseMultiplicative(), op);
            }

            return left;
        }

        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (IsOp("*") || IsOp("/") || IsOp("%"))
            {
                var op = Next().Text[0];
                left = new Binary(left, ParseUnary(), op);
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (IsOp("-"))
            {
                Next();
                return new Binary(new Literal(0d), ParseUnary(), '-');
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var token = Next();
            switch (token.Kind)
            {
                case T.Number:
                    return new Literal(double.Parse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture));
                case T.String:
                    return new Literal(token.Text);
                case T.LParen:
                    var inner = ParseOr();
                    Expect(T.RParen, "')'");
                    return inner;
                case T.Ident:
                    var lower = token.Text.ToLowerInvariant();
                    if (lower is "true" or "yes") return new Literal(true);
                    if (lower is "false" or "no") return new Literal(false);
                    if (lower == "null") return new Literal(null);
                    if (Peek.Kind == T.LParen)
                    {
                        Next();
                        var args = new List<Node>();
                        if (Peek.Kind != T.RParen)
                        {
                            args.Add(ParseOr());
                            while (Peek.Kind == T.Comma)
                            {
                                Next();
                                args.Add(ParseOr());
                            }
                        }

                        Expect(T.RParen, "')'");
                        return new Call(lower, args);
                    }

                    return new Variable(token.Text);
                default:
                    throw new FormatException(token.Kind == T.End ? "Unexpected end of expression." : $"Unexpected '{token.Text}' at position {token.Position + 1}.");
            }
        }
    }

    // ------------------------------------------------------------------ AST

    private abstract class Node
    {
        public abstract object? Eval(IReadOnlyDictionary<string, object?> facts);

        public virtual IReadOnlySet<string> Variables() => Children().SelectMany(c => c.Variables()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        protected virtual IEnumerable<Node> Children() => [];
    }

    private sealed class Literal(object? value) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts) => value;
    }

    private sealed class Variable(string name) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts)
        {
            if (facts.TryGetValue(name, out var value))
            {
                return Normalize(value);
            }

            foreach (var (key, v) in facts)
            {
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return Normalize(v);
                }
            }

            return null;
        }

        public override IReadOnlySet<string> Variables() => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
    }

    private sealed class Binary(Node left, Node right, char op) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts)
        {
            var a = left.Eval(facts);
            var b = right.Eval(facts);
            if (op == '+' && (a is string || b is string) && (AsNumber(a) is null || AsNumber(b) is null))
            {
                return AsString(a) + AsString(b);
            }

            var x = AsNumber(a) ?? 0;
            var y = AsNumber(b) ?? 0;
            return op switch
            {
                '+' => x + y,
                '-' => x - y,
                '*' => x * y,
                '/' => y == 0 ? double.NaN : x / y,
                _ => y == 0 ? double.NaN : x % y,
            };
        }

        protected override IEnumerable<Node> Children() => [left, right];
    }

    private sealed class Comparison(Node left, Node right, string op) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts)
        {
            var a = left.Eval(facts);
            var b = right.Eval(facts);
            return op switch
            {
                "=" or "==" => ValuesEqual(a, b),
                "!=" or "<>" => !ValuesEqual(a, b),
                _ when a is null || b is null => false,
                "<" => Compare(a, b) < 0,
                "<=" => Compare(a, b) <= 0,
                ">" => Compare(a, b) > 0,
                _ => Compare(a, b) >= 0,
            };
        }

        protected override IEnumerable<Node> Children() => [left, right];
    }

    private sealed class Logical(Node left, Node right, bool and) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts) =>
            and ? Truthy(left.Eval(facts)) && Truthy(right.Eval(facts)) : Truthy(left.Eval(facts)) || Truthy(right.Eval(facts));

        protected override IEnumerable<Node> Children() => [left, right];
    }

    private sealed class Not(Node inner) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts) => !Truthy(inner.Eval(facts));

        protected override IEnumerable<Node> Children() => [inner];
    }

    private sealed class InList(Node value, List<Node> items) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts)
        {
            var v = value.Eval(facts);
            return items.Any(i => ValuesEqual(v, i.Eval(facts)));
        }

        protected override IEnumerable<Node> Children() => items.Prepend(value);
    }

    private sealed class StringTest(Node left, Node right, string kind) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts)
        {
            var a = AsString(left.Eval(facts));
            var b = AsString(right.Eval(facts));
            return kind switch
            {
                "contains" => a.Contains(b, StringComparison.OrdinalIgnoreCase),
                "startswith" => a.StartsWith(b, StringComparison.OrdinalIgnoreCase),
                _ => a.EndsWith(b, StringComparison.OrdinalIgnoreCase),
            };
        }

        protected override IEnumerable<Node> Children() => [left, right];
    }

    private sealed class Call(string name, List<Node> args) : Node
    {
        public override object? Eval(IReadOnlyDictionary<string, object?> facts)
        {
            double N(int i) => AsNumber(args[i].Eval(facts)) ?? 0;
            Require(name switch { "min" or "max" or "coalesce" => 1, "if" => 3, "between" => 3, "round" => 1, _ => 1 });
            return name switch
            {
                "min" => args.Select((_, i) => N(i)).Min(),
                "max" => args.Select((_, i) => N(i)).Max(),
                "abs" => Math.Abs(N(0)),
                "round" => args.Count > 1 ? Math.Round(N(0), (int)N(1), MidpointRounding.AwayFromZero) : Math.Round(N(0), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(N(0)),
                "ceil" => Math.Ceiling(N(0)),
                "sqrt" => Math.Sqrt(N(0)),
                "len" => (double)AsString(args[0].Eval(facts)).Length,
                "lower" => AsString(args[0].Eval(facts)).ToLowerInvariant(),
                "upper" => AsString(args[0].Eval(facts)).ToUpperInvariant(),
                "if" => Truthy(args[0].Eval(facts)) ? args[1].Eval(facts) : args[2].Eval(facts),
                "coalesce" => args.Select(a => a.Eval(facts)).FirstOrDefault(v => v is not null),
                "between" => N(0) >= N(1) && N(0) <= N(2),
                "isnull" => args[0].Eval(facts) is null,
                _ => throw new FormatException($"Unknown function '{name}'."),
            };
        }

        private void Require(int count)
        {
            if (args.Count < count)
            {
                throw new FormatException($"Function '{name}' needs at least {count} argument(s).");
            }
        }

        protected override IEnumerable<Node> Children() => args;
    }
}
