using System.Diagnostics;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.Optimization;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

/// <summary>
/// AC optimal power flow. Reference values are MATPOWER 7 <c>runopf</c> (MIPS) results: case9 5296.69 $/h with Pg = 89.80, 134.32, 94.19 MW;
/// case14 8081.53 $/h with Pg = 194.33, 36.72, 28.74, 0, 8.50 MW and λ_P(bus 1) = 36.724 $/MWh.
/// </summary>
public class AcOpfTests(ITestOutputHelper output)
{
    private static readonly AcOpfOptions Tight = new() { Tolerance = 1e-8 };

    [Fact]
    public void Case9_MatchesMatpowerRunopf()
    {
        // TestCases.Case9 stores the Anderson–Fouad set-points, but the OPF frees |V| within 0.9–1.1 pu, so MATPOWER's value applies.
        var r = AcOpf.Solve(TestCases.Case9());
        output.WriteLine($"{r} in {r.Elapsed.TotalMilliseconds:0.0} ms; Pg {string.Join(", ", r.PgMw.Select(p => p.ToString("0.00")))}");
        Assert.True(r.Converged);
        Assert.Equal(5296.69, r.TotalCost, 0.01);
        Assert.Equal(89.80, r.PgMw[0], 0.01);
        Assert.Equal(134.32, r.PgMw[1], 0.01);
        Assert.Equal(94.19, r.PgMw[2], 0.01);
        Assert.All(r.Vm, v => Assert.InRange(v, 0.9 - 1e-6, 1.1 + 1e-6));
        Assert.True(r.MaxMismatchMw < 1e-3);
        // power balance: generation = load + losses
        Assert.Equal(315 + r.LossesMw, r.PgMw.Sum(), 1e-3);
    }

    [Fact]
    public void Case14_MatchesMatpowerRunopf()
    {
        var net = TestCases.Case14();
        // MATPOWER case14 gencost: c2 = 0.0430293, 0.25, 0.01, 0.01, 0.01; c1 = 20, 20, 40, 40, 40; c0 = 0
        double[] c2 = [0.0430293, 0.25, 0.01, 0.01, 0.01], c1 = [20, 20, 40, 40, 40];
        for (int k = 0; k < 5; k++)
        {
            Assert.Equal(c2[k], net.Generators[k].Cost.C2, 12);
            Assert.Equal(c1[k], net.Generators[k].Cost.C1, 12);
            Assert.Equal(0, net.Generators[k].Cost.C0);
        }
        var sw = Stopwatch.StartNew();
        var r = AcOpf.Solve(net);
        sw.Stop();
        output.WriteLine($"{r} in {sw.Elapsed.TotalMilliseconds:0.0} ms (first call, includes JIT)");
        Assert.True(r.Converged);
        Assert.Equal(8081.53, r.TotalCost, 0.01);
        double[] pg = [194.33, 36.72, 28.74, 0, 8.50];
        for (int k = 0; k < 5; k++) Assert.Equal(pg[k], r.PgMw[k], 0.01);
        Assert.Equal(36.724, r.LmpAt(1), 0.001);
        Assert.True(sw.Elapsed.TotalSeconds < 1, "case14 must solve well under a second");
        var warm = Stopwatch.StartNew();
        AcOpf.Solve(net);
        output.WriteLine($"case14 warm (JIT done): {warm.Elapsed.TotalMilliseconds:0.0} ms");
    }

    [Fact]
    public void AcCost_IsAtLeast_LosslessDcCost()
    {
        foreach (var net in new[] { TestCases.Case9(), TestCases.Case14() })
        {
            var ac = AcOpf.Solve(net);
            var dc = DcOpf.Solve(net);
            output.WriteLine($"{net.Name}: AC {ac.TotalCost:0.00} $/h vs DC {dc.TotalCost:0.00} $/h, AC losses {ac.LossesMw:0.###} MW");
            Assert.True(ac.Converged && dc.Converged);
            Assert.True(ac.TotalCost >= dc.TotalCost);
            Assert.True(ac.LossesMw > 0);
        }
    }

