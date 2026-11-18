using System.Globalization;
using System.Text;

namespace GeoPandas.Net.BIM;

/// <summary>An entity reference <c>#id</c>.</summary>
public readonly record struct StepRef(long Id)
{
    public override string ToString() => "#" + Id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An enumeration value <c>.NAME.</c> (booleans are <c>.T.</c> / <c>.F.</c>).</summary>
public readonly record struct StepEnum(string Value)
{
    public override string ToString() => "." + Value + ".";
}

/// <summary>A typed parameter such as <c>IFCLABEL('x')</c>.</summary>
public sealed record StepTyped(string Type, object? Value);

/// <summary>An instance <c>#id = TYPE(args)</c>. Arguments are null ($), "*" (derived), string, double, long, StepRef, StepEnum, StepTyped or List&lt;object?&gt;.</summary>
public sealed class StepEntity(long id, string type, IReadOnlyList<object?> args)
{
    public long Id { get; } = id;
    /// <summary>Upper-case entity type, e.g. IFCWALL.</summary>
    public string Type { get; } = type;
    public IReadOnlyList<object?> Args { get; } = args;
    public object? this[int i] => i < Args.Count ? Args[i] : null;
    public override string ToString() => $"#{Id}={Type}({Args.Count} args)";
}

/// <summary>
/// An ISO 10303-21 (STEP physical file, "SPF") reader and writer, the container format of IFC: header entities and the DATA section,
/// with strings (<c>''</c> quotes, <c>\X\</c>, <c>\X2\…\X0\</c>, <c>\X4\…\X0\</c>, <c>\S\</c> escapes), numbers, enumerations,
/// references, typed values and nested lists.
/// </summary>
public sealed class StepFile
{
    public List<StepEntity> Header { get; } = [];
    public Dictionary<long, StepEntity> Entities { get; } = [];

    /// <summary>Schema identifiers from FILE_SCHEMA, e.g. "IFC4".</summary>
    public IReadOnlyList<string> Schemas => Header.FirstOrDefault(h => h.Type == "FILE_SCHEMA")?[0] is List<object?> l ? l.OfType<string>().ToList() : [];

    public static StepFile Read(string path) => Parse(File.ReadAllText(path, Encoding.Latin1));

    public static StepFile Parse(string text)
    {
        var f = new StepFile();
        var p = new Parser(text);
        p.SkipTo("HEADER;");
        while (true)
        {
            p.SkipWs();
            if (p.TryKeyword("ENDSEC;")) break;
            string type = p.Keyword();
            var args = p.ArgList();
            p.Expect(';');
            f.Header.Add(new StepEntity(0, type, args));
        }
        p.SkipTo("DATA;");
        while (true)
        {
            p.SkipWs();
            if (p.TryKeyword("ENDSEC;") || p.AtEnd) break;
            p.Expect('#');
            long id = p.Integer();
            p.SkipWs(); p.Expect('='); p.SkipWs();
            string type;
            List<object?> args;
            if (p.Peek == '(')
            {
                // complex instance: (A(...)B(...)) — keep the parts as typed values
                p.Expect('(');
                var parts = new List<object?>();
                p.SkipWs();
                while (p.Peek != ')') { var t = p.Keyword(); parts.Add(new StepTyped(t, p.ArgList())); p.SkipWs(); }
                p.Expect(')');
                type = "COMPLEX"; args = parts;
            }
            else { type = p.Keyword(); args = p.ArgList(); }
            p.SkipWs(); p.Expect(';');
            f.Entities[id] = new StepEntity(id, type, args);
        }
        return f;
    }

    private sealed class Parser(string s)
    {
        private int _i;
        public bool AtEnd => _i >= s.Length;
        public char Peek => _i < s.Length ? s[_i] : '\0';

        public void SkipWs()
        {
            while (_i < s.Length)
            {
                if (char.IsWhiteSpace(s[_i])) _i++;
                else if (s[_i] == '/' && _i + 1 < s.Length && s[_i + 1] == '*')
                {
                    int e = s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = e < 0 ? s.Length : e + 2;
                }
                else break;
            }
        }

        public void SkipTo(string kw)
        {
            int k = s.IndexOf(kw, _i, StringComparison.OrdinalIgnoreCase);
            if (k < 0) throw new InvalidDataException($"STEP file has no {kw}");
            _i = k + kw.Length;
        }

        public bool TryKeyword(string kw)
        {
            if (string.Compare(s, _i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) == 0) { _i += kw.Length; return true; }
            return false;
        }

        public void Expect(char c)
        {
            SkipWs();
            if (Peek != c) throw new InvalidDataException($"STEP: expected '{c}' at offset {_i}, found '{Peek}'.");
            _i++;
        }

        public string Keyword()
        {
            SkipWs();
            int st = _i;
            while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_')) _i++;
            if (_i == st) throw new InvalidDataException($"STEP: keyword expected at offset {_i}.");
            return s[st.._i].ToUpperInvariant();
        }

        public long Integer()
        {
            int st = _i;
            while (_i < s.Length && char.IsDigit(s[_i])) _i++;
            return long.Parse(s.AsSpan(st, _i - st), CultureInfo.InvariantCulture);
        }

        public List<object?> ArgList()
        {
            Expect('(');
            var list = new List<object?>();
            SkipWs();
            if (Peek == ')') { _i++; return list; }
            while (true)
            {
                list.Add(Value());
                SkipWs();
                if (Peek == ',') { _i++; continue; }
                Expect(')');
                return list;
            }
        }

        private object? Value()
        {
            SkipWs();
            char c = Peek;
            switch (c)
            {
                case '$': _i++; return null;
                case '*': _i++; return "*";
                case '#': _i++; return new StepRef(Integer());
                case '\'': return String();
                case '.':
                {
                    int e = s.IndexOf('.', _i + 1);
                    var v = s[(_i + 1)..e];
                    _i = e + 1;
                    return new StepEnum(v.ToUpperInvariant());
                }
                case '(': return ArgList();
                case '"':
                {
                    int e = s.IndexOf('"', _i + 1);
                    var v = s[(_i + 1)..e];
                    _i = e + 1;
                    return v;   // binary as hex text
                }
            }
            if (c == '-' || c == '+' || char.IsDigit(c))
            {
                int st = _i; _i++;
                bool real = false;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] is '.' or 'E' or 'e' or '+' or '-'))
                {
                    if (s[_i] is '.' or 'E' or 'e') real = true;
                    if ((s[_i] is '+' or '-') && s[_i - 1] is not ('E' or 'e')) break;
                    _i++;
                }
                var span = s.AsSpan(st, _i - st);
                return real ? double.Parse(span, NumberStyles.Float, CultureInfo.InvariantCulture) : long.Parse(span, CultureInfo.InvariantCulture);
            }
            if (char.IsLetter(c))
            {
                var t = Keyword();
                var inner = ArgList();
                return new StepTyped(t, inner.Count == 1 ? inner[0] : inner);
            }
            throw new InvalidDataException($"STEP: unexpected '{c}' at offset {_i}.");
        }

        private string String()
        {
            _i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= s.Length) throw new InvalidDataException("STEP: unterminated string.");
                char c = s[_i];
                if (c == '\'')
                {
                    if (_i + 1 < s.Length && s[_i + 1] == '\'') { sb.Append('\''); _i += 2; continue; }
                    _i++;
                    return sb.ToString();
                }
                if (c == '\\' && _i + 1 < s.Length)
                {
                    if (Match("\\X2\\")) { _i += 4; Hex(sb, 4); continue; }
                    if (Match("\\X4\\")) { _i += 4; Hex(sb, 8); continue; }
                    if (Match("\\X\\")) { _i += 3; sb.Append((char)Convert.ToInt32(s.Substring(_i, 2), 16)); _i += 2; continue; }
                    if (Match("\\S\\")) { _i += 3; sb.Append((char)(s[_i] + 128)); _i++; continue; }
                    if (Match("\\\\")) { sb.Append('\\'); _i += 2; continue; }
                    if (s[_i + 1] == 'P' && _i + 3 < s.Length && s[_i + 3] == '\\') { _i += 4; continue; }   // code page switch
                }
                sb.Append(c);
                _i++;
            }
        }

        private bool Match(string m) => string.CompareOrdinal(s, _i, m, 0, m.Length) == 0;

        private void Hex(StringBuilder sb, int digits)
        {
            while (!Match("\\X0\\"))
            {
                int cp = Convert.ToInt32(s.Substring(_i, digits), 16);
                sb.Append(char.ConvertFromUtf32(cp));
                _i += digits;
            }
            _i += 4;
        }
    }

    // ------------------------------------------------------------------ writer

    /// <summary>Encodes a string as a STEP literal (non-ASCII as \X2\ hex).</summary>
    public static string Quote(string? v)
    {
        if (v is null) return "$";
        var sb = new StringBuilder("'");
        foreach (var r in v.EnumerateRunes())
        {
            if (r.Value == '\'') sb.Append("''");
            else if (r.Value == '\\') sb.Append("\\\\");
            else if (r.Value is >= 32 and < 127) sb.Append((char)r.Value);
            else if (r.Value <= 0xFFFF) sb.Append("\\X2\\").Append(r.Value.ToString("X4", CultureInfo.InvariantCulture)).Append("\\X0\\");
            else sb.Append("\\X4\\").Append(r.Value.ToString("X8", CultureInfo.InvariantCulture)).Append("\\X0\\");
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>Formats a parameter value for writing.</summary>
    public static string Format(object? v) => v switch
    {
        null => "$",
        string s when s == "*" => "*",
        string s => Quote(s),
        double d => FormatReal(d),
        float f => FormatReal(f),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        bool b => b ? ".T." : ".F.",
        StepRef r => r.ToString(),
        StepEnum e => e.ToString(),
        StepTyped t => $"{t.Type}({Format(t.Value)})",
        System.Collections.IEnumerable e => "(" + string.Join(",", e.Cast<object?>().Select(Format)) + ")",
        _ => throw new ArgumentException($"Cannot write {v.GetType().Name} to STEP.")
    };

    private static string FormatReal(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E')) s += ".";
        return s;
    }
}
