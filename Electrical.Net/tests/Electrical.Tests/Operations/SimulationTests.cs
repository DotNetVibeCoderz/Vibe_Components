using Electrical.Core.Data;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Forecasting;
using Electrical.PowerFlow;
using Electrical.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class SimulationTests(ITestOutputHelper output)
{
    [Fact]
    public void Qsts_Case33Day_ConvergesAndBalancesEnergy()
    {
        var sim = QstsSimulation.Case33Demo(days: 1, pvMw: 1.5);
        var r = sim.Run();
        output.WriteLine($"steps {r.Steps}, load {r.EnergyLoadMwh:0.##} MWh, PV {r.EnergyPvMwh:0.##} MWh, losses {r.EnergyLossesMwh * 1000:0.#} kWh, " +
                         $"import {r.EnergyImportMwh:0.##} MWh, Vmin {r.VminPu.Min():0.####}, Vmax {r.VmaxPu.Max():0.####}, avg it {r.Iterations.Average():0.##}");
        Assert.Equal(96, r.Steps);
        Assert.All(r.Converged, Assert.True);
        for (int t = 0; t < r.Steps; t++) Assert.Equal(r.LoadMw[t] + r.LossesMw[t], r.GridMw[t] + r.PvMw[t], 5);
        Assert.Equal(r.EnergyLoadMwh + r.EnergyLossesMwh, r.EnergyImportMwh - r.EnergyExportMwh + r.EnergyPvMwh, 4);
        Assert.True(r.PvMw[48] > 0.5 && r.PvMw[0] == 0); // noon vs midnight
        Assert.True(r.VminPu.Min() > 0.9);
        // warm-started step equals an independent flat-start power flow of the same snapshot
        int k = 70;
        var snap = sim.BaseNetwork.Clone();
        foreach (var l in snap.Loads) { double m = sim.CategoryProfiles[l.Category!][k]; l.P *= m; l.Q *= m; }
        snap.Generators.Add(new Generator { Bus = 18, P = sim.PvSystems[0].RatedMw * sim.PvSystems[0].ProfilePu[k] });
        var pf = PowerFlowSolver.Solve(snap);
        Assert.Equal(pf.LossesMw, r.LossesMw[k], 7);
        Assert.Equal(pf.Bus(18).Vm, r.VoltageOf(18)[k], 7);
        var series = r.ToChartSeries();
        Assert.All(series.Values, v => Assert.Equal(96, v.Length));
        Assert.Equal(23.75, series["hours"][^1], 9);
    }

    [Fact]
    public void Qsts_EventsTimeline_GeneratorTripAndLoadShedding()
    {
        var net = TestCases.Case9();
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sim = new QstsSimulation(net) { DefaultLoadProfile = new TimeSeries(start, TimeSpan.FromHours(1), Enumerable.Repeat(1.0, 6).ToArray()) };
        sim.Events.Add(new SimulationEvent(TimeSpan.FromHours(2), SimulationEventKind.GeneratorTrip, 2, Description: "G3 trips"));
        sim.Events.Add(new SimulationEvent(TimeSpan.FromHours(4), SimulationEventKind.LoadScale, 0, 0.8, "20 % load shedding"));
        var r = sim.Run();
        Assert.Equal(2, r.Timeline.Count);
        Assert.Equal(start.AddHours(2), r.Timeline[0].Time);
        Assert.Equal(71.64, r.GridMw[0], 1);                 // MATPOWER case9 slack output
        Assert.True(r.GridMw[2] > r.GridMw[1] + 80);         // slack picks up G3's 85 MW
        Assert.Equal(0.8 * 315, r.LoadMw[5], 9);
        Assert.Equal(1.0, sim.BaseNetwork.Generators[2].InService ? 1.0 : 0.0); // base untouched
    }

    [Fact]
    public void ScenarioManager_ClonesAndCompares()
    {
        var mgr = new ScenarioManager(TestCases.Case33BW())
            .Add("Peak +20 %", ScenarioManager.ScaleLoads(1.2))
            .Add("PV 1 MW @18", ScenarioManager.AddPv(18, 1.0))
            .Add("Cap 0.9 Mvar @30", ScenarioManager.AddCapacitor(30, 0.9));
        var s = mgr.RunPowerFlows();
        Assert.Equal(["Base", "Peak +20 %", "PV 1 MW @18", "Cap 0.9 Mvar @30"], s.Select(x => x.Scenario).ToArray());
        Assert.Equal(202.7, s[0].LossesMw * 1000, 0.5);
        Assert.True(s[1].LossesMw > s[0].LossesMw * 1.3);    // losses ~ load²
        Assert.True(s[2].LossesMw < s[0].LossesMw && s[3].LossesMw < s[0].LossesMw);
        Assert.Equal(3.715, mgr.BaseNetwork.TotalLoadMw, 9);
        Assert.Throws<ArgumentException>(() => mgr.Add("base", _ => { }));
    }

    [Fact]
    public void CapacityOutageTable_TextbookValues()
    {
        // Billinton & Allan §2.2: two 50 MW units with FOR 0.02 → P(0 out) = 0.9604, P(50 out) = 0.0392, P(100 out) = 0.0004
        var units = new[] { new GeneratingUnit("A", 50, 980, 20), new GeneratingUnit("B", 50, 980, 20) };
        var t = AdequacyAssessment.CapacityOutageTable(units);
        Assert.Equal(3, t.Count);
        Assert.Equal(0.9604, t[0].Probability, 12);
        Assert.Equal(0.0392, t[1].Probability, 12);
        Assert.Equal(0.0004, t[2].Probability, 12);
        var a = AdequacyAssessment.Analytical(units, [60, 40]);
        Assert.Equal(0.0396 + 0.0004, a.LoleHoursPerYear, 12);
        Assert.Equal(0.0392 * 10 + 0.0004 * 60 + 0.0004 * 40, a.EensMwhPerYear, 12);
    }

    [Fact]
    public void SequentialMonteCarlo_AgreesWithAnalytical()
    {
        // five 50 MW units, FOR 2 %, MTTR 20 h, annual load curve peaking at 200 MW
        var units = Enumerable.Range(1, 5).Select(i => new GeneratingUnit($"U{i}", 50, 980, 20)).ToArray();
        var load = AdequacyAssessment.SyntheticAnnualLoad(200);
        var exact = AdequacyAssessment.Analytical(units, load);
        var mc = AdequacyAssessment.SequentialMonteCarlo(units, load, maxYears: 400, seed: 99);
        var mc2 = AdequacyAssessment.SequentialMonteCarlo(units, load, maxYears: 400, seed: 99);
        output.WriteLine($"LOLE analytical {exact.LoleHoursPerYear:0.###} h/yr, MC {mc.LoleHoursPerYear:0.###} h/yr; EENS {exact.EensMwhPerYear:0.#} vs {mc.EensMwhPerYear:0.#} MWh/yr; LOLF {mc.LolfPerYear:0.##}/yr, CoV {mc.CoefficientOfVariation:P1}");
        Assert.Equal(exact.LoleHoursPerYear, mc.LoleHoursPerYear, exact.LoleHoursPerYear * 0.1);
        Assert.Equal(exact.EensMwhPerYear, mc.EensMwhPerYear, exact.EensMwhPerYear * 0.12);
        Assert.Equal(mc.LoleHoursPerYear, mc2.LoleHoursPerYear);
        Assert.Equal(400, mc.RunningLole.Length);
        Assert.True(mc.LolfPerYear > 0 && mc.LolfPerYear < mc.LoleHoursPerYear);

        var sp = new ServiceCollection().AddElectrical(b => b.AddSimulation()).BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<ISimulationService>());
        Assert.NotNull(sp.GetRequiredService<IForecastingService>());
    }
}
