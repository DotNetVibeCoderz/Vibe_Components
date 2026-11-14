using Electrical.Core.Network;
using Electrical.Markets;
using Electrical.Optimization;
using Xunit.Abstractions;

namespace Electrical.Tests;

/// <summary>Security-constrained unit commitment (MILP) validation.</summary>
public class ScucTests(ITestOutputHelper output)
{
    /// <summary>Daily demand used by the existing priority-list DP test (Wood &amp; Wollenberg three units).</summary>
    internal static readonly double[] WwDemand =
        [450, 420, 400, 400, 450, 550, 700, 850, 950, 1000, 1050, 1050, 1000, 1000, 950, 900, 950, 1050, 1100, 1000, 850, 700, 550, 480];

    /// <summary>Exact (quadratic) cost of a commitment: hourly economic dispatch of the committed units + startup costs.</summary>
    internal static double ExactCost(IReadOnlyList<UcUnit> units, bool[,] status, IReadOnlyList<double> demand)
    {
        double total = 0;
        for (int t = 0; t < demand.Count; t++)
        {
            var on = Enumerable.Range(0, units.Count).Where(i => status[i, t]).ToList();
            var ed = EconomicDispatch.Solve(on.Select(i => new DispatchUnit(units[i].Name, units[i].Cost, units[i].PminMw, units[i].PmaxMw)).ToList(), demand[t]);
            total += ed.TotalCost;
            foreach (int i in on)
            {
                bool prev = t == 0 ? units[i].InitialHours > 0 : status[i, t - 1];
                if (!prev) total += units[i].StartupCost;
            }
        }
        return total;
    }

    internal static void AssertMinUpDown(ScucResult r)
    {
        for (int g = 0; g < r.UnitCount; g++)
        {
            var u = r.Units[g];
            int run = u.InitialHours;
            for (int t = 0; t < r.Periods; t++)
            {
                bool on = r.Commitment[g, t];
                if (on && run < 0) Assert.True(-run >= u.MinDownHours, $"{u.Name} restarted after {-run} h off at t={t}");
                if (!on && run > 0) Assert.True(run >= u.MinUpHours, $"{u.Name} stopped after {run} h on at t={t}");
                run = on ? (run > 0 ? run + 1 : 1) : (run < 0 ? run - 1 : -1);
            }
        }
    }

    [Fact]
    public void Scuc_WoodWollenberg_CostNotAbovePriorityListDp()
    {
        var uc = UnitCommitment.WoodWollenbergThreeUnits();
        var dp = UnitCommitment.Solve(uc, WwDemand, reserveFraction: 0.05);
        var units = uc.Select(u => ScucUnit.FromUcUnit(u)).ToList();
        var r = SecurityConstrainedUnitCommitment.Solve(units, WwDemand, new ScucOptions { ReserveFraction = 0.05, CostSegments = 4 });
        Assert.Equal(SolveStatus.Optimal, r.Status);
        double exact = ExactCost(uc, r.Commitment, WwDemand);
        output.WriteLine($"DP {dp.TotalCost:F2} $, MILP objective (4-segment PWL) {r.Objective:F2} $, MILP commitment at exact quadratic cost {exact:F2} $, " +
                         $"{r.MilpSolution.Nodes} nodes, {r.MilpSolution.SolveTime.TotalMilliseconds:F0} ms");
        Assert.True(exact <= dp.TotalCost + 1e-6, $"MILP {exact} > DP {dp.TotalCost}");
        // the chord approximation over-estimates a convex cost, so the MILP objective bounds its own exact cost from above
        Assert.True(r.Objective >= exact - 1e-6);
        Assert.True((r.Objective - exact) / exact < 0.002);
        AssertMinUpDown(r);
        for (int t = 0; t < WwDemand.Length; t++)
        {
            double sum = 0, cap = 0;
            for (int g = 0; g < 3; g++) { sum += r.DispatchMw[g, t]; if (r.Commitment[g, t]) cap += units[g].PmaxMw; }
            Assert.Equal(WwDemand[t], sum, 6);
            Assert.True(cap >= 1.05 * WwDemand[t] - 1e-6, "spinning reserve");
            Assert.True(r.SystemLambda[t] > 0);
        }
    }

