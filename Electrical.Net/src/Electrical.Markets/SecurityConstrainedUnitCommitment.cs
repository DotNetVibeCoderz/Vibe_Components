using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.Optimization;
using Electrical.PowerFlow;

namespace Electrical.Markets;

/// <summary>Price-responsive demand block for the SCUC / market model.</summary>
internal readonly record struct ElasticBlock(int Period, int BusPos, double QuantityMw, double Price);

/// <summary>
/// Security-constrained unit commitment as a MILP over T periods (three-binary formulation, Carrión &amp; Arroyo 2006 / Morales-España et al. 2013):
/// <list type="bullet">
/// <item>binary commitment u, startup v and shutdown w (v, w continuous in [0, 1]; integral through u_t − u_{t−1} = v_t − w_t and the
/// turn-on/turn-off inequalities Σ_{τ=t−UT+1..t} v_τ ≤ u_t, Σ_{τ=t−DT+1..t} w_τ ≤ 1 − u_t of Rajan &amp; Takriti);</item>
/// <item>generation cost: piecewise linear (offer blocks, or chords of the quadratic cost), no-load cost per committed period and startup cost;</item>
/// <item>Pmin·u ≤ P ≤ Pmax·u, ramp limits P_t − P_{t−1} ≤ RU·u_{t−1} + SU·v_t and P_{t−1} − P_t ≤ RD·u_t + SD·w_t, initial status;</item>
/// <item>power balance per period, spinning reserve Σ(Pmax·u − P) ≥ f·D + R;</item>
/// <item>DC branch limits through PTDFs and, optionally, N-1 post-contingency limits through LODFs, added lazily (only violated rows,
/// re-solving until the dispatch is secure) or upfront.</item>
/// </list>
/// The MILP is solved with any <see cref="IMilpSolver"/> (built-in branch and bound by default). Hourly LMPs are the duals of a second, linear
/// solve with the commitment fixed (the usual "restricted pricing" of ISO markets): LMP_i,t = λ_t + f·ρ_t − Σ_c μ_c·PTDF_c,i.
/// </summary>
public static class SecurityConstrainedUnitCommitment
{
    /// <summary>Unit commitment without a network (single bus). Demand [MW] per period.</summary>
    public static ScucResult Solve(IReadOnlyList<ScucUnit> units, IReadOnlyList<double> demandMw, ScucOptions? options = null)
    {
        var d = new double[1, demandMw.Count];
        for (int t = 0; t < demandMw.Count; t++) d[0, t] = demandMw[t];
        return SolveCore(null, units, d, [], options ?? ScucOptions.Default);
    }

    /// <summary>SCUC on a network with units built from its in-service generators (<see cref="ScucUnit.FromGenerator"/>); the system demand
    /// profile [MW per period] is distributed over the buses in proportion to the network's base-case loads.</summary>
    public static ScucResult Solve(PowerNetwork net, IReadOnlyList<double> demandMw, ScucOptions? options = null) =>
        Solve(net, UnitsOf(net), DistributeDemand(net, demandMw), options);

    /// <summary>SCUC on a network with per-bus demand [MW] indexed [bus position, period], units from the network's generators.</summary>
    public static ScucResult Solve(PowerNetwork net, double[,] busDemandMw, ScucOptions? options = null) =>
        Solve(net, UnitsOf(net), busDemandMw, options);

    /// <summary>SCUC with explicit units (<see cref="ScucUnit.Bus"/> = bus id) on a network (null = single bus) and per-bus demand [MW]
    /// indexed [bus position, period].</summary>
    public static ScucResult Solve(PowerNetwork? net, IReadOnlyList<ScucUnit> units, double[,] busDemandMw, ScucOptions? options = null) =>
        SolveCore(net, units, busDemandMw, [], options ?? ScucOptions.Default);

