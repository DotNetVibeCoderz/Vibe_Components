using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Renewable;
using Electrical.SmartGrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class SmartGridTests(ITestOutputHelper output)
{
    private static readonly double[] DailyLoad =
        [52, 48, 46, 45, 46, 52, 62, 72, 78, 80, 82, 84, 83, 82, 82, 84, 88, 96, 100, 98, 92, 80, 68, 58];

    [Fact]
    public void PriceBasedDemandResponse_ShiftsPeak()
    {
        var price = Enumerable.Range(0, 24).Select(h => h is >= 17 and < 22 ? 150.0 : h is >= 8 and < 17 ? 80 : 40).ToArray();
        var r = DemandResponse.PriceBased(DailyLoad, price, selfElasticity: -0.2, crossElasticity: 0.005);
        Assert.True(r.PeakReductionPercent > 5, $"{r.PeakReductionPercent:0.#} %");
        // closed form for hour 18: d = d0·(1 + E·Δρ_t/ρ0 + c·Σ_{s≠t}Δρ_s/ρ0)
        double rho0 = price.Average(), sumRel = price.Sum(p => (p - rho0) / rho0);
        double rel18 = (150 - rho0) / rho0;
        Assert.Equal(100 * (1 - 0.2 * rel18 + 0.005 * (sumRel - rel18)), r.NewMw[18], 9);
        var shifted = DemandResponse.PriceBased(DailyLoad, price, -0.2, 0.005, conserveEnergy: true);
        Assert.Equal(0, shifted.EnergyChangeMwh, 9);
        Assert.True(shifted.CostAfter < shifted.CostBefore);
    }

    [Fact]
    public void BatteryPeakShaving_FindsLowestHoldablePeak()
    {
        var bat = new BatteryStorage(60, 15, 0.9) { SocMin = 0.1, SocMax = 0.95 };
        var r = DemandResponse.OptimalPeakShaving(DailyLoad, bat);
        output.WriteLine($"peak {r.OriginalPeakMw} → {r.NewPeakMw:0.##} MW");
        Assert.True(r.ThresholdMet);
        Assert.True(r.NewPeakMw < 92 && r.NewPeakMw > 80);
        for (int t = 0; t < 24; t++) Assert.Equal(DailyLoad[t] - r.BatteryMw[t], r.NetMw[t], 9);
        Assert.Equal(0.9, bat.Soc); // original battery untouched
    }

    [Fact]
    public void EvCharging_ValleyFillingFlattensLoadAndMeetsEnergy()
    {
        // 24 h horizon starting at 12:00, residential base load [kW]
        var baseLoad = Enumerable.Range(0, 24).Select(t => 300 + 250 * Math.Exp(-Math.Pow((t - 7.5) / 2.5, 2))).ToArray();
        var fleet = EvCharging.SyntheticFleet(60, feeders: 2);
        var dumb = EvCharging.Uncontrolled(fleet, baseLoad);
        var smart = EvCharging.ValleyFilling(fleet, baseLoad, feederCapacityKw: new Dictionary<int, double> { [0] = 150, [1] = 150 });
        output.WriteLine($"peak uncontrolled {dumb.PeakKw:0} kW, smart {smart.PeakKw:0} kW, LF {dumb.LoadFactor:0.###} → {smart.LoadFactor:0.###}");
        Assert.Equal(0, dumb.TotalUnmetKwh, 6);
        Assert.Equal(0, smart.TotalUnmetKwh, 6);
        Assert.True(smart.PeakKw < dumb.PeakKw - 50);
        Assert.Equal(dumb.EvLoadKw.Sum(), smart.EvLoadKw.Sum(), 6);
        for (int t = 0; t < 24; t++)
            foreach (int f in new[] { 0, 1 })
                Assert.True(Enumerable.Range(0, fleet.Count).Where(s => fleet[s].Feeder == f).Sum(s => smart.PowerKw[s, t]) <= 150 + 1e-9);
        for (int s = 0; s < fleet.Count; s++)
            for (int t = 0; t < 24; t++)
                if (t < fleet[s].ArrivalStep || t >= fleet[s].DepartureStep) Assert.Equal(0, smart.PowerKw[s, t]);
    }

    [Fact]
    public void Microgrid_DynamicProgrammingBeatsRules_AndBalances()
    {
        var cfg = MicrogridConfig.Demo();
        var rule = MicrogridEms.RuleBased(cfg);
        var opt = MicrogridEms.Optimal(cfg with { KeepFinalSoc = false });
        var optKeep = MicrogridEms.Optimal(cfg);
        output.WriteLine($"rule ${rule.TotalCost:0.##}, DP ${opt.TotalCost:0.##}, DP(final SoC ≥ initial) ${optKeep.TotalCost:0.##}");
        Assert.True(opt.TotalCost <= rule.TotalCost + 1e-6);
        Assert.True(optKeep.Soc[^1] >= cfg.Battery.Soc - 0.01);
        foreach (var d in new[] { rule, opt, optKeep })
            for (int t = 0; t < 24; t++)
            {
                double supply = cfg.PvKw[t] + d.BatteryKw[t] + d.DieselKw[t] + d.GridKw[t] - d.CurtailedKw[t] + d.UnservedKw[t];
                Assert.Equal(cfg.LoadKw[t], supply, 6);
                Assert.True(d.GridKw[t] <= cfg.GridImportLimitKw + 1e-9 && -d.GridKw[t] <= cfg.GridExportLimitKw + 1e-9);
                Assert.True(d.DieselKw[t] == 0 || d.DieselKw[t] >= cfg.DieselMinLoading * cfg.DieselMaxKw - 1e-9);
            }
    }

    [Fact]
    public void VirtualPowerPlant_EnvelopeBidAndDispatch()
    {
        var pvShape = Enumerable.Range(0, 24).Select(h => h is >= 6 and <= 18 ? Math.Sin(Math.PI * (h - 6) / 12) : 0).ToArray();
        var vpp = new VirtualPowerPlant()
            .Add(new DerResource("PV", DerKind.Solar, 0, 5, 0, pvShape))
            .Add(new DerResource("CHP", DerKind.Chp, 1, 3, 60))
            .Add(new DerResource("DR", DerKind.FlexibleLoad, -2, 0, 120))
            .Add(new DerResource("BESS", DerKind.Battery, 0, 0, 90, Battery: new BatteryStorage(8, 4, 0.5)));
        var env = vpp.Envelope(24);
        Assert.Equal(1 - 2 - new BatteryStorage(8, 4, 0.5).AvailableChargeMw(1), env.MinMw[0], 9);
        Assert.Equal(5 + 3 + 0 + new BatteryStorage(8, 4, 0.5).AvailableDischargeMw(1), env.MaxMw[12], 9); // BESS SoC-limited to 3.04 MW
        var bid = vpp.BidCurve(12);
        Assert.Equal(["PV", "CHP", "BESS", "DR"], bid.Select(b => b.Resource).ToArray());
        Assert.True(bid.Zip(bid.Skip(1)).All(p => p.First.PricePerMwh <= p.Second.PricePerMwh));
        Assert.Equal(env.MaxMw[12], bid[^1].CumulativeMw, 9);
        var (sp, cost, delivered) = vpp.Dispatch(8, 12);
        Assert.Equal(8, delivered, 9);
        Assert.Equal(5, sp["PV"], 9);
        Assert.Equal(3, sp["CHP"], 9);
        Assert.Equal(2.0, sp["BESS"], 9); // from its charge floor (−3.79 MW) the BESS is raised to +2 MW to close the gap
        Assert.Equal(-2, sp["DR"], 9);
        Assert.True(cost > 0);
    }

    [Fact]
    public void VoltVar_Case33_ReducesLossesAndFixesVoltages()
    {
        var r = VoltVarOptimization.Optimize(TestCases.Case33BW(), VoltVarControls.Case33Default());
        output.WriteLine($"losses {r.LossesBeforeMw * 1000:0.#} → {r.LossesAfterMw * 1000:0.#} kW, V [{r.VminBefore:0.###},{r.VmaxBefore:0.###}] → [{r.VminAfter:0.###},{r.VmaxAfter:0.###}], " +
                         $"OLTC {r.SubstationVoltagePu:0.#####}, caps {string.Join(",", r.CapacitorMvar.Select(c => $"{c.Key}:{c.Value}"))}, inv {string.Join(",", r.InverterMvar.Select(c => $"{c.Key}:{c.Value:0.#}"))}, PFs {r.PowerFlows}");
        Assert.True(r.Feasible);
        Assert.True(r.VminBefore < 0.95);
        Assert.True(r.VminAfter >= 0.95 - 1e-9 && r.VmaxAfter <= 1.05 + 1e-9);
        Assert.True(r.LossesAfterMw < 0.7 * r.LossesBeforeMw);
    }

    [Fact]
    public void Reconfiguration_Case33_ReachesKnownOptimum()
    {
        var net = FeederReconfiguration.Case33WithTies();
        var r = FeederReconfiguration.Optimize(net);
        output.WriteLine($"open {string.Join(", ", r.OpenBranches)}; losses {r.LossesBeforeMw * 1000:0.##} → {r.LossesAfterMw * 1000:0.##} kW; Vmin {r.MinVoltageAfter:0.####}; PFs {r.PowerFlows}");
        Assert.Equal(202.7, r.LossesBeforeMw * 1000, 0.5);
        Assert.True(r.LossesAfterMw * 1000 < 145);
        Assert.True(Topology.IsRadial(r.Network));
        Assert.Single(Topology.Islands(r.Network));
        Assert.Equal(5, r.OpenBranches.Count);
    }

    [Fact]
    public void RadialReliability_BillintonAllanExample()
    {
        // Billinton & Allan, Reliability Evaluation of Power Systems (2nd ed.), §7.2 simple radial system:
        // sections 1, 2, 3 with λ = 0.2, 0.1, 0.15 f/yr and r = 6, 5, 8 h; load points A, B, C at their ends.
        // Table 7.1: λ_A = 0.2, U_A = 1.2 h; λ_B = 0.3, U_B = 1.7 h (r 5.67); λ_C = 0.45, U_C = 2.9 h (r 6.44).
        var b = new NetworkBuilder("radial3", 10);
        b.AddBus(1, 11, BusType.Slack); b.AddBus(2, 11); b.AddBus(3, 11); b.AddBus(4, 11);
        b.AddLineOhm(1, 2, 0.1, 0.1); b.AddLineOhm(2, 3, 0.1, 0.1); b.AddLineOhm(3, 4, 0.1, 0.1);
        b.AddLoad(2, 1.0, 0.2); b.AddLoad(3, 2.0, 0.4); b.AddLoad(4, 0.5, 0.1);
        b.AddGenerator(1, 0, kind: GeneratorKind.Grid);
        var net = b.Build();
        var comps = new Dictionary<int, ComponentReliability> { [0] = new(0.2, 6, true), [1] = new(0.1, 5, true), [2] = new(0.15, 8, true) };
        var cust = new Dictionary<int, int> { [2] = 100, [3] = 50, [4] = 25 };
        var r = RadialReliability.Evaluate(net, comps, cust);
        var a = r.LoadPoints.Single(p => p.BusId == 2); var bb = r.LoadPoints.Single(p => p.BusId == 3); var c = r.LoadPoints.Single(p => p.BusId == 4);
        Assert.Equal(0.2, a.Lambda, 12); Assert.Equal(1.2, a.UnavailabilityHours, 12);
        Assert.Equal(0.3, bb.Lambda, 12); Assert.Equal(1.7, bb.UnavailabilityHours, 12); Assert.Equal(5.67, bb.AverageOutageHours, 2);
        Assert.Equal(0.45, c.Lambda, 12); Assert.Equal(2.9, c.UnavailabilityHours, 12); Assert.Equal(6.44, c.AverageOutageHours, 2);
        Assert.Equal((0.2 * 100 + 0.3 * 50 + 0.45 * 25) / 175, r.Saifi, 12);
        Assert.Equal((1.2 * 100 + 1.7 * 50 + 2.9 * 25) / 175, r.Saidi, 12);
        Assert.Equal(r.Saidi / r.Saifi, r.Caidi, 12);
        Assert.Equal(1 - r.Saidi / 8760, r.Asai, 12);
        Assert.Equal(1.0 * 1.2 + 2.0 * 1.7 + 0.5 * 2.9, r.EnsMwh, 12);
        // Without fuses but with 1 h sectionalizing: A also sees downstream faults for the switching time
        var noFuse = comps.ToDictionary(kv => kv.Key, kv => kv.Value with { ProtectedByFuse = false });
        var s = RadialReliability.Evaluate(net, noFuse, cust, switchingHours: 1);
        var a2 = s.LoadPoints.Single(p => p.BusId == 2);
        Assert.Equal(0.45, a2.Lambda, 12);
        Assert.Equal(1.2 + 0.1 * 1 + 0.15 * 1, a2.UnavailabilityHours, 12);

        var case33 = RadialReliability.Evaluate(TestCases.Case33BW(), RadialReliability.Uniform(TestCases.Case33BW(), 0.1, 4), switchingHours: 1);
        Assert.True(case33.Saifi > 3 && case33.Asai > 0.998);

        var sp = new ServiceCollection().AddElectrical(e => e.AddSmartGrid()).BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<ISmartGridService>());
        Assert.NotNull(sp.GetRequiredService<IRenewableService>());
    }
}
