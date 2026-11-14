using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.SmartGrid;

/// <summary>Discrete Volt/VAR control variables.</summary>
public sealed record VoltVarControls
{
    /// <summary>Switched capacitor banks: bus id and the admissible ratings [Mvar] (include 0 for "off").</summary>
    public IReadOnlyList<(int Bus, double[] StepsMvar)> Capacitors { get; init; } = [];
    /// <summary>OLTC / substation voltage set-points [pu] (empty = keep current).</summary>
    public IReadOnlyList<double> SubstationVoltagePu { get; init; } = [];
    /// <summary>Smart inverters: bus id and the admissible reactive set-points [Mvar] (positive = injection).</summary>
    public IReadOnlyList<(int Bus, double[] StepsMvar)> Inverters { get; init; } = [];

    /// <summary>
    /// Default controls for the Baran–Wu 33-bus feeder: capacitors at buses 14, 24, 30 (0–1.2 Mvar in 0.15 Mvar steps), OLTC 0.95–1.05 pu
    /// in 0.00625 pu (16 × 0.625 %) steps, and ±0.4 Mvar inverters at buses 18 and 33 (0.1 Mvar steps).
    /// </summary>
    public static VoltVarControls Case33Default()
    {
        double[] cap = Enumerable.Range(0, 9).Select(i => i * 0.15).ToArray();
        double[] inv = Enumerable.Range(-4, 9).Select(i => i * 0.1).ToArray();
        return new VoltVarControls
        {
            Capacitors = [(14, cap), (24, cap), (30, cap)],
            SubstationVoltagePu = Enumerable.Range(0, 17).Select(i => 0.95 + i * 0.00625).ToArray(),
            Inverters = [(18, inv), (33, inv)],
        };
    }
}

/// <summary>Volt/VAR optimization outcome (losses in MW, voltages in pu, reactive set-points in Mvar).</summary>
public sealed record VoltVarResult(double LossesBeforeMw, double LossesAfterMw, double VminBefore, double VmaxBefore, double VminAfter, double VmaxAfter,
    double SubstationVoltagePu, IReadOnlyDictionary<int, double> CapacitorMvar, IReadOnlyDictionary<int, double> InverterMvar, bool Feasible, int PowerFlows,
    PowerNetwork Network);

