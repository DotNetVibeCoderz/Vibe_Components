using System.Numerics;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Dynamics;
using Electrical.Stability;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class DynamicSimulationTests
{
    // MATPOWER numbering: Anderson & Fouad bus 7 is MATPOWER bus 8, line 5–7 is MATPOWER branch 8–9 (see StabilityTests).
    private static int Line89(PowerNetwork net) => TransientStability.BranchIndex(net, 8, 9);

    [Theory]
    [InlineData(IntegrationMethod.ImplicitTrapezoidal)]
    [InlineData(IntegrationMethod.ModifiedEuler)]
    public void Case9_FlatRun_StaysAtEquilibrium(IntegrationMethod method)
    {
        var sys = DynamicTestCases.Case9().Build();
        var (dmax, mismatch) = sys.InitializationResidual();
        Assert.True(dmax < 1e-9, $"max |f(x0)| = {dmax:E2}");
        Assert.True(mismatch < 1e-9, $"network mismatch {mismatch:E2}");
        var r = DynamicSimulation.Run(sys, [], new SimulationOptions { EndTimeS = 10, Method = method });
        double drift = sys.InitialState.Select((x, i) => Math.Abs(r.FinalState[i] - x)).Max();
        Assert.True(drift < 1e-6, $"state drift {drift:E2} over 10 s");
        Assert.All(r.SpeedPu, s => Assert.All(s, w => Assert.Equal(1.0, w, 9)));
        Assert.Equal(1201, r.Time.Length);  // 60 Hz → half-cycle step 8.333 ms
    }

    [Fact]
    public void Case9_GenrouNearClassicalLimit_MatchesClassicalFirstSwing()
    {
        // GENROU with X''≈X'd = X'q ≈ Xd = Xq and T'do = T'qo = 1000 s keeps E' constant behind X'd: the classical model.
        var net = TestCases.Case9();
        int line = Line89(net);
        var cl = TransientStability.SimulateFault(ClassicalModel.FromPowerFlow(net), 8, 0.083, line,
            new TransientStabilityOptions { TimeStepS = 0.002, EndTimeS = 1, StopWhenUnstable = false });
        var m = new DynamicModelSet();
        for (int i = 0; i < 3; i++)
        {
            var g = net.Generators[i];
            double x = g.XdPrime;
            m.Add(i, new Genrou(1000, 0.03, 1000, 0.05, g.H, 0, 1.001 * x, 1.001 * x, x, x, 0.99 * x, 0.5 * x));
        }
        var sys = DynamicSystem.Build(net, m);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.Fault(0, 8), DynamicEvent.Clear(0.083, 8), DynamicEvent.TripBranch(0.083, line)],
            new SimulationOptions { EndTimeS = 1, TimeStepS = 0.002 });
        double err = 0, peak = 0;
        for (int k = 0; k < r.Time.Length; k++)
        {
            int j = Array.FindIndex(cl.Time, t => t >= r.Time[k] - 1e-9);
            for (int g = 1; g < 3; g++)
            {
                double d = r.AngleDeg[g][k] - r.AngleDeg[0][k], c = cl.AngleDeg[g][j] - cl.AngleDeg[0][j];
                err = Math.Max(err, Math.Abs(d - c)); peak = Math.Max(peak, c);
            }
        }
        Assert.InRange(peak, 70, 100);  // classical first swing δ2 − δ1 ≈ 85°
        Assert.True(err < 3, $"max deviation from classical over the first swing {err:0.00}°"); // ours 2.3° (torque vs power swing form)
        Assert.Equal(model0(net), r.AngleDeg[1][0], 1);
        static double model0(PowerNetwork n) => ClassicalModel.FromPowerFlow(n).Machines[1].Delta0Deg;
    }

    [Fact]
    public void Case9_DetailedFault_ExplicitAndImplicitAgree()
    {
        var c = DynamicTestCases.Case9();
        var sys = c.Build();
        int line = Line89(c.Network);
        var ev = new[] { DynamicEvent.Fault(0.1, 8), DynamicEvent.Clear(0.183, 8), DynamicEvent.TripBranch(0.183, line) };
        var imp = DynamicSimulation.Run(sys, ev, new SimulationOptions { EndTimeS = 15, Method = IntegrationMethod.ImplicitTrapezoidal, TimeStepS = 0.005 });
        var exp = DynamicSimulation.Run(sys, ev, new SimulationOptions { EndTimeS = 15, Method = IntegrationMethod.ModifiedEuler, TimeStepS = 0.005 });
        Assert.True(imp.Stable && exp.Stable);
        Assert.Equal(0, imp.ConvergenceFailures);
        double diff = 0;
        for (int k = 0; k < imp.Time.Length; k++) diff = Math.Max(diff, Math.Abs(imp.AngleCoiDeg[2][k] - exp.AngleCoiDeg[2][k]));
        Assert.True(diff < 1.0, $"implicit vs explicit COI angle difference {diff:0.000}°");
        // the exciters respond: field voltage rises during the fault and terminal voltage recovers
        Assert.True(imp.FieldVoltagePu[1].Max() > imp.FieldVoltagePu[1][0] + 0.5);
        Assert.InRange(imp.TerminalVoltagePu[1][^1], 1.0, 1.05); // slow (≈ 0.15 Hz) exciter swing has settled at 15 s
        Assert.Equal(0, imp.AngleCoiDeg.Select((a, i) => a[0] * sys.Units[i].InertiaWeight).Sum(), 6);
        Assert.NotEmpty(imp.Channel("EFD G2"));
        Assert.Equal(9, imp.BusVoltagePu.Length);
    }

    [Fact]
    public void Case9_DetailedSmallSignal_HasTwoElectromechanicalModesNearClassical()
    {
        var em = DynamicSmallSignal.Analyze(DynamicTestCases.Case9().Build()).ElectromechanicalModes();
        Assert.Equal(2, em.Count);
        // classical model (StabilityTests): 1.38 Hz and 2.13 Hz; the detailed model with IEEET1 exciters gives ≈ 1.33 Hz and 2.10 Hz
        Assert.InRange(em[0].FrequencyHz, 1.2, 1.5);
        Assert.InRange(em[1].FrequencyHz, 1.9, 2.3);
        Assert.All(em, m => Assert.True(m.DampingRatio > 0));
    }

    [Fact]
    public void ReferenceAndLoadSteps_WithGovernors_ReachDroopSteadyState()
    {
        // case9 with TGOV1 (R = 0.05) on all machines: +20 MW at bus 5 → Δf ≈ −ΔP / Σ(1/R) = −0.2 / 60 pu (constant-Z load: ΔP slightly less)
        var c = DynamicTestCases.Case9();
        for (int i = 0; i < 3; i++) c.Models.Add(i, new Tgov1(0.05, 0.5, 5, 0, 1.0, 3.0, 0));
        var sys = c.Build();
        Assert.True(sys.InitializationResidual().MaxDerivative < 1e-9);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.LoadStep(0.5, 5, 20, 0)], new SimulationOptions { EndTimeS = 30, TimeStepS = 0.02 });
        double df = r.SpeedPu[0][^1] - 1;
        Assert.InRange(df, -0.2 / 60 * 1.05, -0.2 / 60 * 0.85);
        Assert.All(r.SpeedPu, s => Assert.Equal(r.SpeedPu[0][^1], s[^1], 6));
        double dpm = Enumerable.Range(0, 3).Sum(i => r.MechanicalPowerPu[i][^1] - r.MechanicalPowerPu[i][0]);
        Assert.Equal(-df * 60, dpm, 3); // ΔPm = −Δω·Σ(1/R)
        // Vref step on G2 raises its terminal voltage
        var v = DynamicSimulation.Run(sys, [DynamicEvent.ReferenceStep(0.2, 1, "Vref", 0.02)], new SimulationOptions { EndTimeS = 5 });
        Assert.True(v.TerminalVoltagePu[1][^1] > v.TerminalVoltagePu[1][0] + 0.005);
    }

    [Fact]
    public void GeneratorTrip_And_Unmodelled_Generators_DefaultToClassical()
    {
        var net = TestCases.Case9();
        var sys = DynamicSystem.Build(net); // all GENCLS from Generator data
        Assert.All(sys.Units, u => Assert.IsType<Gencls>(((SynchronousMachineUnit)u).Machine));
        Assert.Equal(6, sys.StateCount);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.TripGenerator(0.5, 2)], new SimulationOptions { EndTimeS = 2 });
        Assert.True(double.IsNaN(r.SpeedPu[2][^1]));
        Assert.True(r.SpeedPu[0][^1] < 1, "remaining machines decelerate without governors");
        Assert.Equal(0, r.ActivePowerMw[2][^1]);
        Assert.Equal(DynamicEventKind.ClearFault, DynamicEvent.From(StabilityEvent.Clear(1, 8)).Kind);
    }

    [Fact]
    public void ZipAndDynamicLoads_InitializeAtEquilibrium()
    {
        var net = TestCases.Case9();
        foreach (var l in net.Loads) l.Model = new ZipModel(0.4, 0.3, 0.3);
        var models = DynamicTestCases.Case9().Models;
        models.Loads.Add(new ExponentialRecoveryLoad(5, tp: 2, tq: 2, alphaS: 0, alphaT: 2, betaS: 0, betaT: 2, fraction: 0.5));
        var sys = DynamicSystem.Build(net, models, new DynamicSystemOptions { Loads = LoadRepresentation.NetworkZip });
        var (d, mm) = sys.InitializationResidual();
        Assert.True(d < 1e-9 && mm < 1e-9, $"{d:E2} {mm:E2}");
        var flat = DynamicSimulation.Run(sys, [], new SimulationOptions { EndTimeS = 2 });
        Assert.True(sys.InitialState.Select((x, i) => Math.Abs(flat.FinalState[i] - x)).Max() < 1e-8);
        // a voltage depression: the dynamic load drops (αt = 2) and then recovers towards constant power (αs = 0)
        var r = DynamicSimulation.Run(sys, [DynamicEvent.TripBranch(0.2, TransientStability.BranchIndex(net, 4, 5))], new SimulationOptions { EndTimeS = 15 });
        int li = r.UnitIndex("LOAD5");
        double p0 = -r.ActivePowerMw[li][0], pmin = -r.ActivePowerMw[li].Max(), pend = -r.ActivePowerMw[li][^1];
        Assert.True(pmin < p0 - 0.5, $"{p0} {pmin}");
        Assert.True(Math.Abs(pend - p0) < Math.Abs(pmin - p0) * 0.3, $"recovery {p0:0.00} → {pmin:0.00} → {pend:0.00}");
    }

    [Fact]
    public void AddDynamics_RegistersService()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddDynamics()).BuildServiceProvider();
        var svc = sp.GetRequiredService<IDynamicsService>();
        Assert.Contains("Dynamics", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
        var c = DynamicTestCases.KundurTwoArea(KundurExcitation.ConstantField);
        var models = svc.ParseDyr(DynamicTestCases.KundurDyr(KundurExcitation.ConstantField), c.Network);
        var sys = svc.Build(c.Network, models);
        Assert.Equal(24, sys.StateCount);
        Assert.True(svc.SmallSignal(sys).ElectromechanicalModes().Count == 3);
        Assert.Equal(11, svc.Simulate(sys, [], new SimulationOptions { EndTimeS = 0.1, TimeStepS = 0.01 }).Time.Length);
    }
}
