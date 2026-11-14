using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;

namespace Electrical.PowerFlow;

/// <summary>Per-unit data prepared once per solve: Y-bus, scheduled injections, bus classification and ZIP load split.</summary>
internal sealed class PowerFlowCase
{
    public readonly PowerNetwork Net;
    public readonly AdmittanceMatrix Adm;
    public readonly int N;
    public readonly BusType[] Types;
    public readonly Complex[] SGen;          // scheduled generation [pu]
    public readonly Complex[] LoadZ, LoadI, LoadP; // load components [pu] at 1 pu voltage
    public readonly double[] VSet;
    public readonly List<int>[] GensAt;

    public PowerFlowCase(PowerNetwork net, bool flatStart, out Complex[] v0)
    {
        Net = net;
        N = net.BusCount;
        Adm = AdmittanceMatrix.Build(net);
        Types = new BusType[N];
        SGen = new Complex[N];
        LoadZ = new Complex[N]; LoadI = new Complex[N]; LoadP = new Complex[N];
        VSet = new double[N];
        GensAt = new List<int>[N];
        for (int i = 0; i < N; i++) { GensAt[i] = []; Types[i] = net.Buses[i].Type; VSet[i] = net.Buses[i].Vm; }

        for (int k = 0; k < net.Generators.Count; k++)
        {
            var g = net.Generators[k];
            if (!g.InService) continue;
            int i = net.IndexOf(g.Bus);
            GensAt[i].Add(k);
            SGen[i] += new Complex(g.P, g.Q) / net.BaseMva;
            VSet[i] = g.Vset;
        }
        for (int i = 0; i < N; i++)
            if (Types[i] == BusType.PV && GensAt[i].Count == 0) Types[i] = BusType.PQ;
        foreach (var l in net.Loads)
        {
            if (!l.InService) continue;
            int i = net.IndexOf(l.Bus);
            var s = new Complex(l.P, l.Q) / net.BaseMva;
            LoadZ[i] += s * l.Model.Z; LoadI[i] += s * l.Model.I; LoadP[i] += s * l.Model.P;
        }
        if (!Types.Contains(BusType.Slack))
        {
            // promote the largest generator bus to reference
            int best = Enumerable.Range(0, N).Where(i => GensAt[i].Count > 0)
                .OrderByDescending(i => GensAt[i].Sum(k => net.Generators[k].Pmax)).DefaultIfEmpty(0).First();
            Types[best] = BusType.Slack;
        }

        // Flat start: angles from a DC power flow (includes phase shifters), which keeps Newton–Raphson in the basin of the
        // operational solution when large phase shifts or heavy transfers are present.
        double[]? dcAngles = flatStart ? DcStartAngles(net) : null;
        v0 = new Complex[N];
        for (int i = 0; i < N; i++)
        {
            double vm = Types[i] is BusType.PV or BusType.Slack ? VSet[i] : (flatStart ? 1.0 : net.Buses[i].Vm);
            double va = flatStart && Types[i] != BusType.Slack ? dcAngles?[i] ?? 0 : net.Buses[i].VaDeg * Scalar.Deg2Rad;
            if (Types[i] == BusType.Isolated) vm = 0;
            v0[i] = Complex.FromPolarCoordinates(vm, va);
        }
    }

