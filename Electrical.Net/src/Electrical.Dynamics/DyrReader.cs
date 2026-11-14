using System.Globalization;
using System.Text;
using Electrical.Core.Network;

namespace Electrical.Dynamics;

/// <summary>
/// Reader for PSS/E dynamic data (.dyr): records <c>bus 'MODEL' id p1 p2 … /</c>, possibly spread over several lines and terminated by
/// <c>/</c> (text after the slash is a comment). Lines starting with <c>//</c>, <c>!</c>, <c>#</c> or <c>@!</c> are comments.
/// Models are matched to generators by bus and machine id: a numeric id k selects the k-th generator at the bus (1-based), otherwise
/// a generator whose <c>Name</c> equals the id, otherwise the generators at the bus in order of appearance. Unknown models are skipped
/// with a warning.
/// </summary>
public static class DyrReader
{
    /// <summary>Parses <paramref name="text"/> against <paramref name="network"/>.</summary>
    public static DynamicModelSet Parse(string text, PowerNetwork network)
    {
        var set = new DynamicModelSet();
        var byBus = new Dictionary<int, List<int>>();
        for (int g = 0; g < network.Generators.Count; g++)
        {
            int b = network.Generators[g].Bus;
            if (!byBus.TryGetValue(b, out var l)) byBus[b] = l = [];
            l.Add(g);
        }
        var used = new Dictionary<(int, string), int>();
        foreach (var (line, rec) in Records(text))
        {
            var tok = Tokenize(rec);
            if (tok.Count < 2) continue;
            if (!int.TryParse(tok[0].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bus))
            {
                set.Warnings.Add($"line {line}: record does not start with a bus number, skipped");
                continue;
            }
            string model = tok[1].Text.ToUpperInvariant();
            string id = tok.Count > 2 ? tok[2].Text.Trim() : "1";
            var values = new List<double>();
            for (int k = 3; k < tok.Count; k++)
                values.Add(tok[k].Quoted ? double.NaN : double.TryParse(tok[k].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN);
            if (!DynamicModelLibrary.TryCreate(model, [.. values], out var comp, out var err))
            {
                set.Warnings.Add($"line {line}: {err} (bus {bus} id {id}), skipped");
                continue;
            }
            int gi = MatchGenerator(network, byBus, used, bus, id, comp!.Category);
            if (gi < 0)
            {
                set.Warnings.Add($"line {line}: {model} at bus {bus} id {id} has no matching generator, skipped");
                continue;
            }
            set.Add(gi, comp);
        }
        return set;
    }

    /// <summary>Reads a .dyr file.</summary>
    public static DynamicModelSet Load(string path, PowerNetwork network) => Parse(File.ReadAllText(path), network);

    private static int MatchGenerator(PowerNetwork net, Dictionary<int, List<int>> byBus, Dictionary<(int, string), int> used, int bus, string id,
        ModelCategory cat)
    {
        if (!byBus.TryGetValue(bus, out var gens) || gens.Count == 0) return -1;
        if (used.TryGetValue((bus, id), out int known)) return known;
        int gi = -1;
        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) && k >= 1 && k <= gens.Count) gi = gens[k - 1];
        if (gi < 0) gi = gens.FirstOrDefault(g => string.Equals(net.Generators[g].Name, id, StringComparison.OrdinalIgnoreCase), -1);
        if (gi < 0) gi = gens.FirstOrDefault(g => !used.ContainsValue(g), gens[0]);
        used[(bus, id)] = gi;
        return gi;
    }

    private static IEnumerable<(int Line, string Record)> Records(string text)
    {
        var sb = new StringBuilder();
        int start = 0, ln = 0;
        foreach (var raw in text.Split('\n'))
        {
            ln++;
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith('!') || trimmed.StartsWith('#') || trimmed.StartsWith("@!")) continue;
            int slash = IndexOfTerminator(line);
            if (sb.Length == 0) start = ln;
            if (slash < 0) { sb.Append(' ').Append(line); continue; }
            sb.Append(' ').Append(line[..slash]);
            var rec = sb.ToString().Trim();
            sb.Clear();
            if (rec.Length > 0) yield return (start, rec);
        }
        var rest = sb.ToString().Trim();
        if (rest.Length > 0) yield return (start, rest);
    }

    private static int IndexOfTerminator(string line)
    {
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\'' || line[i] == '"') q = !q;
            else if (line[i] == '/' && !q) return i;
        }
        return -1;
    }

    private readonly record struct Token(string Text, bool Quoted);

    private static List<Token> Tokenize(string rec)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < rec.Length)
        {
            char c = rec[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (c == '\'' || c == '"')
            {
                int j = rec.IndexOf(c, i + 1);
                if (j < 0) j = rec.Length;
                list.Add(new Token(rec[(i + 1)..j], true));
                i = j + 1;
                continue;
            }
            int s = i;
            while (i < rec.Length && !char.IsWhiteSpace(rec[i]) && rec[i] != ',') i++;
            list.Add(new Token(rec[s..i], false));
        }
        return list;
    }
}
