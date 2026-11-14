using System.Diagnostics;
using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;

namespace Electrical.Optimization;

/// <summary>Initial point of <see cref="AcOpf"/>.</summary>
public enum AcOpfStart
{
    /// <summary>MATPOWER-style: all angles at the reference angle, every bounded variable at the middle of its range.</summary>
    Flat,
    /// <summary>Warm start from a Newton–Raphson power flow of the network (falls back to <see cref="Flat"/> if it does not converge).</summary>
    PowerFlow,
    /// <summary>Use the voltages and generator outputs currently stored in the network.</summary>
    Network,
}

/// <summary>Options of <see cref="AcOpf"/>.</summary>
public sealed record AcOpfOptions
{
    /// <summary>Termination tolerance applied to the feasibility, gradient, complementarity and cost conditions of the interior-point method.</summary>
    public double Tolerance { get; init; } = 1e-6;
    public int MaxIterations { get; init; } = 150;
    public AcOpfStart Start { get; init; } = AcOpfStart.Flat;
    /// <summary>Enforce branch apparent-power limits (RateA; 0 = unlimited).</summary>
    public bool EnforceLineLimits { get; init; } = true;
    /// <summary>Multiplier applied to RateA (and to overrides).</summary>
    public double RatingFactor { get; init; } = 1.0;
    /// <summary>Overrides RateA per branch index [MVA].</summary>
    public IReadOnlyDictionary<int, double>? RatingOverrides { get; init; }
    /// <summary>Enable the interior-point step-length control (slower, more robust on hard cases).</summary>
    public bool StepControl { get; init; }
    /// <summary>Internal objective scaling of the interior-point method (MATPOWER uses 10⁻⁴).</summary>
    public double CostScaling { get; init; } = 1e-4;
    /// <summary>Shadow price above which a constraint is reported as binding [$/MWh, $/MVAh or $/h per pu].</summary>
    public double BindingThreshold { get; init; } = 1e-4;

    public static AcOpfOptions Default { get; } = new();
}

/// <summary>
/// AC optimal power flow in polar coordinates (MATPOWER <c>runopf</c> formulation): minimizes Σ C_i(Pg_i) [$/h] subject to the full AC
/// power balance at every bus, bus voltage limits, generator P/Q limits and branch apparent-power limits |S| ≤ RateA at both ends
/// (squared form). Solved by the primal-dual <see cref="InteriorPointSolver"/> with exact Hessians. LMPs are the active-power balance
/// multipliers. Loads are treated as constant power.
/// </summary>
public static class AcOpf
{
    public static AcOpfResult Solve(PowerNetwork net, AcOpfOptions? options = null)
    {
        options ??= AcOpfOptions.Default;
        var sw = Stopwatch.StartNew();
        var model = new AcOpfModel(net, options);
        int nx = model.Nx;
        var lo = new double[nx]; var up = new double[nx];
        model.Bounds(lo, up);
        var x0 = InitialPoint(model, lo, up, options.Start);
        var nlp = new NonlinearProgram
        {
            VariableCount = nx, EqualityCount = model.EqualityCount, InequalityCount = model.InequalityCount,
            Lower = lo, Upper = up,
            Objective = model.Objective, Constraints = model.Constraints, Jacobians = model.Jacobians, Hessian = model.Hessian,
        };
        var ipm = new InteriorPointOptions
        {
            FeasibilityTolerance = options.Tolerance, GradientTolerance = options.Tolerance, ComplementarityTolerance = options.Tolerance,
            CostTolerance = options.Tolerance, MaxIterations = options.MaxIterations, StepControl = options.StepControl,
            CostMultiplier = options.CostScaling,
        };
        var res = InteriorPointSolver.Solve(nlp, x0, ipm);
        return AcOpfResult.Build(model, res, options, sw.Elapsed);
    }

