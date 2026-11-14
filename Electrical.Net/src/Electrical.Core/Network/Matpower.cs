using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Electrical.Core.Network;

/// <summary>Reads and writes MATPOWER case files (version 2 <c>mpc.*</c> format: bus, gen, branch, gencost).</summary>
public static partial class Matpower
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static PowerNetwork Load(string path) => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static PowerNetwork Parse(string text, string name = "case")
    {
        var net = new PowerNetwork { Name = name };
        var fn = FunctionName().Match(text);
        if (fn.Success) net.Name = fn.Groups[1].Value;
        var bm = BaseMva().Match(text);
        if (bm.Success) net.BaseMva = double.Parse(bm.Groups[1].Value, Inv);

        foreach (var row in Matrix(text, "bus"))
        {
            net.Buses.Add(new Bus
            {
                Id = (int)row[0], Type = (BusType)(int)row[1], Gs = row[4], Bs = row[5], Area = (int)row[6],
                Vm = row[7], VaDeg = row[8], BaseKv = row[9] == 0 ? 1 : row[9], Zone = (int)row[10], Vmax = row[11], Vmin = row[12],
            });
            if (row[2] != 0 || row[3] != 0) net.Loads.Add(new Load { Bus = (int)row[0], P = row[2], Q = row[3] });
        }
        foreach (var row in Matrix(text, "gen"))
        {
            net.Generators.Add(new Generator
            {
                Bus = (int)row[0], P = row[1], Q = row[2], Qmax = row[3], Qmin = row[4], Vset = row[5], MBase = row[6] == 0 ? net.BaseMva : row[6],
                InService = row[7] > 0, Pmax = row[8], Pmin = row[9],
            });
        }
        foreach (var row in Matrix(text, "branch"))
        {
            var br = new Branch
            {
                From = (int)row[0], To = (int)row[1], R = row[2], X = row[3], B = row[4], RateA = row[5],
                Tap = row[8], ShiftDeg = row[9], InService = row[10] > 0,
            };
            if (br.IsTransformer) br.Kind = BranchKind.Transformer;
            net.Branches.Add(br);
        }
        var costs = Matrix(text, "gencost");
        for (int i = 0; i < Math.Min(costs.Count, net.Generators.Count); i++)
        {
            var c = costs[i];
            if ((int)c[0] != 2) continue; // only polynomial costs
            int nc = (int)c[3];
            double c2 = 0, c1 = 0, c0 = 0;
            if (nc >= 3) { c2 = c[4]; c1 = c[5]; c0 = c[6]; }
            else if (nc == 2) { c1 = c[4]; c0 = c[5]; }
            else if (nc == 1) { c0 = c[4]; }
            net.Generators[i].Cost = new PolynomialCost(c2, c1, c0);
        }
        return net;
    }

    private static List<double[]> Matrix(string text, string field)
    {
        var m = Regex.Match(text, @"mpc\." + field + @"\s*=\s*\[(.*?)\]\s*;", RegexOptions.Singleline);
        var rows = new List<double[]>();
        if (!m.Success) return rows;
        foreach (var rawLine in m.Groups[1].Value.Split('\n'))
        {
            var line = rawLine;
            int pct = line.IndexOf('%');
            if (pct >= 0) line = line[..pct];
            foreach (var part in line.Split(';'))
            {
                var toks = part.Split([' ', '\t', ',', '\r'], StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length == 0) continue;
                var row = new double[Math.Max(toks.Length, 13)];
                for (int i = 0; i < toks.Length; i++)
                    row[i] = toks[i] switch { "Inf" or "inf" => double.PositiveInfinity, "-Inf" or "-inf" => double.NegativeInfinity, var t => double.Parse(t, Inv) };
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>Serializes a network to MATPOWER format (loads are aggregated per bus).</summary>
    public static string Write(PowerNetwork net)
    {
        var sb = new StringBuilder();
        string F(double v) => double.IsPositiveInfinity(v) ? "Inf" : double.IsNegativeInfinity(v) ? "-Inf" : v.ToString("0.######", Inv);
        sb.AppendLine($"function mpc = {SafeName(net.Name)}");
        sb.AppendLine($"% Exported by {ElectricalInfo.Name} {ElectricalInfo.Version} — {ElectricalInfo.CreditEn}");
        sb.AppendLine("mpc.version = '2';");
        sb.AppendLine($"mpc.baseMVA = {F(net.BaseMva)};");
        sb.AppendLine("%% bus_i type Pd Qd Gs Bs area Vm Va baseKV zone Vmax Vmin");
        sb.AppendLine("mpc.bus = [");
        foreach (var b in net.Buses)
        {
            double pd = net.Loads.Where(l => l.Bus == b.Id && l.InService).Sum(l => l.P);
            double qd = net.Loads.Where(l => l.Bus == b.Id && l.InService).Sum(l => l.Q);
            sb.AppendLine($"\t{b.Id}\t{(int)b.Type}\t{F(pd)}\t{F(qd)}\t{F(b.Gs)}\t{F(b.Bs)}\t{b.Area}\t{F(b.Vm)}\t{F(b.VaDeg)}\t{F(b.BaseKv)}\t{b.Zone}\t{F(b.Vmax)}\t{F(b.Vmin)};");
        }
        sb.AppendLine("];");
        sb.AppendLine("%% bus Pg Qg Qmax Qmin Vg mBase status Pmax Pmin");
        sb.AppendLine("mpc.gen = [");
        foreach (var g in net.Generators)
            sb.AppendLine($"\t{g.Bus}\t{F(g.P)}\t{F(g.Q)}\t{F(g.Qmax)}\t{F(g.Qmin)}\t{F(g.Vset)}\t{F(g.MBase)}\t{(g.InService ? 1 : 0)}\t{F(g.Pmax)}\t{F(g.Pmin)};");
        sb.AppendLine("];");
        sb.AppendLine("%% fbus tbus r x b rateA rateB rateC ratio angle status angmin angmax");
        sb.AppendLine("mpc.branch = [");
        foreach (var r in net.Branches)
            sb.AppendLine($"\t{r.From}\t{r.To}\t{F(r.R)}\t{F(r.X)}\t{F(r.B)}\t{F(r.RateA)}\t{F(r.RateA)}\t{F(r.RateA)}\t{F(r.Tap)}\t{F(r.ShiftDeg)}\t{(r.InService ? 1 : 0)}\t-360\t360;");
        sb.AppendLine("];");
        sb.AppendLine("%% 2 startup shutdown n c2 c1 c0");
        sb.AppendLine("mpc.gencost = [");
        foreach (var g in net.Generators)
            sb.AppendLine($"\t2\t0\t0\t3\t{F(g.Cost.C2)}\t{F(g.Cost.C1)}\t{F(g.Cost.C0)};");
        sb.AppendLine("];");
        return sb.ToString();
    }

    private static string SafeName(string s)
    {
        var t = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return t.Length == 0 || !char.IsLetter(t[0]) ? "case_" + t : t;
    }

    [GeneratedRegex(@"function\s+mpc\s*=\s*(\w+)")]
    private static partial Regex FunctionName();

    [GeneratedRegex(@"mpc\.baseMVA\s*=\s*([0-9.eE+-]+)")]
    private static partial Regex BaseMva();
}
