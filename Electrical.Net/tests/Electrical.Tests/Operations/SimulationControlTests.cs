using Electrical.Core.Data;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.Simulation;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class SimulationControlTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TimeSeries Flat(int n, double v = 1.0) => new(Start, TimeSpan.FromMinutes(15), Enumerable.Repeat(v, n).ToArray());

    /// <summary>Case33 behind a 10 MVA substation transformer (bus 0 slack), as in <see cref="QstsSimulation.Case33ControlDemo"/>.</summary>
    private static (QstsSimulation Sim, int Tx) Feeder(int steps, double load = 1.0)
    {
        var sim = QstsSimulation.Case33ControlDemo(withControl: false, pvMw: 0);
        sim.CategoryProfiles.Clear();
        sim.PvSystems.Clear();
        sim.DefaultLoadProfile = Flat(steps, load);
        return (sim, sim.BaseNetwork.Branches.Count - 1);
    }

    [Fact]
    public void Case33ControlDemo_OltcAndCapacitorReduceViolations()
    {
        var off = QstsSimulation.Case33ControlDemo(withControl: false).Run();
        var sim = QstsSimulation.Case33ControlDemo(withControl: true);
        var on = sim.Run();
        int vOff = off.VoltageViolationSteps(), vOn = on.VoltageViolationSteps();
        output.WriteLine($"violations (0.95-1.05 pu): without control {vOff}/96, with control {vOn}/96");
        output.WriteLine($"Vmin without {off.VminPu.Min():0.0000}, with {on.VminPu.Min():0.0000}; Vmax with {on.VmaxPu.Max():0.0000}");
        output.WriteLine($"OLTC tap range {on.ControllerState("OLTC T1").Min()}..{on.ControllerState("OLTC T1").Max()}, tap steps {on.TotalOperations("OLTC T1")}; " +
                         $"capacitor ops {on.TotalOperations("Cap bus 30")}, max stages {on.ControllerState("Cap bus 30").Max()}; re-solves {on.ControlIterations.Sum()}");
        Assert.All(on.Converged, Assert.True);
        Assert.True(vOn < vOff);
        Assert.True(on.VminPu.Min() > off.VminPu.Min() + 0.02);
        Assert.Equal(["OLTC T1", "Cap bus 30"], on.ControllerNames);
        Assert.Equal(["OLTC", "Capacitor"], on.ControllerKinds);
        Assert.Equal((96, 2), (on.ControllerStates.GetLength(0), on.ControllerStates.GetLength(1)));
        Assert.True(on.TotalOperations(0) > 0);
        Assert.True(on.TotalOperations(1) > 0);
        Assert.All(on.ControllerState(1), s => Assert.InRange(s, 0, 2));
        Assert.All(on.ControllerState(0), s => Assert.InRange(s, -16, 16));
        // cumulative counters never decrease
        for (int t = 1; t < on.Steps; t++) Assert.True(on.ControllerOperations[t, 0] >= on.ControllerOperations[t - 1, 0]);
        // energy balance still holds with control actions
        for (int t = 0; t < on.Steps; t++) Assert.Equal(on.LoadMw[t] + on.LossesMw[t], on.GridMw[t] + on.PvMw[t], 5);
        // the result is deterministic and the controllers reset between runs
        var again = sim.Run();
        Assert.Equal(on.VminPu, again.VminPu);
        Assert.Equal(on.TotalOperations(0), again.TotalOperations(0));
        // without controllers the new arrays are empty
        Assert.Empty(off.ControllerNames);
        Assert.Empty(off.ControlIterations);
    }

    [Fact]
    public void Oltc_RegulatesIntoBandAndMatchesIndependentPowerFlow()
    {
        var (sim, tx) = Feeder(4);
        var oltc = new OltcController(tx, 1) { Name = "T1", TargetPu = 1.02, BandwidthPu = 0.01 };
        sim.Controllers.Add(oltc);
        var r = sim.Run();
        double v1 = r.VoltageOf(1)[^1];
        output.WriteLine($"tap {oltc.TapPosition}, V1 {v1:0.00000} pu, ops {oltc.Operations}");
        Assert.InRange(v1, 1.015, 1.025);
        Assert.True(oltc.TapPosition > 0);                       // boost against the transformer drop
        Assert.Equal(oltc.TapPosition, oltc.Operations);         // moved once from neutral, then held
        Assert.Equal(oltc.TapPosition, r.ControllerState("T1")[0]);
        Assert.Equal(1, r.ControlIterations[0]);
        Assert.Equal(0, r.ControlIterations[1]);
        // independent power flow with the final tap reproduces the step
        var net = sim.BaseNetwork.Clone();
        net.Branches[tx].Tap = 1 / (1 + oltc.TapPosition * 0.00625);
        Assert.Equal(PowerFlowSolver.Solve(net).Bus(18).Vm, r.VoltageOf(18)[^1], 7);
        Assert.Equal(1, sim.BaseNetwork.Branches[tx].Tap);       // base case untouched
    }

    [Fact]
    public void Oltc_HonoursTapLimitsDelayAndSingleStepMoves()
    {
        // tight limit: cannot reach the target, stays at MaxTap
        var (sim, tx) = Feeder(3);
        var limited = new OltcController(tx, 18) { TargetPu = 1.0, MaxTap = 3, MinTap = -3 };
        sim.Controllers.Add(limited);
        var r = sim.Run();
        Assert.Equal(3, limited.TapPosition);
        Assert.All(r.ControllerState(0), s => Assert.Equal(3, s));

        // delay of 2 steps and one tap per operation: first move at step 2, then one tap every third step
        var (sim2, tx2) = Feeder(9);
        var slow = new OltcController(tx2, 1) { TargetPu = 1.03, DelaySteps = 2, MaxTapsPerOperation = 1 };
        sim2.Controllers.Add(slow);
        var r2 = sim2.Run();
        var taps = r2.ControllerState(0);
        output.WriteLine(string.Join(", ", taps));
        Assert.Equal([0, 0, 1, 1, 1, 2, 2, 2, 3], taps);
        Assert.Equal(3, r2.TotalOperations(0));
    }

    [Fact]
    public void Capacitor_VoltageAndVarModes()
    {
        // voltage mode: low voltage at bus 30 switches both stages in (one per step without delay → both in step 0)
        var (sim, _) = Feeder(3);
        var cap = new CapacitorController(30, 0.3, 2) { OnThreshold = 0.95, OffThreshold = 1.05 };
        sim.Controllers.Add(cap);
        var r = sim.Run();
        Assert.Equal(2, cap.StagesOn);
        Assert.Equal(2, cap.Operations);
        Assert.Equal(0, sim.BaseNetwork.GetBus(30).Bs);              // base case untouched
        var none = Feeder(3).Sim.Run();
        Assert.True(r.VoltageOf(30)[0] > none.VoltageOf(30)[0] + 0.01);

        // var mode on the substation transformer: Q import > 2 Mvar → on; light load (Q < 1 Mvar) → off
        var (sim2, tx) = Feeder(6);
        sim2.DefaultLoadProfile = new TimeSeries(Start, TimeSpan.FromMinutes(15), [1, 1, 1, 0.3, 0.3, 0.3]);
        var varCap = new CapacitorController(30, 0.9, 1)
        {
            Name = "VarCap", Mode = CapacitorControlMode.ReactivePower, MonitoredBranchIndex = tx, OnThreshold = 2.0, OffThreshold = 1.0, DelaySteps = 1,
        };
        sim2.Controllers.Add(varCap);
        var r2 = sim2.Run();
        Assert.Equal([0, 1, 1, 1, 0, 0], r2.ControllerState("VarCap"));
        Assert.Equal(2, r2.TotalOperations("VarCap"));

        Assert.Throws<ArgumentException>(() => new QstsSimulation(TestCases.Case33BW())
        {
            DefaultLoadProfile = Flat(2), Controllers = { new CapacitorController(30, 1) { Mode = CapacitorControlMode.ReactivePower } },
        }.Run());
    }

    [Fact]
    public void Regulator_LineDropCompensationRaisesFarEndVoltage()
    {
        // Regulator on the substation transformer; LDC set to the impedance up to bus 6 (R/X sum of branches 1-6 in pu on 10 MVA).
        var (plainSim, tx) = Feeder(2);
        var net = plainSim.BaseNetwork;
        double rL = 0, xL = 0;
        for (int k = 0; k < 5; k++) { rL += net.Branches[k].R; xL += net.Branches[k].X; }
        var plain = VoltageRegulatorController.Create(net, tx, targetPu: 1.0);
        plainSim.Controllers.Add(plain);
        var rp = plainSim.Run();

        var (ldcSim, tx2) = Feeder(2);
        var ldc = VoltageRegulatorController.Create(ldcSim.BaseNetwork, tx2, rL, xL, targetPu: 1.0);
        ldcSim.Controllers.Add(ldc);
        var rl = ldcSim.Run();
        output.WriteLine($"plain tap {plain.TapPosition} V6 {rp.VoltageOf(6)[^1]:0.0000}; LDC tap {ldc.TapPosition} V6 {rl.VoltageOf(6)[^1]:0.0000} (R {rL:0.0000}, X {xL:0.0000} pu)");
        Assert.Equal("Regulator", ldc.Kind);
        Assert.True(ldc.TapPosition > plain.TapPosition);
        Assert.InRange(rl.VoltageOf(6)[^1], 0.97, 1.03);                       // load-centre voltage near target (full-current LDC overcompensates slightly)
        Assert.True(Math.Abs(rl.VoltageOf(6)[^1] - 1.0) < Math.Abs(rp.VoltageOf(6)[^1] - 1.0));
    }
}
