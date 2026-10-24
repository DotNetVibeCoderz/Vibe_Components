using System.Text.RegularExpressions;

namespace GnuRadio.Gallery;

/// <summary>Token kinds for the sample-code view.</summary>
public enum CodeToken
{
    Plain,
    Keyword,
    Type,
    String,
    Number,
    Comment,
}

/// <summary>A tiny C# tokenizer for syntax colouring in both galleries (no dependency on Roslyn).</summary>
public static partial class CodeHighlighter
{
    private static readonly HashSet<string> Keywords =
    [
        "using", "var", "new", "const", "foreach", "for", "in", "if", "else", "return", "true", "false", "null", "public", "private",
        "static", "class", "record", "void", "await", "async", "is", "out", "ref", "params", "default", "while", "this",
    ];

    private static readonly HashSet<string> Types =
    [
        "double", "float", "int", "long", "byte", "short", "bool", "string", "object", "Console", "Math", "File", "TimeSpan",
        "Dictionary", "Encoding",
    ];

    [GeneratedRegex(@"(?<comment>//[^\n]*)|(?<string>""(?:[^""\\]|\\.)*""|\$""(?:[^""\\]|\\.)*"")|(?<number>\b\d[\d_]*(?:\.\d+)?(?:e[+-]?\d+)?[fdm]?\b)|(?<word>[A-Za-z_][A-Za-z0-9_]*)|(?<other>\s+|.)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <summary>Splits <paramref name="code"/> into coloured runs.</summary>
    public static IEnumerable<(CodeToken Kind, string Text)> Tokenize(string code)
    {
        foreach (Match m in TokenRegex().Matches(code))
        {
            if (m.Groups["comment"].Success) yield return (CodeToken.Comment, m.Value);
            else if (m.Groups["string"].Success) yield return (CodeToken.String, m.Value);
            else if (m.Groups["number"].Success) yield return (CodeToken.Number, m.Value);
            else if (m.Groups["word"].Success)
            {
                var w = m.Value;
                if (Keywords.Contains(w)) yield return (CodeToken.Keyword, w);
                else if (Types.Contains(w) || (char.IsUpper(w[0]) && IsTypeLike(w))) yield return (CodeToken.Type, w);
                else yield return (CodeToken.Plain, w);
            }
            else yield return (CodeToken.Plain, m.Value);
        }
    }

    // PascalCase identifiers that read as types in sample code (Flowgraph, SignalSource…), not constants.
    private static bool IsTypeLike(string w) => w.Length > 1 && w.Any(char.IsLower);
}