    [Fact]
    public void Scuc_RampAndMinimumUpDownLimitsRespected()
    {
        var uc = UnitCommitment.WoodWollenbergThreeUnits();
        var units = uc.Select(u => ScucUnit.FromUcUnit(u) with { RampUpMwPerHour = 120, RampDownMwPerHour = 120, InitialOutputMw = u.InitialHours > 0 ? 250 : 0 })
            .ToList();
        units[2] = units[2] with { MinUpHours = 4, MinDownHours = 3 };
        var r = SecurityConstrainedUnitCommitment.Solve(units, WwDemand, new ScucOptions { ReserveFraction = 0.05 });
        Assert.Equal(SolveStatus.Optimal, r.Status);
        AssertMinUpDown(r);
        for (int g = 0; g < 3; g++)
        {
            var u = units[g];
            double prev = u.InitialOutputMw;
            bool prevOn = u.InitialHours > 0;
            for (int t = 0; t < WwDemand.Length; t++)
            {
                double p = r.DispatchMw[g, t];
                bool on = r.Commitment[g, t];
                if (on && prevOn) Assert.InRange(p - prev, -120 - 1e-6, 120 + 1e-6);
                if (on && !prevOn) Assert.True(p <= Math.Max(u.PminMw, 120) + 1e-6, "startup ramp");
                if (!on && prevOn) Assert.True(prev <= Math.Max(u.PminMw, 120) + 1e-6, "shutdown ramp");
                prev = p; prevOn = on;
            }
        }
        var free = SecurityConstrainedUnitCommitment.Solve(uc.Select(u => ScucUnit.FromUcUnit(u)).ToList(), WwDemand, new ScucOptions { ReserveFraction = 0.05 });
        output.WriteLine($"ramp-limited cost {r.TotalCost:F2} $ vs unconstrained {free.TotalCost:F2} $");
        Assert.True(r.Objective >= free.Objective - 1e-6);
    }

    [Fact]
    public void Scuc_SmallCase_MatchesBruteForceOverCommitments()
    {
        var uc = UnitCommitment.WoodWollenbergThreeUnits();
        double[] demand = [500, 900, 700];
        var units = uc.Select(u => ScucUnit.FromUcUnit(u) with { MinUpHours = 1, MinDownHours = 1, StartupCost = u.StartupCost * 2 }).ToList();
        var opt = new ScucOptions { ReserveFraction = 0.1, CostSegments = 3, ComputePrices = false };
        var milp = SecurityConstrainedUnitCommitment.Solve(units, demand, opt);
        double best = double.PositiveInfinity;
        for (int mask = 0; mask < 1 << 9; mask++)
        {
            var fixedUnits = units.Select((u, g) => u with { FixedCommitment = Enumerable.Range(0, 3).Select(t => (mask >> (g * 3 + t) & 1) != 0).ToArray() }).ToList();
            var r = SecurityConstrainedUnitCommitment.Solve(fixedUnits, demand, opt);
            if (r.HasSolution) best = Math.Min(best, r.Objective);
        }
        output.WriteLine($"MILP {milp.Objective:F4}, brute force over 512 commitments {best:F4}");
        Assert.Equal(best, milp.Objective, 4);
    }

    /// <summary>case9 over five hours with the base-case load scaled by 0.7 … 1.3.</summary>
    internal static (PowerNetwork Net, double[] Demand) Case9Profile()
    {
        var net = TestCases.Case9();
        double load = net.TotalLoadMw;
        return (net, new[] { 0.7, 0.85, 1.0, 1.15, 1.3 }.Select(f => f * load).ToArray());
    }

