using System.Globalization;
using System.Text;
using GnuRadio.Digital;

namespace GnuRadio.Grc;

/// <summary>
/// A parsed GRC parameter expression. GRC parameters are Python expressions; this covers the subset that
/// real flowgraphs use for numbers: literals (ints, floats, complex <c>1j</c>, strings, lists/tuples), variables,
/// arithmetic (<c>+ - * / // % **</c>), comparisons, conditional expressions, and common functions
/// (<c>int</c>, <c>float</c>, <c>abs</c>, <c>round</c>, <c>min</c>, <c>max</c>, <c>pow</c>, <c>math.*</c>, <c>numpy.*</c>,
/// <c>pmt.intern</c>, <c>digital.constellation_*</c>). Each node can be evaluated or translated to C#.
/// </summary>
public abstract class GrcExpression
{
    public abstract object? Evaluate(IReadOnlyDictionary<string, object?> scope);

    /// <summary>Equivalent C# expression (variables become identifiers of the generated program).</summary>
    public abstract string ToCSharp();

    public static GrcExpression Parse(string text) => new Parser(text).ParseAll();

    /// <summary>Parses and evaluates; unparsable text is returned as a plain string (GRC's enum/raw parameters).</summary>
    public static object? Evaluate(string text, IReadOnlyDictionary<string, object?> scope)
    {
        try
        {
            return Parse(text).Evaluate(scope);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    internal static double ToDouble(object? v) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        bool b => b ? 1 : 0,
        Complex32 c => c.Real,
        string s => double.Parse(s, CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"'{v}' is not a number."),
    };

