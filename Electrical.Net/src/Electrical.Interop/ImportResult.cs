using System.Globalization;
using Electrical.Core.Network;

namespace Electrical.Interop;

/// <summary>Network exchange formats recognised by <see cref="NetworkFormats"/>.</summary>
public enum NetworkFormat
{
    /// <summary>Not recognised.</summary>
    Unknown,
    /// <summary>MATPOWER version-2 case file (<c>.m</c>, <c>mpc.bus = [...]</c>).</summary>
    Matpower,
    /// <summary>Siemens PTI PSS/E RAW power-flow data (<c>.raw</c>, revisions 33–35 supported).</summary>
    PsseRaw,
    /// <summary>IEEE Common Data Format (fixed-column text, "BUS DATA FOLLOWS").</summary>
    IeeeCdf,
    /// <summary>Electrical.Cloud network JSON (<c>{"buses": [...], "branches": [...]}</c>).</summary>
    CloudJson,
}

/// <summary>Result of importing a network file: the model, the detected format and conversion warnings.</summary>
/// <param name="Network">Imported network in MATPOWER per-unit conventions (powers in MW/Mvar, impedances in pu on <see cref="PowerNetwork.BaseMva"/>).</param>
/// <param name="Format">Format the data was read as.</param>
/// <param name="Warnings">Human-readable notes about approximations (e.g. three-winding transformers converted to a star equivalent).</param>
public sealed record ImportResult(PowerNetwork Network, NetworkFormat Format, IReadOnlyList<string> Warnings);

/// <summary>Shared text helpers for the readers (invariant culture, quote-aware tokenizer).</summary>
internal static class InteropText
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Returns the file content when <paramref name="textOrPath"/> is a single-line path of an existing file, else the text itself.</summary>
    public static string ResolveText(string textOrPath, out string? path)
    {
        path = null;
        if (textOrPath.Length < 1024 && textOrPath.IndexOfAny(['\n', '\r']) < 0 && File.Exists(textOrPath))
        {
            path = textOrPath;
            return File.ReadAllText(textOrPath);
        }
        return textOrPath;
    }

    public static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Splits a PSS/E style record: fields separated by commas and/or blanks, strings in single or double quotes,
    /// everything after an unquoted <c>/</c> is a comment. Consecutive commas give empty (default) fields.
    /// </summary>
    public static List<string> Tokenize(string line, out string? comment)
    {
        comment = null;
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, hadQuote = false, wsPushed = false;
        char q = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == q) quoted = false; else cur.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quoted = true; hadQuote = true; q = c; wsPushed = false; continue; }
            if (c == '/') { comment = line[(i + 1)..].Trim(); break; }
            if (c == ',')
            {
                if (cur.Length > 0 || hadQuote) Push();
                else if (!wsPushed) tokens.Add("");
                wsPushed = false;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0 || hadQuote) { Push(); wsPushed = true; }
                continue;
            }
            wsPushed = false;
            cur.Append(c);
        }
        if (cur.Length > 0 || hadQuote) Push();
        return tokens;

        void Push()
        {
            tokens.Add(hadQuote ? cur.ToString().Trim() : cur.ToString());
            cur.Clear();
            hadQuote = false;
        }
    }

    public static bool TryNum(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, Inv, out v);
}
