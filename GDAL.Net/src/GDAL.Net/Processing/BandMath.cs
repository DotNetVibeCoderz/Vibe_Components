using System.Buffers;
using System.Globalization;
using System.Numerics.Tensors;

namespace GDAL.Net.Processing;

/// <summary>
/// Vectorized raster calculator. Expressions reference bands by name (e.g. <c>(NIR - RED) / (NIR + RED)</c> or
/// <c>B4 * 0.0001</c>) and support <c>+ - * / ^</c>, comparisons (<c>&lt; &lt;= &gt; &gt;= == !=</c>), logic
/// (<c>&amp;&amp; || !</c>), the conditional <c>cond ? a : b</c> and functions
/// <c>abs sqrt exp log log10 sin cos tan floor ceil round min max pow clamp where</c>.
/// Rasters are streamed in row blocks with pooled buffers, so memory use is constant regardless of raster size.
/// </summary>
public sealed class BandMath
{
    private readonly Node _root;
    private readonly string[] _variables;

    private BandMath(string expression, Node root, string[] variables)
    {
        Expression = expression;
        _root = root;
        _variables = variables;
    }

    public string Expression { get; }

    /// <summary>Variable names used by the expression (upper-case).</summary>
    public IReadOnlyList<string> Variables => _variables;

    /// <summary>Parses and validates an expression.</summary>
    public static BandMath Compile(string expression)
    {
        var parser = new Parser(expression);
        var root = parser.ParseExpression();
        parser.ExpectEnd();
        return new BandMath(expression, root, [.. parser.Variables]);
    }

    /// <summary>Evaluates the expression over in-memory arrays (same length). Pixels where any input is NaN become NaN.</summary>
    public void Evaluate(IReadOnlyDictionary<string, float[]> inputs, Span<float> output)
    {
        var len = output.Length;
        var arrays = new float[_variables.Length][];
        for (var i = 0; i < _variables.Length; i++)
        {
            if (!TryGet(inputs, _variables[i], out var arr)) throw new KeyNotFoundException($"No input named '{_variables[i]}'.");
            if (arr.Length < len) throw new ArgumentException($"Input '{_variables[i]}' is shorter than the output.");
            arrays[i] = arr;
        }
        var ctx = new EvalContext(arrays.Select(a => (ReadOnlyMemory<float>)a.AsMemory(0, len)).ToArray());
        _root.Eval(ctx, output);
    }

