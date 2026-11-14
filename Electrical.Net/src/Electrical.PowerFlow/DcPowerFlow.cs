using System.Diagnostics;
using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;

namespace Electrical.PowerFlow;

/// <summary>
/// Linear DC power flow (|V| = 1, lossless, small angles): B·θ = P − P_shift. Also provides PTDF / LODF sensitivities
/// used by security-constrained dispatch, N-1 screening and congestion management.
/// </summary>
public static class DcPowerFlow
{
    public static PowerFlowResult Solve(PowerNetwork net)
    {
        var sw = Stopwatch.StartNew();
        var model = DcModel.Build(net);
        var p = new double[model.N];
        foreach (var g in net.Generators) if (g.InService) p[net.IndexOf(g.Bus)] += g.P / net.BaseMva;
        foreach (var l in net.Loads) if (l.InService) p[net.IndexOf(l.Bus)] -= l.P / net.BaseMva;
        var theta = model.Angles(p);
        var v = theta.Select(t => Complex.FromPolarCoordinates(1, t)).ToArray();

        var flows = model.BranchFlowsMw(theta);
        int slack = model.Slack;
        double slackP = -p.Where((_, i) => i != slack).Sum() * net.BaseMva; // injection needed at slack (lossless)
        var buses = new List<BusResult>();
        var gens = new List<GeneratorResult>();
        for (int i = 0; i < model.N; i++)
        {
            var b = net.Buses[i];
            double pl = net.Loads.Where(l => l.InService && l.Bus == b.Id).Sum(l => l.P);
            double pg = net.Generators.Where(g => g.InService && g.Bus == b.Id).Sum(g => g.P);
            if (i == slack) pg = slackP + pl;
            buses.Add(new BusResult(b.Id, b.Name, 1, theta[i] * Scalar.Rad2Deg, pg, 0, pl, 0, i == slack ? BusType.Slack : b.Type));
        }
        var slackGens = net.Generators.Select((g, k) => (g, k)).Where(x => x.g.InService && net.IndexOf(x.g.Bus) == slack).ToList();
        for (int k = 0; k < net.Generators.Count; k++)
        {
            var g = net.Generators[k];
            if (!g.InService) continue;
            double pg = net.IndexOf(g.Bus) == slack ? buses[slack].PGen / Math.Max(1, slackGens.Count) : g.P;
            gens.Add(new GeneratorResult(k, g.Bus, g.Name, pg, 0, false));
        }
        var branches = net.Branches.Select((br, k) => new BranchResult(k, br.From, br.To, br.Name, flows[k], 0, -flows[k], 0, br.RateA)).ToList();
        return new PowerFlowResult
        {
            Network = net, Method = PowerFlowMethod.DC, Converged = true, Iterations = 1, MaxMismatch = 0, Voltages = v,
            Buses = buses, Branches = branches, Generators = gens, Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// Power transfer distribution factors: PTDF[l, i] = change of flow on branch l [MW] per 1 MW injected at bus i
    /// and withdrawn at the slack bus.
    /// </summary>
    public static DenseMatrix<double> Ptdf(PowerNetwork net)
    {
        var model = DcModel.Build(net);
        var ptdf = new DenseMatrix<double>(net.Branches.Count, model.N);
        var e = new double[model.N];
        for (int i = 0; i < model.N; i++)
        {
            if (i == model.Slack) continue;
            Array.Clear(e);
            e[i] = 1;
            var theta = model.Angles(e, includeShift: false);
            for (int l = 0; l < net.Branches.Count; l++)
            {
                var br = net.Branches[l];
                if (!br.InService) continue;
                ptdf[l, i] = (theta[net.IndexOf(br.From)] - theta[net.IndexOf(br.To)]) / (br.X * br.EffectiveTap);
            }
        }
        return ptdf;
    }

    /// <summary>
    /// Line outage distribution factors: LODF[l, k] = fraction of the pre-outage flow of branch k that moves onto branch l
    /// when k trips. Diagonal is −1. Radial (islanding) outages give NaN columns.
    /// </summary>
    public static DenseMatrix<double> Lodf(PowerNetwork net, DenseMatrix<double>? ptdf = null)
    {
        ptdf ??= Ptdf(net);
        int m = net.Branches.Count;
        var lodf = new DenseMatrix<double>(m, m);
        for (int k = 0; k < m; k++)
        {
            var bk = net.Branches[k];
            int fk = net.IndexOf(bk.From), tk = net.IndexOf(bk.To);
            double denom = 1 - (ptdf[k, fk] - ptdf[k, tk]);
            for (int l = 0; l < m; l++)
            {
                if (l == k) { lodf[l, k] = -1; continue; }
                lodf[l, k] = Math.Abs(denom) < 1e-9 ? double.NaN : (ptdf[l, fk] - ptdf[l, tk]) / denom;
            }
        }
        return lodf;
    }
}

/// <summary>Reduced DC network (B matrix without the slack row/column), reusable for many right-hand sides.</summary>
public sealed class DcModel
{
    private readonly PowerNetwork _net;
    private readonly SparseLU<double>? _lu;
    private readonly int[] _reduced; // bus position -> reduced index (−1 for slack)
    private readonly double[] _shiftInjection;

    private DcModel(PowerNetwork net, int slack, SparseLU<double>? lu, int[] reduced, double[] shiftInjection)
    {
        _net = net; Slack = slack; _lu = lu; _reduced = reduced; _shiftInjection = shiftInjection;
    }

    public int N => _net.BusCount;
    public int Slack { get; }

    public static DcModel Build(PowerNetwork net)
    {
        int n = net.BusCount;
        int slack = net.SlackBus is { } s ? net.IndexOf(s.Id) : 0;
        var reduced = new int[n];
        for (int i = 0, r = 0; i < n; i++) reduced[i] = i == slack ? -1 : r++;
        var b = new SparseBuilder<double>(n - 1, n - 1);
        var pShift = new double[n];
        for (int i = 0; i < n - 1; i++) b.Add(i, i, 0);
        foreach (var br in net.Branches)
        {
            if (!br.InService) continue;
            int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
            double y = 1 / (br.X * br.EffectiveTap);
            int rf = reduced[f], rt = reduced[t];
            if (rf >= 0) b.Add(rf, rf, y);
            if (rt >= 0) b.Add(rt, rt, y);
            if (rf >= 0 && rt >= 0) { b.Add(rf, rt, -y); b.Add(rt, rf, -y); }
            double shift = br.ShiftDeg * Scalar.Deg2Rad * y;
            pShift[f] -= shift; pShift[t] += shift;
        }
        var lu = n > 1 ? SparseLU<double>.Factorize(b.ToCsr()) : null;
        return new DcModel(net, slack, lu, reduced, pShift);
    }

    /// <summary>Bus angles [rad] for net injections p [pu] (slack angle from the network, default 0).</summary>
    public double[] Angles(ReadOnlySpan<double> p, bool includeShift = true)
    {
        int n = N;
        var rhs = new double[n - 1];
        for (int i = 0; i < n; i++)
            if (_reduced[i] >= 0) rhs[_reduced[i]] = p[i] - (includeShift ? _shiftInjection[i] : 0);
        var x = _lu?.Solve(rhs) ?? [];
        var theta = new double[n];
        double slackAngle = includeShift ? _net.Buses[Slack].VaDeg * Scalar.Deg2Rad : 0;
        for (int i = 0; i < n; i++) theta[i] = (_reduced[i] >= 0 ? x[_reduced[i]] : 0) + slackAngle;
        return theta;
    }

    public double[] BranchFlowsMw(ReadOnlySpan<double> theta)
    {
        var flows = new double[_net.Branches.Count];
        for (int k = 0; k < flows.Length; k++)
        {
            var br = _net.Branches[k];
            if (!br.InService) continue;
            int f = _net.IndexOf(br.From), t = _net.IndexOf(br.To);
            flows[k] = (theta[f] - theta[t] - br.ShiftDeg * Scalar.Deg2Rad) / (br.X * br.EffectiveTap) * _net.BaseMva;
        }
        return flows;
    }
}
