using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.DigitalTwin;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.StateEstimation;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class DigitalTwinTests
{
    [Fact]
    public void Simulator_TracksTruthOverADay()
    {
        var sim = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions { Steps = 24 });
        var run = sim.Run();
        Assert.Equal(24, run.States.Count);
        Assert.All(run.States, s => Assert.True(s.Converged, s.ToString()));
        // RTU noise σ_V = 0.001 pu → WLS estimate well within 0.005 pu of the true voltages
        Assert.True(run.MaxVoltageErrorPu < 0.005, $"max error {run.MaxVoltageErrorPu}");
        Assert.True(run.MeanVoltageErrorPu < 0.0015, $"mean error {run.MeanVoltageErrorPu}");
        for (int i = 0; i < run.States.Count; i++)
        {
            double truthLoad = run.Truth[i].TotalLoadMw;
            Assert.InRange(run.States[i].EstimatedLoadMw, truthLoad * 0.97, truthLoad * 1.03);
        }
        // the residential profile peaks in the evening
        int peak = run.States.Select((s, i) => (s.EstimatedLoadMw, i)).MaxBy(x => x.EstimatedLoadMw).i;
        Assert.InRange(peak, 18, 21);
        Assert.Equal(24, sim.Twin.History.Count);
    }

    [Fact]
    public void BadData_IsRemovedAndPublished()
    {
        var bus = new InMemoryGridEventBus();
        var bad = new List<TwinBadMeasurement>();
        bus.Subscribe<TwinBadMeasurement>(bad.Add);
        var sim = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions
        {
            Steps = 4,
            BadData = [new TwinBadDataInjection(2, "BR3.PF", 150)],
        }, bus: bus);
        var run = sim.Run();
        Assert.Empty(run.States[1].RemovedMeasurements);
        var removed = Assert.Single(run.States[2].RemovedMeasurements);
        Assert.Equal(MeasurementType.ActiveFlow, removed.Measurement.Type);
        Assert.Equal(3, removed.Measurement.Location);
        Assert.True(removed.NormalizedResidual > 3);
        Assert.Single(bad);
        Assert.Contains(run.States[2].Alarms, a => a.Kind == TwinAlarmKind.BadData && a.Message.Contains("rejected as bad data"));
        // the estimate is still accurate after removal
        Assert.True(run.MaxVoltageErrorPu < 0.005);
    }

    [Fact]
    public void Explainer_AttributesUndervoltageToOverloadedLine()
    {
        // Baran & Wu feeder: Vmin ≈ 0.913 pu at bus 18 in the base case
        var net = TestCases.Case33BW();
        Assert.True(BranchRatings.AssignMissing(net) > 0);
        var baseAlarms = AlarmExplainer.Explain(PowerFlowSolver.Solve(net));
        var uv18 = Assert.Single(baseAlarms, a => a.Kind == TwinAlarmKind.Undervoltage && a.Element == "bus 18");
        Assert.StartsWith("Bus 18 voltage 0.91", uv18.Message);
        Assert.Contains("below 0.95 because", uv18.Message);
        Assert.Contains("karena", uv18.MessageId);
        Assert.DoesNotContain(baseAlarms, a => a.Kind == TwinAlarmKind.Overload); // rated at 125 % of the base flow

        var heavy = net.Clone();
        new ScaleLoad(1.5).Apply(heavy);
        var alarms = AlarmExplainer.Explain(PowerFlowSolver.Solve(heavy));
        var uv = alarms.First(a => a.Kind == TwinAlarmKind.Undervoltage && a.Element == "bus 18");
        Assert.Matches(@"^Bus 18 voltage 0\.\d{3} pu below 0\.95 because line \d+-\d+ carries \d+% load$", uv.Message);
        Assert.Contains(uv.Causes, c => c.StartsWith("overload:"));
        Assert.Contains(alarms, a => a.Kind == TwinAlarmKind.Overload && a.Message.Contains("overloaded"));
    }

    [Fact]
    public void Explainer_AttributesOverloadToOutage()
    {
        var net = TestCases.Case14();
        BranchRatings.AssignMissing(net);
        var r = WhatIfSimulator.Run(net, [new OpenBranch(0)]); // line 1-2 carries ≈ 157 MW in the base case
        Assert.True(r.Scenario.Converged);
        Assert.True(r.Scenario.MaxLoadingPercent > r.Baseline.MaxLoadingPercent);
        var ov = r.Alarms.First(a => a.Kind == TwinAlarmKind.Overload);
        Assert.Contains("after the outage of line 1-2", ov.Message);
        Assert.Contains("outage:1-2", ov.Causes);
        Assert.Contains("What-if [open branch #0]", r.Summary);
    }

    [Fact]
    public void AssetHealth_FollowsIeeeC5791()
    {
        var p = TransformerThermalParameters.Default;
        Assert.Equal(110, p.HotSpotC(1.0, 30), 6);                                    // rated hot spot
        Assert.Equal(1.0, TransformerThermalParameters.AgingAccelerationFactor(110), 9); // F_AA = 1 at 110 °C
        Assert.Equal(2.0, TransformerThermalParameters.AgingAccelerationFactor(116.8), 1); // ≈ doubles every 6–7 K
        Assert.True(p.HotSpotC(0.5, 30) < 80);

        var net = TestCases.Case14();
        BranchRatings.AssignMissing(net);
        var pf = PowerFlowSolver.Solve(net);
        var tracker = new AssetHealthTracker();
        tracker.Update(net, pf.Branches, 2, 30);
        tracker.Update(net, pf.Branches, 2, 30);
        var tx = tracker.Assets.First(a => a.IsTransformer);
        Assert.Equal(4, tx.ElapsedHours, 9);
        Assert.Equal(tx.AgingFactor * 4, tx.EquivalentAgingHours, 9);
        Assert.InRange(tx.HealthIndex, 80, 100); // 80 % loading: below normal ageing

        var hot = pf.Branches.Select(b => b with { PFrom = b.PFrom * 1.4, QFrom = b.QFrom * 1.4, PTo = b.PTo * 1.4, QTo = b.QTo * 1.4 }).ToList();
        var t2 = new AssetHealthTracker();
        t2.Update(net, hot, 1, 30);
        Assert.True(t2.Get(tx.BranchIndex)!.AgingFactor > 1);
        Assert.True(t2.Get(tx.BranchIndex)!.HealthIndex < tx.HealthIndex);
    }

    [Fact]
    public void WhatIf_CapacitorAndPvImproveFeeder()
    {
        var sim = new TwinSimulator(TestCases.Case33BW(), new TwinSimulatorOptions { Steps = 20 });
        sim.Run();
        var twin = sim.Twin;
        Assert.True(twin.Current!.Converged);
        var cap = twin.WhatIf(new AddShuntCapacitor(30, 1.0));
        Assert.True(cap.Scenario.LossesMw < cap.Baseline.LossesMw);
        Assert.True(cap.Scenario.MinVoltagePu > cap.Baseline.MinVoltagePu);
        var pv = twin.WhatIf(new AddPv(18, 0.5));
        Assert.True(pv.DeltaMinVoltagePu > 0);
        Assert.Contains("add 0.5 MW PV at bus 18", pv.Description);
        Assert.Contains("tambah PV", pv.DescriptionId);
        var outage = twin.WhatIf(new OpenBranch(0)); // cutting the substation feeder islands everything
        Assert.False(outage.Scenario.Converged);
    }

    [Fact]
    public void Forecast_DeviationAndTopologyChangesAreExplained()
    {
        var sim = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions
        {
            Steps = 6,
            ForecastBias = -0.2, // forecast 20 % low → actual ≈ 25 % above forecast
            Events = [new TwinScenarioEvent(3, new OpenBranch(6))],
        });
        var run = sim.Run();
        Assert.All(run.States, s => Assert.InRange(s.ForecastDeviationPercent!.Value, 20, 30));
        Assert.All(run.States, s => Assert.Contains(s.Alarms, a => a.Kind == TwinAlarmKind.ForecastDeviation && a.Message.Contains("above forecast")));
        Assert.True(sim.Twin.Model.Branches[6].InService == false);
        Assert.Equal([6], sim.Twin.OpenBranches());
        Assert.True(run.States[2].Kpis.MaxLoadingPercent < run.States[3].Kpis.MaxLoadingPercent + 50);
        // forecast-deviation alarm raised once (stays active), not every cycle
        Assert.Single(run.RaisedAlarms, a => a.Kind == TwinAlarmKind.ForecastDeviation);
        Assert.Contains("deviation from forecast", sim.Twin.Summary());
        Assert.Contains("deviasi terhadap prakiraan", sim.Twin.Summary("id"));
    }

    [Fact]
    public void TagMapper_AndDependencyInjection()
    {
        Assert.True(TagMeasurementMapper.TryParse("RTU1.BR12.QT", out var p, "RTU1."));
        Assert.Equal(new TagPoint("BR", 12, "QT"), p);
        Assert.False(TagMeasurementMapper.TryParse("PUMP.SPEED", out _));

        var services = new ServiceCollection().AddElectrical(b => b.AddDigitalTwin()).BuildServiceProvider();
        var reg = services.GetRequiredService<ElectricalModuleRegistry>();
        Assert.Contains("DigitalTwin", reg.Modules);
        Assert.Contains("StateEstimation", reg.Modules);
        var bus = services.GetRequiredService<IGridEventBus>();
        int updates = 0;
        bus.Subscribe<TwinStateUpdated>(_ => updates++);
        var twin = services.GetRequiredService<IGridTwinFactory>().Create(TestCases.Case9());
        var pf = PowerFlowSolver.Solve(TestCases.Case9());
        twin.Ingest(MeasurementGenerator.FromPowerFlow(pf, noise: 0.5));
        var s = twin.Update();
        Assert.True(s.Converged);
        Assert.Equal(1, updates);
        Assert.Equal(pf.TotalLoadMw, s.EstimatedLoadMw, 0);
        Assert.Equal(0, twin.PendingMeasurements);
        // no measurements → explained estimator failure
        var f = twin.Update();
        Assert.False(f.Converged);
        Assert.Equal(TwinAlarmKind.EstimatorFailure, f.Alarms[0].Kind);
    }
}
