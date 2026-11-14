using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.Core.Units;
using Electrical.PowerFlow;

namespace Electrical.Tests;

public class PowerFlowTests
{
    [Theory]
    [InlineData(PowerFlowMethod.NewtonRaphson)]
    [InlineData(PowerFlowMethod.FastDecoupled)]
    [InlineData(PowerFlowMethod.GaussSeidel)]
    public void Case9_MatchesMatpower(PowerFlowMethod method)
    {
        var r = PowerFlowSolver.Solve(TestCases.Case9(), new PowerFlowOptions { Method = method, Tolerance = 1e-8 });
        Assert.True(r.Converged, r.ToString());
        Assert.Equal(71.64, r.Generators[0].P, 2);
        Assert.Equal(4.641, r.LossesMw, 2);
        Assert.Equal(0.9956, r.Bus(9).Vm, 3); // Anderson & Fouad: load A bus 0.996 pu
    }

    [Fact]
    public void Case14_MatchesMatpower()
    {
        var r = PowerFlowSolver.Solve(TestCases.Case14());
        Assert.True(r.Converged);
        Assert.InRange(r.Iterations, 2, 6);
        Assert.Equal(232.39, r.Generators[0].P, 1);
        Assert.Equal(13.393, r.LossesMw, 2);
        Assert.Equal(-16.04, r.Bus(14).VaDeg, 1);
        Assert.Equal(1.036, r.Bus(14).Vm, 3);
    }

    [Theory]
    [InlineData(PowerFlowMethod.NewtonRaphson)]
    [InlineData(PowerFlowMethod.BackwardForwardSweep)]
    public void Case33bw_Losses(PowerFlowMethod method)
    {
        var r = PowerFlowSolver.Solve(TestCases.Case33BW(), new PowerFlowOptions { Method = method });
        Assert.True(r.Converged, r.ToString());
        Assert.Equal(202.7, r.LossesMw * 1000, 0);
        Assert.Equal(18, r.MinVoltageBus.BusId);
        Assert.Equal(0.9131, r.MinVoltageBus.Vm, 3);
    }

    [Fact]
    public void Dc_Case9_FlowsBalance()
    {
        var r = PowerFlowSolver.Solve(TestCases.Case9(), new PowerFlowOptions { Method = PowerFlowMethod.DC });
        Assert.Equal(315 - 163 - 85, r.Generators[0].P, 6);
        Assert.Equal(0, r.LossesMw, 9);
    }

    [Fact]
    public void Ptdf_PredictsDcFlowChange()
    {
        var net = TestCases.Case14();
        var ptdf = DcPowerFlow.Ptdf(net);
        var r0 = DcPowerFlow.Solve(net);
        net.Loads.First(l => l.Bus == 14).P += 10;
        var r1 = DcPowerFlow.Solve(net);
        int bus14 = net.IndexOf(14);
        for (int l = 0; l < net.Branches.Count; l++)
            Assert.Equal(r1.Branches[l].PFrom - r0.Branches[l].PFrom, -10 * ptdf[l, bus14], 6);
    }

    [Fact]
    public void QLimits_AreEnforced()
    {
        var r = PowerFlowSolver.Solve(TestCases.Case14(), new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(r.Converged);
        foreach (var g in r.Generators.Skip(1))
        {
            var gen = r.Network.Generators[g.Index];
            Assert.InRange(g.Q, gen.Qmin - 1e-3, gen.Qmax + 1e-3);
        }
    }

    [Fact]
    public void N1_Case14_RanksIslandingFirst()
    {
        var res = ContingencyAnalysis.RunN1(TestCases.Case14());
        Assert.Equal(20, res.Count);
        Assert.Contains(res, c => c.Islanding); // 7-8 radial to synchronous condenser
    }

    [Fact]
    public void Synthetic1000_SolvesQuickly()
    {
        var r = PowerFlowSolver.Solve(TestCases.SyntheticGrid(1000));
        Assert.True(r.Converged, r.ToString());
        Assert.True(r.Elapsed.TotalSeconds < 5);
    }

    [Fact]
    public void Matpower_RoundTrip()
    {
        var net = TestCases.Case14();
        var back = Matpower.Parse(Matpower.Write(net));
        Assert.Equal(net.Buses.Count, back.Buses.Count);
        Assert.Equal(net.TotalLoadMw, back.TotalLoadMw, 6);
        var a = PowerFlowSolver.Solve(net); var b = PowerFlowSolver.Solve(back);
        Assert.Equal(a.LossesMw, b.LossesMw, 6);
    }
}

public class CoreTests
{
    [Fact]
    public void SparseLU_MatchesDense()
    {
        var rng = new Random(1);
        int n = 60;
        var b = new SparseBuilder<Complex>(n, n);
        for (int i = 0; i < n; i++)
        {
            b.Add(i, i, new Complex(10 + rng.NextDouble(), rng.NextDouble()));
            int j = rng.Next(n);
            if (j != i) { var x = new Complex(rng.NextDouble(), -rng.NextDouble()); b.Add(i, j, x); b.Add(j, i, x); }
        }
        var a = b.ToCsr();
        var rhs = Enumerable.Range(0, n).Select(i => new Complex(i, 1)).ToArray();
        var xs = a.Solve(rhs);
        var xd = a.ToDense().Solve(rhs);
        for (int i = 0; i < n; i++) Assert.True((xs[i] - xd[i]).Magnitude < 1e-10);
    }

    [Fact]
    public void SymmetricalComponents_RoundTrip()
    {
        var p = new ThreePhase(new Complex(1, 0.1), new Complex(-0.4, -0.9), new Complex(-0.5, 0.7));
        var back = SymmetricalComponents.ToPhases(SymmetricalComponents.FromPhases(p));
        Assert.True((back.A - p.A).Magnitude < 1e-12 && (back.B - p.B).Magnitude < 1e-12 && (back.C - p.C).Magnitude < 1e-12);
        var bal = SymmetricalComponents.FromPhases(ThreePhase.Balanced(1));
        Assert.Equal(1, bal.Positive.Magnitude, 12);
        Assert.Equal(0, bal.Negative.Magnitude, 12);
    }

    [Fact]
    public void PerUnit_Conversions()
    {
        var pu = new PerUnitBase(100, 20);
        Assert.Equal(4, pu.BaseOhm, 12);
        Assert.Equal(2.886751, pu.BaseKa, 5);
    }
}