/// <summary>
/// Volt/VAR optimization: discrete coordinate descent over capacitor steps, OLTC set-point and inverter reactive power minimizing
/// feeder losses plus a large penalty on voltages outside [Vmin, Vmax]; every evaluation is an AC power flow. Repeats full passes
/// until no single-variable change improves the objective.
/// </summary>
public static class VoltVarOptimization
{
    public static VoltVarResult Optimize(PowerNetwork net, VoltVarControls controls, double vminPu = 0.95, double vmaxPu = 1.05, int maxPasses = 10)
    {
        int pfs = 0;
        var baseRes = PowerFlowSolver.Solve(net); pfs++;
        var caps = controls.Capacitors.Select(c => c.StepsMvar.Contains(0) ? 0.0 : c.StepsMvar[0]).ToArray();
        var invs = controls.Inverters.Select(c => c.StepsMvar.Contains(0) ? 0.0 : c.StepsMvar[0]).ToArray();
        var slackGen = net.Generators.FirstOrDefault(g => g.InService && net.SlackBus is { } sb && g.Bus == sb.Id);
        double vs = slackGen?.Vset ?? net.SlackBus?.Vm ?? 1.0;

        PowerNetwork Build(double[] cp, double[] iv, double v0)
        {
            var c = net.Clone();
            for (int k = 0; k < cp.Length; k++) c.GetBus(controls.Capacitors[k].Bus).Bs += cp[k];
            for (int k = 0; k < iv.Length; k++)
                c.Generators.Add(new Generator { Bus = controls.Inverters[k].Bus, Kind = GeneratorKind.Solar, Name = $"VVO-Q{controls.Inverters[k].Bus}", Q = iv[k] });
            foreach (var g in c.Generators) if (net.SlackBus is { } sb && g.Bus == sb.Id) g.Vset = v0;
            if (c.SlackBus is { } s) s.Vm = v0;
            return c;
        }
        (double Obj, double Loss, bool Ok) Eval(double[] cp, double[] iv, double v0)
        {
            pfs++;
            var r = PowerFlowSolver.Solve(Build(cp, iv, v0));
            if (!r.Converged) return (double.PositiveInfinity, double.NaN, false);
            double viol = r.Buses.Sum(b => Math.Max(0, vminPu - b.Vm) + Math.Max(0, b.Vm - vmaxPu));
            return (r.LossesMw + 1e3 * viol * Math.Max(1, r.TotalLoadMw), r.LossesMw, viol < 1e-9);
        }
        var cur = Eval(caps, invs, vs);
        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool improved = false;
            if (controls.SubstationVoltagePu.Count > 0)
                foreach (double v in controls.SubstationVoltagePu)
                {
                    var e = Eval(caps, invs, v);
                    if (e.Obj < cur.Obj - 1e-12) { cur = e; vs = v; improved = true; }
                }
            for (int k = 0; k < caps.Length; k++)
                foreach (double q in controls.Capacitors[k].StepsMvar)
                {
                    var trial = (double[])caps.Clone(); trial[k] = q;
                    var e = Eval(trial, invs, vs);
                    if (e.Obj < cur.Obj - 1e-12) { cur = e; caps = trial; improved = true; }
                }
            for (int k = 0; k < invs.Length; k++)
                foreach (double q in controls.Inverters[k].StepsMvar)
                {
                    var trial = (double[])invs.Clone(); trial[k] = q;
                    var e = Eval(caps, trial, vs);
                    if (e.Obj < cur.Obj - 1e-12) { cur = e; invs = trial; improved = true; }
                }
            if (!improved) break;
        }
        var final = Build(caps, invs, vs);
        var after = PowerFlowSolver.Solve(final); pfs++;
        return new VoltVarResult(baseRes.LossesMw, after.LossesMw, baseRes.Buses.Min(b => b.Vm), baseRes.Buses.Max(b => b.Vm),
            after.Buses.Min(b => b.Vm), after.Buses.Max(b => b.Vm), vs,
            controls.Capacitors.Select((c, k) => (c.Bus, caps[k])).ToDictionary(t => t.Bus, t => t.Item2),
            controls.Inverters.Select((c, k) => (c.Bus, invs[k])).ToDictionary(t => t.Bus, t => t.Item2), cur.Ok, pfs, final);
    }
}

/// <summary>Feeder reconfiguration outcome.</summary>
/// <param name="OpenBranches">Open switches as "from-to" (sorted).</param>
public sealed record ReconfigurationResult(IReadOnlyList<string> OpenBranches, double LossesBeforeMw, double LossesAfterMw, double MinVoltageBefore,
    double MinVoltageAfter, int PowerFlows, PowerNetwork Network)
{
    public double LossReductionPercent => LossesBeforeMw > 0 ? 100 * (1 - LossesAfterMw / LossesBeforeMw) : 0;
}

/// <summary>
/// Distribution feeder reconfiguration for loss minimization: (1) sequential switch opening (Shirmohammadi &amp; Hong 1989) starting from the
/// fully meshed network — repeatedly open the loop branch carrying the smallest current; (2) branch-exchange refinement (Civanlar et al.
/// 1988) — for every open switch, try closing it and opening each other switch in the loop it forms, accepting the best improving exchange.
/// Radiality and connectivity are maintained; every candidate is evaluated with an AC power flow.
/// </summary>
public static class FeederReconfiguration
{
    /// <summary>
    /// Baran–Wu 33-bus feeder with its five normally-open tie switches (8-21, 9-15, 12-22: 2 + j2 Ω; 18-33, 25-29: 0.5 + j0.5 Ω).
    /// Known loss-minimal configuration opens 7-8, 9-10, 14-15, 32-33, 25-29 with ≈ 139.5 kW losses (vs. 202.7 kW base).
    /// </summary>
    public static PowerNetwork Case33WithTies()
    {
        var net = TestCases.Case33BW();
        double zb = 12.66 * 12.66 / net.BaseMva;
        foreach (var (f, t, r) in new[] { (8, 21, 2.0), (9, 15, 2.0), (12, 22, 2.0), (18, 33, 0.5), (25, 29, 0.5) })
            net.Branches.Add(new Branch { From = f, To = t, R = r / zb, X = r / zb, InService = false, Name = $"Tie {f}-{t}" });
        return net;
    }

