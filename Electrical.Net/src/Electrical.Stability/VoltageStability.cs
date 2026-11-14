using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;

namespace Electrical.Stability;

/// <summary>Options for the PV-curve (load-scaling continuation) study.</summary>
public sealed record PvCurveOptions
{
    /// <summary>Initial loading-multiplier step.</summary>
    public double InitialStep { get; init; } = 0.1;
    /// <summary>Stop when the step has been halved below this value (accuracy of λ_max).</summary>
    public double MinimumStep { get; init; } = 1e-3;
    /// <summary>Upper bound on the loading multiplier.</summary>
    public double MaxLambda { get; init; } = 20;
    /// <summary>Buses whose loads are scaled (null = all loads).</summary>
    public IReadOnlyCollection<int>? LoadBuses { get; init; }
    /// <summary>Scale the active power of non-slack generators with λ (otherwise the slack picks up all of the increase).</summary>
    public bool ScaleGeneration { get; init; } = true;
    /// <summary>Generator reactive limits during the sweep.</summary>
    public bool EnforceQLimits { get; init; }
}

/// <summary>PV-curve result. λ is the load multiplier (1 = base case).</summary>
public sealed class PvCurveResult
{
    public required PowerNetwork Network { get; init; }
    public required double[] Lambda { get; init; }
    /// <summary>Total scaled load [MW] at each point.</summary>
    public required double[] TotalLoadMw { get; init; }
    /// <summary>Bus voltages [pu]: bus position × point.</summary>
    public required double[][] Voltages { get; init; }
    /// <summary>Maximum loading multiplier reached (nose point estimate).</summary>
    public double LambdaMax => Lambda[^1];
    /// <summary>Loadability margin (λ_max − 1) in per unit of the base load.</summary>
    public double MarginPu => LambdaMax - 1;
    /// <summary>Loadability margin [MW].</summary>
    public double MarginMw => TotalLoadMw[^1] - TotalLoadMw[0];
    /// <summary>Bus with the lowest voltage at the nose (weakest bus).</summary>
    public int CriticalBusId => Network.Buses[Enumerable.Range(0, Network.BusCount).MinBy(i => Voltages[i][^1])].Id;

    /// <summary>Voltage trajectory of a bus [pu].</summary>
    public double[] Voltage(int busId) => Voltages[Network.IndexOf(busId)];
}

/// <summary>QV-curve result at one bus: reactive injection needed to hold each voltage.</summary>
public sealed record QvCurveResult(int BusId, double[] VoltagePu, double[] QMvar)
{
    /// <summary>Minimum of the curve [Mvar]; negative values mean the bus has reactive margin.</summary>
    public double MinimumQMvar => QMvar.Min();
    /// <summary>Reactive power margin = −Q_min [Mvar] (positive = stable margin at the operating point).</summary>
    public double ReactiveMarginMvar => -MinimumQMvar;
    /// <summary>Voltage at the curve minimum (critical voltage) [pu].</summary>
    public double CriticalVoltagePu => VoltagePu[Array.IndexOf(QMvar, MinimumQMvar)];
}

/// <summary>Kessel–Glavitsch L-index per load bus (0 = no load, 1 = voltage collapse).</summary>
public sealed record LIndexResult(IReadOnlyDictionary<int, double> ByBus)
{
    public double Max => ByBus.Count == 0 ? 0 : ByBus.Values.Max();
    public int CriticalBusId => ByBus.MaxBy(kv => kv.Value).Key;
}