    [Fact]
    public void Scuc_Case9_CongestedLineChangesDispatchAndSeparatesLmps()
    {
        var (net, demand) = Case9Profile();
        var baseOpt = new ScucOptions { RatingOverrides = Enumerable.Range(0, net.Branches.Count).ToDictionary(l => l, _ => 1000.0) };
        var free = SecurityConstrainedUnitCommitment.Solve(net, demand, baseOpt);
        Assert.Equal(SolveStatus.Optimal, free.Status);
        int peak = demand.Length - 1;
        // most loaded line of the meshed 230 kV ring (R > 0; the R = 0 branches are radial generator step-up connections)
        int line = Enumerable.Range(0, net.Branches.Count).Where(l => net.Branches[l].R > 0).MaxBy(l => Math.Abs(free.FlowsMw[l, peak]));
        double limit = 0.7 * Math.Abs(free.FlowsMw[line, peak]);
        var overrides = Enumerable.Range(0, net.Branches.Count).ToDictionary(l => l, l => l == line ? limit : 1000.0);
        var lazy = SecurityConstrainedUnitCommitment.Solve(net, demand, baseOpt with { RatingOverrides = overrides });
        var full = SecurityConstrainedUnitCommitment.Solve(net, demand, baseOpt with { RatingOverrides = overrides, LazyNetworkConstraints = false });
        output.WriteLine($"branch {net.Branches[line].From}-{net.Branches[line].To}: uncongested {free.FlowsMw[line, peak]:F1} MW, limit {limit:F1} MW");
        output.WriteLine($"cost free {free.Objective:F2}, congested {lazy.Objective:F2} (lazy, {lazy.NetworkIterations} rounds, {lazy.NetworkConstraints} rows) / " +
                         $"{full.Objective:F2} (all {full.NetworkConstraints} rows)");
        output.WriteLine("peak LMPs: " + string.Join(", ", lazy.BusIds.Select((b, i) => $"{b}:{lazy.Lmp[i, peak]:F2}")));
        Assert.Equal(SolveStatus.Optimal, lazy.Status);
        Assert.Equal(full.Objective, lazy.Objective, 3);
        Assert.True(lazy.Objective > free.Objective + 1e-3);
        Assert.True(Math.Abs(lazy.FlowsMw[line, peak]) <= limit + 1e-5);
        double change = Enumerable.Range(0, lazy.UnitCount).Max(g => Math.Abs(lazy.DispatchMw[g, peak] - free.DispatchMw[g, peak]));
        Assert.True(change > 1, "dispatch must change");
        var lmpPeak = Enumerable.Range(0, net.BusCount).Select(i => lazy.Lmp[i, peak]).ToArray();
        Assert.True(lmpPeak.Max() - lmpPeak.Min() > 1, "LMPs must separate");
        // uncongested: one price everywhere; congested: the slack-bus LMP is the energy component
        Assert.True(Enumerable.Range(0, net.BusCount).All(i => Math.Abs(free.Lmp[i, peak] - free.SystemLambda[peak]) < 1e-6));
        int slack = net.IndexOf(net.SlackBus!.Id);
        Assert.Equal(lazy.SystemLambda[peak], lazy.Lmp[slack, peak], 6);
        var c = lazy.LmpComponents[peak];
        for (int i = 0; i < net.BusCount; i++) Assert.Equal(c.Lmp[i], c.Energy[i] + c.Congestion[i] + c.Loss[i], 9);
        Assert.Contains(lazy.Binding, b => b.Kind == "Flow" && b.Branch == line && b.Hour == peak && b.ShadowPrice > 0);
    }

