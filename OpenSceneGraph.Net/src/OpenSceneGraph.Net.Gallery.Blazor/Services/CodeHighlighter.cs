using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace OpenSceneGraph.Net.Gallery.Web.Services;

/// <summary>Tiny C# highlighter for the sample snippets (no external scripts, no runtime CDN).</summary>
public static partial class CodeHighlighter
{
    private static readonly HashSet<string> Keywords =
    [
        "var", "new", "for", "foreach", "in", "if", "else", "return", "while", "do", "switch", "case", "break", "continue",
        "true", "false", "null", "this", "base", "using", "public", "private", "static", "readonly", "const", "class", "struct",
        "record", "void", "int", "float", "double", "bool", "string", "long", "uint", "byte", "is", "not", "and", "or", "with",
        "async", "await", "out", "ref", "params", "default", "typeof", "nameof", "get", "set", "init", "sealed", "override",
    ];

    [GeneratedRegex("""(?<c>//[^\n]*)|(?<s>\$?@?"(?:[^"\\\n]|\\.)*")|(?<ch>'(?:[^'\\]|\\.)')|(?<n>\b\d+(?:\.\d+)?(?:[eE][-+]?\d+)?[fFdDmMuUlL]?\b)|(?<w>[A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex Tokens();

    public static MarkupString Highlight(string code)
    {
        var sb = new StringBuilder(code.Length * 2);
        int last = 0;
        foreach (Match m in Tokens().Matches(code))
        {
            if (m.Index > last) sb.Append(WebUtility.HtmlEncode(code[last..m.Index]));
            string text = WebUtility.HtmlEncode(m.Value);
            string? cls = null;
            if (m.Groups["c"].Success) cls = "tk-c";
            else if (m.Groups["s"].Success || m.Groups["ch"].Success) cls = "tk-s";
            else if (m.Groups["n"].Success) cls = "tk-n";
            else if (m.Groups["w"].Success)
            {
                if (Keywords.Contains(m.Value)) cls = "tk-k";
                else if (char.IsUpper(m.Value[0])) cls = "tk-t";
            }
            if (cls is null) sb.Append(text);
            else sb.Append("<span class=\"").Append(cls).Append("\">").Append(text).Append("</span>");
            last = m.Index + m.Length;
        }
        if (last < code.Length) sb.Append(WebUtility.HtmlEncode(code[last..]));
        return new MarkupString(sb.ToString());
    }
}

/// <summary>Monoline glyphs (24x24 SVG paths) for the sample domains.</summary>
public static class Glyphs
{
    public static string For(SampleCategory c) => c switch
    {
        // reticle
        SampleCategory.Military => "M12 3v4M12 17v4M3 12h4M17 12h4M12 7a5 5 0 1 0 0 10a5 5 0 1 0 0-10z",
        // open book
        SampleCategory.Academic => "M3 5.5c3-1 6-1 9 1c3-2 6-2 9-1V19c-3-1-6-1-9 1c-3-2-6-2-9-1zM12 6.5V20",
        // orbit + nucleus
        SampleCategory.Science => "M12 10.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3zM3 12c0-2.5 4-4.5 9-4.5s9 2 9 4.5s-4 4.5-9 4.5s-9-2-9-4.5z",
        // pulse
        SampleCategory.Medical => "M3 12h4l2-5l4 10l2-5h6",
        // bars
        SampleCategory.Office => "M4 20V11M10 20V5M16 20v-6M21 20H3",
        // controller diamond
        SampleCategory.Entertainment => "M12 3l9 9l-9 9l-9-9zM12 8v8M8 12h8",
        // gauge
        SampleCategory.Simulator => "M4 16a8 8 0 1 1 16 0M12 16l4-5M4 16h2M18 16h2",
        // globe
        SampleCategory.Gis => "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18",
        // node tree
        _ => "M12 4v5M12 9l-6 6M12 9l6 6M10.5 3h3v3h-3zM4.5 15h3v3h-3zM16.5 15h3v3h-3z",
    };

    /// <summary>URL slug for a category filter.</summary>
    public static string Slug(SampleCategory c) => c.ToString().ToLowerInvariant();

    public static SampleCategory? FromSlug(string? slug) =>
        Enum.TryParse<SampleCategory>(slug, ignoreCase: true, out var c) ? c : null;
}