    private static bool TryGet<T>(IReadOnlyDictionary<string, T> d, string key, out T value)
    {
        foreach (var kv in d)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Evaluates the expression over raster bands (all of the same size) and writes the result into <paramref name="output"/>.
    /// Pixels where any input equals its nodata value (or is NaN) are written as <paramref name="outputNoData"/>.
    /// </summary>
    public void Evaluate(IReadOnlyDictionary<string, RasterBand> inputs, RasterBand output, float outputNoData = float.NaN,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var bands = new RasterBand[_variables.Length];
        for (var i = 0; i < _variables.Length; i++)
        {
            if (!TryGet(inputs, _variables[i], out var band)) throw new KeyNotFoundException($"No input band named '{_variables[i]}'.");
            bands[i] = band;
        }
        var width = output.Width;
        var height = output.Height;
        foreach (var b in bands)
        {
            if (b.Width != width || b.Height != height) throw new ArgumentException("All bands must have the same size as the output (warp them first).");
        }

        var noData = bands.Select(b => b.NoDataValue is { } v ? (float?)(float)v : null).ToArray();
        var rows = Math.Clamp((1 << 20) / Math.Max(1, width), 1, height);
        var pixels = rows * width;
        var pool = ArrayPool<float>.Shared;
        var buffers = bands.Select(_ => pool.Rent(pixels)).ToArray();
        var outBuffer = pool.Rent(pixels);
        try
        {
            for (var y = 0; y < height; y += rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = Math.Min(rows, height - y);
                var count = n * width;
                for (var i = 0; i < bands.Length; i++)
                {
                    bands[i].Read<float>(0, y, width, n, buffers[i].AsSpan(0, count));
                }
                var ctx = new EvalContext(buffers.Select(b => (ReadOnlyMemory<float>)b.AsMemory(0, count)).ToArray());
                var outSpan = outBuffer.AsSpan(0, count);
                _root.Eval(ctx, outSpan);

                for (var i = 0; i < bands.Length; i++)
                {
                    var src = buffers[i].AsSpan(0, count);
                    var nd = noData[i];
                    for (var p = 0; p < count; p++)
                    {
                        var v = src[p];
                        if (float.IsNaN(v) || (nd.HasValue && v == nd.Value)) outSpan[p] = outputNoData;
                    }
                }
                for (var p = 0; p < count; p++)
                {
                    if (float.IsInfinity(outSpan[p])) outSpan[p] = outputNoData;
                }
                output.Write<float>(0, y, width, n, outSpan);
                progress?.Report(new GdalProgress((double)(y + n) / height, null));
            }
            if (!float.IsNaN(outputNoData)) output.NoDataValue = outputNoData;
        }
        finally
        {
            foreach (var b in buffers) pool.Return(b);
            pool.Return(outBuffer);
        }
    }

    /// <summary>
    /// One-liner: evaluates the expression over bands of <paramref name="source"/> named B1..Bn (plus optional aliases such as
    /// NIR=4, RED=3) and returns a new single-band Float32 in-memory dataset with the same georeferencing.
    /// </summary>
    public static Dataset Calculate(Dataset source, string expression, IReadOnlyDictionary<string, int>? aliases = null, float outputNoData = float.NaN,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var math = Compile(expression);
        var inputs = new Dictionary<string, RasterBand>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= source.RasterCount; i++) inputs[$"B{i}"] = source.GetBand(i);
        if (aliases is not null)
        {
            foreach (var kv in aliases) inputs[kv.Key] = source.GetBand(kv.Value);
        }
        var result = RasterFactory.CreateLike(source, 1, DataType.Float32);
        math.Evaluate(inputs, result.GetBand(1), outputNoData, progress, cancellationToken);
        return result;
    }

    // ================================================================== evaluation

    private sealed class EvalContext(ReadOnlyMemory<float>[] inputs)
    {
        public ReadOnlyMemory<float>[] Inputs { get; } = inputs;
    }

    private abstract class Node
    {
        public abstract void Eval(EvalContext ctx, Span<float> dst);

        protected static float[] Rent(int n) => ArrayPool<float>.Shared.Rent(n);
        protected static void Return(float[] a) => ArrayPool<float>.Shared.Return(a);
    }

    private sealed class Const(float value) : Node
    {
        public float Value { get; } = value;
        public override void Eval(EvalContext ctx, Span<float> dst) => dst.Fill(Value);
    }

    private sealed class Var(int index) : Node
    {
        public override void Eval(EvalContext ctx, Span<float> dst) => ctx.Inputs[index].Span[..dst.Length].CopyTo(dst);
    }

