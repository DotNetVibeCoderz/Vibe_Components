using System.Text.RegularExpressions;

namespace Electrical.Gallery.Web.Components.Shared;

/// <summary>Tiny tokenizer for code samples (C#, XML, JSON). Returns (css class, text) runs; class is null for plain text.</summary>
public static partial class Highlight
{
    private static readonly HashSet<string> Keywords =
    [
        "using", "var", "new", "foreach", "in", "for", "if", "else", "return", "true", "false", "null", "string", "double", "int", "bool",
        "byte", "await", "async", "public", "static", "class", "record", "void", "out", "is", "not", "and", "or", "switch", "with",
    ];

    [GeneratedRegex("""(?<c>//[^\n]*)|(?<s>\$?@?"(?:[^"\\n]|\.)*")|(?<n>\b\d+(?:\.\d+)?(?:e[+-]?\d+)?\b)|(?<w>\b[A-Za-z_][A-Za-z0-9_]*\b)""")]
    private static partial Regex CSharpRx();

    [GeneratedRegex("""(?<c><!--[\s\S]*?-->)|(?<t></?[A-Za-z_][\w:.-]*)|(?<a>\s[\w:.-]+(?==))|(?<s>"[^"]*")|(?<p>/?>|\?>|<\?xml)""")]
    private static partial Regex XmlRx();

    [GeneratedRegex("""(?<k>"(?:[^"\]|\.)*"(?=\s*:))|(?<s>"(?:[^"\]|\.)*")|(?<n>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|(?<w>\btrue\b|\bfalse\b|\bnull\b)""")]
    private static partial Regex JsonRx();

    public static IReadOnlyList<(string? Cls, string Text)> Tokens(string code, string language) => language switch
    {
        "xml" => Run(code, XmlRx(), m => m.Groups["c"].Success ? "tk-com" : m.Groups["t"].Success ? "tk-key" : m.Groups["a"].Success ? "tk-type" : m.Groups["s"].Success ? "tk-str" : "tk-pun"),
        "json" => Run(code, JsonRx(), m => m.Groups["k"].Success ? "tk-type" : m.Groups["s"].Success ? "tk-str" : m.Groups["n"].Success ? "tk-num" : "tk-key"),
        _ => Run(code, CSharpRx(), m =>
        {
            if (m.Groups["c"].Success) return "tk-com";
            if (m.Groups["s"].Success) return "tk-str";
            if (m.Groups["n"].Success) return "tk-num";
            var w = m.Value;
            if (Keywords.Contains(w)) return "tk-key";
            return char.IsUpper(w[0]) ? "tk-type" : null;
        }),
    };

    private static List<(string?, string)> Run(string code, Regex rx, Func<Match, string?> cls)
    {
        var list = new List<(string?, string)>();
        int pos = 0;
        foreach (Match m in rx.Matches(code))
        {
            if (m.Index > pos) list.Add((null, code[pos..m.Index]));
            list.Add((cls(m), m.Value));
            pos = m.Index + m.Length;
        }
        if (pos < code.Length) list.Add((null, code[pos..]));
        return list;
    }
}