    /// <param name="switchable">Branch indices that may be opened/closed (default: all).</param>
    public static ReconfigurationResult Optimize(PowerNetwork net, IReadOnlyList<int>? switchable = null, int maxExchanges = 50)
    {
        int pfs = 0;
        var sw = (switchable ?? Enumerable.Range(0, net.Branches.Count).ToList()).ToHashSet();
        var baseRes = PowerFlowSolver.Solve(net); pfs++;
        var work = net.Clone();
        double Loss(PowerNetwork n)
        {
            pfs++;
            if (Topology.Islands(n).Count > 1) return double.PositiveInfinity;
            var r = PowerFlowSolver.Solve(n);
            return r.Converged ? r.LossesMw : double.PositiveInfinity;
        }

        // 1) sequential switch opening from the meshed network
        foreach (int k in sw) work.Branches[k].InService = true;
        while (!Topology.IsRadial(work))
        {
            pfs++;
            var r = PowerFlowSolver.Solve(work);
            int best = -1; double bestI = double.PositiveInfinity;
            foreach (int k in sw)
            {
                var br = work.Branches[k];
                if (!br.InService) continue;
                br.InService = false;
                bool connected = Topology.Islands(work).Count == 1;
                br.InService = true;
                if (!connected) continue;
                double i = r.Branches[k].SFrom / Math.Max(r.Bus(br.From).Vm, 1e-6);
                if (i < bestI) { bestI = i; best = k; }
            }
            if (best < 0) break;
            work.Branches[best].InService = false;
        }
        double current = Loss(work);

        // 2) branch exchange
        for (int ex = 0; ex < maxExchanges; ex++)
        {
            double bestLoss = current; (int Close, int Open) bestPair = (-1, -1);
            foreach (int c in sw)
            {
                if (work.Branches[c].InService) continue;
                work.Branches[c].InService = true;
                foreach (int o in LoopBranches(work, c).Where(sw.Contains))
                {
                    if (o == c) continue;
                    work.Branches[o].InService = false;
                    double l = Loss(work);
                    if (l < bestLoss - 1e-9) { bestLoss = l; bestPair = (c, o); }
                    work.Branches[o].InService = true;
                }
                work.Branches[c].InService = false;
            }
            if (bestPair.Close < 0) break;
            work.Branches[bestPair.Close].InService = true;
            work.Branches[bestPair.Open].InService = false;
            current = bestLoss;
        }
        var after = PowerFlowSolver.Solve(work); pfs++;
        var open = work.Branches.Where(b => !b.InService).Select(b => $"{b.From}-{b.To}").Order(StringComparer.Ordinal).ToList();
        return new ReconfigurationResult(open, baseRes.LossesMw, after.LossesMw, baseRes.MinVoltageBus.Vm, after.MinVoltageBus.Vm, pfs, work);
    }

    /// <summary>Branch indices on the unique loop created by the (closed) branch <paramref name="closed"/> in an otherwise radial network.</summary>
    public static List<int> LoopBranches(PowerNetwork net, int closed)
    {
        var br = net.Branches[closed];
        int n = net.BusCount;
        var adj = new List<(int To, int Branch)>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var b = net.Branches[k];
            if (!b.InService || k == closed) continue;
            int f = net.IndexOf(b.From), t = net.IndexOf(b.To);
            adj[f].Add((t, k)); adj[t].Add((f, k));
        }
        int s = net.IndexOf(br.From), e = net.IndexOf(br.To);
        var prev = new (int Bus, int Branch)[n];
        Array.Fill(prev, (-1, -1));
        var seen = new bool[n];
        var q = new Queue<int>();
        q.Enqueue(s); seen[s] = true;
        while (q.Count > 0)
        {
            int u = q.Dequeue();
            if (u == e) break;
            foreach (var (v, k) in adj[u]) if (!seen[v]) { seen[v] = true; prev[v] = (u, k); q.Enqueue(v); }
        }
        var loop = new List<int> { closed };
        if (!seen[e]) return loop;
        for (int v = e; v != s; v = prev[v].Bus) loop.Add(prev[v].Branch);
        return loop;
    }
}
