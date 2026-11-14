using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Optimization;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class OptimizationTests(ITestOutputHelper output)
{
    [Fact]
    public void QpSolver_SmallProblems()
    {
        // min x² + y²  s.t. x + y = 1  → (½, ½), multiplier y* = ∂f*/∂b = 1
        var qp = new QuadraticProgram(2);
        qp.Q[0, 0] = 2; qp.Q[1, 1] = 2;
        qp.AddEquality([1, 1], 1);
        var r = QpSolver.Solve(qp);
        Assert.True(r.Converged);
        Assert.Equal(0.5, r.X[0], 6);
        Assert.Equal(1.0, r.EqualityDuals[0], 6);
        // add x ≥ 0.7 → (0.7, 0.3), bound multiplier = 2·0.7 − 2·0.3 = 0.8
        qp.AddInequality([-1, 0], -0.7);
        r = QpSolver.Solve(qp);
        Assert.Equal(0.7, r.X[0], 6);
        Assert.Equal(0.8, r.InequalityDuals[0], 5);
    }

    [Fact]
    public void EconomicDispatch_Case9_Lossless()
    {
        // MATPOWER case9 costs, 315 MW: λ = 24.04 $/MWh, P = 86.56 / 134.38 / 94.06 MW (equal incremental cost).
        var ed = EconomicDispatch.Solve(TestCases.Case9());
        Assert.True(ed.Converged);
        Assert.Equal(24.04, ed.Lambda, 0.01);
        Assert.Equal(86.56, ed.OutputMw[0], 0.01);
        Assert.Equal(134.38, ed.OutputMw[1], 0.01);
        Assert.Equal(94.06, ed.OutputMw[2], 0.01);
        Assert.Equal(315, ed.TotalGenerationMw, 6);
        // limits: low demand pins the cheap-at-low-output unit at Pmin
        var units = new List<DispatchUnit> { new("a", new(0.01, 10, 0), 50, 200), new("b", new(0.01, 20, 0), 20, 200) };
        var r = EconomicDispatch.Solve(units, 100);
        Assert.Equal(80, r.OutputMw[0], 6);
        Assert.Equal(20, r.OutputMw[1], 6);
        Assert.Equal(11.6, r.Lambda, 6);
    }

    [Fact]
    public void LossFormula_IsExactAtOperatingPoint_AndLossyDispatchMatchesAcPowerFlow()
    {
        var net = TestCases.Case9();
        var pf = PowerFlowSolver.Solve(net);
        var b = LossCoefficients.FromPowerFlow(pf);
        double pl = b.Losses(pf.Generators.Select(g => g.P).ToArray());
        Assert.Equal(pf.LossesMw, pl, 1e-6 * 1000);

        var ed = EconomicDispatch.SolveWithLosses(net);
        output.WriteLine($"lossy ED: λ={ed.Lambda:0.####} P=({string.Join(", ", ed.OutputMw.Select(p => p.ToString("0.###")))}) " +
                         $"losses={ed.LossesMw:0.###} cost={ed.TotalCost:0.##}");
        Assert.True(ed.Lambda > 24.04);
        Assert.InRange(ed.LossesMw, 2, 6);
        // AC check: set the dispatch, the slack unit must produce what the ED planned
        var check = net.Clone();
        for (int i = 0; i < 3; i++) check.Generators[i].P = ed.OutputMw[i];
        var ac = PowerFlowSolver.Solve(check);
        Assert.Equal(ed.OutputMw[0], ac.Generators[0].P, 0.05);
        Assert.Equal(ac.LossesMw, ed.LossesMw, 0.05);
        // MATPOWER runopf(case9) AC-OPF optimum: 5296.69 $/h (with voltage optimisation); the B-loss ED is within 0.5 %.
        Assert.Equal(5296.69, ed.TotalCost, 5296.69 * 0.005);
    }

    [Fact]
    public void DcOpf_WithoutCongestion_EqualsEconomicDispatch()
    {
        var net = TestCases.Case9();
        var ed = EconomicDispatch.Solve(net);
        var opf = DcOpf.Solve(net);
        Assert.True(opf.Converged);
        Assert.DoesNotContain(opf.Binding, b => b.Kind != "Pmin");
        for (int i = 0; i < 3; i++) Assert.Equal(ed.OutputMw[i], opf.DispatchMw[i], 1e-4);
        Assert.Equal(ed.TotalCost, opf.TotalCost, 1e-4);
        Assert.Equal(ed.Lambda, opf.Lambda, 1e-4);
        Assert.All(opf.Lmp, l => Assert.Equal(ed.Lambda, l, 1e-4));
    }

    [Fact]
    public void DcOpf_Congestion_SeparatesLmps_AndLmpIsMarginalCost()
    {
        var net = TestCases.Case9();
        var free = DcOpf.Solve(net);
        // tighten line 5-6 (index 2) to 70 % of its uncongested flow
        double limit = 0.7 * Math.Abs(free.FlowsMw[2]);
        var opt = new DcOpfOptions { RatingOverrides = new Dictionary<int, double> { [2] = limit } };
        var c = DcOpf.Solve(net, opt);
        output.WriteLine($"5-6 flow uncongested {free.FlowsMw[2]:0.##} MW, limit {limit:0.##}; LMPs {string.Join(", ", c.Lmp.Select(l => l.ToString("0.##")))}");
        Assert.True(c.Converged);
        Assert.Equal(limit, Math.Abs(c.FlowsMw[2]), 1e-3);
        Assert.Contains(c.Binding, b => b.Kind == "Flow" && b.Index == 2);
        Assert.True(c.Lmp.Max() - c.Lmp.Min() > 1, "LMPs must separate under congestion");
        Assert.True(c.TotalCost > free.TotalCost);
        Assert.True(c.CongestionRent > 0);
        // central finite-difference check of the LMP at bus 5: ±0.5 MW load
        var up = net.Clone(); up.Loads.First(l => l.Bus == 5).P += 0.5;
        var dn = net.Clone(); dn.Loads.First(l => l.Bus == 5).P -= 0.5;
        Assert.Equal(c.LmpAt(5), DcOpf.Solve(up, opt).TotalCost - DcOpf.Solve(dn, opt).TotalCost, 0.01);
    }

    [Fact]
    public void ScOpf_SecuresContingencies()
    {
        var net = TestCases.Case9();
        foreach (var br in net.Branches.Where(b => b.From != 1 && b.From != 3 && b.To != 2)) br.RateA = Math.Min(br.RateA, 130); // outage of 8-9 forces G2 (134 MW) onto 7-8
        var base0 = DcOpf.Solve(net);
        var sc = DcOpf.SolveSecurityConstrained(net, emergencyRatingFactor: 1.0);
        output.WriteLine($"base cost {base0.TotalCost:0.##}, SC cost {sc.TotalCost:0.##}, binding: {string.Join("; ", sc.Binding.Select(b => b.Description))}");
        Assert.True(sc.Converged);
        Assert.True(sc.TotalCost > base0.TotalCost + 1, "N-1 security must cost something here");
        Assert.Contains(sc.Binding, b => b.Kind == "ContingencyFlow");
        // verify every secured outage with a DC power flow of the post-contingency network
        var lodf = DcPowerFlow.Lodf(net);
        for (int k = 0; k < net.Branches.Count; k++)
        {
            if (Enumerable.Range(0, net.Branches.Count).Any(l => l != k && double.IsNaN(lodf[l, k]))) continue;
            var post = net.Clone();
            for (int g = 0; g < 3; g++) post.Generators[g].P = sc.DispatchMw[g];
            post.Branches[k].InService = false;
            var dc = DcPowerFlow.Solve(post);
            for (int l = 0; l < net.Branches.Count; l++)
                if (l != k) Assert.True(Math.Abs(dc.Branches[l].PFrom) <= net.Branches[l].RateA + 1e-3, $"outage {k} overloads {l}");
        }
    }

    [Fact]
    public void UnitCommitment_WoodWollenberg_PriorityListDp()
    {
        var units = UnitCommitment.WoodWollenbergThreeUnits();
        // W&W Example 4A full-load average costs: 9.79, 9.40, 11.19 $/MWh → priority 2, 1, 3
        Assert.Equal(9.79, units[0].FullLoadAverageCost, 0.01);
        Assert.Equal(9.40, units[1].FullLoadAverageCost, 0.01);
        Assert.Equal(11.19, units[2].FullLoadAverageCost, 0.01);
        double[] demand = [450, 420, 400, 400, 450, 550, 700, 850, 950, 1000, 1050, 1050, 1000, 1000, 950, 900, 950, 1050, 1100, 1000, 850, 700, 550, 480];
        var uc = UnitCommitment.Solve(units, demand, reserveFraction: 0.05);
        Assert.True(uc.Feasible);
        Assert.Equal([1, 0, 2], uc.PriorityOrder);
        for (int t = 0; t < demand.Length; t++)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++) sum += uc.DispatchMw[i, t];
            Assert.Equal(demand[t], sum, 1e-4);
            Assert.True(uc.SpinningReserveMw[t] >= 0.05 * demand[t] - 1e-6);
        }
        // minimum up/down times respected
        for (int i = 0; i < 3; i++)
        {
            int runLen = 0; bool? prev = null;
            for (int t = 0; t < demand.Length; t++)
            {
                if (prev is bool p && p != uc.Status[i, t])
                {
                    int min = p ? units[i].MinUpHours : units[i].MinDownHours;
                    bool firstRunFromInitial = t == runLen && ((p && units[i].InitialHours > 0) || (!p && units[i].InitialHours < 0));
                    Assert.True(runLen + (firstRunFromInitial ? Math.Abs(units[i].InitialHours) : 0) >= min);
                    runLen = 0;
                }
                runLen++; prev = uc.Status[i, t];
            }
        }
        // committing everything all day is feasible but more expensive
        double allOn = demand.Sum(d => EconomicDispatch.Solve(units.Select(u => new DispatchUnit(u.Name, u.Cost, u.PminMw, u.PmaxMw)).ToList(), d).TotalCost)
                       + units[2].StartupCost;
        output.WriteLine($"UC cost {uc.TotalCost:0.##} (startups {uc.StartupCost}), all-on {allOn:0.##}, unit-3 starts {uc.StartsOf(2)}");
        Assert.True(uc.TotalCost < allOn);
        Assert.True(uc.Status[1, 0] && uc.Status[0, 0]);
    }

    [Fact]
    public void CapacitorPlacement_Case33_ReducesLosses()
    {
        var r = CapacitorPlacement.Optimize(TestCases.Case33BW(), maxCapacitors: 3, stepMvar: 0.05, maxMvarPerBus: 1.2, candidates: 8);
        output.WriteLine($"caps: {string.Join(", ", r.Capacitors.Select(c => $"bus {c.BusId}: {c.Mvar * 1000:0} kvar"))}; losses {r.LossesBeforeMw * 1000:0.##} → {r.LossesAfterMw * 1000:0.##} kW; Vmin {r.MinVoltageAfter:0.####}; PFs {r.PowerFlows}");
        Assert.Equal(202.7, r.LossesBeforeMw * 1000, 0.5);
        // literature (e.g. loss-sensitivity + metaheuristics on Baran–Wu 33-bus): ≈ 132–145 kW with three banks
        Assert.True(r.LossesAfterMw * 1000 < 145, $"{r.LossesAfterMw * 1000:0.##} kW");
        Assert.True(r.MinVoltageAfter > r.MinVoltageBefore);
        Assert.True(r.Capacitors.Count is > 0 and <= 3);
    }

    [Fact]
    public void GeneralOptimizers_RosenbrockAndRastrigin()
    {
        static double Rosen(double[] x) => 100 * Math.Pow(x[1] - x[0] * x[0], 2) + Math.Pow(1 - x[0], 2);
        var nm = NelderMead.Minimize(Rosen, [-1.2, 1]);
        Assert.True(nm.Converged);
        Assert.Equal(1, nm.X[0], 1e-4);
        Assert.Equal(1, nm.X[1], 1e-4);

        static double Rastrigin(double[] x) => 20 + x.Sum(v => v * v - 10 * Math.Cos(2 * Math.PI * v));
        var a = ParticleSwarm.Minimize(Rastrigin, [-5.12, -5.12], [5.12, 5.12], particles: 30, iterations: 300, seed: 7);
        var b = ParticleSwarm.Minimize(Rastrigin, [-5.12, -5.12], [5.12, 5.12], particles: 30, iterations: 300, seed: 7);
        Assert.True(a.Value < 1e-6, $"{a.Value}");
        Assert.Equal(a.X, b.X); // deterministic

        var sp = new ServiceCollection().AddElectrical(e => e.AddOptimization()).BuildServiceProvider();
        Assert.Equal(24.04, sp.GetRequiredService<IOptimizationService>().EconomicDispatch(TestCases.Case9()).Lambda, 0.01);
    }
}
