using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GDAL.Net.Gallery.Blazor.Services;

/// <summary>Tiny C# syntax highlighter for the code tab (keywords, types, strings, numbers, comments, calls).</summary>
public static partial class CodeHighlighter
{
    private static readonly HashSet<string> Keywords =
    [
        "using", "var", "new", "foreach", "in", "for", "if", "else", "return", "await", "async", "string", "int", "double", "float",
        "long", "bool", "byte", "true", "false", "null", "public", "private", "static", "class", "record", "void", "out", "ref",
        "is", "not", "and", "or", "switch", "case", "default", "break", "throw", "try", "catch", "finally", "while", "do", "const",
        "readonly", "params", "this", "nameof", "typeof", "with", "when", "ulong", "object",
    ];

    [GeneratedRegex("""(?<comment>//[^\n]*)|(?<string>\$?@?"(?:[^"\\n]|\.)*")|(?<number>\b\d[\d_]*(?:\.\d+)?(?:e[+-]?\d+)?[fFdDmMlL]?\b)|(?<word>[A-Za-z_][A-Za-z0-9_]*)|(?<other>[\s\S])""")]
    private static partial Regex Tokens();

    public static string ToHtml(string code)
    {
        var sb = new StringBuilder(code.Length * 2);
        var matches = Tokens().Matches(code);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var text = WebUtility.HtmlEncode(m.Value);
            if (m.Groups["comment"].Success) Span(sb, "c", text);
            else if (m.Groups["string"].Success) Span(sb, "s", text);
            else if (m.Groups["number"].Success) Span(sb, "n", text);
            else if (m.Groups["word"].Success)
            {
                var next = NextNonSpace(code, m.Index + m.Length);
                if (Keywords.Contains(m.Value)) Span(sb, "k", text);
                else if (next == '(') Span(sb, "f", text);
                else if (char.IsUpper(m.Value[0])) Span(sb, "t", text);
                else sb.Append(text);
            }
            else sb.Append(text);
        }
        return sb.ToString();
    }

    private static char NextNonSpace(string s, int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
        return i < s.Length ? s[i] : '\0';
    }

    private static void Span(StringBuilder sb, string cls, string text) => sb.Append("<span class=\"tok-").Append(cls).Append("\">").Append(text).Append("</span>");
}
