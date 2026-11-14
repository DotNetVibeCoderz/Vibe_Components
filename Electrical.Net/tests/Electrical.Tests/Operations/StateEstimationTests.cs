using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.StateEstimation;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class StateEstimationTests(ITestOutputHelper output)
{
    private static (PowerNetwork Net, PowerFlowResult Pf) Case14()
    {
        var net = TestCases.Case14();
        return (net, PowerFlowSolver.Solve(net));
    }

    [Fact]
    public void ChiSquareQuantiles_MatchTables()
    {
        // Standard χ² table values (e.g. NIST/SEMATECH e-Handbook, Table 1.3.6.7.4)
        Assert.Equal(18.307, Distributions.ChiSquareInverseCdf(0.95, 10), 3);
        Assert.Equal(6.635, Distributions.ChiSquareInverseCdf(0.99, 1), 3);
        Assert.Equal(135.807, Distributions.ChiSquareInverseCdf(0.99, 100), 3);
        Assert.Equal(1.959964, Distributions.NormalInverseCdf(0.975), 5);
        Assert.Equal(0.95, Distributions.ChiSquareCdf(18.307, 10), 4);
    }

    [Fact]
    public void Case14_NoiseFree_RecoversPowerFlowExactly()
    {
        var (net, pf) = Case14();
        var z = MeasurementGenerator.FromPowerFlow(pf, noise: 0);
        var se = WlsEstimator.Estimate(net, z);
        Assert.True(se.Converged && se.Observable);
        Assert.InRange(se.Iterations, 2, 8);
        foreach (var b in pf.Buses)
        {
            Assert.Equal(b.Vm, se.VmAt(b.BusId), 6);
            Assert.Equal(b.VaDeg, se.VaDegAt(b.BusId), 5);
        }
        Assert.True(se.ObjectiveJ < 1e-8);
    }

    [Fact]
    public void Case14_FullSetWithNoise_WithinNoiseAndPassesChiSquare()
    {
        var (net, pf) = Case14();
        var z = MeasurementGenerator.FromPowerFlow(pf, seed: 11);
        var se = WlsEstimator.Estimate(net, z);
        Assert.True(se.Converged);
        double maxDv = pf.Buses.Max(b => Math.Abs(b.Vm - se.VmAt(b.BusId)));
        double maxDa = pf.Buses.Max(b => Math.Abs(b.VaDeg - se.VaDegAt(b.BusId)));
        output.WriteLine($"m={z.Count}, J={se.ObjectiveJ:0.##}, χ² threshold={se.ChiSquareThreshold:0.##} (dof {se.DegreesOfFreedom}), max ΔV={maxDv:E2} pu, max Δθ={maxDa:0.###}°");
        Assert.True(maxDv < 0.004, "voltage error below σ_V");
        Assert.True(maxDa < 0.3);
        Assert.False(se.BadDataSuspected);
        Assert.True(se.NormalizedResiduals.Where(r => !double.IsNaN(r)).Max() < 3.5);
        // estimated flows are closer to the truth than the raw meters on average (filtering effect)
        double rawErr = 0, estErr = 0;
        var exact = MeasurementGenerator.FromPowerFlow(pf, noise: 0);
        for (int i = 0; i < z.Count; i++) { rawErr += Math.Abs(z[i].Value - exact[i].Value); estErr += Math.Abs(se.Estimated[i] - exact[i].Value); }
        Assert.True(estErr < rawErr);
    }

    [Fact]
    public void GrossError_IsDetectedIdentifiedAndRemoved()
    {
        var (net, pf) = Case14();
        var z = MeasurementGenerator.FromPowerFlow(pf, seed: 5);
        int bad = z.FindIndex(m => m.Type == MeasurementType.ActiveFlow && m.Location == 2 && m.End == BranchEnd.From); // P flow 2-3
        var corrupt = z[bad] with { Value = z[bad].Value + 25 * z[bad].Sigma };
        z[bad] = corrupt;
        var se = WlsEstimator.Estimate(net, z);
        Assert.True(se.BadDataSuspected, $"J={se.ObjectiveJ:0.#} vs {se.ChiSquareThreshold:0.#}");
        Assert.Equal(bad, se.LargestNormalizedResidualIndex);
        var bd = BadDataDetector.IdentifyAndRemove(net, z);
        output.WriteLine($"J0={bd.InitialObjective:0.#}; removed: {string.Join("; ", bd.Removed.Select(r => $"{r.Measurement} (rN={r.NormalizedResidual:0.#})"))}; J={bd.Final.ObjectiveJ:0.#}");
        Assert.False(bd.InitialChiSquarePassed);
        Assert.Single(bd.Removed);
        Assert.Same(corrupt, bd.Removed[0].Measurement);
        Assert.False(bd.Final.BadDataSuspected);
        Assert.True(pf.Buses.Max(b => Math.Abs(b.Vm - bd.Final.VmAt(b.BusId))) < 0.004);
    }

    [Fact]
    public void Observability_DetectsMissingInformation()
    {
        var (net, pf) = Case14();
        var full = MeasurementGenerator.FromPowerFlow(pf, noise: 0);
        var ok = Observability.Check(net, full);
        Assert.True(ok.IsObservable);
        Assert.Equal(27, ok.StateCount);
        Assert.Equal(27, ok.Rank);
        // voltage magnitudes only: no angle information
        var vOnly = full.Where(m => m.Type == MeasurementType.VoltageMagnitude).ToList();
        var r = Observability.Check(net, vOnly);
        Assert.False(r.IsObservable);
        Assert.Equal(14, r.Rank);
        Assert.Equal(13, r.UnobservableStates.Count(s => s.StartsWith("θ")));
        // remove everything that involves bus 8 (radial via 7-8: flows on 7-8, bus-8 meters and the bus-7 injections)
        int b78 = net.Branches.FindIndex(b => b.From == 7 && b.To == 8);
        var no8 = full.Where(m => !(m.IsFlow && m.Location == b78) && !(!m.IsFlow && m.Location == 8)
                                  && !(m.Location == 7 && m.Type is MeasurementType.ActiveInjection or MeasurementType.ReactiveInjection)).ToList();
        var r8 = Observability.Check(net, no8);
        Assert.False(r8.IsObservable);
        Assert.Contains("θ@bus 8", r8.UnobservableStates);
        Assert.Contains("V@bus 8", r8.UnobservableStates);
        Assert.False(WlsEstimator.Estimate(net, no8).Observable);
    }

    [Fact]
    public void Jacobian_MatchesFiniteDifferences_AndPmuImprovesAngles()
    {
        var (net, pf) = Case14();
        var z = MeasurementGenerator.FromPowerFlow(pf, new MeasurementPlan { PmuBuses = [4, 9, 13] }, noise: 0);
        var vm = pf.Buses.Select(b => b.Vm).ToArray();
        var va = pf.Buses.Select(b => b.VaDeg).ToArray();
        var hJ = WlsEstimator.Jacobian(net, z, vm, va);
        // finite-difference on θ of bus 4 (state column 2: slack angle excluded) and V of bus 9 (column 13 + 8)
        double eps = 1e-6;
        foreach (var (col, bus, isAngle) in new[] { (2, 3, true), (13 + 8, 8, false) })
        {
            var vm2 = (double[])vm.Clone(); var va2 = (double[])va.Clone();
            if (isAngle) va2[bus] += eps * 180 / Math.PI; else vm2[bus] += eps;
            var h0 = WlsEstimator.EvaluateMeasurements(net, z, vm, va);
            var h1 = WlsEstimator.EvaluateMeasurements(net, z, vm2, va2);
            for (int i = 0; i < z.Count; i++) Assert.Equal((h1[i] - h0[i]) / eps, hJ[i, col], 1e-3);
        }
        // PMU angles reduce the angle error for the same noise realisation
        var noisyNoPmu = MeasurementGenerator.FromPowerFlow(pf, new MeasurementPlan { Flows = false }, seed: 3);
        var noisyPmu = MeasurementGenerator.FromPowerFlow(pf, new MeasurementPlan { Flows = false, PmuBuses = [2, 4, 7, 9, 13, 14] }, seed: 3);
        double Err(StateEstimationResult s) => pf.Buses.Sum(b => Math.Abs(b.VaDeg - s.VaDegAt(b.BusId)));
        Assert.True(Err(WlsEstimator.Estimate(net, noisyPmu)) < Err(WlsEstimator.Estimate(net, noisyNoPmu)));

        var sp = new ServiceCollection().AddElectrical(b => b.AddStateEstimation()).BuildServiceProvider();
        Assert.True(sp.GetRequiredService<IStateEstimationService>().CheckObservability(net, z).IsObservable);
    }
}