    private double[]? DcStartAngles(PowerNetwork net)
    {
        try
        {
            if (net.Branches.Any(b => b.InService && Math.Abs(b.X) < 1e-9)) return null;
            if (Topology.Islands(net).Count > 1) return null;
            var model = DcModel.Build(net);
            var p = new double[N];
            for (int i = 0; i < N; i++) p[i] = (SGen[i] - LoadZ[i] - LoadI[i] - LoadP[i]).Real;
            var theta = model.Angles(p);
            // reject implausible starts (e.g. ill-conditioned B matrix)
            return theta.All(t => double.IsFinite(t) && Math.Abs(t) < Math.PI) ? theta : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Total load at bus i for voltage magnitude vm [pu].</summary>
    public Complex LoadAt(int i, double vm) => LoadZ[i] * (vm * vm) + LoadI[i] * vm + LoadP[i];

    /// <summary>d(load)/d|V| at bus i.</summary>
    public Complex LoadDerivative(int i, double vm) => LoadZ[i] * (2 * vm) + LoadI[i];

    public Complex Scheduled(int i, double vm) => SGen[i] - LoadAt(i, vm);

    public int[] Indices(params BusType[] types) => Enumerable.Range(0, N).Where(i => types.Contains(Types[i])).ToArray();

    public PowerFlowResult BuildResult(PowerFlowMethod method, Complex[] v, bool converged, int iterations, double mismatch,
        TimeSpan elapsed, IReadOnlyList<double> history, HashSet<int>? qLimited = null)
    {
        double baseMva = Net.BaseMva;
        var sInj = Adm.Injections(v);
        var buses = new BusResult[N];
        var gens = new List<GeneratorResult>();
        var genP = new double[Net.Generators.Count];
        var genQ = new double[Net.Generators.Count];
        for (int i = 0; i < N; i++)
        {
            double vm = v[i].Magnitude;
            var load = LoadAt(i, vm) * baseMva;
            var sg = sInj[i] * baseMva + load;
            var list = GensAt[i];
            if (list.Count > 0)
            {
                bool computeP = Types[i] == BusType.Slack;
                double fixedP = list.Sum(k => Net.Generators[k].P);
                double pmaxSum = list.Sum(k => Math.Max(Net.Generators[k].Pmax, 1e-9));
                double qRange = list.Sum(k => Math.Max(Net.Generators[k].Qmax - Net.Generators[k].Qmin, 1e-9));
                foreach (int k in list)
                {
                    var g = Net.Generators[k];
                    genP[k] = computeP ? (list.Count == 1 ? sg.Real : sg.Real * Math.Max(g.Pmax, 1e-9) / pmaxSum) : g.P;
                    genQ[k] = Types[i] == BusType.PQ && !(qLimited?.Contains(i) ?? false)
                        ? g.Q
                        : (list.Count == 1 ? sg.Imaginary : sg.Imaginary * Math.Max(g.Qmax - g.Qmin, 1e-9) / qRange);
                }
                if (!computeP) sg = new Complex(fixedP, sg.Imaginary);
            }
            buses[i] = new BusResult(Net.Buses[i].Id, Net.Buses[i].Name, vm, v[i].Phase * Scalar.Rad2Deg,
                list.Count > 0 ? sg.Real : 0, list.Count > 0 ? sg.Imaginary : 0, load.Real, load.Imaginary, Types[i]);
        }
        for (int k = 0; k < Net.Generators.Count; k++)
        {
            var g = Net.Generators[k];
            if (!g.InService) continue;
            int i = Net.IndexOf(g.Bus);
            bool atLimit = qLimited?.Contains(i) ?? false;
            gens.Add(new GeneratorResult(k, g.Bus, g.Name, genP[k], genQ[k], atLimit));
        }
        var branches = new List<BranchResult>(Net.Branches.Count);
        for (int k = 0; k < Net.Branches.Count; k++)
        {
            var br = Net.Branches[k];
            if (!br.InService) { branches.Add(new BranchResult(k, br.From, br.To, br.Name, 0, 0, 0, 0, br.RateA)); continue; }
            int f = Net.IndexOf(br.From), t = Net.IndexOf(br.To);
            var a = Adm.Branches[k];
            var iF = a.Yff * v[f] + a.Yft * v[t];
            var iT = a.Ytf * v[f] + a.Ytt * v[t];
            var sF = v[f] * Complex.Conjugate(iF) * baseMva;
            var sT = v[t] * Complex.Conjugate(iT) * baseMva;
            branches.Add(new BranchResult(k, br.From, br.To, br.Name, sF.Real, sF.Imaginary, sT.Real, sT.Imaginary, br.RateA));
        }
        return new PowerFlowResult
        {
            Network = Net, Method = method, Converged = converged, Iterations = iterations, MaxMismatch = mismatch, Voltages = v,
            Buses = buses, Branches = branches, Generators = gens, Elapsed = elapsed, History = history,
        };
    }
}
