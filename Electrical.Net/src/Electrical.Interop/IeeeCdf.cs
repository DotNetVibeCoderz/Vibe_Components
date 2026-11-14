using Electrical.Core.Network;

namespace Electrical.Interop;

/// <summary>
/// Reader for the IEEE Common Data Format (W. Group on a Common Format for Exchange of Solved Load Flow Data,
/// IEEE Trans. PAS-92(6), 1973) as used by the classic <c>ieee14cdf.txt</c>/<c>ieee30cdf.txt</c>/<c>ieee118cdf.txt</c> archives.
/// <para>
/// Title card: MVA base (columns 32–37) and case identification (46–73). Bus cards: number, name, type (0/1 = PQ, 2 = PV, 3 = swing),
/// final voltage [pu] and angle [deg], load and generation [MW/Mvar], base kV, desired voltage [pu], Q limits [Mvar], shunt G/B [pu].
/// Branch cards: terminals, type (0 = line, 1–4 = transformer), R/X/B [pu], MVA ratings, final turns ratio and phase angle [deg].
/// Fixed columns are used when a card is long enough, otherwise blank-separated fields; loss zone, interchange and tie-line sections are skipped.
/// Shunt G/B are converted to MW/Mvar at 1 pu; regulating-transformer and remote-control data are frozen at their final values.
/// </para>
/// </summary>
public static class IeeeCdf
{
    /// <summary>Reads CDF data from text, or from a file when <paramref name="textOrPath"/> is the path of an existing file.</summary>
    /// <exception cref="FormatException">No "BUS DATA FOLLOWS" section or malformed cards.</exception>
    public static ImportResult Read(string textOrPath)
    {
        var text = InteropText.ResolveText(textOrPath, out var path);
        var warnings = new List<string>();
        var net = Parse(text, warnings);
        if (path is not null && net.Name == "ieee_cdf") net.Name = Path.GetFileNameWithoutExtension(path);
        return new ImportResult(net, NetworkFormat.IeeeCdf, warnings);
    }