/// <summary>Static voltage-stability tools: PV curves (continuation by load scaling with step halving), QV curves and the L-index.</summary>
public static class VoltageStability
{
    /// <summary>
    /// PV curve by increasing the loading multiplier λ (loads at constant power factor, optionally generation) and re-solving Newton–Raphson
    /// power flows from the previous point; when a step fails it is halved until <see cref="PvCurveOptions.MinimumStep"/>. The last converged point
    /// approximates the nose (maximum loadability). This is a simple predictor (warm start) / corrector (NR) scheme; it does not trace the lower branch.
    /// </summary>
    public static PvCurveResult PvCurve(PowerNetwork network, PvCurveOptions? options = null)
    {
        var opt = options ?? new PvCurveOptions();
        var baseNet = network.Clone();
        var pfo = PowerFlowOptions.Default with { FlatStart = false, EnforceQLimits = opt.EnforceQLimits, MaxIterations = 25 };
        var p0 = baseNet.Loads.Select(l => (l.P, l.Q)).ToArray();
        var g0 = baseNet.Generators.Select(g => g.P).ToArray();
        var slackBuses = baseNet.Buses.Where(b => b.Type == BusType.Slack).Select(b => b.Id).ToHashSet();
        bool Scaled(Load l) => opt.LoadBuses is null || opt.LoadBuses.Contains(l.Bus);

        PowerFlowResult? Solve(PowerNetwork warm, double lambda)
        {
            var c = warm.Clone();
            for (int k = 0; k < c.Loads.Count; k++)
                if (Scaled(c.Loads[k])) { c.Loads[k].P = p0[k].P * lambda; c.Loads[k].Q = p0[k].Q * lambda; }
            if (opt.ScaleGeneration)
                for (int k = 0; k < c.Generators.Count; k++)
                    if (!slackBuses.Contains(c.Generators[k].Bus)) c.Generators[k].P = g0[k] * lambda;
            try
            {
                var r = PowerFlowSolver.Solve(c, pfo);
                return r.Converged && r.Voltages.All(v => v.Magnitude > 0.2 && double.IsFinite(v.Magnitude)) ? r : null;
            }
            catch (Exception e) when (e is InvalidOperationException or ArithmeticException) { return null; }
        }

        var first = Solve(baseNet, 1.0) ?? throw new InvalidOperationException("Base-case power flow did not converge.");
        var lambdas = new List<double> { 1.0 };
        var loads = new List<double> { Load(first) };
        var volts = new List<double[]> { first.Voltages.Select(v => v.Magnitude).ToArray() };
        var warmNet = baseNet.Clone();
        first.ApplyTo(warmNet);
        RestoreGenP(warmNet, g0);
        double lam = 1, step = opt.InitialStep;
        while (step >= opt.MinimumStep && lam + step <= opt.MaxLambda)
        {
            var r = Solve(warmNet, lam + step);
            // reject jumps to the lower (unstable) branch: voltages must not rise as load increases
            if (r is not null && r.Voltages.Min(v => v.Magnitude) <= volts[^1].Min() + 1e-6)
            {
                lam += step;
                lambdas.Add(lam); loads.Add(Load(r)); volts.Add(r.Voltages.Select(v => v.Magnitude).ToArray());
                r.ApplyTo(warmNet);
                RestoreGenP(warmNet, g0);
            }
            else step /= 2;
        }
        int n = baseNet.BusCount;
        var vb = Enumerable.Range(0, n).Select(i => volts.Select(v => v[i]).ToArray()).ToArray();
        return new PvCurveResult { Network = baseNet, Lambda = lambdas.ToArray(), TotalLoadMw = loads.ToArray(), Voltages = vb };

        double Load(PowerFlowResult r) => r.TotalLoadMw;
        static void RestoreGenP(PowerNetwork net, double[] g) { for (int k = 0; k < net.Generators.Count; k++) net.Generators[k].P = g[k]; }
    }