    [Fact]
    public void TightenedBranchLimit_IsBinding_AndSeparatesLmps()
    {
        var net = TestCases.Case9();
        var free = AcOpf.Solve(net);
        // line 5-6 (index 2): limit at 70 % of its unconstrained apparent-power flow
        double sFree = Math.Max(free.Branches[2].SFrom, free.Branches[2].STo);
        double limit = 0.7 * sFree;
        var opt = new AcOpfOptions { RatingOverrides = new Dictionary<int, double> { [2] = limit } };
        var c = AcOpf.Solve(net, opt);
        output.WriteLine($"5-6 |S| free {sFree:0.##} MVA, limit {limit:0.##}; cost {free.TotalCost:0.##} → {c.TotalCost:0.##} $/h; " +
                         $"LMP {string.Join(", ", c.Lmp.Select(l => l.ToString("0.##")))}; shadow {c.BranchShadowPriceFrom[2]:0.###}/{c.BranchShadowPriceTo[2]:0.###} $/MVAh");
        Assert.True(c.Converged);
        Assert.Equal(limit, Math.Max(c.Branches[2].SFrom, c.Branches[2].STo), 1e-3);
        Assert.Contains(c.Binding, b => b.Kind == "Flow" && b.Index == 2 && b.ShadowPrice > 0);
        Assert.True(Math.Max(c.BranchShadowPriceFrom[2], c.BranchShadowPriceTo[2]) > 0.1);
        Assert.True(c.Lmp.Max() - c.Lmp.Min() > 1, "LMPs must separate under congestion");
        Assert.True(c.TotalCost > free.TotalCost);

        // ignoring line limits reproduces the unconstrained optimum
        var ignored = AcOpf.Solve(net, opt with { EnforceLineLimits = false });
        Assert.Equal(free.TotalCost, ignored.TotalCost, 1e-3);
        Assert.DoesNotContain(ignored.Binding, b => b.Kind == "Flow");
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(false, 9)]
    [InlineData(true, 5)]
    [InlineData(true, 7)]
    public void Lmp_MatchesFiniteDifferenceOfCost(bool congested, int bus)
    {
        var net = TestCases.Case9();
        var opt = Tight;
        if (congested)
        {
            var free = AcOpf.Solve(net, Tight);
            opt = Tight with { RatingOverrides = new Dictionary<int, double> { [2] = 0.7 * Math.Max(free.Branches[2].SFrom, free.Branches[2].STo) } };
        }
        var r = AcOpf.Solve(net, opt);
        const double eps = 0.5; // MW, central difference ±ε
        var up = net.Clone(); up.Loads.First(l => l.Bus == bus).P += eps;
        var dn = net.Clone(); dn.Loads.First(l => l.Bus == bus).P -= eps;
        double fd = (AcOpf.Solve(up, opt).TotalCost - AcOpf.Solve(dn, opt).TotalCost) / (2 * eps);
        output.WriteLine($"congested={congested} bus {bus}: LMP {r.LmpAt(bus):0.0000} $/MWh, finite difference {fd:0.0000}");
        Assert.True(r.Converged);
        Assert.Equal(r.LmpAt(bus), fd, Math.Abs(fd) * 0.01);
    }

    [Fact]
    public void AcLmpDecomposition_SumsToLmp()
    {
        var net = TestCases.Case9();
        var free = AcOpf.Solve(net, Tight);
        var d0 = free.Decompose();
        Assert.Equal(1, d0.ReferenceBusId);
        Assert.True(d0.MaxResidual < 1e-3, $"residual {d0.MaxResidual}");
        Assert.All(d0.Congestion, cg => Assert.Equal(0, cg, 1e-6)); // no binding branch limit
        Assert.All(d0.Energy, e => Assert.Equal(free.LmpAt(1), e, 1e-9));
        Assert.Equal(0, d0.LossFactors[0]);

        double limit = 0.7 * Math.Max(free.Branches[2].SFrom, free.Branches[2].STo);
        var c = AcOpf.Solve(net, Tight with { RatingOverrides = new Dictionary<int, double> { [2] = limit } });
        var d = c.Decompose();
        for (int i = 0; i < d.Lmp.Length; i++)
            output.WriteLine($"bus {d.BusIds[i]}: LMP {d.Lmp[i]:0.0000} = E {d.Energy[i]:0.0000} + L {d.Loss[i]:0.0000} + C {d.Congestion[i]:0.0000} + V {d.Voltage[i]:0.0000}  (LF {d.LossFactors[i]:0.0000})");
        Assert.True(d.MaxResidual < 1e-3, $"residual {d.MaxResidual}");
        Assert.True(d.Congestion.Max(Math.Abs) > 0.5, "congestion must show up in the decomposition");
        Assert.Contains(d.Loss, l => Math.Abs(l) > 0.01);

        // other reference bus: the energy component changes, the sum still holds
        var d2 = c.Decompose(referenceBusId: 5);
        Assert.Equal(c.LmpAt(5), d2.Energy[0], 1e-9);
        Assert.True(d2.MaxResidual < 1e-3);
    }

    [Fact]
    public void LossFactors_ExplainUncongestedLmps()
    {
        // without binding branch limits LMP_i = λ_ref·(1 − LF_i) + voltage term (case9 has |V| = 1.1 pu binding at buses 1, 6, 8, as in
        // MATPOWER); the LMPs themselves are checked by finite differences above
        var r = AcOpf.Solve(TestCases.Case9(), Tight);
        var d = r.Decompose();
        Assert.DoesNotContain(r.Binding, b => b.Kind == "Flow");
        Assert.Equal(new[] { 1, 6, 8 }, r.Binding.Where(b => b.Kind == "Vmax").Select(b => r.BusIds[b.Index]).ToArray());
        double lr = r.LmpAt(1);
        for (int i = 0; i < d.Lmp.Length; i++)
        {
            output.WriteLine($"bus {d.BusIds[i]}: LF {d.LossFactors[i]:0.000000}, voltage component {d.Voltage[i]:0.000000} $/MWh");
            Assert.Equal(d.Lmp[i], lr * (1 - d.LossFactors[i]) + d.Voltage[i], 1e-5);
            Assert.Equal(d.Energy[i] * (1 - d.LossFactors[i]), d.Energy[i] + d.Loss[i], 1e-9);
            Assert.Equal(1 - d.LossFactors[i], d.DeliveryFactors[i], 1e-12);
        }
        // load bus 5 is far from the cheap units: withdrawing there increases losses (negative loss factor, LMP above λ_ref)
        Assert.True(d.LossFactors[d.PositionOf(5)] < 0);
        Assert.InRange(d.LossFactors.Max(Math.Abs), 1e-3, 0.1);
    }

    [Fact]
    public void DcLmpDecomposition_SumsToLmp()
    {
        var net = TestCases.Case9();
        var free = DcOpf.Solve(net);
        var opt = new DcOpfOptions { RatingOverrides = new Dictionary<int, double> { [2] = 0.7 * Math.Abs(free.FlowsMw[2]) } };
        var c = DcOpf.Solve(net, opt);
        var d = LmpDecomposition.Decompose(c);
        Assert.True(d.MaxResidual < 1e-9);
        Assert.All(d.Loss, l => Assert.Equal(0, l));
        Assert.All(d.Energy, e => Assert.Equal(c.Lambda, e));
        Assert.True(d.Congestion.Max(Math.Abs) > 0.5);
        Assert.Equal(1, d.ReferenceBusId);
    }

    [Fact]
    public void WarmStart_And_StepControl_ReachSameOptimum()
    {
        var net = TestCases.Case9();
        var flat = AcOpf.Solve(net);
        var warm = AcOpf.Solve(net, new AcOpfOptions { Start = AcOpfStart.PowerFlow });
        var sc = AcOpf.Solve(net, new AcOpfOptions { StepControl = true });
        output.WriteLine($"flat {flat.Iterations} it, warm {warm.Iterations} it, step control {sc.Iterations} it");
        Assert.True(warm.Converged && sc.Converged);
        Assert.Equal(flat.TotalCost, warm.TotalCost, 0.01);
        Assert.Equal(flat.TotalCost, sc.TotalCost, 0.01);
        for (int k = 0; k < 3; k++) Assert.Equal(flat.PgMw[k], warm.PgMw[k], 0.01);
    }

    [Fact]
    public void ToPowerFlowResult_IsConsistent()
    {
        var r = AcOpf.Solve(TestCases.Case14());
        var pf = r.ToPowerFlowResult();
        Assert.True(pf.Converged);
        Assert.Equal(14, pf.Buses.Count);
        Assert.Equal(r.PgMw.Sum(), pf.TotalGenerationMw, 1e-9);
        Assert.Equal(pf.TotalLoadMw + pf.LossesMw, pf.TotalGenerationMw, 1e-3);
        Assert.Equal(r.VmAt(14), pf.Bus(14).Vm, 1e-12);
        Assert.Empty(pf.VoltageViolations());
    }

    [Fact]
    public void InteriorPointSolver_SolvesHs071()
    {
        // Hock–Schittkowski problem 71: f* = 17.0140173, x* = (1, 4.7429994, 3.8211503, 1.3794082)
        var nlp = new NonlinearProgram
        {
            VariableCount = 4, EqualityCount = 1, InequalityCount = 1,
            Lower = [1, 1, 1, 1], Upper = [5, 5, 5, 5],
            Objective = (x, g) =>
            {
                g[0] = x[3] * (2 * x[0] + x[1] + x[2]); g[1] = x[0] * x[3]; g[2] = x[0] * x[3] + 1; g[3] = x[0] * (x[0] + x[1] + x[2]);
                return x[0] * x[3] * (x[0] + x[1] + x[2]) + x[2];
            },
            Constraints = (x, g, h) =>
            {
                g[0] = x[0] * x[0] + x[1] * x[1] + x[2] * x[2] + x[3] * x[3] - 40;
                h[0] = 25 - x[0] * x[1] * x[2] * x[3];
            },
            Jacobians = x =>
            {
                var jg = new SparseBuilder<double>(1, 4); var jh = new SparseBuilder<double>(1, 4);
                for (int i = 0; i < 4; i++) jg.Add(0, i, 2 * x[i]);
                jh.Add(0, 0, -x[1] * x[2] * x[3]); jh.Add(0, 1, -x[0] * x[2] * x[3]); jh.Add(0, 2, -x[0] * x[1] * x[3]); jh.Add(0, 3, -x[0] * x[1] * x[2]);
                return (jg.ToCsr(), jh.ToCsr());
            },
            Hessian = (x, s, lam, mu) =>
            {
                var h = new DenseMatrix<double>(4, 4);
                h[0, 0] = s * 2 * x[3]; h[0, 1] = s * x[3]; h[0, 2] = s * x[3]; h[0, 3] = s * (2 * x[0] + x[1] + x[2]);
                h[1, 3] = s * x[0]; h[2, 3] = s * x[0];
                for (int i = 0; i < 4; i++) h[i, i] += 2 * lam[0];
                h[0, 1] -= mu[0] * x[2] * x[3]; h[0, 2] -= mu[0] * x[1] * x[3]; h[0, 3] -= mu[0] * x[1] * x[2];
                h[1, 2] -= mu[0] * x[0] * x[3]; h[1, 3] -= mu[0] * x[0] * x[2]; h[2, 3] -= mu[0] * x[0] * x[1];
                for (int i = 0; i < 4; i++) for (int j = 0; j < i; j++) h[i, j] = h[j, i];
                return SparseMatrix<double>.FromDense(h);
            },
        };
        var r = InteriorPointSolver.Solve(nlp, [1, 5, 5, 1]);
        Assert.True(r.Converged, r.Status.ToString());
        Assert.Equal(17.0140173, r.Objective, 1e-5);
        double[] xs = [1, 4.7429994, 3.8211503, 1.3794082];
        for (int i = 0; i < 4; i++) Assert.Equal(xs[i], r.X[i], 1e-4);
        Assert.True(r.Mu[0] > 0);          // product constraint active
        Assert.True(r.MuLower[0] > 0);     // x1 at its lower bound
    }

    [Fact]
    public void SyntheticGrid300_Converges()
    {
        var net = TestCases.SyntheticGrid(300);
        var sw = Stopwatch.StartNew();
        var r = AcOpf.Solve(net);
        double first = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        AcOpf.Solve(net);
        double second = sw.Elapsed.TotalMilliseconds;
        var d = r.Decompose();
        var ed = EconomicDispatch.Solve(net); // lossless, network-free lower bound
        output.WriteLine($"synthetic300: {r}; {first:0} ms first call, {second:0} ms second; dense fallbacks {r.Solver.DenseFallbacks}; " +
                         $"decomposition residual {d.MaxResidual:E2}; lossless ED {ed.TotalCost:0.00} $/h ({net.Generators.Count} generators, {net.Branches.Count} branches)");
        Assert.True(r.Converged, r.Status.ToString());
        Assert.Equal(0, r.Solver.DenseFallbacks);
        Assert.True(d.MaxResidual < 1e-3, $"residual {d.MaxResidual}");
        Assert.True(r.TotalCost >= ed.TotalCost);
    }

    [Fact]
    public void AcOpfService_IsRegisteredByAddOptimization()
    {
        var sp = new ServiceCollection().AddElectrical(e => e.AddOptimization()).BuildServiceProvider();
        var svc = sp.GetRequiredService<IAcOpfService>();
        var r = svc.Solve(TestCases.Case9());
        Assert.Equal(5296.69, r.TotalCost, 0.01);
        Assert.True(svc.Decompose(r).MaxResidual < 1e-3);
    }
}