    private static double[] InitialPoint(AcOpfModel m, double[] lo, double[] up, AcOpfStart start)
    {
        var x = new double[m.Nx];
        for (int i = 0; i < m.Nx; i++)
        {
            double l = double.IsNegativeInfinity(lo[i]) ? -1e10 : lo[i], u = double.IsPositiveInfinity(up[i]) ? 1e10 : up[i];
            x[i] = (l + u) / 2;
        }
        double refAngle = lo[m.IVa + m.Ref];
        for (int i = 0; i < m.Nb; i++) x[m.IVa + i] = refAngle;
        if (start == AcOpfStart.Flat) return x;

        var net = m.Net;
        var vm = new double[m.Nb]; var va = new double[m.Nb];
        var pg = new double[net.Generators.Count]; var qg = new double[net.Generators.Count];
        if (start == AcOpfStart.PowerFlow)
        {
            PowerFlowResult pf;
            try { pf = PowerFlowSolver.Solve(net, new PowerFlowOptions { MaxIterations = 30 }); }
            catch (InvalidOperationException) { return x; }
            if (!pf.Converged) return x;
            for (int i = 0; i < m.Nb; i++) { vm[i] = pf.Buses[i].Vm; va[i] = pf.Buses[i].VaDeg * Scalar.Deg2Rad; }
            foreach (var g in pf.Generators) { pg[g.Index] = g.P; qg[g.Index] = g.Q; }
        }
        else
        {
            for (int i = 0; i < m.Nb; i++) { vm[i] = net.Buses[i].Vm; va[i] = net.Buses[i].VaDeg * Scalar.Deg2Rad; }
            for (int k = 0; k < net.Generators.Count; k++) { pg[k] = net.Generators[k].P; qg[k] = net.Generators[k].Q; }
        }
        double shift = refAngle - va[m.Ref];
        for (int i = 0; i < m.Nb; i++)
        {
            x[m.IVa + i] = va[i] + shift;
            x[m.IVm + i] = Math.Clamp(vm[i], lo[m.IVm + i], up[m.IVm + i]);
        }
        for (int k = 0; k < m.Ng; k++)
        {
            int gk = m.GenIndex[k];
            x[m.IPg + k] = Math.Clamp(pg[gk] / m.BaseMva, lo[m.IPg + k], up[m.IPg + k]);
            x[m.IQg + k] = Math.Clamp(qg[gk] / m.BaseMva, lo[m.IQg + k], up[m.IQg + k]);
        }
        return x;
    }
}

/// <summary>DI facade for AC optimal power flow (registered by <c>AddOptimization()</c>).</summary>
public interface IAcOpfService
{
    AcOpfResult Solve(PowerNetwork net, AcOpfOptions? options = null);
    LmpComponents Decompose(AcOpfResult result, int? referenceBusId = null);
    LmpComponents Decompose(DcOpfResult result);
}

internal sealed class AcOpfService : IAcOpfService
{
    public AcOpfResult Solve(PowerNetwork net, AcOpfOptions? options = null) => AcOpf.Solve(net, options);
    public LmpComponents Decompose(AcOpfResult result, int? referenceBusId = null) => LmpDecomposition.Decompose(result, referenceBusId);
    public LmpComponents Decompose(DcOpfResult result) => LmpDecomposition.Decompose(result);
}

/// <summary>AC-OPF solution. Powers in MW / Mvar, voltages in pu / degrees, prices in $/MWh ($/Mvarh), cost in $/h.</summary>
public sealed class AcOpfResult
{
    internal AcOpfModel Model { get; private init; } = null!;

