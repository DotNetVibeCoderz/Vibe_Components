using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;
using Electrical.Stability;

namespace Electrical.Tests;

public class StabilityTests
{
    [Fact]
    public void Eigenvalues_KnownMatrices()
    {
        // Symmetric block: eigenvalues 1 (from [[3,4],[4,9]] → 1, 11) and 2
        var e = EigenSolver.Eigenvalues(new double[,] { { 2, 0, 0 }, { 0, 3, 4 }, { 0, 4, 9 } });
        Assert.Equal([11, 2, 1], e.Select(z => Math.Round(z.Real, 10)));
        // Companion matrix of (x−1)(x−2)(x−3)(x²+1) = x⁵ − 6x⁴ + 12x³ − 12x² + 11x − 6 → roots 1, 2, 3, ±j
        double[] c = [-6, 11, -12, 12, -6]; // a0..a4
        var comp = new double[5, 5];
        for (int i = 1; i < 5; i++) comp[i, i - 1] = 1;
        for (int i = 0; i < 5; i++) comp[i, 4] = -c[i];
        var r = EigenSolver.Eigenvalues(comp);
        Complex[] expected = [3, 2, new(0, 1), new(0, -1), 1];
        Assert.Equal(5, r.Length);
        foreach (var x in expected) Assert.Contains(r, z => (z - x).Magnitude < 1e-8);
    }