    private sealed class Unary(char op, Node operand) : Node
    {
        public override void Eval(EvalContext ctx, Span<float> dst)
        {
            operand.Eval(ctx, dst);
            if (op == '-') TensorPrimitives.Negate(dst, dst);
            else if (op == '!')
            {
                for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] == 0 ? 1 : 0;
            }
        }
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override void Eval(EvalContext ctx, Span<float> dst)
        {
            left.Eval(ctx, dst);
            if (right is Const c)
            {
                // Scalar fast paths.
                switch (op)
                {
                    case "+": TensorPrimitives.Add(dst, c.Value, dst); return;
                    case "-": TensorPrimitives.Subtract(dst, c.Value, dst); return;
                    case "*": TensorPrimitives.Multiply(dst, c.Value, dst); return;
                    case "/": TensorPrimitives.Divide(dst, c.Value, dst); return;
                }
            }
            var tmp = Rent(dst.Length);
            try
            {
                var r = tmp.AsSpan(0, dst.Length);
                right.Eval(ctx, r);
                switch (op)
                {
                    case "+": TensorPrimitives.Add(dst, r, dst); break;
                    case "-": TensorPrimitives.Subtract(dst, r, dst); break;
                    case "*": TensorPrimitives.Multiply(dst, r, dst); break;
                    case "/": TensorPrimitives.Divide(dst, r, dst); break;
                    case "^": TensorPrimitives.Pow(dst, r, dst); break;
                    case "<": for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] < r[i] ? 1 : 0; break;
                    case "<=": for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] <= r[i] ? 1 : 0; break;
                    case ">": for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] > r[i] ? 1 : 0; break;
                    case ">=": for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] >= r[i] ? 1 : 0; break;
                    case "==": for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] == r[i] ? 1 : 0; break;
                    case "!=": for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] != r[i] ? 1 : 0; break;
                    case "&&": for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] != 0 && r[i] != 0 ? 1 : 0; break;
                    case "||": for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] != 0 || r[i] != 0 ? 1 : 0; break;
                    default: throw new InvalidOperationException($"Unknown operator {op}");
                }
            }
            finally
            {
                Return(tmp);
            }
        }
    }

    private sealed class Conditional(Node condition, Node whenTrue, Node whenFalse) : Node
    {
        public override void Eval(EvalContext ctx, Span<float> dst)
        {
            var t = Rent(dst.Length);
            var f = Rent(dst.Length);
            try
            {
                condition.Eval(ctx, dst);
                var ts = t.AsSpan(0, dst.Length);
                var fs = f.AsSpan(0, dst.Length);
                whenTrue.Eval(ctx, ts);
                whenFalse.Eval(ctx, fs);
                for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] != 0 ? ts[i] : fs[i];
            }
            finally
            {
                Return(t);
                Return(f);
            }
        }
    }

    private sealed class Call(string name, Node[] args) : Node
    {
        public override void Eval(EvalContext ctx, Span<float> dst)
        {
            if (name == "where")
            {
                new Conditional(args[0], args[1], args[2]).Eval(ctx, dst);
                return;
            }
            args[0].Eval(ctx, dst);
            switch (name)
            {
                case "abs": TensorPrimitives.Abs(dst, dst); return;
                case "sqrt": TensorPrimitives.Sqrt(dst, dst); return;
                case "exp": TensorPrimitives.Exp(dst, dst); return;
                case "log": TensorPrimitives.Log(dst, dst); return;
                case "log10": TensorPrimitives.Log10(dst, dst); return;
                case "sin": TensorPrimitives.Sin(dst, dst); return;
                case "cos": TensorPrimitives.Cos(dst, dst); return;
                case "tan": TensorPrimitives.Tan(dst, dst); return;
                case "floor": TensorPrimitives.Floor(dst, dst); return;
                case "ceil": TensorPrimitives.Ceiling(dst, dst); return;
                case "round": TensorPrimitives.Round(dst, dst); return;
            }
            var tmp = Rent(dst.Length);
            var tmp2 = name == "clamp" ? Rent(dst.Length) : null;
            try
            {
                var b = tmp.AsSpan(0, dst.Length);
                args[1].Eval(ctx, b);
                switch (name)
                {
                    case "min": TensorPrimitives.Min(dst, b, dst); break;
                    case "max": TensorPrimitives.Max(dst, b, dst); break;
                    case "pow": TensorPrimitives.Pow(dst, b, dst); break;
                    case "clamp":
                        var c = tmp2!.AsSpan(0, dst.Length);
                        args[2].Eval(ctx, c);
                        TensorPrimitives.Max(dst, b, dst);
                        TensorPrimitives.Min(dst, c, dst);
                        break;
                    default: throw new InvalidOperationException($"Unknown function {name}");
                }
            }
            finally
            {
                Return(tmp);
                if (tmp2 is not null) Return(tmp2);
            }
        }
    }

    // ================================================================== parser (recursive descent)

    private sealed class Parser(string text)
    {
        private static readonly Dictionary<string, int> s_functions = new()
        {
            ["abs"] = 1, ["sqrt"] = 1, ["exp"] = 1, ["log"] = 1, ["log10"] = 1, ["sin"] = 1, ["cos"] = 1, ["tan"] = 1,
            ["floor"] = 1, ["ceil"] = 1, ["round"] = 1, ["min"] = 2, ["max"] = 2, ["pow"] = 2, ["clamp"] = 3, ["where"] = 3,
        };

        private int _pos;
        public List<string> Variables { get; } = [];

        public Node ParseExpression()
        {
            var cond = ParseBinary(0);
            SkipWs();
            if (Peek('?'))
            {
                _pos++;
                var a = ParseExpression();
                Expect(':');
                var b = ParseExpression();
                return new Conditional(cond, a, b);
            }
            return cond;
        }

        private static readonly string[][] s_levels =
        [
            ["||"],
            ["&&"],
            ["==", "!="],
            ["<=", ">=", "<", ">"],
            ["+", "-"],
            ["*", "/"],
        ];

        private Node ParseBinary(int level)
        {
            if (level == s_levels.Length) return ParsePower();
            var left = ParseBinary(level + 1);
            while (true)
            {
                SkipWs();
                var op = s_levels[level].FirstOrDefault(o => string.CompareOrdinal(text, _pos, o, 0, o.Length) == 0);
                if (op is null) return left;
                _pos += op.Length;
                var right = ParseBinary(level + 1);
                left = Fold(op, left, right);
            }
        }

        private Node ParsePower()
        {
            var b = ParseUnary();
            SkipWs();
            if (Peek('^'))
            {
                _pos++;
                var e = ParsePower(); // right associative
                return Fold("^", b, e);
            }
            return b;
        }

        private Node ParseUnary()
        {
            SkipWs();
            if (Peek('-'))
            {
                _pos++;
                var operand = ParseUnary();
                return operand is Const c ? new Const(-c.Value) : new Unary('-', operand);
            }
            if (Peek('+'))
            {
                _pos++;
                return ParseUnary();
            }
            if (Peek('!') && !(text.Length > _pos + 1 && text[_pos + 1] == '='))
            {
                _pos++;
                return new Unary('!', ParseUnary());
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipWs();
            if (_pos >= text.Length) throw Error("Unexpected end of expression");
            var ch = text[_pos];
            if (ch == '(')
            {
                _pos++;
                var e = ParseExpression();
                Expect(')');
                return e;
            }
            if (char.IsDigit(ch) || ch == '.')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.')) _pos++;
                if (_pos < text.Length && (text[_pos] == 'e' || text[_pos] == 'E'))
                {
                    _pos++;
                    if (_pos < text.Length && (text[_pos] == '+' || text[_pos] == '-')) _pos++;
                    while (_pos < text.Length && char.IsDigit(text[_pos])) _pos++;
                }
                return new Const(float.Parse(text.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
                var name = text[start.._pos];
                SkipWs();
                var lower = name.ToLowerInvariant();
                if (Peek('(') && s_functions.TryGetValue(lower, out var arity))
                {
                    _pos++;
                    var args = new List<Node>();
                    SkipWs();
                    if (!Peek(')'))
                    {
                        while (true)
                        {
                            args.Add(ParseExpression());
                            SkipWs();
                            if (Peek(',')) { _pos++; continue; }
                            break;
                        }
                    }
                    Expect(')');
                    if (args.Count != arity) throw Error($"Function {lower} expects {arity} argument(s)");
                    return new Call(lower, [.. args]);
                }
                if (lower == "pi") return new Const(MathF.PI);
                if (lower == "e") return new Const(MathF.E);
                var upper = name.ToUpperInvariant();
                var idx = Variables.IndexOf(upper);
                if (idx < 0)
                {
                    Variables.Add(upper);
                    idx = Variables.Count - 1;
                }
                return new Var(idx);
            }
            throw Error($"Unexpected character '{ch}'");
        }

        private static Node Fold(string op, Node l, Node r)
        {
            if (l is Const a && r is Const b)
            {
                return op switch
                {
                    "+" => new Const(a.Value + b.Value),
                    "-" => new Const(a.Value - b.Value),
                    "*" => new Const(a.Value * b.Value),
                    "/" => new Const(a.Value / b.Value),
                    "^" => new Const(MathF.Pow(a.Value, b.Value)),
                    _ => new Binary(op, l, r),
                };
            }
            return new Binary(op, l, r);
        }

        public void ExpectEnd()
        {
            SkipWs();
            if (_pos < text.Length) throw Error($"Unexpected '{text[_pos..]}'");
        }

        private void Expect(char c)
        {
            SkipWs();
            if (!Peek(c)) throw Error($"Expected '{c}'");
            _pos++;
        }

        private bool Peek(char c) => _pos < text.Length && text[_pos] == c;

        private void SkipWs()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private FormatException Error(string message) => new($"{message} at position {_pos} in \"{text}\".");
    }
}
