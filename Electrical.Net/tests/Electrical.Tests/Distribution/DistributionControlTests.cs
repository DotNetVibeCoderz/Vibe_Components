using Electrical.Core.Hosting;
using Electrical.Distribution;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class DistributionControlTests
{
    [Fact]
    public void OltcController_BringsLoadBusIntoBand()
    {
        var net = DistributionTestCases.Ieee4Node();
        var before = UnbalancedPowerFlow.Solve(net);
        Assert.True(before.Bus("4").VmPu.Average() < 0.95);   // ≈ 0.83 pu without tap control
        var oltc = new OltcController(net.Transformer("T1"), "4") { TargetPu = 1.0, BandwidthPu = 0.02, MaxTapPu = 1.25, StepPu = 0.00625 };
        net.Controllers.Add(oltc);
        var r = UnbalancedPowerFlow.Solve(net);
        Assert.True(r.Converged);
        Assert.InRange(oltc.MeasuredPu(r), 0.99, 1.01);
        Assert.True(net.Transformer("T1").TapSecondary > 1.1);
        Assert.True(oltc.Operations >= oltc.TapPosition);   // constant-power load: the first estimate may overshoot by a few steps
    }

    [Fact]
    public void CapacitorController_VoltageMode_SwitchesWithHysteresis()
    {
        var net = DistributionTestCases.Ieee13Node();
        var cap = net.Capacitor("cap675");
        cap.IsOn = false;
        var low = UnbalancedPowerFlow.Solve(net, new DistributionPowerFlowOptions { ControlMode = ControlMode.Off });
        double vOff = low.Bus("675").VmPu[0];
        var ctrl = new CapacitorController(cap, CapacitorControlMode.Voltage) { MonitoredPhase = 0, OnSetting = vOff + 0.005, OffSetting = 1.08 };
        net.Controllers.Add(ctrl);
        var r = UnbalancedPowerFlow.Solve(net);
        Assert.True(cap.IsOn);
        Assert.Equal(1, ctrl.Operations);
        Assert.True(r.Bus("675").VmPu[0] > vOff);
        // var control on the feeder head: switching off when the head imports less than the on threshold
        var var = new CapacitorController(net.Capacitor("cap611"), CapacitorControlMode.ReactivePower)
        { MonitoredElement = "650632", OnSetting = 5000, OffSetting = 4000 };
        net.Controllers.Add(var);
        UnbalancedPowerFlow.Solve(net);
        Assert.False(net.Capacitor("cap611").IsOn);
    }

    [Fact]
    public void TimeSeries_DailyProfileCountsTapOperations()
    {
        var net = DistributionTestCases.Ieee13Node(automaticRegulator: true);
        net.Controllers.Add(new CapacitorController(net.Capacitor("cap675"), CapacitorControlMode.Time) { OnSetting = 7, OffSetting = 22 });
        var shape = DistributionTimeSeries.DailyLoadShape();
        var ts = DistributionTimeSeries.Run(net, shape, 3600);
        Assert.Equal(24, ts.Steps.Count);
        Assert.True(ts.AllConverged);
        Assert.True(ts.TapOperations > 10);
        Assert.Equal(3, ts.CapacitorSwitchings);   // initially on: off at 00:00, on at 07:00, off at 22:00
        Assert.False(ts.Steps[3].CapacitorStates["cap675"]);
        Assert.True(ts.Steps[12].CapacitorStates["cap675"]);
        // peak hour has higher taps than the night valley on phase A
        Assert.True(ts.Steps[18].RegulatorTaps["Reg"][0] > ts.Steps[3].RegulatorTaps["Reg"][0]);
        Assert.InRange(ts.MinVoltagePu, 0.95, 1.0);
        Assert.True(ts.LossesKwh > 0 && ts.EnergyKwh > 24 * 0.46 * 3000);

        // deterministic: a second identical run gives identical results
        var net2 = DistributionTestCases.Ieee13Node(automaticRegulator: true);
        net2.Controllers.Add(new CapacitorController(net2.Capacitor("cap675"), CapacitorControlMode.Time) { OnSetting = 7, OffSetting = 22 });
        var ts2 = DistributionTimeSeries.Run(net2, shape, 3600);
        Assert.Equal(ts.LossesKwh, ts2.LossesKwh, 9);
    }

    [Fact]
    public void TimeSeries_TimeDelayHoldsTapChanges()
    {
        var net = DistributionTestCases.Ieee13Node(automaticRegulator: true);
        var ctrl = (VoltageRegulatorController)net.Controllers[0];
        ctrl.TimeDelay = 30;
        var ts = DistributionTimeSeries.Run(net, [1.0, 1.0, 1.0, 1.0], stepSeconds: 10);
        Assert.All(ts.Steps.Take(2), s => Assert.Equal(0, s.RegulatorTaps["Reg"][0]));   // 0 s and 10 s: delay not elapsed
        Assert.True(ts.Steps[2].RegulatorTaps["Reg"][0] > 0);                                // acts at 20 s (20 + 10 ≥ 30)
    }

    [Fact]
    public void ToBalanced_PositiveSequenceEquivalentSolvesWithNewton()
    {
        var net = DistributionTestCases.Ieee4Node();
        var three = UnbalancedPowerFlow.Solve(net);
        var pn = net.ToBalanced(baseMva: 10);
        Assert.Empty(pn.Validate());
        var r = PowerFlowSolver.Solve(pn);
        Assert.True(r.Converged);
        double avg = three.Bus("4").VmPu.Average();
        Assert.Equal(avg, r.Bus(4).Vm, 2);
        Assert.Equal(5.4, r.TotalLoadMw, 6);

        var p13 = DistributionTestCases.Ieee13Node().ToBalanced();
        var r13 = PowerFlowSolver.Solve(p13);
        Assert.True(r13.Converged);
        Assert.InRange(r13.Bus(p13.Buses.First(b => b.Name == "675").Id).Vm, 0.97, 1.04);
    }

    [Fact]
    public void AddDistribution_RegistersService()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddDistribution().AddDistribution()).BuildServiceProvider();
        var svc = sp.GetRequiredService<IDistributionService>();
        var r = svc.Solve(DistributionTestCases.Ieee4Node());
        Assert.True(r.Converged);
        Assert.Contains("Distribution", sp.GetRequiredService<Electrical.Core.Hosting.ElectricalModuleRegistry>().Modules);
        Assert.Equal(4, svc.ReadOpenDss("New Circuit.c basekv=12.47\nNew Line.a bus1=sourcebus bus2=x\nNew Line.b bus1=x bus2=y\nNew Line.c bus1=y bus2=z").Buses.Count);
    }
}