    [Fact]
    public void Eigenvalues_RandomMatrix_TraceAndEigenvectorResidual()
    {
        var rng = new Random(11);
        int n = 12;
        var a = new DenseMatrix<double>(n, n);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) a[i, j] = rng.NextDouble() * 2 - 1;
        var ev = EigenSolver.Eigenvalues(a);
        double trace = Enumerable.Range(0, n).Sum(i => a[i, i]);
        Assert.Equal(trace, ev.Sum(z => z.Real), 9);
        Assert.Equal(0, ev.Sum(z => z.Imaginary), 9);
        foreach (var l in ev)
        {
            var v = EigenSolver.RightEigenvector(a, l);
            var av = new Complex[n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) av[i] += a[i, j] * v[j];
            double res = Enumerable.Range(0, n).Max(i => (av[i] - l * v[i]).Magnitude);
            Assert.True(res < 1e-8, $"residual {res} for {l}");
        }
    }

    // SMIB: H = 5 s, 60 Hz, Pm = 0.8 pu, E′ = 1.17 pu, V∞ = 1 pu, X = 0.65 pu (Pmax = 1.8 pu), bolted fault at the machine terminals
    private static readonly SmibSystem Smib1 = new(H: 5, Pm: 0.8, E: 1.17, V: 1.0, XPre: 0.65, XFault: double.PositiveInfinity, XPost: 0.65);

    [Fact]
    public void Smib_EqualAreaCriterion_MatchesSimulation()
    {
        var eac = Smib.EqualArea(Smib1);
        // δ0 = asin(0.8/1.8) = 26.39°; δmax = 180 − 26.39 = 153.61°; cos δcr = [0.8(δmax − δ0) + 1.8 cos δmax]/1.8
        double d0 = Math.Asin(0.8 / 1.8), dm = Math.PI - d0;
        double dcr = Math.Acos((0.8 * (dm - d0) + 1.8 * Math.Cos(dm)) / 1.8);
        Assert.Equal(d0 * 180 / Math.PI, eac.Delta0Deg, 9);
        Assert.Equal(dcr * 180 / Math.PI, eac.CriticalClearingAngleDeg, 9);
        // t_cr = √(4H(δcr − δ0)/(ωs·Pm))
        double tcr = Math.Sqrt(4 * 5 * (dcr - d0) / (2 * Math.PI * 60 * 0.8));
        Assert.Equal(tcr, eac.CriticalClearingTimeS!.Value, 9);
        Assert.Equal(tcr, Smib.CriticalClearingTime(Smib1), 3);
        Assert.True(Smib.Simulate(Smib1, tcr - 0.01).Stable);
        Assert.False(Smib.Simulate(Smib1, tcr + 0.01).Stable);
    }

    [Fact]
    public void Smib_PartialFault_CriticalAngleMatchesSimulatedAngleAtClearing()
    {
        // Fault-on reactance 2.0 pu (Pmax2 = 0.585), post-fault line tripped: X = 0.9 pu (Pmax3 = 1.3)
        var s = Smib1 with { XFault = 2.0, XPost = 0.9 };
        var eac = Smib.EqualArea(s);
        double tcr = Smib.CriticalClearingTime(s, toleranceS: 1e-5);
        var traj = Smib.Simulate(s, 10, endTimeS: tcr, stepS: 1e-5); // sustained fault up to tcr
        Assert.Equal(eac.CriticalClearingAngleDeg, traj.DeltaDeg[^1], 0);
    }

    private static PowerNetwork SmibNetwork()
    {
        // Generator (H = 5 s, X′d = 0.3 pu) → line X = 0.5 pu → infinite bus (H = 0 → constant angle, X′d ≈ 0)
        var net = new PowerNetwork { Name = "smib", BaseMva = 100, FrequencyHz = 60 };
        net.Buses.Add(new Bus { Id = 1, Type = BusType.PV, Vm = 1.0, BaseKv = 230 });
        net.Buses.Add(new Bus { Id = 2, Type = BusType.Slack, Vm = 1.0, BaseKv = 230 });
        net.Branches.Add(new Branch { From = 1, To = 2, X = 0.5 });
        net.Generators.Add(new Generator { Bus = 1, P = 80, Vset = 1.0, H = 5, XdPrime = 0.3, D = 0, MBase = 100 });
        net.Generators.Add(new Generator { Bus = 2, P = 0, Vset = 1.0, H = 0, XdPrime = 1e-6, MBase = 100 });
        return net;
    }

    [Fact]
    public void SmibNetwork_SmallSignalAndCct_MatchClosedForm()
    {
        var model = ClassicalModel.FromPowerFlow(SmibNetwork());
        var g = model.Machines[0];
        Assert.True(model.Machines[1].Infinite);
        double x = 0.3 + 0.5 + 1e-6;
        double e = g.E, v = model.Machines[1].E;
        Assert.Equal(0.8, e * v / x * Math.Sin(g.Delta0Rad - model.Machines[1].Delta0Rad), 6); // Pe(δ0) = Pm
        // Small signal: λ = ±j·√(ωs·K/(2H)), K = E·V·cos δ0 / X
        var ss = SmallSignal.Analyze(model);
        var smib = new SmibSystem(5, 0.8, e, v, x, double.PositiveInfinity, x);
        var mode = Assert.Single(ss.OscillatoryModes);
        Assert.Equal(smib.NaturalFrequencyRadS, mode.Eigenvalue.Imaginary, 4);
        Assert.Equal(0, mode.DampingRatio, 6);
        // Bolted fault at the generator bus, no line trip: CCT equals the closed-form EAC value
        double cct = TransientStability.CriticalClearingTime(model, 1, toleranceS: 0.0005,
            options: new TransientStabilityOptions { TimeStepS = 0.0005, EndTimeS = 2 });
        Assert.Equal(Smib.EqualArea(smib).CriticalClearingTimeS!.Value, cct, 2);
    }

    // Case9 in MATPOWER numbering: Anderson & Fouad bus 7 (generator-2 HV bus) is MATPOWER bus 8 and A&F line 5–7 is MATPOWER branch 8–9.
    [Fact]
    public void Case9_FaultNearBus7_ClearedAt83ms_IsStable_AndCctInRange()
    {
        var net = TestCases.Case9();
        var model = ClassicalModel.FromPowerFlow(net);
        int line = TransientStability.BranchIndex(net, 8, 9);
        var opt = new TransientStabilityOptions { TimeStepS = 0.002, EndTimeS = 2.0 };
        var r = TransientStability.SimulateFault(model, 8, 0.083, line, opt);
        Assert.True(r.Stable, $"max deviation {r.MaxAngleDeviationDeg:0.0}°");
        double cct = TransientStability.CriticalClearingTime(model, 8, line, 0.05, 0.6, 0.002, opt);
        Assert.InRange(cct, 0.15, 0.35);  // task window; classical-model literature value ≈ 0.16–0.17 s (this model: 0.162 s)
        Assert.InRange(cct, 0.155, 0.175);
        Assert.False(TransientStability.SimulateFault(model, 8, cct + 0.02, line, opt).Stable);
    }

    [Fact]
    public void Case9_NoDisturbance_StaysInEquilibrium()
    {
        var model = ClassicalModel.FromPowerFlow(TestCases.Case9());
        // classical initial angles (Anderson & Fouad: δ1 = 2.27°, δ2 = 19.73°, δ3 = 13.17° relative to bus 1 angle 0)
        Assert.Equal(2.27, model.Machines[0].Delta0Deg, 1);
        Assert.Equal(19.73, model.Machines[1].Delta0Deg, 1);
        Assert.Equal(13.17, model.Machines[2].Delta0Deg, 1);
        var r = TransientStability.Simulate(model, [], new TransientStabilityOptions { EndTimeS = 1 });
        Assert.True(r.MaxAngleDeviationDeg - Math.Abs(r.AngleCoiDeg.Max(a => Math.Abs(a[0]))) < 1e-6);
        for (int i = 0; i < 3; i++) Assert.Equal(r.AngleDeg[i][0], r.AngleDeg[i][^1], 6);
    }

    [Fact]
    public void Case9_SmallSignal_TwoElectromechanicalModes()
    {
        var ss = SmallSignal.Analyze(TestCases.Case9());
        var osc = ss.OscillatoryModes.OrderBy(m => m.FrequencyHz).ToList();
        Assert.Equal(2, osc.Count);
        Assert.Equal(8.69, osc[0].Eigenvalue.Imaginary, 1);
        Assert.Equal(13.36, osc[1].Eigenvalue.Imaginary, 1);
        // Classical 9-bus model: ω ≈ 8.7 rad/s (1.38 Hz) and 13.4 rad/s (2.13 Hz) (Sauer & Pai, Anderson & Fouad)
        Assert.InRange(osc[0].FrequencyHz, 1.2, 1.6);
        Assert.InRange(osc[1].FrequencyHz, 1.9, 2.4);
        Assert.True(ss.IsStable(1e-5));
        Assert.Equal(1, osc[1].Participation!.Sum(), 9);
    }

    private static PowerNetwork TwoBus()
    {
        var net = new PowerNetwork { Name = "2bus", BaseMva = 100, FrequencyHz = 50 };
        net.Buses.Add(new Bus { Id = 1, Type = BusType.Slack, Vm = 1.0, BaseKv = 110 });
        net.Buses.Add(new Bus { Id = 2, Type = BusType.PQ, BaseKv = 110 });
        net.Branches.Add(new Branch { From = 1, To = 2, X = 0.1 });
        net.Generators.Add(new Generator { Bus = 1, Vset = 1.0 });
        net.Loads.Add(new Load { Bus = 2, P = 100, Q = 0 });
        return net;
    }

    [Fact]
    public void PvCurve_TwoBus_MatchesAnalyticalMaximumTransfer()
    {
        // Unity-pf load through X = 0.1 pu: P_max = V²/(2X) = 5 pu = 500 MW, V_nose = 1/√2
        var pv = VoltageStability.PvCurve(TwoBus(), new PvCurveOptions { InitialStep = 0.5, MinimumStep = 1e-4 });
        Assert.InRange(pv.LambdaMax, 4.95, 5.0001);
        Assert.InRange(pv.Voltage(2)[^1], 0.70, 0.75);
        Assert.Equal(2, pv.CriticalBusId);
        Assert.True(pv.Voltage(2).Zip(pv.Voltage(2).Skip(1)).All(p => p.Second <= p.First));
        // L-index: |1 − V1/V2| → 0.1 at base load, → 1 at the nose (exactly 1 at V2 = V1/√2 ∠ −45°)
        var lBase = VoltageStability.LIndex(TwoBus());
        Assert.InRange(lBase.ByBus[2], 0.09, 0.12);
        var nose = TwoBus();
        nose.Loads[0].P = 100 * pv.LambdaMax;
        Assert.InRange(VoltageStability.LIndex(nose).Max, 0.85, 1.0);
    }

    [Fact]
    public void PvCurve_Case14_HasMargin()
    {
        var pv = VoltageStability.PvCurve(TestCases.Case14(), new PvCurveOptions { MinimumStep = 0.01 });
        Assert.True(pv.LambdaMax > 2, $"λmax = {pv.LambdaMax}");   // ≈ 4.05 without generator Q limits
        Assert.True(pv.MarginMw > 250);
        Assert.Contains(pv.CriticalBusId, new[] { 4, 5, 9, 10, 11, 12, 13, 14 });
        var l = VoltageStability.LIndex(TestCases.Case14());
        Assert.InRange(l.Max, 0.01, 0.3);
    }

    [Fact]
    public void QvCurve_TwoBus_MarginShrinksWithLoad()
    {
        var light = VoltageStability.QvCurve(TwoBus(), 2, 0.4, 1.1, 0.01);
        var heavy = TwoBus(); heavy.Loads[0].P = 400;
        var hv = VoltageStability.QvCurve(heavy, 2, 0.4, 1.1, 0.01);
        Assert.True(light.ReactiveMarginMvar > hv.ReactiveMarginMvar);
        Assert.True(light.ReactiveMarginMvar > 0);
        // At V = 1.0 with P = 1 pu through X = 0.1: Q_inj = V²/X − √((V·V1/X)² − P²)·... = 10 − √(100 − 1) = 0.0501 pu → 5.01 Mvar
        int k = Array.FindIndex(light.VoltagePu, v => Math.Abs(v - 1.0) < 1e-9);
        Assert.Equal(100 * (10 - Math.Sqrt(99)), light.QMvar[k], 2);
    }

    [Fact]
    public void FrequencyResponse_RocofNadirSteadyState()
    {
        // H = 5 s, D = 1, R = 5 %, ΔP = 0.1 pu, 50 Hz: ROCOF = −0.1·50/10 = −0.5 Hz/s, Δf_ss = −0.1/(1 + 20)·50 = −0.238 Hz
        var p = new FrequencyResponseParameters(5, 1, 0.05, 0.1);
        var r = FrequencyResponse.Simulate(p, 60, 0.005);
        Assert.Equal(-0.5, r.InitialRocofHzPerS, 9);
        Assert.Equal(-0.5, (r.FrequencyHz[1] - r.FrequencyHz[0]) / 0.005, 2);
        Assert.Equal(-0.1 / 21 * 50, r.DeltaFrequencyHz[^1], 3);
        Assert.True(r.NadirHz < 50 + r.SteadyStateDeviationHz);
        Assert.InRange(r.NadirTimeS, 0.5, 10);
        var agg = FrequencyResponseParameters.FromNetwork(TestCases.Case9(), 50);
        Assert.Equal((23.64 + 6.4 + 3.01), agg.H, 9);
    }

    [Fact]
    public void Case9_GeneratorTrip_DeceleratesRemainingMachines()
    {
        var model = ClassicalModel.FromPowerFlow(TestCases.Case9());
        var r = TransientStability.Simulate(model, [StabilityEvent.TripGenerator(0.1, 2)], new TransientStabilityOptions { EndTimeS = 1 });
        Assert.True(r.SpeedPu[0][^1] < 1 && r.SpeedPu[1][^1] < 1);
        Assert.Equal(0, r.ElectricalPowerPu[2][^1], 9);
    }
}