    internal static string Literal(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? d.ToString("0.0", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    private sealed class Constant(object? value, string csharp) : GrcExpression
    {
        public object? Value => value;
        public override object? Evaluate(IReadOnlyDictionary<string, object?> scope) => value;
        public override string ToCSharp() => csharp;
    }

    private sealed class Variable(string name) : GrcExpression
    {
        public string Name => name;

        public override object? Evaluate(IReadOnlyDictionary<string, object?> scope)
        {
            if (scope.TryGetValue(name, out var v)) return v;
            return name switch
            {
                "math.pi" or "numpy.pi" or "np.pi" or "pi" => Math.PI,
                "math.e" or "numpy.e" or "np.e" => Math.E,
                "True" => true,
                "False" => false,
                "None" => null,
                _ => throw new KeyNotFoundException($"Unknown variable '{name}'."),
            };
        }

        public override string ToCSharp() => name switch
        {
            "math.pi" or "numpy.pi" or "np.pi" or "pi" => "Math.PI",
            "math.e" or "numpy.e" or "np.e" => "Math.E",
            "True" => "true",
            "False" => "false",
            "None" => "null",
            _ => CSharpName(name),
        };
    }

    internal static string CSharpName(string name) => name.Replace('.', '_');

    private sealed class Unary(string op, GrcExpression operand) : GrcExpression
    {
        public override object? Evaluate(IReadOnlyDictionary<string, object?> scope)
        {
            var v = operand.Evaluate(scope);
            return op switch
            {
                "-" => v is long l ? -l : v is Complex32 c ? -c : (object)-ToDouble(v),
                "+" => v,
                "not" => !(v is bool b ? b : ToDouble(v) != 0),
                _ => throw new FormatException(op),
            };
        }

        public override string ToCSharp() => op == "not" ? $"!({operand.ToCSharp()})" : $"{op}{operand.ToCSharp()}";
    }

    private sealed class Binary(string op, GrcExpression left, GrcExpression right) : GrcExpression
    {
        public override object? Evaluate(IReadOnlyDictionary<string, object?> scope)
        {
            var a = left.Evaluate(scope);
            var b = right.Evaluate(scope);
            if (op == "+" && (a is string || b is string)) return $"{a}{b}";
            if (a is Complex32 || b is Complex32)
            {
                var ca = a is Complex32 x ? x : new Complex32((float)ToDouble(a), 0);
                var cb = b is Complex32 y ? y : new Complex32((float)ToDouble(b), 0);
                return op switch
                {
                    "+" => ca + cb,
                    "-" => ca - cb,
                    "*" => ca * cb,
                    "/" => ca / cb,
                    _ => throw new FormatException($"Operator {op} is not supported on complex values."),
                };
            }
            if (a is long la && b is long lb && op is "+" or "-" or "*" or "//" or "%" or "**")
            {
                return op switch
                {
                    "+" => la + lb,
                    "-" => la - lb,
                    "*" => la * lb,
                    "//" => (long)Math.Floor((double)la / lb),
                    "%" => ((la % lb) + lb) % lb,
                    _ => lb >= 0 ? (long)Math.Pow(la, lb) : Math.Pow(la, lb),
                };
            }
            var da = ToDouble(a);
            var db = ToDouble(b);
            return op switch
            {
                "+" => da + db,
                "-" => da - db,
                "*" => da * db,
                "/" => da / db,
                "//" => Math.Floor(da / db),
                "%" => da - db * Math.Floor(da / db),
                "**" => Math.Pow(da, db),
                "<" => da < db,
                ">" => da > db,
                "<=" => da <= db,
                ">=" => da >= db,
                "==" => da == db,
                "!=" => da != db,
                "and" => da != 0 && db != 0,
                "or" => da != 0 || db != 0,
                _ => throw new FormatException(op),
            };
        }

        public override string ToCSharp() => op switch
        {
            "**" => $"Math.Pow({left.ToCSharp()}, {right.ToCSharp()})",
            "//" => $"Math.Floor((double)({left.ToCSharp()}) / ({right.ToCSharp()}))",
            "and" => $"({left.ToCSharp()} && {right.ToCSharp()})",
            "or" => $"({left.ToCSharp()} || {right.ToCSharp()})",
            "/" => $"((double)({left.ToCSharp()}) / ({right.ToCSharp()}))",
            _ => $"({left.ToCSharp()} {op} {right.ToCSharp()})",
        };
    }

    private sealed class Conditional(GrcExpression whenTrue, GrcExpression condition, GrcExpression whenFalse) : GrcExpression
    {
        public override object? Evaluate(IReadOnlyDictionary<string, object?> scope)
        {
            var c = condition.Evaluate(scope);
            var truth = c is bool b ? b : ToDouble(c) != 0;
            return truth ? whenTrue.Evaluate(scope) : whenFalse.Evaluate(scope);
        }

        public override string ToCSharp() => $"({condition.ToCSharp()} ? {whenTrue.ToCSharp()} : {whenFalse.ToCSharp()})";
    }

    private sealed class ListLiteral(IReadOnlyList<GrcExpression> items) : GrcExpression
    {
        public override object? Evaluate(IReadOnlyDictionary<string, object?> scope) => items.Select(i => i.Evaluate(scope)).ToArray();
        public override string ToCSharp() => $"new[] {{ {string.Join(", ", items.Select(i => i.ToCSharp()))} }}";
    }

    private sealed class Call(string function, IReadOnlyList<GrcExpression> args) : GrcExpression
    {
        public override object? Evaluate(IReadOnlyDictionary<string, object?> scope)
        {
            var v = args.Select(a => a.Evaluate(scope)).ToArray();
            double D(int i) => ToDouble(v[i]);
            return function switch
            {
                "int" => (long)D(0),
                "float" => D(0),
                "abs" => Math.Abs(D(0)),
                "round" => args.Count > 1 ? Math.Round(D(0), (int)D(1)) : Math.Round(D(0)),
                "min" => v.Select(ToDouble).Min(),
                "max" => v.Select(ToDouble).Max(),
                "pow" or "math.pow" => Math.Pow(D(0), D(1)),
                "len" => v[0] is Array a ? (long)a.Length : v[0] is string s ? s.Length : throw new FormatException("len"),
                "range" => Enumerable.Range(0, (int)D(0)).Select(i => (object?)(long)i).ToArray(),
                "complex" => new Complex32((float)D(0), args.Count > 1 ? (float)D(1) : 0),
                "pmt.intern" => Pmt.Symbol(Convert.ToString(v[0], CultureInfo.InvariantCulture) ?? ""),
                "pmt.from_double" => Pmt.From(D(0)),
                "pmt.from_long" => Pmt.From((long)D(0)),
                "pmt.PMT_T" => Pmt.True,
                "digital.constellation_bpsk" => Constellation.Bpsk(),
                "digital.constellation_qpsk" => Constellation.Qpsk(),
                "digital.constellation_8psk" => Constellation.Psk8(),
                "digital.constellation_16qam" => Constellation.Qam(16),
                _ when function.StartsWith("math.", StringComparison.Ordinal) || function.StartsWith("numpy.", StringComparison.Ordinal) || function.StartsWith("np.", StringComparison.Ordinal)
                    => MathFunction(function[(function.IndexOf('.') + 1)..], D(0)),
                _ => throw new FormatException($"Function '{function}' is not supported."),
            };
        }

        private static double MathFunction(string name, double x) => name switch
        {
            "sin" => Math.Sin(x),
            "cos" => Math.Cos(x),
            "tan" => Math.Tan(x),
            "atan" => Math.Atan(x),
            "sqrt" => Math.Sqrt(x),
            "exp" => Math.Exp(x),
            "log" => Math.Log(x),
            "log10" => Math.Log10(x),
            "log2" => Math.Log2(x),
            "floor" => Math.Floor(x),
            "ceil" => Math.Ceiling(x),
            "fabs" or "abs" => Math.Abs(x),
            "radians" => x * Math.PI / 180,
            "degrees" => x * 180 / Math.PI,
            _ => throw new FormatException($"math.{name} is not supported."),
        };

        public override string ToCSharp()
        {
            var a = args.Select(x => x.ToCSharp()).ToArray();
            return function switch
            {
                "int" => $"(long)({a[0]})",
                "float" => $"(double)({a[0]})",
                "abs" => $"Math.Abs({a[0]})",
                "round" => $"Math.Round({string.Join(", ", a)})",
                "min" => $"Math.Min({string.Join(", ", a)})",
                "max" => $"Math.Max({string.Join(", ", a)})",
                "pow" or "math.pow" => $"Math.Pow({a[0]}, {a[1]})",
                "complex" => $"new Complex32((float)({a[0]}), (float)({(a.Length > 1 ? a[1] : "0")}))",
                "pmt.intern" => $"Pmt.Symbol({a[0]})",
                "pmt.from_double" or "pmt.from_long" => $"Pmt.From({a[0]})",
                "digital.constellation_bpsk" => "Constellation.Bpsk()",
                "digital.constellation_qpsk" => "Constellation.Qpsk()",
                "digital.constellation_8psk" => "Constellation.Psk8()",
                "digital.constellation_16qam" => "Constellation.Qam(16)",
                _ when function.Contains('.') => $"Math.{Capitalize(function[(function.IndexOf('.') + 1)..])}({a[0]})",
                _ => $"{function}({string.Join(", ", a)})",
            };
        }

        private static string Capitalize(string s) => s switch
        {
            "log" => "Log",
            "fabs" => "Abs",
            "ceil" => "Ceiling",
            _ => char.ToUpperInvariant(s[0]) + s[1..],
        };
    }

    private sealed class Parser(string text)
    {
        private readonly List<(string Kind, string Value)> _tokens = Tokenize(text);
        private int _pos;

        public GrcExpression ParseAll()
        {
            var e = ParseConditional();
            if (_pos != _tokens.Count) throw new FormatException($"Unexpected '{_tokens[_pos].Value}' in '{text}'.");
            return e;
        }

        private (string Kind, string Value) Peek => _pos < _tokens.Count ? _tokens[_pos] : ("eof", "");

        private bool Accept(string value)
        {
            if (_pos < _tokens.Count && _tokens[_pos].Value == value && _tokens[_pos].Kind is "op" or "name")
            {
                _pos++;
                return true;
            }
            return false;
        }

        private void Expect(string value)
        {
            if (!Accept(value)) throw new FormatException($"Expected '{value}' in '{text}'.");
        }

        private GrcExpression ParseConditional()
        {
            var e = ParseOr();
            if (Peek is ("name", "if"))
            {
                _pos++;
                var condition = ParseOr();
                Expect("else");
                var other = ParseConditional();
                return new Conditional(e, condition, other);
            }
            return e;
        }

        private GrcExpression ParseOr()
        {
            var e = ParseAnd();
            while (Accept("or")) e = new Binary("or", e, ParseAnd());
            return e;
        }

        private GrcExpression ParseAnd()
        {
            var e = ParseNot();
            while (Accept("and")) e = new Binary("and", e, ParseNot());
            return e;
        }

        private GrcExpression ParseNot() => Accept("not") ? new Unary("not", ParseNot()) : ParseComparison();

        private GrcExpression ParseComparison()
        {
            var e = ParseAdditive();
            while (Peek.Value is "<" or ">" or "<=" or ">=" or "==" or "!=" && Peek.Kind == "op")
            {
                var op = _tokens[_pos++].Value;
                e = new Binary(op, e, ParseAdditive());
            }
            return e;
        }

        private GrcExpression ParseAdditive()
        {
            var e = ParseMultiplicative();
            while (Peek is ("op", "+") or ("op", "-"))
            {
                var op = _tokens[_pos++].Value;
                e = new Binary(op, e, ParseMultiplicative());
            }
            return e;
        }

        private GrcExpression ParseMultiplicative()
        {
            var e = ParseUnary();
            while (Peek is ("op", "*") or ("op", "/") or ("op", "//") or ("op", "%"))
            {
                var op = _tokens[_pos++].Value;
                e = new Binary(op, e, ParseUnary());
            }
            return e;
        }

        private GrcExpression ParseUnary()
        {
            if (Peek is ("op", "-") or ("op", "+"))
            {
                var op = _tokens[_pos++].Value;
                return new Unary(op, ParseUnary());
            }
            return ParsePower();
        }

        private GrcExpression ParsePower()
        {
            var e = ParsePrimary();
            if (Accept("**")) return new Binary("**", e, ParseUnary());
            return e;
        }

        private GrcExpression ParsePrimary()
        {
            var (kind, value) = Peek;
            switch (kind)
            {
                case "int":
                    _pos++;
                    return new Constant(long.Parse(value, CultureInfo.InvariantCulture), value);
                case "float":
                    _pos++;
                    var d = double.Parse(value, CultureInfo.InvariantCulture);
                    return new Constant(d, Literal(d));
                case "imag":
                    _pos++;
                    var im = float.Parse(value, CultureInfo.InvariantCulture);
                    return new Constant(new Complex32(0, im), $"new Complex32(0f, {im.ToString(CultureInfo.InvariantCulture)}f)");
                case "string":
                    _pos++;
                    return new Constant(value, "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                case "name":
                {
                    _pos++;
                    if (Accept("(")) return new Call(value, ParseArguments(")"));
                    return new Variable(value);
                }
                case "op" when value == "(":
                {
                    _pos++;
                    var items = ParseArguments(")");
                    return items.Count == 1 ? items[0] : new ListLiteral(items);
                }
                case "op" when value == "[":
                    _pos++;
                    return new ListLiteral(ParseArguments("]"));
                default:
                    throw new FormatException($"Unexpected '{value}' in '{text}'.");
            }
        }

        private List<GrcExpression> ParseArguments(string close)
        {
            var items = new List<GrcExpression>();
            if (Accept(close)) return items;
            do
            {
                if (Peek.Value == close) break;
                items.Add(ParseConditional());
            } while (Accept(","));
            Expect(close);
            return items;
        }

        private static List<(string, string)> Tokenize(string s)
        {
            var tokens = new List<(string, string)>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    var start = i;
                    var isFloat = false;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '_'))
                    {
                        if (s[i] == '.') isFloat = true;
                        i++;
                    }
                    if (i < s.Length && s[i] is 'e' or 'E')
                    {
                        isFloat = true;
                        i++;
                        if (i < s.Length && s[i] is '+' or '-') i++;
                        while (i < s.Length && char.IsDigit(s[i])) i++;
                    }
                    var number = s[start..i].Replace("_", "");
                    if (i < s.Length && s[i] is 'j' or 'J')
                    {
                        i++;
                        tokens.Add(("imag", number));
                    }
                    else
                    {
                        tokens.Add((isFloat ? "float" : "int", number));
                    }
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.')) i++;
                    tokens.Add(("name", s[start..i]));
                    continue;
                }
                if (c is '\'' or '"')
                {
                    var end = s.IndexOf(c, i + 1);
                    if (end < 0) throw new FormatException("Unterminated string.");
                    tokens.Add(("string", s[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "**" or "//" or "<=" or ">=" or "==" or "!=")
                {
                    tokens.Add(("op", two));
                    i += 2;
                    continue;
                }
                if ("+-*/%()[],<>".Contains(c))
                {
                    tokens.Add(("op", c.ToString()));
                    i++;
                    continue;
                }
                throw new FormatException($"Unexpected character '{c}' in '{s}'.");
            }
            return tokens;
        }
    }

    internal static string Describe(object? value) => value switch
    {
        null => "None",
        double d => d.ToString("G", CultureInfo.InvariantCulture),
        Array a => "[" + string.Join(", ", a.Cast<object?>().Select(Describe)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    internal static string Join(IEnumerable<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p);
        return sb.ToString();
    }
}