    [Fact]
    public void Scuc_Case9_N1ContingencyConstraintsSecurePostOutageFlows()
    {
        // load profile peaking at the base case (315 MW); the six ring lines rated 130 MW and their outages secured
        var (net, profile) = Case9Profile();
        var demand = profile.Select(d => d / 1.3).ToArray();
        var ring = Enumerable.Range(0, net.Branches.Count).Where(l => net.Branches[l].R > 0).ToList();
        var rating = ring.ToDictionary(l => l, _ => 130.0);
        var opt = new ScucOptions { RatingOverrides = rating, Contingencies = ring };
        var basecase = SecurityConstrainedUnitCommitment.Solve(net, demand, opt with { Contingencies = [] });
        var secure = SecurityConstrainedUnitCommitment.Solve(net, demand, opt);
        var highs = SecurityConstrainedUnitCommitment.Solve(net, demand, opt with { Solver = MilpTests.External("HiGHS"), LazyNetworkConstraints = false });
        Assert.Equal(SolveStatus.Optimal, secure.Status);
        int bindingN1 = secure.Binding.Count(b => b.Kind == "ContingencyFlow");
        output.WriteLine($"base-case SCUC {basecase.Objective:F2} $, N-1 secure {secure.Objective:F2} $ (HiGHS, all rows: {highs.Objective:F2} $), " +
                         $"{secure.NetworkConstraints} lazily added security rows, {bindingN1} binding contingency constraints");
        Assert.True(secure.Objective > basecase.Objective + 1, "securing the outages must cost something");
        Assert.True(bindingN1 > 0);
        Assert.Equal(highs.Objective, secure.Objective, 3);
        // 120 MW ratings cannot be secured: reported infeasible by both solvers
        var tight = opt with { RatingOverrides = ring.ToDictionary(l => l, _ => 120.0) };
        Assert.Equal(SolveStatus.Infeasible, SecurityConstrainedUnitCommitment.Solve(net, demand, tight).Status);
        var lodf = Electrical.PowerFlow.DcPowerFlow.Lodf(net);
        for (int t = 0; t < demand.Length; t++)
            foreach (int k in ring)
                for (int l = 0; l < net.Branches.Count; l++)
                {
                    if (l == k || double.IsNaN(lodf[l, k])) continue;
                    double post = secure.FlowsMw[l, t] + lodf[l, k] * secure.FlowsMw[k, t];
                    double rate = rating.TryGetValue(l, out var r) ? r : net.Branches[l].RateA;
                    Assert.True(Math.Abs(post) <= rate + 1e-4, $"t={t} outage {k}: branch {l} {post:F2} MW");
                }
    }

    [Fact]
    public void Scuc_Case14_CongestionWithExternalSolverParity()
    {
        var net = TestCases.Case14();
        double load = net.TotalLoadMw;
        double[] demand = [0.8 * load, 1.0 * load, 1.1 * load];
        // MATPOWER case14 has no ratings: limit branch 1-2 (index 0) to 120 MW
        var opt = new ScucOptions { RatingOverrides = new Dictionary<int, double> { [0] = 120 } };
        var a = SecurityConstrainedUnitCommitment.Solve(net, demand, opt);
        Assert.Equal(SolveStatus.Optimal, a.Status);
        Assert.True(Math.Abs(a.FlowsMw[0, 2]) <= 120 + 1e-5);
        var lmp = Enumerable.Range(0, net.BusCount).Select(i => a.Lmp[i, 2]).ToArray();
        Assert.True(lmp.Max() - lmp.Min() > 1);
        foreach (var name in new[] { "HiGHS", "OR-Tools" })
        {
            var ext = MilpTests.External(name);
            var b = SecurityConstrainedUnitCommitment.Solve(net, demand, opt with { Solver = ext });
            output.WriteLine($"{name}: {b.Objective:F4} vs built-in {a.Objective:F4}; peak LMP spread {lmp.Max() - lmp.Min():F2} $/MWh");
            Assert.Equal(SolveStatus.Optimal, b.Status);
            Assert.Equal(a.Objective, b.Objective, 3);
            for (int i = 0; i < net.BusCount; i++) Assert.Equal(a.Lmp[i, 2], b.Lmp[i, 2], 3);
        }
    }
}