    /// <summary>
    /// QV curve at a PQ bus: a fictitious synchronous condenser holds the bus voltage at each value from <paramref name="vMaxPu"/> down to
    /// <paramref name="vMinPu"/>; its reactive output Q(V) [Mvar] is recorded until the power flow fails.
    /// </summary>
    public static QvCurveResult QvCurve(PowerNetwork network, int busId, double vMinPu = 0.6, double vMaxPu = 1.1, double stepPu = 0.01)
    {
        var net = network.Clone();
        var bus = net.GetBus(busId);
        if (bus.Type != BusType.PQ) throw new ArgumentException("QV curves are computed at PQ buses.", nameof(busId));
        var basePf = PowerFlowSolver.Solve(net);
        if (!basePf.Converged) throw new InvalidOperationException("Base-case power flow did not converge.");
        basePf.ApplyTo(net);
        bus.Type = BusType.PV;
        net.Generators.Add(new Generator { Bus = busId, P = 0, Qmax = 1e6, Qmin = -1e6, Name = "QV condenser", Pmax = 0 });
        int gk = net.Generators.Count - 1;
        var vs = new List<double>(); var qs = new List<double>();
        var pfo = PowerFlowOptions.Default with { FlatStart = false };
        int count = (int)Math.Round((vMaxPu - vMinPu) / stepPu);
        for (int s = 0; s <= count; s++)
        {
            double v = vMaxPu - s * stepPu;
            net.Generators[gk].Vset = v;
            bus.Vm = v;
            PowerFlowResult r;
            try { r = PowerFlowSolver.Solve(net, pfo); }
            catch (Exception e) when (e is InvalidOperationException or ArithmeticException) { break; }
            if (!r.Converged) break;
            vs.Add(v);
            qs.Add(r.Generators.First(g => g.Index == gk).Q);
            r.ApplyTo(net);
            net.Generators[gk].P = 0;
        }
        return new QvCurveResult(busId, vs.ToArray(), qs.ToArray());
    }

    /// <summary>
    /// L-index L_j = |1 − Σ_{i∈G} F_ji·V_i / V_j| with F_LG = −Y_LL⁻¹·Y_LG (Kessel &amp; Glavitsch 1986), from a solved power flow.
    /// Generator buses are those of type PV or slack in the result.
    /// </summary>
    public static LIndexResult LIndex(PowerNetwork network, PowerFlowResult? powerFlow = null)
    {
        var pf = powerFlow ?? PowerFlowSolver.Solve(network);
        var y = AdmittanceMatrix.Build(network).Y;
        int n = network.BusCount;
        var isGen = Enumerable.Range(0, n).Select(i => pf.Buses[i].Type is BusType.PV or BusType.Slack).ToArray();
        var loadIdx = Enumerable.Range(0, n).Where(i => !isGen[i]).ToArray();
        var genIdx = Enumerable.Range(0, n).Where(i => isGen[i]).ToArray();
        var pos = new int[n];
        Array.Fill(pos, -1);
        for (int k = 0; k < loadIdx.Length; k++) pos[loadIdx[k]] = k;
        var result = new Dictionary<int, double>();
        if (loadIdx.Length == 0) return new LIndexResult(result);
        var b = new SparseBuilder<Complex>(loadIdx.Length, loadIdx.Length);
        foreach (int i in loadIdx)
        {
            var idx = y.RowIndices(i); var val = y.RowValues(i);
            for (int q = 0; q < idx.Length; q++) if (pos[idx[q]] >= 0) b.Add(pos[i], pos[idx[q]], val[q]);
        }
        var lu = SparseLU<Complex>.Factorize(b.ToCsr());
        var v = pf.Voltages;
        var sum = new Complex[loadIdx.Length];
        foreach (int g in genIdx)
        {
            var col = new Complex[loadIdx.Length];
            foreach (int i in loadIdx) col[pos[i]] = y[i, g];
            var x = lu.Solve(col); // Y_LL⁻¹·Y_LG(:,g)
            for (int k = 0; k < loadIdx.Length; k++) sum[k] += -x[k] * v[g];
        }
        for (int k = 0; k < loadIdx.Length; k++)
            result[network.Buses[loadIdx[k]].Id] = (1 - sum[k] / v[loadIdx[k]]).Magnitude;
        return new LIndexResult(result);
    }
}