    public required PowerNetwork Network { get; init; }
    public required bool Converged { get; init; }
    public required int Iterations { get; init; }
    public required InteriorPointStatus Status { get; init; }
    /// <summary>Total generation cost Σ C_i(Pg_i) [$/h] (including constant terms).</summary>
    public required double TotalCost { get; init; }
    /// <summary>Bus ids aligned with the per-bus arrays.</summary>
    public required int[] BusIds { get; init; }
    /// <summary>Voltage magnitudes [pu] per bus position.</summary>
    public required double[] Vm { get; init; }
    /// <summary>Voltage angles [degrees] per bus position.</summary>
    public required double[] VaDeg { get; init; }
    /// <summary>Active power output [MW] aligned with <see cref="PowerNetwork.Generators"/> (0 for out-of-service units).</summary>
    public required double[] PgMw { get; init; }
    /// <summary>Reactive power output [Mvar] aligned with <see cref="PowerNetwork.Generators"/>.</summary>
    public required double[] QgMvar { get; init; }
    /// <summary>Locational marginal price of active power per bus position [$/MWh] (λ_P of the bus power balance).</summary>
    public required double[] Lmp { get; init; }
    /// <summary>Marginal price of reactive power per bus position [$/Mvarh] (λ_Q).</summary>
    public required double[] LmpQ { get; init; }
    /// <summary>Branch flows (MW/Mvar at both ends) aligned with <see cref="PowerNetwork.Branches"/>; RateA holds the enforced rating [MVA].</summary>
    public required IReadOnlyList<BranchResult> Branches { get; init; }
    /// <summary>Shadow price of the from-end MVA limit per branch [$/MVAh] (0 when unlimited or not binding).</summary>
    public required double[] BranchShadowPriceFrom { get; init; }
    /// <summary>Shadow price of the to-end MVA limit per branch [$/MVAh].</summary>
    public required double[] BranchShadowPriceTo { get; init; }
    /// <summary>Shadow price of the upper voltage limit per bus position [$/h per pu].</summary>
    public required double[] VmaxShadowPrice { get; init; }
    /// <summary>Shadow price of the lower voltage limit per bus position [$/h per pu].</summary>
    public required double[] VminShadowPrice { get; init; }
    /// <summary>Shadow prices of generator limits [$/MWh] / [$/Mvarh] aligned with <see cref="PowerNetwork.Generators"/>.</summary>
    public required double[] PmaxShadowPrice { get; init; }
    public required double[] PminShadowPrice { get; init; }
    public required double[] QmaxShadowPrice { get; init; }
    public required double[] QminShadowPrice { get; init; }
    /// <summary>Binding constraints: "Flow" (Description says which end), "Vmax", "Vmin", "Pmax", "Pmin", "Qmax", "Qmin".</summary>
    public required IReadOnlyList<BindingConstraint> Binding { get; init; }
    /// <summary>Raw interior-point solution (pu variables, multipliers in $/h per pu).</summary>
    public required InteriorPointResult Solver { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>Total active losses [MW].</summary>
    public double LossesMw => Branches.Sum(b => b.PLoss) + BusShuntLossMw;
    /// <summary>Active power absorbed by bus shunt conductances [MW].</summary>
    public double BusShuntLossMw { get; init; }
    /// <summary>Largest power-balance mismatch at the solution [MW or Mvar].</summary>
    public double MaxMismatchMw { get; init; }

    public double LmpAt(int busId) => Lmp[Array.IndexOf(BusIds, busId)];
    public double VmAt(int busId) => Vm[Array.IndexOf(BusIds, busId)];

    /// <summary>Decomposes the LMPs into energy, loss and congestion components (see <see cref="LmpDecomposition"/>).</summary>
    public LmpComponents Decompose(int? referenceBusId = null) => LmpDecomposition.Decompose(this, referenceBusId);

    /// <summary>
    /// Converts the operating point to a <see cref="PowerFlowResult"/> (for visualization, reports and warm starts). <c>Method</c> is reported as
    /// Newton–Raphson because the result is a full AC solution.
    /// </summary>
    public PowerFlowResult ToPowerFlowResult()
    {
        var net = Network;
        int nb = BusIds.Length;
        var v = new Complex[nb];
        for (int i = 0; i < nb; i++) v[i] = Complex.FromPolarCoordinates(Vm[i], VaDeg[i] * Scalar.Deg2Rad);
        var pgBus = new double[nb]; var qgBus = new double[nb]; var pl = new double[nb]; var ql = new double[nb];
        var gens = new List<GeneratorResult>();
        for (int k = 0; k < net.Generators.Count; k++)
        {
            var g = net.Generators[k];
            if (!g.InService) continue;
            int i = net.IndexOf(g.Bus);
            pgBus[i] += PgMw[k]; qgBus[i] += QgMvar[k];
            bool atQ = QmaxShadowPrice[k] > 0 || QminShadowPrice[k] > 0;
            gens.Add(new GeneratorResult(k, g.Bus, g.Name, PgMw[k], QgMvar[k], atQ));
        }
        foreach (var l in net.Loads) if (l.InService) { int i = net.IndexOf(l.Bus); pl[i] += l.P; ql[i] += l.Q; }
        var buses = new BusResult[nb];
        bool[] hasGen = new bool[nb];
        foreach (var g in gens) hasGen[net.IndexOf(g.Bus)] = true;
        for (int i = 0; i < nb; i++)
        {
            var b = net.Buses[i];
            buses[i] = new BusResult(b.Id, b.Name, Vm[i], VaDeg[i], hasGen[i] ? pgBus[i] : 0, hasGen[i] ? qgBus[i] : 0, pl[i], ql[i], b.Type);
        }
        return new PowerFlowResult
        {
            Network = net, Method = PowerFlowMethod.NewtonRaphson, Converged = Converged, Iterations = Iterations, MaxMismatch = MaxMismatchMw / net.BaseMva,
            Voltages = v, Buses = buses, Branches = Branches, Generators = gens, Elapsed = Elapsed,
            History = Solver.History.Select(h => h.Feasibility).ToArray(),
        };
    }

    public override string ToString() =>
        $"AC-OPF: {(Converged ? "converged" : Status.ToString())} in {Iterations} it, cost {TotalCost:0.##} $/h, losses {LossesMw:0.###} MW, " +
        $"LMP {Lmp.Min():0.##}–{Lmp.Max():0.##} $/MWh";

    internal static AcOpfResult Build(AcOpfModel m, InteriorPointResult r, AcOpfOptions o, TimeSpan elapsed)
    {
        var net = m.Net;
        int nb = m.Nb, ng = net.Generators.Count, nl = m.Limited.Length;
        double bmva = m.BaseMva, thr = o.BindingThreshold;
        var x = r.X;
        var v = m.Voltages(x);
        var vm = new double[nb]; var va = new double[nb];
        for (int i = 0; i < nb; i++) { vm[i] = x[m.IVm + i]; va[i] = x[m.IVa + i] * Scalar.Rad2Deg; }
        var pg = new double[ng]; var qg = new double[ng];
        var pmaxS = new double[ng]; var pminS = new double[ng]; var qmaxS = new double[ng]; var qminS = new double[ng];
        var binding = new List<BindingConstraint>();
        for (int k = 0; k < m.Ng; k++)
        {
            int gk = m.GenIndex[k];
            pg[gk] = x[m.IPg + k] * bmva; qg[gk] = x[m.IQg + k] * bmva;
            pmaxS[gk] = r.MuUpper[m.IPg + k] / bmva; pminS[gk] = r.MuLower[m.IPg + k] / bmva;
            qmaxS[gk] = r.MuUpper[m.IQg + k] / bmva; qminS[gk] = r.MuLower[m.IQg + k] / bmva;
            string name = net.Generators[gk].Name ?? $"G@{net.Generators[gk].Bus}";
            if (pmaxS[gk] > thr) binding.Add(new("Pmax", $"{name} at Pmax", pmaxS[gk], gk));
            if (pminS[gk] > thr) binding.Add(new("Pmin", $"{name} at Pmin", pminS[gk], gk));
            if (qmaxS[gk] > thr) binding.Add(new("Qmax", $"{name} at Qmax", qmaxS[gk], gk));
            if (qminS[gk] > thr) binding.Add(new("Qmin", $"{name} at Qmin", qminS[gk], gk));
        }
        var lmp = new double[nb]; var lmpQ = new double[nb];
        var vmaxS = new double[nb]; var vminS = new double[nb];
        for (int i = 0; i < nb; i++)
        {
            lmp[i] = r.Lambda[i] / bmva; lmpQ[i] = r.Lambda[nb + i] / bmva;
            vmaxS[i] = r.MuUpper[m.IVm + i]; vminS[i] = r.MuLower[m.IVm + i];
            int id = net.Buses[i].Id;
            if (vmaxS[i] > thr) binding.Add(new("Vmax", $"Bus {id} at Vmax", vmaxS[i], i));
            if (vminS[i] > thr) binding.Add(new("Vmin", $"Bus {id} at Vmin", vminS[i], i));
        }
        var shF = new double[net.Branches.Count]; var shT = new double[net.Branches.Count];
        var rating = net.Branches.Select(b => o.EnforceLineLimits ? 0.0 : b.RateA).ToArray();
        for (int l = 0; l < nl; l++)
        {
            var b = m.Limited[l];
            rating[b.Index] = b.RatingMva;
            // h = |S|²/base² − rate²/base²  ⇒  ∂h/∂|S|[MVA] = 2·rate/base² at the limit
            double conv = 2 * b.RatingMva / (bmva * bmva);
            shF[b.Index] = r.Mu[l] * conv; shT[b.Index] = r.Mu[nl + l] * conv;
            var br = net.Branches[b.Index];
            if (shF[b.Index] > thr) binding.Add(new("Flow", $"Branch {br.From}-{br.To} (from end)", shF[b.Index], b.Index));
            if (shT[b.Index] > thr) binding.Add(new("Flow", $"Branch {br.From}-{br.To} (to end)", shT[b.Index], b.Index));
        }
        var branches = new List<BranchResult>(net.Branches.Count);
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            if (!br.InService) { branches.Add(new BranchResult(k, br.From, br.To, br.Name, 0, 0, 0, 0, rating[k])); continue; }
            var lb = new AcOpfModel.LimitedBranch(k, net.IndexOf(br.From), net.IndexOf(br.To), m.Adm.Branches[k], rating[k]);
            var (sf, st) = AcOpfModel.BranchFlows(lb, v);
            sf *= bmva; st *= bmva;
            branches.Add(new BranchResult(k, br.From, br.To, br.Name, sf.Real, sf.Imaginary, st.Real, st.Imaginary, rating[k]));
        }
        double shunt = 0;
        for (int i = 0; i < nb; i++) shunt += net.Buses[i].Gs * vm[i] * vm[i];
        var g = new double[m.EqualityCount]; var h = new double[m.InequalityCount];
        m.Constraints(x, g, h);
        double cost = 0;
        foreach (int gk in m.GenIndex) cost += net.Generators[gk].Cost.Evaluate(pg[gk]);
        return new AcOpfResult
        {
            Model = m, Network = net, Converged = r.Converged, Iterations = r.Iterations, Status = r.Status, TotalCost = cost,
            BusIds = net.Buses.Select(b => b.Id).ToArray(), Vm = vm, VaDeg = va, PgMw = pg, QgMvar = qg, Lmp = lmp, LmpQ = lmpQ,
            Branches = branches, BranchShadowPriceFrom = shF, BranchShadowPriceTo = shT, VmaxShadowPrice = vmaxS, VminShadowPrice = vminS,
            PmaxShadowPrice = pmaxS, PminShadowPrice = pminS, QmaxShadowPrice = qmaxS, QminShadowPrice = qminS, Binding = binding,
            Solver = r, Elapsed = elapsed, BusShuntLossMw = shunt, MaxMismatchMw = g.Length == 0 ? 0 : g.Max(Math.Abs) * bmva,
        };
    }
}