    /// <summary>Parses CDF text into a network; notes are appended to <paramref name="warnings"/>.</summary>
    public static PowerNetwork Parse(string text, ICollection<string>? warnings = null)
    {
        warnings ??= [];
        var lines = InteropText.Lines(text);
        int start = Array.FindIndex(lines, l => l.Contains("BUS DATA FOLLOWS", StringComparison.OrdinalIgnoreCase));
        if (start < 0) throw new FormatException("Not an IEEE Common Data Format file (no 'BUS DATA FOLLOWS').");
        var net = new PowerNetwork { Name = "ieee_cdf", BaseMva = 100, FrequencyHz = 60 };
        if (start > 0) ParseTitle(lines[start - 1], net);

        string section = "";
        bool remoteWarned = false, tapWarned = false;
        for (int i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("END OF DATA", StringComparison.OrdinalIgnoreCase)) break;
            if (t.Contains("FOLLOWS", StringComparison.OrdinalIgnoreCase))
            {
                section = t.StartsWith("BUS", StringComparison.OrdinalIgnoreCase) ? "bus"
                        : t.StartsWith("BRANCH", StringComparison.OrdinalIgnoreCase) ? "branch" : "other";
                continue;
            }
            if (t.StartsWith("-9", StringComparison.Ordinal)) { section = ""; continue; }
            if (section == "bus") Bus(line, net, warnings, ref remoteWarned);
            else if (section == "branch") Branch(line, net, warnings, ref tapWarned);
        }
        if (net.SlackBus is null) warnings.Add("No swing bus (type 3) in the case.");
        return net;
    }

    private static void ParseTitle(string title, PowerNetwork net)
    {
        if (title.Length >= 37 && InteropText.TryNum(title.Substring(31, 6).Trim(), out double mva) && mva > 0) net.BaseMva = mva;
        else
        {
            var toks = title.Length > 19 ? title[19..].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            foreach (var tk in toks)
                if (tk.Contains('.') && InteropText.TryNum(tk, out double v) && v > 0) { net.BaseMva = v; break; }
        }
        if (title.Length > 45)
        {
            var id = title[45..Math.Min(title.Length, 73)].Trim();
            if (id.Length > 0) net.Name = id;
        }
    }

    private static string Col(string s, int from1, int to1) =>
        from1 - 1 >= s.Length ? "" : s.Substring(from1 - 1, Math.Min(to1, s.Length) - (from1 - 1)).Trim();

    private static bool Fixed(string line, (int a, int b)[] cols, out double[] v)
    {
        v = new double[cols.Length];
        for (int k = 0; k < cols.Length; k++)
        {
            var s = Col(line, cols[k].a, cols[k].b);
            if (s.Length == 0) { v[k] = 0; continue; }
            if (!InteropText.TryNum(s, out v[k])) return false;
        }
        return true;
    }

    private static readonly (int, int)[] BusCols =
        [(19, 20), (21, 23), (25, 26), (28, 33), (34, 40), (41, 49), (50, 59), (60, 67), (68, 75), (77, 83), (85, 90), (91, 98), (99, 106), (107, 114), (115, 122), (124, 127)];

    private static readonly (int, int)[] BranchCols =
        [(1, 4), (6, 9), (11, 12), (13, 14), (17, 17), (19, 19), (20, 29), (30, 39), (41, 50), (51, 55), (57, 61), (63, 67), (69, 72), (74, 74), (77, 82), (84, 90)];

    private static void Bus(string line, PowerNetwork net, ICollection<string> warnings, ref bool remoteWarned)
    {
        double[] f;
        int id;
        string name;
        if (line.Length >= 122 && int.TryParse(Col(line, 1, 4), out id) && Fixed(line, BusCols, out f))
            name = Col(line, 6, 17);
        else
        {
            var toks = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            int numeric = 0;
            for (int k = toks.Length - 1; k >= 1 && InteropText.TryNum(toks[k], out _); k--) numeric++;
            int nf = numeric >= 16 ? 16 : numeric >= 15 ? 15 : throw new FormatException($"Malformed CDF bus card: '{line.Trim()}'.");
            if (!int.TryParse(toks[0], out id)) throw new FormatException($"Malformed CDF bus number: '{line.Trim()}'.");
            f = new double[16];
            for (int k = 0; k < nf; k++) InteropText.TryNum(toks[toks.Length - nf + k], out f[k]);
            name = string.Join(' ', toks[1..(toks.Length - nf)]);
        }
        int type = (int)f[2];
        var bus = new Bus
        {
            Id = id, Name = name.Length == 0 ? null : name, Area = Math.Max(1, (int)f[0]), Zone = Math.Max(1, (int)f[1]),
            Type = type switch { 3 => BusType.Slack, 2 => BusType.PV, _ => BusType.PQ },
            Vm = f[3] > 0 ? f[3] : 1, VaDeg = f[4], BaseKv = f[9] > 0 ? f[9] : 1,
            Gs = f[13] * net.BaseMva, Bs = f[14] * net.BaseMva,
        };
        net.Buses.Add(bus);
        if (f[5] != 0 || f[6] != 0) net.Loads.Add(new Load { Bus = id, P = f[5], Q = f[6] });
        if (type >= 2 || f[7] != 0 || f[8] != 0)
        {
            double qmax = f[11], qmin = f[12];
            if (type < 2 || (qmax == 0 && qmin == 0) || qmax < qmin) { qmax = 9999; qmin = -9999; }
            net.Generators.Add(new Generator
            {
                Bus = id, P = f[7], Q = f[8], Vset = f[10] > 0 ? f[10] : bus.Vm, Qmax = qmax, Qmin = qmin, MBase = net.BaseMva, Pmin = 0, Pmax = 9999,
            });
            int remote = (int)f[15];
            if (type == 2 && remote != 0 && remote != id && !remoteWarned)
            {
                warnings.Add("Remote voltage control is not modelled; PV buses regulate their own voltage.");
                remoteWarned = true;
            }
        }
    }

    private static void Branch(string line, PowerNetwork net, ICollection<string> warnings, ref bool tapWarned)
    {
        double[] f;
        if (!(line.Length >= 90 && Fixed(line, BranchCols, out f)))
        {
            var toks = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length < 9) throw new FormatException($"Malformed CDF branch card: '{line.Trim()}'.");
            f = new double[16];
            for (int k = 0; k < Math.Min(16, toks.Length); k++) InteropText.TryNum(toks[k], out f[k]);
        }
        int type = (int)f[5];
        var br = new Branch
        {
            From = (int)f[0], To = (int)f[1], R = f[6], X = f[7], B = f[8], RateA = f[9],
            Kind = type == 0 ? BranchKind.Line : BranchKind.Transformer, Tap = type == 0 ? 0 : f[14], ShiftDeg = type == 0 ? 0 : f[15],
        };
        if (type >= 2 && !tapWarned)
        {
            warnings.Add("Regulating transformers (types 2–4) are fixed at their final turns ratio / phase angle.");
            tapWarned = true;
        }
        net.Branches.Add(br);
    }
}