    /// <summary>Units from the in-service generators of a network.</summary>
    public static IReadOnlyList<ScucUnit> UnitsOf(PowerNetwork net) =>
        net.Generators.Select((g, k) => (g, k)).Where(t => t.g.InService).Select(t => ScucUnit.FromGenerator(t.g, t.k)).ToList();

    /// <summary>Spreads a system demand profile over the buses by their share of the base-case load. Result [bus position, period] in MW.</summary>
    public static double[,] DistributeDemand(PowerNetwork net, IReadOnlyList<double> demandMw)
    {
        var share = new double[net.BusCount];
        foreach (var l in net.Loads) if (l.InService) share[net.IndexOf(l.Bus)] += l.P;
        double total = share.Sum();
        if (total <= 0) throw new ArgumentException("The network has no load to distribute the demand profile over.", nameof(net));
        var d = new double[net.BusCount, demandMw.Count];
        for (int i = 0; i < net.BusCount; i++)
            for (int t = 0; t < demandMw.Count; t++) d[i, t] = demandMw[t] * share[i] / total;
        return d;
    }

    private sealed record FlowRow(ConstraintRef Row, int Branch, int Contingency, int Period, int Sign, double[] BusCoef, double Limit);

    internal static ScucResult SolveCore(PowerNetwork? net, IReadOnlyList<ScucUnit> units, double[,] busDemand, IReadOnlyList<ElasticBlock> elastic,
        ScucOptions o)
    {
        int G = units.Count, T = busDemand.GetLength(1), nb = busDemand.GetLength(0);
        if (net is not null && nb != net.BusCount) throw new ArgumentException("busDemand must have one row per network bus.", nameof(busDemand));
        if (net is null && nb != 1) throw new ArgumentException("Without a network busDemand must have a single row.", nameof(busDemand));
        double dt = o.PeriodHours;
        var unitBus = units.Select(u => net is null ? 0 : net.IndexOf(u.Bus)).ToArray();
        var demand = new double[T];
        for (int t = 0; t < T; t++) for (int i = 0; i < nb; i++) demand[t] += busDemand[i, t];

        var m = new LinearModel { Name = "SCUC" };
        var u = new Variable[G, T];
        var v = new Variable[G, T];
        var w = new Variable[G, T];
        var p = new Variable[G, T];
        var segs = new List<(Variable X, double Slope)>[G, T];
        var fixedCost = new double[G];
        var obj = new LinearExpression();
        for (int g = 0; g < G; g++)
        {
            var un = units[g];
            double pmax = un.EffectivePmax, pmin = Math.Min(un.PminMw, pmax);
            // cost model: fixed (per committed period) + baseMw carried by u + segments
            double baseMw;
            List<(double Width, double Slope)> segments;
            if (un.Blocks is { Count: > 0 } blocks)
            {
                baseMw = 0;
                fixedCost[g] = un.NoLoadCost;
                segments = blocks.Where(b => b.QuantityMw > 0).OrderBy(b => b.Price).Select(b => (b.QuantityMw, b.Price)).ToList();
                double cap = 0;
                for (int k = 0; k < segments.Count; k++)
                {
                    double wk = Math.Min(segments[k].Width, Math.Max(0, pmax - cap));
                    cap += segments[k].Width;
                    segments[k] = (wk, segments[k].Slope);
                }
            }
            else
            {
                baseMw = pmin;
                fixedCost[g] = un.Cost.Evaluate(pmin);
                segments = [];
                int K = Math.Max(1, o.CostSegments);
                double width = (pmax - pmin) / K;
                if (width > 1e-9)
                    for (int k = 0; k < K; k++)
                    {
                        double a = pmin + k * width, b = a + width;
                        segments.Add((width, (un.Cost.Evaluate(b) - un.Cost.Evaluate(a)) / width));
                    }
            }
            bool initOn = un.InitialHours > 0;
            int mustOn = initOn && un.InitialHours < un.MinUpHours ? un.MinUpHours - un.InitialHours : 0;
            int mustOff = !initOn && -un.InitialHours < un.MinDownHours ? un.MinDownHours + un.InitialHours : 0;
            double ru = un.RampUpMwPerHour * dt, rd = un.RampDownMwPerHour * dt;
            double su = double.IsNaN(un.StartupRampMw) ? Math.Max(pmin, ru) : un.StartupRampMw;
            double sd = double.IsNaN(un.ShutdownRampMw) ? Math.Max(pmin, rd) : un.ShutdownRampMw;
            for (int t = 0; t < T; t++)
            {
                double lo = 0, hi = 1;
                if (un.FixedCommitment is { } fc) lo = hi = fc[t] ? 1 : 0;
                else if (t < mustOn) lo = 1;
                else if (t < mustOff) hi = 0;
                u[g, t] = m.AddVariable(lo, hi, VariableType.Binary, $"u[{un.Name},{t}]");
                v[g, t] = m.AddContinuous(0, 1, $"v[{un.Name},{t}]");
                w[g, t] = m.AddContinuous(0, 1, $"w[{un.Name},{t}]");
                p[g, t] = m.AddContinuous(0, pmax, $"p[{un.Name},{t}]");
                obj.Add(u[g, t], dt * fixedCost[g]).Add(v[g, t], un.StartupCost);
                var list = new List<(Variable, double)>();
                var pdef = new LinearExpression().Add(p[g, t]).Add(u[g, t], -baseMw);
                for (int k = 0; k < segments.Count; k++)
                {
                    var s = m.AddContinuous(0, segments[k].Width, $"s[{un.Name},{t},{k}]");
                    list.Add((s, segments[k].Slope));
                    obj.Add(s, dt * segments[k].Slope);
                    pdef.Add(s, -1);
                }
                segs[g, t] = list;
                m.AddConstraint(pdef, ConstraintSense.Equal, 0, $"pdef[{un.Name},{t}]");
                m.AddConstraint(p[g, t] - pmax * u[g, t], ConstraintSense.LessOrEqual, 0, $"pmax[{un.Name},{t}]");
                if (baseMw < pmin - 1e-12) m.AddConstraint(p[g, t] - pmin * u[g, t], ConstraintSense.GreaterOrEqual, 0, $"pmin[{un.Name},{t}]");
                // logic
                var logic = new LinearExpression().Add(u[g, t]).Add(v[g, t], -1).Add(w[g, t]);
                if (t > 0) m.AddConstraint(logic.Add(u[g, t - 1], -1), ConstraintSense.Equal, 0, $"logic[{un.Name},{t}]");
                else m.AddConstraint(logic, ConstraintSense.Equal, initOn ? 1 : 0, $"logic[{un.Name},{t}]");
                // minimum up / down (turn-on / turn-off inequalities; always present so that v, w are exact)
                var up = new LinearExpression().Add(u[g, t], -1);
                for (int tau = Math.Max(0, t - Math.Max(1, un.MinUpHours) + 1); tau <= t; tau++) up.Add(v[g, tau]);
                m.AddConstraint(up, ConstraintSense.LessOrEqual, 0, $"minup[{un.Name},{t}]");
                var down = new LinearExpression().Add(u[g, t]);
                for (int tau = Math.Max(0, t - Math.Max(1, un.MinDownHours) + 1); tau <= t; tau++) down.Add(w[g, tau]);
                m.AddConstraint(down, ConstraintSense.LessOrEqual, 1, $"mindown[{un.Name},{t}]");
                // ramps
                bool prevKnown = t > 0 || !double.IsNaN(un.InitialOutputMw);
                if (prevKnown && !double.IsInfinity(ru))
                {
                    var e = new LinearExpression().Add(p[g, t]).Add(v[g, t], -su);
                    double rhs = 0;
                    if (t > 0) e.Add(p[g, t - 1], -1).Add(u[g, t - 1], -ru);
                    else rhs = un.InitialOutputMw + (initOn ? ru : 0);
                    m.AddConstraint(e, ConstraintSense.LessOrEqual, rhs, $"rampup[{un.Name},{t}]");
                }
                if (prevKnown && !double.IsInfinity(rd))
                {
                    var e = new LinearExpression().Add(p[g, t], -1).Add(u[g, t], -rd).Add(w[g, t], -sd);
                    double rhs = 0;
                    if (t > 0) e.Add(p[g, t - 1]);
                    else rhs = -un.InitialOutputMw;
                    m.AddConstraint(e, ConstraintSense.LessOrEqual, rhs, $"rampdown[{un.Name},{t}]");
                }
            }
        }
        var dvar = new Variable[elastic.Count];
        for (int e = 0; e < elastic.Count; e++)
        {
            dvar[e] = m.AddContinuous(0, elastic[e].QuantityMw, $"d[{e}]");
            obj.Add(dvar[e], -dt * elastic[e].Price);
        }
        var balance = new ConstraintRef[T];
        var reserve = new ConstraintRef?[T];
        for (int t = 0; t < T; t++)
        {
            var e = new LinearExpression();
            for (int g = 0; g < G; g++) e.Add(p[g, t]);
            for (int k = 0; k < elastic.Count; k++) if (elastic[k].Period == t) e.Add(dvar[k], -1);
            balance[t] = m.AddConstraint(e, ConstraintSense.Equal, demand[t], $"balance[{t}]");
            double req = o.ReserveFraction * demand[t] + o.ReserveMw;
            if (req > 0)
            {
                var r = new LinearExpression();
                for (int g = 0; g < G; g++) r.Add(u[g, t], units[g].EffectivePmax).Add(p[g, t], -1);
                reserve[t] = m.AddConstraint(r, ConstraintSense.GreaterOrEqual, req, $"reserve[{t}]");
            }
        }
        m.SetObjective(obj, ObjectiveSense.Minimize);

        // ---- network
        bool network = net is not null && o.EnforceNetwork && net.Branches.Count > 0;
        DenseMatrix<double>? ptdf = null, lodf = null;
        var monitored = new List<int>();
        var flowRows = new List<FlowRow>();
        var present = new HashSet<(int, int, int, int)>();
        if (network)
        {
            ptdf = DcPowerFlow.Ptdf(net!);
            for (int l = 0; l < net!.Branches.Count; l++) if (net.Branches[l].InService && o.Rating(net, l) > 0) monitored.Add(l);
            if (o.Contingencies.Count > 0) lodf = DcPowerFlow.Lodf(net, ptdf);
        }
        double[] RowOf(int l, int k)
        {
            var r = new double[nb];
            for (int i = 0; i < nb; i++) r[i] = ptdf![l, i] + (k >= 0 ? lodf![l, k] * ptdf[k, i] : 0);
            return r;
        }
        void AddFlowRow(int l, int k, int t, int sign)
        {
            if (!present.Add((l, k, t, sign))) return;
            var coef = RowOf(l, k);
            for (int i = 0; i < nb; i++) coef[i] *= sign;
            double limit = o.Rating(net!, l) * (k >= 0 ? o.EmergencyRatingFactor : o.RatingFactor);
            var e = new LinearExpression();
            for (int g = 0; g < G; g++) if (coef[unitBus[g]] != 0) e.Add(p[g, t], coef[unitBus[g]]);
            for (int x = 0; x < elastic.Count; x++)
                if (elastic[x].Period == t && coef[elastic[x].BusPos] != 0) e.Add(dvar[x], -coef[elastic[x].BusPos]);
            double rhs = limit;
            for (int i = 0; i < nb; i++) rhs += coef[i] * busDemand[i, t];
            var c = m.AddConstraint(e, ConstraintSense.LessOrEqual, rhs, k < 0 ? $"flow[{l},{t},{sign}]" : $"cflow[{l},{k},{t},{sign}]");
            flowRows.Add(new FlowRow(c, l, k, t, sign, coef, limit));
        }
        var contingencies = network ? o.Contingencies.Where(k => net!.Branches[k].InService).ToList() : [];
        bool ValidPair(int l, int k) => l != k && !double.IsNaN(lodf![l, k]);
        if (network && !o.LazyNetworkConstraints)
            for (int t = 0; t < T; t++)
                foreach (int l in monitored)
                {
                    AddFlowRow(l, -1, t, 1); AddFlowRow(l, -1, t, -1);
                    foreach (int k in contingencies) if (ValidPair(l, k)) { AddFlowRow(l, k, t, 1); AddFlowRow(l, k, t, -1); }
                }

        var milp = o.Solver ?? new BranchAndBoundSolver();
        LinearSolution sol;
        int rounds = 0;
        while (true)
        {
            rounds++;
            sol = milp.Solve(m, o.SolverOptions);
            if (!network || !sol.HasSolution || rounds >= o.MaxNetworkIterations) break;
            int added = 0;
            for (int t = 0; t < T; t++)
            {
                var inj = Injections(sol.Values, t);
                var f = new double[net!.Branches.Count];
                foreach (int l in monitored) f[l] = Dot(ptdf!, l, inj);
                foreach (int k in contingencies) if (f[k] == 0) f[k] = Dot(ptdf!, k, inj);
                foreach (int l in monitored)
                {
                    double lim = o.Rating(net, l) * o.RatingFactor;
                    if (Math.Abs(f[l]) > lim + 1e-6 * Math.Max(1, lim)) { AddFlowRow(l, -1, t, Math.Sign(f[l])); added++; }
                    double elim = o.Rating(net, l) * o.EmergencyRatingFactor;
                    foreach (int k in contingencies)
                    {
                        if (!ValidPair(l, k)) continue;
                        double fc = f[l] + lodf![l, k] * f[k];
                        if (Math.Abs(fc) > elim + 1e-6 * Math.Max(1, elim)) { AddFlowRow(l, k, t, Math.Sign(fc)); added++; }
                    }
                }
            }
            if (added == 0) break;
        }

        double[] Injections(double[] x, int t)
        {
            var inj = new double[nb];
            for (int i = 0; i < nb; i++) inj[i] = -busDemand[i, t];
            for (int g = 0; g < G; g++) inj[unitBus[g]] += x[p[g, t].Index];
            for (int k = 0; k < elastic.Count; k++) if (elastic[k].Period == t) inj[elastic[k].BusPos] -= x[dvar[k].Index];
            return inj;
        }

        // ---- results
        var commit = new bool[G, T];
        var start = new bool[G, T];
        var stop = new bool[G, T];
        var disp = new double[G, T];
        var lmp = new double[nb, T];
        var lambda = new double[T];
        var reserveMw = new double[T];
        int[] busIds = net is null ? [0] : net.Buses.Select(b => b.Id).ToArray();
        var comps = new DcLmpComponents[T];
        var flows = new double[network ? net!.Branches.Count : 0, T];
        var binding = new List<MarketConstraint>();
        var elasticMw = new double[elastic.Count];
        LinearSolution? pricing = null;
        double production = 0, startup = 0;
        for (int i = 0; i < nb; i++) for (int t = 0; t < T; t++) lmp[i, t] = double.NaN;
        Array.Fill(lambda, double.NaN);
        if (sol.HasSolution)
        {
            var x = sol.Values;
            if (o.ComputePrices)
            {
                var lpSolver = o.LpSolver ?? (milp as ILpSolver) ?? new SimplexSolver();
                var lpModel = m.FixIntegers(x);
                var lp = lpSolver.SolveLp(lpModel, o.SolverOptions);
                pricing = lp;
                if (lp.HasSolution && lp.Duals is not null)
                {
                    x = lp.Values;
                    var y = lp.Duals;
                    for (int t = 0; t < T; t++)
                    {
                        double energy = y[balance[t].Index] + (reserve[t] is { } rc ? o.ReserveFraction * y[rc.Index] : 0);
                        lambda[t] = energy / dt;
                        var cong = new double[nb];
                        foreach (var fr in flowRows)
                        {
                            if (fr.Period != t) continue;
                            double dual = y[fr.Row.Index];
                            if (dual == 0) continue;
                            for (int i = 0; i < nb; i++) cong[i] += dual * fr.BusCoef[i] / dt;
                        }
                        for (int i = 0; i < nb; i++) lmp[i, t] = lambda[t] + cong[i];
                        comps[t] = new DcLmpComponents(busIds, Enumerable.Range(0, nb).Select(i => lmp[i, t]).ToArray(),
                            Enumerable.Repeat(lambda[t], nb).ToArray(), cong, new double[nb]);
                    }
                }
            }
            for (int g = 0; g < G; g++)
            {
                bool prev = units[g].InitialHours > 0;
                for (int t = 0; t < T; t++)
                {
                    commit[g, t] = x[u[g, t].Index] > 0.5;
                    start[g, t] = commit[g, t] && !prev;
                    stop[g, t] = !commit[g, t] && prev;
                    prev = commit[g, t];
                    disp[g, t] = Math.Max(0, x[p[g, t].Index]);
                    if (commit[g, t])
                    {
                        production += dt * fixedCost[g];
                        reserveMw[t] += units[g].EffectivePmax - disp[g, t];
                    }
                    foreach (var (s, slope) in segs[g, t]) production += dt * slope * x[s.Index];
                    if (start[g, t]) startup += units[g].StartupCost;
                }
            }
            for (int k = 0; k < elastic.Count; k++) elasticMw[k] = x[dvar[k].Index];
            if (network)
                for (int t = 0; t < T; t++)
                {
                    var inj = Injections(x, t);
                    for (int l = 0; l < net!.Branches.Count; l++) flows[l, t] = Dot(ptdf!, l, inj);
                }
            if (pricing?.Duals is { } duals)
            {
                foreach (var fr in flowRows)
                {
                    double mu = -duals[fr.Row.Index] / dt;
                    if (mu <= 1e-7) continue;
                    var inj = Injections(x, fr.Period);
                    double f = 0;
                    for (int i = 0; i < nb; i++) f += fr.BusCoef[i] * inj[i];
                    binding.Add(new MarketConstraint(fr.Contingency < 0 ? "Flow" : "ContingencyFlow", fr.Period, fr.Branch, fr.Contingency, mu, f, fr.Limit));
                }
                for (int t = 0; t < T; t++)
                    if (reserve[t] is { } rc && duals[rc.Index] / dt > 1e-7)
                        binding.Add(new MarketConstraint("Reserve", t, -1, -1, duals[rc.Index] / dt, reserveMw[t],
                            o.ReserveFraction * demand[t] + o.ReserveMw));
            }
        }
        for (int t = 0; t < T; t++)
            if (comps[t] is null) comps[t] = new DcLmpComponents(busIds, Enumerable.Repeat(double.NaN, nb).ToArray(), Enumerable.Repeat(double.NaN, nb).ToArray(),
                new double[nb], new double[nb]);
        return new ScucResult
        {
            Status = sol.Status, Units = units, Commitment = commit, Startup = start, Shutdown = stop, DispatchMw = disp, ElasticDemandMw = elasticMw,
            Objective = sol.Objective, ProductionCost = production, StartupCost = startup, MipGap = sol.MipGap, BusIds = busIds, Lmp = lmp,
            SystemLambda = lambda, LmpComponents = comps, FlowsMw = flows, SpinningReserveMw = reserveMw, Binding = binding,
            NetworkIterations = rounds, NetworkConstraints = flowRows.Count, MilpSolution = sol, PricingSolution = pricing,
        };
    }

    private static double Dot(DenseMatrix<double> ptdf, int l, double[] inj)
    {
        double s = 0;
        for (int i = 0; i < inj.Length; i++) s += ptdf[l, i] * inj[i];
        return s;
    }
}
