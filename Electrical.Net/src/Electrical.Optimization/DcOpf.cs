using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;

namespace Electrical.Optimization;

/// <summary>Options for <see cref="DcOpf"/>.</summary>
public sealed record DcOpfOptions
{
    /// <summary>Multiplier applied to RateA for base-case limits.</summary>
    public double RatingFactor { get; init; } = 1.0;
    /// <summary>Multiplier applied to RateA for post-contingency limits (short-term emergency rating).</summary>
    public double EmergencyRatingFactor { get; init; } = 1.0;
    /// <summary>Branch indices whose outage is secured (N-1 via LODF). Empty = plain DC-OPF.</summary>
    public IReadOnlyList<int> Contingencies { get; init; } = [];
    /// <summary>Overrides RateA per branch index [MW] (useful for congestion studies without editing the network).</summary>
    public IReadOnlyDictionary<int, double>? RatingOverrides { get; init; }
    public double Tolerance { get; init; } = 1e-8;
    /// <summary>Multiplier above which a constraint is reported as binding [$/MWh].</summary>
    public double BindingThreshold { get; init; } = 1e-4;

    public static DcOpfOptions Default { get; } = new();
}

/// <summary>A binding (active) constraint with its shadow price.</summary>
/// <param name="Kind">"Flow", "ContingencyFlow", "Pmax" or "Pmin".</param>
/// <param name="Description">Human-readable name, e.g. "Branch 5-6 (from→to)".</param>
/// <param name="ShadowPrice">Marginal cost reduction per MW of relaxation [$/MWh].</param>
/// <param name="Index">Branch or generator index.</param>
/// <param name="ContingencyIndex">Outaged branch index for "ContingencyFlow" (−1 otherwise).</param>
public sealed record BindingConstraint(string Kind, string Description, double ShadowPrice, int Index, int ContingencyIndex = -1);

/// <summary>DC optimal power flow result. Powers in MW, prices in $/MWh, cost in $/h.</summary>
public sealed class DcOpfResult
{
    public required bool Converged { get; init; }
    public required int Iterations { get; init; }
    /// <summary>Total generation cost Σ C_i(P_i) [$/h] (including constant terms).</summary>
    public required double TotalCost { get; init; }
    /// <summary>Generator outputs [MW], aligned with <see cref="PowerNetwork.Generators"/> (0 for out-of-service units).</summary>
    public required double[] DispatchMw { get; init; }
    /// <summary>Energy component λ of the LMP (dual of the power balance) [$/MWh].</summary>
    public required double Lambda { get; init; }
    /// <summary>Locational marginal price per bus position [$/MWh]: LMP_i = λ − Σ_c μ_c·PTDF_c[i].</summary>
    public required double[] Lmp { get; init; }
    /// <summary>Base-case branch flows [MW] (from → to positive).</summary>
    public required double[] FlowsMw { get; init; }
    public required IReadOnlyList<BindingConstraint> Binding { get; init; }
    /// <summary>Bus ids aligned with <see cref="Lmp"/>.</summary>
    public required int[] BusIds { get; init; }

    public double LmpAt(int busId) => Lmp[Array.IndexOf(BusIds, busId)];

    /// <summary>Congestion rent Σ_i LMP_i·(load_i − gen_i) [$/h] (merchandising surplus).</summary>
    public double CongestionRent { get; init; }
}

