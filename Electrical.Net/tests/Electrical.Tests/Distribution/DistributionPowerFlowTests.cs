using System.Numerics;
using Electrical.Distribution;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class DistributionPowerFlowTests(ITestOutputHelper output)
{
    /// <summary>Compares solved node voltages with published values: magnitude within <paramref name="magTol"/> (relative), angle in degrees.</summary>
    private void AssertMatches(DistributionPowerFlowResult r, IReadOnlyDictionary<string, PublishedVoltage[]> published, double magTol, double angTolDeg,
        Func<DistributionBusResult, int, (double Mag, double Ang)> measure)
    {
        double worst = 0;
        foreach (var (bus, ref3) in published)
        {
            var b = r.Bus(bus);
            for (int p = 0; p < 3; p++)
            {
                if (double.IsNaN(ref3[p].Magnitude)) continue;
                var (mag, ang) = measure(b, p);
                double err = Math.Abs(mag - ref3[p].Magnitude) / ref3[p].Magnitude;
                double dAng = Math.Abs(Math.IEEERemainder(ang - ref3[p].AngleDeg, 360));
                worst = Math.Max(worst, err);
                output.WriteLine($"{bus}.{"abc"[p]}: {mag:0.0000}∠{ang:0.00}  ref {ref3[p].Magnitude:0.0000}∠{ref3[p].AngleDeg:0.00}  err {err * 100:0.000}%");
                Assert.True(err <= magTol, $"{bus} phase {"abc"[p]}: |V| {mag:0.0000} vs {ref3[p].Magnitude:0.0000} ({err * 100:0.00} %)");
                Assert.True(dAng <= angTolDeg, $"{bus} phase {"abc"[p]}: angle {ang:0.00} vs {ref3[p].AngleDeg:0.00}");
            }
        }
        output.WriteLine($"worst magnitude error {worst * 100:0.000} %");
    }

    private static (double, double) Volts(DistributionBusResult b, int p) => (b.VoltageV[p].Magnitude, b.VaDeg[p]);

    [Theory]
    [InlineData(Ieee4NodeConnection.GrYGrY, false)]
    [InlineData(Ieee4NodeConnection.GrYGrY, true)]
    [InlineData(Ieee4NodeConnection.DeltaGrY, false)]
    [InlineData(Ieee4NodeConnection.DeltaGrY, true)]
    public void Ieee4Node_MatchesPublishedResults(Ieee4NodeConnection connection, bool unbalanced)
    {
        // IEEE 4 Node Test Feeder (W. H. Kersting, IEEE PES 2001), published node voltages in volts; tolerance 0.5 % / 0.3°.
        var net = DistributionTestCases.Ieee4Node(connection, unbalanced);
        var r = UnbalancedPowerFlow.Solve(net);
        Assert.True(r.Converged);
        Assert.Equal(DistributionSolverMethod.LadderSweep, r.Method);
        var published = DistributionTestCases.Ieee4NodePublished(connection, unbalanced);
        if (connection == Ieee4NodeConnection.DeltaGrY)
        {
            // node 2 published as line-to-line voltages for the delta primary
            var lineToLine = new Dictionary<string, PublishedVoltage[]> { ["2"] = published["2"] };
            AssertMatches(r, lineToLine, 0.005, 0.3, (b, p) =>
            {
                var v = b.LineVoltage(p, (p + 1) % 3);
                return (v.Magnitude, v.Phase * 180 / Math.PI);
            });
            published = published.Where(kv => kv.Key != "2").ToDictionary();
        }
        AssertMatches(r, published, 0.005, 0.3, Volts);
    }

    [Fact]
    public void Ieee13Node_FixedTaps_MatchesPublishedVoltages()
    {
        // IEEE 13 Node Test Feeder, radial flow summary (IEEE PES Distribution System Analysis Subcommittee):
        // with regulator taps 10/8/11, node 675 = 0.9835/1.0553/0.9758 pu, node 671 = 0.9900/1.0529/0.9778 pu.
        var net = DistributionTestCases.Ieee13Node();
        var r = UnbalancedPowerFlow.Solve(net);
        output.WriteLine(r.ToString());
        Assert.True(r.Converged);
        AssertMatches(r, DistributionTestCases.Ieee13NodePublished, 0.005, 0.5, (b, p) => (b.VmPu[p], b.VaDeg[p]));
        Assert.Equal(0.9835, r.Bus("675").VmPu[0], 2);
        Assert.Equal(1.0553, r.Bus("675").VmPu[1], 2);
        Assert.Equal(0.9758, r.Bus("675").VmPu[2], 2);
        // published total input at 650: 3577 kW / 1724 kvar; losses ≈ 111 kW (within 2 %)
        Assert.InRange(r.TotalSourceKva.Real, 3577 * 0.98, 3577 * 1.02);
        Assert.InRange(r.TotalSourceKva.Imaginary, 1724 * 0.97, 1724 * 1.03);
        Assert.InRange(r.LossesKw, 100, 125);
        Assert.InRange(r.Bus("671").VoltageUnbalanceFactor, 0.01, 0.06);
        Assert.InRange(r.Bus("671").NemaUnbalance, 0.005, 0.06);
        Assert.True(double.IsNaN(r.Bus("652").VoltageUnbalanceFactor));
    }

    [Fact]
    public void Ieee13Node_AutomaticRegulatorFindsPublishedTaps()
    {
        // LDC settings of the IEEE 13-node feeder (122 V, band 2 V, PT 20, CT 700 A, R' = 3 V, X' = 9 V): published taps 10, 8, 11.
        var net = DistributionTestCases.Ieee13Node(automaticRegulator: true);
        var r = UnbalancedPowerFlow.Solve(net);
        Assert.True(r.Converged);
        var taps = r.RegulatorTaps["Reg"];
        output.WriteLine($"taps {taps[0]}/{taps[1]}/{taps[2]} after {r.ControlIterations} control rounds");
        foreach (var p in new[] { 0, 1, 2 }) Assert.InRange(taps[p], new[] { 10, 8, 11 }[p] - 1, new[] { 10, 8, 11 }[p] + 1);
        var ctrl = (VoltageRegulatorController)net.Controllers[0];
        for (int p = 0; p < 3; p++) Assert.InRange(ctrl.RelayVoltage(r, p), 121 - 1e-6, 123 + 1e-6);
        Assert.True(ctrl.Operations >= 25);
    }

    [Fact]
    public void LadderAndCurrentInjectionAgree_AndMeshIsSolved()
    {
        var net = DistributionTestCases.Ieee13Node();
        var ladder = UnbalancedPowerFlow.Solve(net, new DistributionPowerFlowOptions { Method = DistributionSolverMethod.LadderSweep });
        var ci = UnbalancedPowerFlow.Solve(net, new DistributionPowerFlowOptions { Method = DistributionSolverMethod.CurrentInjection });
        Assert.True(ladder.Converged && ci.Converged);
        foreach (var b in ladder.Buses)
            foreach (var p in b.Phases.Indices())
                Assert.True((b.VoltageV[p] - ci.Bus(b.Name).VoltageV[p]).Magnitude / (b.BaseKvLL * 1000 / Math.Sqrt(3)) < 1e-6, $"{b.Name}.{p}");
        Assert.Equal(ladder.LossesKw, ci.LossesKw, 3);

        // close a tie 680–675 (weak mesh): ladder refuses, Auto falls back to current injection
        net.AddLine("tie", "680", "675", DistributionTestCases.Ieee13Config(601), 2000, LengthUnit.Foot);
        Assert.Throws<NonRadialNetworkException>(() =>
            UnbalancedPowerFlow.Solve(net, new DistributionPowerFlowOptions { Method = DistributionSolverMethod.LadderSweep }));
        var mesh = UnbalancedPowerFlow.Solve(net);
        Assert.Equal(DistributionSolverMethod.CurrentInjection, mesh.Method);
        Assert.True(mesh.Converged);
        Assert.True(Math.Abs(mesh.Branch("tie").TotalPowerFromKva.Real) > 1);
        Assert.True(mesh.LossesKw < ladder.LossesKw);   // the tie relieves 671-692-675
        // KCL: source power = loads + losses + capacitor reactive generation
        var bal = mesh.TotalSourceKva - mesh.LoadKva - mesh.LossesKva;
        Assert.True(Math.Abs(bal.Real) < 0.01, $"P balance {bal}");
    }

    [Fact]
    public void PowerBalanceAndPerPhaseResults()
    {
        var r = UnbalancedPowerFlow.Solve(DistributionTestCases.Ieee4Node(Ieee4NodeConnection.GrYGrY, unbalanced: true));
        var bal = r.TotalSourceKva - r.LoadKva - r.LossesKva;
        Assert.True(bal.Magnitude < 1e-3, bal.ToString());
        Assert.Equal(1275 + 1800 + 2375, r.LoadKva.Real, 3);
        var line = r.Branch("3-4");
        Assert.Equal(PhaseSet.ABC, line.Phases);
        // phase c carries the most power, phase a the least
        Assert.True(line.PowerFromKva[2].Real > line.PowerFromKva[0].Real);
        Assert.True(r.MaxVoltageUnbalance > 0.02);
        Assert.Equal("4", r.MinVoltage.Bus);
        Assert.Equal(2, r.MinVoltage.Phase);
    }
}
