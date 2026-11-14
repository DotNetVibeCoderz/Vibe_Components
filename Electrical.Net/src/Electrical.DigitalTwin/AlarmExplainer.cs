using System.Globalization;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.DigitalTwin;

/// <summary>Context used by <see cref="AlarmExplainer"/> to attribute causes.</summary>
/// <param name="OpenBranches">Indices of branches that are out of service compared with the normal topology (outages).</param>
/// <param name="ForecastDeviationPercent">Estimated system load vs forecast [%] (null when unknown).</param>
/// <param name="LoadScale">Load level relative to the model (1 = nominal).</param>
public sealed record AlarmContext(IReadOnlyCollection<int>? OpenBranches = null, double? ForecastDeviationPercent = null, double LoadScale = 1);

/// <summary>
/// Turns limit violations of an operating point into explained alarms. Causes are attributed by walking the supply path from the reference
/// bus (breadth-first tree over in-service branches) to the violating bus: an overloaded branch on that path, a recent outage next to the
/// path, load above forecast, a heavily loaded path branch, or (fallback) the accumulated voltage drop of a long path.
/// </summary>
public static class AlarmExplainer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<ExplainedAlarm> Explain(PowerFlowResult pf, GridTwinOptions? options = null, AlarmContext? context = null) =>
        pf.Converged
            ? Explain(pf.Network, pf.Buses, pf.Branches, options, context)
            : [new ExplainedAlarm(TwinAlarmKind.EstimatorFailure, "system", 0, 0, TwinAlarmSeverity.Critical,
                "Power flow did not converge: the operating point is beyond the voltage-stability limit or the network is split.",
                "Aliran daya tidak konvergen: titik operasi melewati batas stabilitas tegangan atau jaringan terpisah.", ["non-convergence"])];

    /// <summary>Explains voltage and loading violations of bus/branch results computed on <paramref name="net"/>.</summary>
    public static IReadOnlyList<ExplainedAlarm> Explain(PowerNetwork net, IReadOnlyList<BusResult> buses, IReadOnlyList<BranchResult> branches,
        GridTwinOptions? options = null, AlarmContext? context = null)
    {
        var o = options ?? GridTwinOptions.Default;
        var ctx = context ?? new AlarmContext();
        var open = ctx.OpenBranches ?? [];
        var alarms = new List<ExplainedAlarm>();
        var parent = SupplyTree(net);
        var loading = branches.ToDictionary(b => b.Index, b => b.LoadingPercent);
        double L(int k) => loading.TryGetValue(k, out var v) ? v : 0;
        bool aboveForecast = ctx.ForecastDeviationPercent is { } d && d > o.ForecastDeviationPercent;

        // Branch overloads / heavy loading
        foreach (var r in branches.Where(b => b.RateA > 0 && b.LoadingPercent > o.HighLoadingPercent).OrderByDescending(b => b.LoadingPercent))
        {
            var br = net.Branches[r.Index];
            bool overload = r.LoadingPercent > o.OverloadPercent;
            double s = Math.Max(r.SFrom, r.STo);
            var causes = new List<string>();
            string en, id;
            var outage = open.Where(k => k != r.Index).Select(k => net.Branches[k]).FirstOrDefault();
            if (outage is not null)
            {
                causes.Add($"outage:{outage.From}-{outage.To}");
                en = $"flow redistributed after the outage of {BranchRatings.Label(outage)}";
                id = $"aliran daya teralihkan setelah {BranchRatings.Label(outage, "id")} padam";
            }
            else if (aboveForecast)
            {
                causes.Add("load-above-forecast");
                en = $"system load is {F(ctx.ForecastDeviationPercent!.Value, 0)}% above forecast";
                id = $"beban sistem {F(ctx.ForecastDeviationPercent!.Value, 0)}% di atas prakiraan";
            }
            else
            {
                causes.Add("high-demand");
                en = $"demand is at {F(100 * ctx.LoadScale, 0)}% of the nominal load";
                id = $"permintaan berada pada {F(100 * ctx.LoadScale, 0)}% dari beban nominal";
            }
            string head = overload ? "overloaded" : "heavily loaded";
            string headId = overload ? "berbeban lebih" : "berbeban tinggi";
            string lab = BranchRatings.Label(br), labId = BranchRatings.Label(br, "id");
            alarms.Add(new ExplainedAlarm(overload ? TwinAlarmKind.Overload : TwinAlarmKind.HighLoading, $"branch {br.From}-{br.To}",
                r.LoadingPercent, overload ? o.OverloadPercent : o.HighLoadingPercent,
                overload ? (r.LoadingPercent > 1.2 * o.OverloadPercent ? TwinAlarmSeverity.Critical : TwinAlarmSeverity.High) : TwinAlarmSeverity.Warning,
                $"{Cap(lab)} {head}: {F(r.LoadingPercent, 0)}% ({F(s, 2)} MVA of {F(r.RateA, 2)} MVA) because {en}",
                $"{Cap(labId)} {headId}: {F(r.LoadingPercent, 0)}% ({F(s, 2)} MVA dari {F(r.RateA, 2)} MVA) karena {id}", causes));
        }

        // Voltage violations
        foreach (var b in buses.OrderBy(b => b.Vm))
        {
            bool low = b.Vm < o.VoltageLowPu - 1e-9, high = b.Vm > o.VoltageHighPu + 1e-9;
            if (!low && !high) continue;
            int bi = net.IndexOf(b.BusId);
            var path = PathToSource(net, parent, bi);
            var causes = new List<string>();
            string en, id;
            if (low)
            {
                int worst = path.Count == 0 ? -1 : path.MaxBy(L);
                var pathBuses = path.SelectMany(k => new[] { net.Branches[k].From, net.Branches[k].To }).Append(b.BusId).ToHashSet();
                var outage = open.Select(k => net.Branches[k]).FirstOrDefault(br => pathBuses.Contains(br.From) || pathBuses.Contains(br.To))
                             ?? open.Select(k => net.Branches[k]).FirstOrDefault();
                if (worst >= 0 && L(worst) > o.OverloadPercent)
                {
                    var br = net.Branches[worst];
                    causes.Add($"overload:{br.From}-{br.To}");
                    en = $"{BranchRatings.Label(br)} carries {F(L(worst), 0)}% load";
                    id = $"{BranchRatings.Label(br, "id")} berbeban {F(L(worst), 0)}%";
                }
                else if (outage is not null)
                {
                    causes.Add($"outage:{outage.From}-{outage.To}");
                    en = $"{BranchRatings.Label(outage)} is out of service and supply takes a longer path";
                    id = $"{BranchRatings.Label(outage, "id")} tidak beroperasi sehingga suplai melalui jalur lebih panjang";
                }
                else if (aboveForecast)
                {
                    causes.Add("load-above-forecast");
                    en = $"system load is {F(ctx.ForecastDeviationPercent!.Value, 0)}% above forecast";
                    id = $"beban sistem {F(ctx.ForecastDeviationPercent!.Value, 0)}% di atas prakiraan";
                }
                else if (worst >= 0 && L(worst) > o.HighLoadingPercent * 0.8)
                {
                    var br = net.Branches[worst];
                    causes.Add($"heavy-loading:{br.From}-{br.To}");
                    en = $"{BranchRatings.Label(br)} is heavily loaded ({F(L(worst), 0)}%)";
                    id = $"{BranchRatings.Label(br, "id")} berbeban tinggi ({F(L(worst), 0)}%)";
                }
                else
                {
                    double vSrc = buses[net.IndexOf(net.Buses[SourceIndex(net)].Id)].Vm;
                    causes.Add("voltage-drop");
                    en = $"the cumulative voltage drop over {path.Count} branch(es) from the source is {F(vSrc - b.Vm, 3)} pu (insufficient reactive support)";
                    id = $"jatuh tegangan kumulatif pada {path.Count} cabang dari sumber sebesar {F(vSrc - b.Vm, 3)} pu (dukungan daya reaktif kurang)";
                }
                alarms.Add(new ExplainedAlarm(TwinAlarmKind.Undervoltage, $"bus {b.BusId}", b.Vm, o.VoltageLowPu,
                    b.Vm < o.VoltageLowPu - 0.05 ? TwinAlarmSeverity.Critical : TwinAlarmSeverity.High,
                    $"Bus {b.BusId} voltage {F(b.Vm, 3)} pu below {F(o.VoltageLowPu, 2)} because {en}",
                    $"Tegangan bus {b.BusId} {F(b.Vm, 3)} pu di bawah {F(o.VoltageLowPu, 2)} karena {id}", causes));
            }
            else
            {
                var reverse = path.Select(k => branches.FirstOrDefault(r => r.Index == k)).Where(r => r is not null)
                    .FirstOrDefault(r => FlowsTowardSource(net, parent, r!));
                if (reverse is not null)
                {
                    causes.Add($"reverse-flow:{reverse.From}-{reverse.To}");
                    en = $"local generation pushes reverse power flow ({F(Math.Abs(reverse.PFrom), 2)} MW) on {BranchRatings.Label(net.Branches[reverse.Index])}";
                    id = $"pembangkit lokal menyebabkan aliran daya balik ({F(Math.Abs(reverse.PFrom), 2)} MW) pada {BranchRatings.Label(net.Branches[reverse.Index], "id")}";
                }
                else if (ctx.LoadScale < 0.6)
                {
                    causes.Add("light-load");
                    en = $"the system is lightly loaded ({F(100 * ctx.LoadScale, 0)}% of nominal) and line charging/capacitors dominate";
                    id = $"beban sistem ringan ({F(100 * ctx.LoadScale, 0)}% dari nominal) sehingga kapasitansi saluran/kapasitor dominan";
                }
                else
                {
                    causes.Add("high-setpoint");
                    en = "generator or tap voltage set-points are high";
                    id = "setelan tegangan generator atau tap trafo terlalu tinggi";
                }
                alarms.Add(new ExplainedAlarm(TwinAlarmKind.Overvoltage, $"bus {b.BusId}", b.Vm, o.VoltageHighPu,
                    b.Vm > o.VoltageHighPu + 0.05 ? TwinAlarmSeverity.Critical : TwinAlarmSeverity.Warning,
                    $"Bus {b.BusId} voltage {F(b.Vm, 3)} pu above {F(o.VoltageHighPu, 2)} because {en}",
                    $"Tegangan bus {b.BusId} {F(b.Vm, 3)} pu di atas {F(o.VoltageHighPu, 2)} karena {id}", causes));
            }
        }

        if (ctx.ForecastDeviationPercent is { } dev && Math.Abs(dev) > o.ForecastDeviationPercent)
        {
            bool above = dev > 0;
            alarms.Add(new ExplainedAlarm(TwinAlarmKind.ForecastDeviation, "system load", dev, above ? o.ForecastDeviationPercent : -o.ForecastDeviationPercent,
                Math.Abs(dev) > 2 * o.ForecastDeviationPercent ? TwinAlarmSeverity.High : TwinAlarmSeverity.Warning,
                $"System load {F(Math.Abs(dev), 1)}% {(above ? "above" : "below")} forecast" +
                (above ? " (unexpected demand, check for weather effects or metering errors)" : " (possible load loss, unmetered generation or feeder trip)"),
                $"Beban sistem {F(Math.Abs(dev), 1)}% {(above ? "di atas" : "di bawah")} prakiraan" +
                (above ? " (permintaan tak terduga, periksa pengaruh cuaca atau kesalahan meter)" : " (kemungkinan kehilangan beban, pembangkitan tak terukur atau trip penyulang)"),
                [above ? "load-above-forecast" : "load-below-forecast"]));
        }
        return alarms;
    }

    /// <summary>Index of the reference bus (slack, else first generator bus, else 0).</summary>
    internal static int SourceIndex(PowerNetwork net)
    {
        if (net.SlackBus is { } s) return net.IndexOf(s.Id);
        var g = net.Generators.FirstOrDefault(x => x.InService);
        return g is null ? 0 : net.IndexOf(g.Bus);
    }

    /// <summary>Breadth-first supply tree: parent branch index of every bus (−1 for the source and unreachable buses).</summary>
    internal static int[] SupplyTree(PowerNetwork net)
    {
        int n = net.BusCount;
        var adj = new List<(int Bus, int Branch)>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            if (!br.InService) continue;
            int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
            adj[f].Add((t, k)); adj[t].Add((f, k));
        }
        var parent = Enumerable.Repeat(-1, n).ToArray();
        var seen = new bool[n];
        int src = SourceIndex(net);
        var q = new Queue<int>();
        q.Enqueue(src); seen[src] = true;
        while (q.Count > 0)
        {
            int u = q.Dequeue();
            foreach (var (v, k) in adj[u])
                if (!seen[v]) { seen[v] = true; parent[v] = k; q.Enqueue(v); }
        }
        return parent;
    }

    private static List<int> PathToSource(PowerNetwork net, int[] parent, int busIndex)
    {
        var path = new List<int>();
        int u = busIndex;
        for (int guard = 0; guard < net.BusCount && parent[u] >= 0; guard++)
        {
            int k = parent[u];
            path.Add(k);
            var br = net.Branches[k];
            int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
            u = f == u ? t : f;
        }
        return path;
    }

    private static bool FlowsTowardSource(PowerNetwork net, int[] parent, BranchResult r)
    {
        // the "child" end of a tree branch is the bus whose parent is this branch; power flowing from child to parent = reverse flow
        int f = net.IndexOf(r.From);
        bool fromIsChild = parent[f] == r.Index;
        double pFromChild = fromIsChild ? r.PFrom : r.PTo;
        return pFromChild > 1e-4 * net.BaseMva;
    }

    private static string F(double v, int decimals) => v.ToString("F" + decimals, Inv);
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