/// <summary>
/// DC optimal power flow (PTDF formulation) with quadratic generator costs, generator limits, branch limits and optional N-1 security
/// constraints (post-contingency flows f_l + LODF[l,k]·f_k within the emergency rating), solved with <see cref="QpSolver"/>.
/// LMPs follow from the duals: LMP_i = λ − Σ_c μ_c·a_c[i] where a_c is the PTDF row of constraint c (sign included).
/// </summary>
public static class DcOpf
{
    public static DcOpfResult Solve(PowerNetwork net, DcOpfOptions? options = null)
    {
        options ??= DcOpfOptions.Default;
        int nb = net.BusCount;
        var gens = net.Generators.Select((g, k) => (g, k)).Where(t => t.g.InService).ToList();
        int n = gens.Count;
        var ptdf = DcPowerFlow.Ptdf(net);
        var pd = new double[nb];
        foreach (var l in net.Loads) if (l.InService) pd[net.IndexOf(l.Bus)] += l.P;
        double demand = pd.Sum();

        var qp = new QuadraticProgram(n);
        for (int j = 0; j < n; j++)
        {
            qp.Q[j, j] = 2 * gens[j].g.Cost.C2;
            qp.C[j] = gens[j].g.Cost.C1;
        }
        qp.AddEquality(Enumerable.Repeat(1.0, n).ToArray(), demand);

        // flow constraints: rows a_c over buses (injection sensitivities) with sign
        var rows = new List<(double[] BusRow, string Kind, string Desc, int Index, int Cont)>();
        double Rating(int l) => options.RatingOverrides is not null && options.RatingOverrides.TryGetValue(l, out var r) ? r : net.Branches[l].RateA;
        void AddFlow(double[] busRow, double limit, string kind, string desc, int index, int cont)
        {
            foreach (int sign in new[] { 1, -1 })
            {
                var g = new double[n];
                for (int j = 0; j < n; j++) g[j] = sign * busRow[net.IndexOf(gens[j].g.Bus)];
                double fixedPart = 0;
                for (int i = 0; i < nb; i++) fixedPart += busRow[i] * pd[i];
                qp.AddInequality(g, limit + sign * fixedPart);
                rows.Add((busRow.Select(v => sign * v).ToArray(), kind, $"{desc} ({(sign > 0 ? "from→to" : "to→from")})", index, cont));
            }
        }
        double[] PtdfRow(int l) { var r = new double[nb]; for (int i = 0; i < nb; i++) r[i] = ptdf[l, i]; return r; }

        for (int l = 0; l < net.Branches.Count; l++)
        {
            var br = net.Branches[l];
            double rate = Rating(l);
            if (!br.InService || rate <= 0) continue;
            AddFlow(PtdfRow(l), rate * options.RatingFactor, "Flow", $"Branch {br.From}-{br.To}", l, -1);
        }
        if (options.Contingencies.Count > 0)
        {
            var lodf = DcPowerFlow.Lodf(net, ptdf);
            foreach (int k in options.Contingencies)
            {
                if (!net.Branches[k].InService) continue;
                for (int l = 0; l < net.Branches.Count; l++)
                {
                    var br = net.Branches[l];
                    double rate = Rating(l);
                    if (l == k || !br.InService || rate <= 0 || double.IsNaN(lodf[l, k])) continue;
                    var row = new double[nb];
                    for (int i = 0; i < nb; i++) row[i] = ptdf[l, i] + lodf[l, k] * ptdf[k, i];
                    var bk = net.Branches[k];
                    AddFlow(row, rate * options.EmergencyRatingFactor, "ContingencyFlow", $"Branch {br.From}-{br.To} after outage {bk.From}-{bk.To}", l, k);
                }
            }
        }
        int flowCount = qp.Inequalities.Count;
        var boundIdx = new List<(int Upper, int Lower)>();
        for (int j = 0; j < n; j++) boundIdx.Add(qp.AddBounds(j, gens[j].g.Pmin, gens[j].g.Pmax));

        // Start from a Pmax-proportional share of the demand (meets the balance row, inside the generator limits when feasible).
        double capacity = gens.Sum(t => Math.Max(t.g.Pmax - t.g.Pmin, 0));
        var x0 = gens.Select(t => t.g.Pmin + (capacity > 0 ? Math.Max(t.g.Pmax - t.g.Pmin, 0) / capacity * Math.Max(demand - gens.Sum(u => u.g.Pmin), 0) : 0)).ToArray();
        var res = QpSolver.Solve(qp, options.Tolerance, initialX: x0);
        double lambda = res.EqualityDuals[0];
        var lmp = new double[nb];
        for (int i = 0; i < nb; i++)
        {
            double s = lambda;
            for (int c = 0; c < flowCount; c++) s -= res.InequalityDuals[c] * rows[c].BusRow[i];
            lmp[i] = s;
        }
        var binding = new List<BindingConstraint>();
        for (int c = 0; c < flowCount; c++)
            if (res.InequalityDuals[c] > options.BindingThreshold)
                binding.Add(new BindingConstraint(rows[c].Kind, rows[c].Desc, res.InequalityDuals[c], rows[c].Index, rows[c].Cont));
        for (int j = 0; j < n; j++)
        {
            var (u, lo) = boundIdx[j];
            string name = gens[j].g.Name ?? $"G@{gens[j].g.Bus}";
            if (res.InequalityDuals[u] > options.BindingThreshold) binding.Add(new("Pmax", $"{name} at Pmax", res.InequalityDuals[u], gens[j].k));
            if (res.InequalityDuals[lo] > options.BindingThreshold) binding.Add(new("Pmin", $"{name} at Pmin", res.InequalityDuals[lo], gens[j].k));
        }
        var dispatch = new double[net.Generators.Count];
        double cost = 0;
        var inj = (double[])pd.Clone();
        for (int i = 0; i < nb; i++) inj[i] = -pd[i];
        for (int j = 0; j < n; j++)
        {
            dispatch[gens[j].k] = res.X[j];
            cost += gens[j].g.Cost.Evaluate(res.X[j]);
            inj[net.IndexOf(gens[j].g.Bus)] += res.X[j];
        }
        var flows = new double[net.Branches.Count];
        for (int l = 0; l < flows.Length; l++)
        {
            double f = 0;
            for (int i = 0; i < nb; i++) f += ptdf[l, i] * inj[i];
            flows[l] = f;
        }
        double rent = 0;
        for (int i = 0; i < nb; i++) rent -= lmp[i] * inj[i];
        return new DcOpfResult
        {
            Converged = res.Converged, Iterations = res.Iterations, TotalCost = cost, DispatchMw = dispatch, Lambda = lambda, Lmp = lmp,
            FlowsMw = flows, Binding = binding, BusIds = net.Buses.Select(b => b.Id).ToArray(), CongestionRent = rent,
        };
    }

    /// <summary>Security-constrained DC-OPF securing every non-islanding branch outage (or the given subset).</summary>
    public static DcOpfResult SolveSecurityConstrained(PowerNetwork net, IReadOnlyList<int>? contingencies = null, double emergencyRatingFactor = 1.0)
    {
        if (contingencies is null)
        {
            var lodf = DcPowerFlow.Lodf(net);
            contingencies = Enumerable.Range(0, net.Branches.Count)
                .Where(k => net.Branches[k].InService && Enumerable.Range(0, net.Branches.Count).All(l => l == k || !double.IsNaN(lodf[l, k]))).ToList();
        }
        return Solve(net, new DcOpfOptions { Contingencies = contingencies, EmergencyRatingFactor = emergencyRatingFactor });
    }
}
