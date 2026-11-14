using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.Optimization;

/// <summary>A placed shunt capacitor bank.</summary>
/// <param name="BusId">Bus id.</param>
/// <param name="Mvar">Rated reactive power at 1 pu [Mvar].</param>
public sealed record CapacitorBank(int BusId, double Mvar);

/// <summary>Result of <see cref="CapacitorPlacement.Optimize"/>. Losses in MW, voltages in pu.</summary>
public sealed record CapacitorPlacementResult(IReadOnlyList<CapacitorBank> Capacitors, double LossesBeforeMw, double LossesAfterMw,
    double MinVoltageBefore, double MinVoltageAfter, IReadOnlyList<(int BusId, double Lsf)> LossSensitivity, int PowerFlows)
{
    public double TotalMvar => Capacitors.Sum(c => c.Mvar);
    public double LossReductionPercent => LossesBeforeMw > 0 ? 100 * (1 - LossesAfterMw / LossesBeforeMw) : 0;
}

/// <summary>
/// Optimal capacitor placement for distribution feeders: candidate buses are ranked by the loss sensitivity factor
/// LSF = ∂P_loss/∂Q_eff = 2·Q_eff·R/V² of the branch feeding each bus (Prakash &amp; Sydulu 2007), then banks of discrete size are
/// placed greedily (largest loss reduction per power flow), followed by a coordinate-descent refinement of the sizes, subject to
/// all voltages ≤ Vmax. Every candidate is evaluated with a full AC power flow.
/// </summary>
public static class CapacitorPlacement
{
    /// <param name="maxCapacitors">Maximum number of banks.</param>
    /// <param name="stepMvar">Discrete bank size step [Mvar].</param>
    /// <param name="maxMvarPerBus">Largest bank at a bus [Mvar].</param>
    /// <param name="candidates">Number of top-LSF buses to consider.</param>
    /// <param name="vmaxPu">Upper voltage limit [pu].</param>
    public static CapacitorPlacementResult Optimize(PowerNetwork net, int maxCapacitors = 3, double stepMvar = 0.05, double maxMvarPerBus = 1.5,
        int candidates = 10, double vmaxPu = 1.05)
    {
        int pfCount = 0;
        var opts = new PowerFlowOptions();
        var baseRes = PowerFlowSolver.Solve(net, opts); pfCount++;
        var lsf = LossSensitivity(baseRes);
        var cand = lsf.Take(candidates).Select(t => t.BusId).ToList();
        var work = net.Clone();
        var placed = new Dictionary<int, double>();

        double Eval(Dictionary<int, double> caps)
        {
            var c = net.Clone();
            foreach (var (bus, q) in caps) c.GetBus(bus).Bs += q;
            pfCount++;
            var r = PowerFlowSolver.Solve(c, opts);
            if (!r.Converged || r.Buses.Max(b => b.Vm) > vmaxPu + 1e-9) return double.PositiveInfinity;
            return r.LossesMw;
        }

        double current = baseRes.LossesMw;
        int nSizes = (int)Math.Round(maxMvarPerBus / stepMvar);
        for (int round = 0; round < maxCapacitors; round++)
        {
            double bestLoss = current; int bestBus = -1; double bestSize = 0;
            foreach (int bus in cand)
            {
                if (placed.ContainsKey(bus)) continue;
                for (int s = 1; s <= nSizes; s++)
                {
                    var trial = new Dictionary<int, double>(placed) { [bus] = s * stepMvar };
                    double l = Eval(trial);
                    if (l < bestLoss - 1e-9) { bestLoss = l; bestBus = bus; bestSize = s * stepMvar; }
                }
            }
            if (bestBus < 0) break;
            placed[bestBus] = bestSize;
            current = bestLoss;
            // refinement: adjust every bank ±step while losses decrease
            bool improved = true;
            while (improved)
            {
                improved = false;
                foreach (int bus in placed.Keys.ToList())
                    foreach (int dir in new[] { 1, -1 })
                    {
                        double q = placed[bus] + dir * stepMvar;
                        if (q < stepMvar - 1e-12 || q > maxMvarPerBus + 1e-12) continue;
                        var trial = new Dictionary<int, double>(placed) { [bus] = q };
                        double l = Eval(trial);
                        if (l < current - 1e-9) { placed[bus] = q; current = l; improved = true; }
                    }
            }
        }
        var final = net.Clone();
        foreach (var (bus, q) in placed) final.GetBus(bus).Bs += q;
        var after = PowerFlowSolver.Solve(final, opts); pfCount++;
        return new CapacitorPlacementResult(placed.Select(kv => new CapacitorBank(kv.Key, Math.Round(kv.Value, 6))).OrderBy(c => c.BusId).ToList(),
            baseRes.LossesMw, after.LossesMw, baseRes.MinVoltageBus.Vm, after.MinVoltageBus.Vm, lsf, pfCount);
    }

    /// <summary>
    /// Loss sensitivity factors 2·Q_eff·R/V² [MW/Mvar·pu] for the receiving bus of every in-service branch, sorted descending.
    /// Q_eff is the reactive power arriving at the receiving end (pu), R the branch resistance (pu) and V the receiving voltage (pu).
    /// </summary>
    public static IReadOnlyList<(int BusId, double Lsf)> LossSensitivity(PowerFlowResult pf)
    {
        var net = pf.Network;
        var list = new List<(int, double)>();
        foreach (var b in pf.Branches)
        {
            var br = net.Branches[b.Index];
            if (!br.InService) continue;
            // receiving end = end where active power arrives
            bool toIsReceiving = b.PFrom >= 0;
            int recv = toIsReceiving ? b.To : b.From;
            double qEff = (toIsReceiving ? -b.QTo : -b.QFrom) / net.BaseMva;
            double v = pf.Bus(recv).Vm;
            list.Add((recv, 2 * qEff * br.R / (v * v)));
        }
        return list.GroupBy(t => t.Item1).Select(g => (g.Key, g.Max(t => t.Item2))).OrderByDescending(t => t.Item2).ToList();
    }
}
