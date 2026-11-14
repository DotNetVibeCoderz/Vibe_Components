using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Markets;
using Electrical.Optimization;
using Electrical.Optimization.Highs;
using Electrical.Optimization.OrTools;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

/// <summary>Market clearing, LMP decomposition, settlement and FTR validation.</summary>
public class MarketClearingTests(ITestOutputHelper output)
{
    /// <summary>Two buses joined by one line (x = 0.1 pu) limited to 100 MW; bus 1 is the reference.</summary>
    internal static PowerNetwork TwoBus(double limitMw = 100)
    {
        var net = new PowerNetwork { Name = "two-bus", BaseMva = 100 };
        net.Buses.Add(new Bus { Id = 1, Type = BusType.Slack });
        net.Buses.Add(new Bus { Id = 2 });
        net.Branches.Add(new Branch { From = 1, To = 2, X = 0.1, RateA = limitMw });
        return net;
    }

    [Fact]
    public void TwoBus_BindingLine_LmpsAndCongestionRentByHand()
    {
        // G1 @ bus 1 offers 200 MW at 20 $/MWh, G2 @ bus 2 offers 200 MW at 50 $/MWh, 150 MW fixed load at bus 2, line limit 100 MW.
        // By hand: G1 = 100 MW (line at limit), G2 = 50 MW; LMP1 = 20, LMP2 = 50 $/MWh; line shadow price 30 $/MWh;
        // load pays 150·50 = 7500, generators receive 100·20 + 50·50 = 4500 → congestion rent 3000 $ = 100 MW × 30 $/MWh.
        var net = TwoBus();
        SupplyOffer[] offers = [new("G1", 1, [new OfferBlock(200, 20)]), new("G2", 2, [new OfferBlock(200, 50)])];
        DemandBid[] bids = [new("L2", 2) { FixedMw = 150 }];
        var r = MarketClearing.ClearSinglePeriod(net, offers, bids);
        Assert.Equal(SolveStatus.Optimal, r.Status);
        Assert.Equal(100, r.OfferDispatchMw[0], 6);
        Assert.Equal(50, r.OfferDispatchMw[1], 6);
        Assert.Equal(20, r.LmpAt(1), 6);
        Assert.Equal(50, r.LmpAt(2), 6);
        Assert.Equal(20, r.SystemLambda, 6);
        Assert.Equal([20.0, 20.0], r.Components.Energy);
        Assert.Equal(0, r.Components.Congestion[0], 9);
        Assert.Equal(30, r.Components.Congestion[1], 6);
        Assert.All(r.Components.Loss, l => Assert.Equal(0, l));
        var s = r.Settlement;
        Assert.Equal(7500, s.TotalLoadPayment, 6);
        Assert.Equal(4500, s.TotalGeneratorRevenue, 6);
        Assert.Equal(3000, s.CongestionRent, 6);
        Assert.Equal(3000, s.CongestionRentFromShadowPrices, 6);
        var line = Assert.Single(r.Binding);
        Assert.Equal(30, line.ShadowPrice, 6);
        Assert.Equal(100, line.FlowMw, 6);
        // producer surplus: G1 (20 − 20)·100 = 0, G2 (50 − 50)·50 = 0
        Assert.Equal(0, s.ProducerSurplus, 6);
        // FTR 1 → 2 for the full 100 MW line capacity collects exactly the congestion rent
        var ftr = FinancialTransmissionRights.Settle([new FtrObligation("F12", 1, 2, 100)], r);
        Assert.Equal(3000, ftr.TotalPayout, 6);
        Assert.True(ftr.RevenueAdequate);
        Assert.Equal(0, ftr.Surplus, 6);
    }

    [Fact]
    public void TwoBus_UncongestedLine_SinglePrice()
    {
        var net = TwoBus(limitMw: 500);
        SupplyOffer[] offers = [new("G1", 1, [new OfferBlock(200, 20)]), new("G2", 2, [new OfferBlock(200, 50)])];
        var r = MarketClearing.ClearSinglePeriod(net, offers, [new DemandBid("L2", 2) { FixedMw = 150 }]);
        Assert.Equal(150, r.OfferDispatchMw[0], 6);
        Assert.Equal(20, r.LmpAt(1), 6);
        Assert.Equal(20, r.LmpAt(2), 6);
        Assert.Equal(0, r.Settlement.CongestionRent, 6);
        Assert.Empty(r.Binding);
    }

    [Fact]
    public void ElasticDemand_ClearsAtBidPrice_AndWelfareIdentityHolds()
    {
        // single bus: offers 100 MW @ 10 and 100 MW @ 30; bids 120 MW @ 40 and 60 MW @ 25 → clears 120 MW + 0 MW? no:
        // supply 0–100 at 10, 100–200 at 30; demand 0–120 at 40, 120–180 at 25 → intersection at 120 MW, price between 25 and 30:
        // the marginal unit is the 30 $/MWh block (serves 20 MW) while the 25 $/MWh bid is rejected → LMP = 30
        SupplyOffer[] offers = [new("A", 0, [new OfferBlock(100, 10), new OfferBlock(100, 30)])];
        DemandBid[] bids = [new("D", 0) { Blocks = [new OfferBlock(120, 40), new OfferBlock(60, 25)] }];
        var r = MarketClearing.ClearSinglePeriod(null, offers, bids);
        Assert.Equal(120, r.OfferDispatchMw[0], 6);
        Assert.Equal(120, r.BidClearedMw[0], 6);
        Assert.Equal(30, r.LmpAt(0), 6);
        var s = r.Settlement;
        // consumer surplus (40 − 30)·120 = 1200, producer surplus (30 − 10)·100 + 0 = 2000, no congestion
        Assert.Equal(1200, s.ConsumerSurplus, 6);
        Assert.Equal(2000, s.ProducerSurplus, 6);
        Assert.Equal(3200, s.SocialWelfare, 6);
        Assert.Equal(40 * 120 - (10 * 100 + 30 * 20), s.SocialWelfare, 6); // gross benefit − as-offered cost
    }

    internal static (PowerNetwork Net, SupplyOffer[] Offers, DemandBid[] Bids, MarketOptions Options) Case14Market()
    {
        var net = TestCases.Case14();
        // offers: three blocks per generator from the quadratic costs' marginal cost at 1/6, 1/2 and 5/6 of Pmax
        var offers = net.Generators.Select((g, k) => new SupplyOffer($"G{k + 1}", g.Bus,
            [.. new[] { 1 / 6.0, 0.5, 5 / 6.0 }.Select(f => new OfferBlock(g.Pmax / 3, Math.Round(g.Cost.Marginal(f * g.Pmax), 2)))])).ToArray();
        // each load: 80 % fixed + 20 % price responsive at 45 $/MWh
        var bids = net.Loads.Select(l => new DemandBid($"L{l.Bus}", l.Bus) { FixedMw = 0.8 * l.P, Blocks = [new OfferBlock(0.2 * l.P, 45)] }).ToArray();
        var options = new MarketOptions { Scuc = new ScucOptions { RatingOverrides = new Dictionary<int, double> { [0] = 110, [6] = 30 } } };
        return (net, offers, bids, options);
    }

    [Fact]
    public void Case14_CongestionRentEqualsShadowPriceTimesFlow_AndFtrRevenueAdequacy()
    {
        var (net, offers, bids, options) = Case14Market();
        var r = MarketClearing.ClearSinglePeriod(net, offers, bids, options);
        Assert.Equal(SolveStatus.Optimal, r.Status);
        output.WriteLine("LMP: " + string.Join(", ", r.BusIds.Select((b, i) => $"{b}:{r.Lmp[i]:F2}")));
        output.WriteLine($"binding: {string.Join("; ", r.Binding.Select(b => $"br {b.Branch} μ={b.ShadowPrice:F3} f={b.FlowMw:F2}"))}");
        var s = r.Settlement;
        output.WriteLine($"rent {s.CongestionRent:F3} $, Σ μ·f {s.CongestionRentFromShadowPrices:F3} $, welfare {s.SocialWelfare:F2} $");
        Assert.NotEmpty(r.Binding);
        Assert.True(s.CongestionRent > 1);
        Assert.Equal(s.CongestionRent, s.CongestionRentFromShadowPrices, 5);
        for (int i = 0; i < r.Lmp.Length; i++) Assert.Equal(r.Lmp[i], r.Components.Energy[i] + r.Components.Congestion[i], 9);
        // welfare identity: CS + PS + CR = gross benefit (bids, fixed load at VOLL) − as-offered cost
        double gross = bids.Sum(b => b.FixedMw) * options.ValueOfLostLoad;
        for (int b = 0; b < bids.Length; b++) gross += (r.BidClearedMw[b] - bids[b].FixedMw) * 45;
        double cost = offers.Select((o, g) => o.EnergyCost(r.OfferDispatchMw[g])).Sum();
        Assert.Equal(gross - cost, s.SocialWelfare, 4);

        // simultaneously feasible FTR set: deterministic random obligations scaled into the network limits → payouts ≤ congestion rent
        var rnd = new Random(3);
        var ids = net.Buses.Select(b => b.Id).ToArray();
        var ftrs = Enumerable.Range(0, 12).Select(k => new FtrObligation($"F{k}", ids[rnd.Next(ids.Length)], ids[rnd.Next(ids.Length)], rnd.Next(5, 60)))
            .Where(f => f.SourceBus != f.SinkBus).ToList();
        var flows = FinancialTransmissionRights.Flows(net, ftrs);
        double worst = Enumerable.Range(0, flows.Length).Where(l => options.Scuc.Rating(net, l) > 0)
            .Max(l => Math.Abs(flows[l]) / options.Scuc.Rating(net, l));
        double scale = Math.Min(1, 0.999 / worst);
        ftrs = ftrs.Select(f => f with { Mw = f.Mw * scale }).ToList();
        Assert.True(FinancialTransmissionRights.IsSimultaneouslyFeasible(net, ftrs, options.Scuc));
        var fs = FinancialTransmissionRights.Settle(ftrs, r);
        output.WriteLine($"FTR payouts {fs.TotalPayout:F3} $ ≤ rent {fs.CongestionRent:F3} $");
        Assert.True(fs.RevenueAdequate);
        // an infeasible set (10× the scale) may be revenue inadequate and is flagged as such
        Assert.False(FinancialTransmissionRights.IsSimultaneouslyFeasible(net, ftrs.Select(f => f with { Mw = f.Mw * 10 }).ToList(), options.Scuc));
    }

    [Fact]
    public void DayAhead_WithScuc_CommitsAndSettlesHourly()
    {
        var (net, offers, bids, options) = Case14Market();
        // commitment data: expensive peakers (G3–G5) have no-load and startup costs and start offline
        offers = offers.Select((o, g) => g < 2 ? o : o with { NoLoadCost = 100, StartupCost = 300, InitialHours = -4, MinOutputMw = 10, MinUpHours = 2 })
            .ToArray();
        double[] shape = [0.6, 0.8, 1.0, 1.1];
        var bidsPerHour = shape.Select(f => (IReadOnlyList<DemandBid>)bids.Select(b => b with
        {
            FixedMw = b.FixedMw * f, Blocks = b.Blocks.Select(x => x with { QuantityMw = x.QuantityMw * f }).ToArray(),
        }).ToArray()).ToList();
        var da = MarketClearing.ClearDayAhead(net, offers, bidsPerHour, commit: true, options);
        Assert.Equal(SolveStatus.Optimal, da.Status);
        output.WriteLine("commitment: " + string.Join(" | ", Enumerable.Range(0, offers.Length).Select(g =>
            offers[g].Name + " " + string.Concat(Enumerable.Range(0, shape.Length).Select(t => da.Scuc.Commitment[g, t] ? '1' : '0')))));
        output.WriteLine("λ per hour: " + string.Join(", ", da.Scuc.SystemLambda.Select(l => l.ToString("F2"))));
        Assert.Equal(shape.Length, da.Periods.Count);
        AssertMinUp(da, offers);
        foreach (var p in da.Periods)
        {
            Assert.Equal(p.Settlement.CongestionRent, p.Settlement.CongestionRentFromShadowPrices, 4);
            double gen = p.OfferDispatchMw.Sum(), load = p.BidClearedMw.Sum();
            Assert.Equal(load, gen, 6);
        }
        Assert.Equal(da.Periods.Sum(p => p.Settlement.CongestionRent), da.Settlement.CongestionRent, 6);
        // a higher demand can only raise or keep the cleared energy price in this convex offer stack
        Assert.True(da.Scuc.SystemLambda[3] >= da.Scuc.SystemLambda[0] - 1e-6);

        // given-commitment day-ahead (pure LP) with everything committed costs at least as much as the optimised commitment
        var allOn = offers.Select(o => o with { Committed = true }).ToArray();
        var lp = MarketClearing.ClearDayAhead(net, allOn, bidsPerHour, commit: false, options);
        Assert.Equal(SolveStatus.Optimal, lp.Status);
        Assert.True(lp.Scuc.Objective >= da.Scuc.Objective - 1e-6);
    }

    private static void AssertMinUp(DayAheadResult da, IReadOnlyList<SupplyOffer> offers)
    {
        for (int g = 0; g < offers.Count; g++)
            for (int t = 1; t < da.Periods.Count; t++)
                if (da.Scuc.Startup[g, t - 1] && t < da.Periods.Count) Assert.True(da.Scuc.Commitment[g, t] || offers[g].MinUpHours <= 1);
    }

    [Fact]
    public void RealTime_RampLimitedCheapUnit_SetsPriceAtExpensiveUnit()
    {
        // cheap unit at 50 MW can ramp 60 MW/h → +5 MW in a 5-minute interval; the rest of a 120 MW load comes from the 40 $/MWh unit
        SupplyOffer[] offers =
        [
            new("Cheap", 0, [new OfferBlock(200, 15)]) { RampUpMwPerHour = 60, RampDownMwPerHour = 60, InitialOutputMw = 50 },
            new("Peaker", 0, [new OfferBlock(200, 40)]) { InitialOutputMw = 60 },
        ];
        var r = MarketClearing.ClearRealTime(null, offers, [new DemandBid("L", 0) { FixedMw = 120 }], intervalMinutes: 5);
        Assert.Equal(SolveStatus.Optimal, r.Status);
        Assert.Equal(55, r.OfferDispatchMw[0], 6);
        Assert.Equal(65, r.OfferDispatchMw[1], 6);
        Assert.Equal(40, r.LmpAt(0), 6);
        Assert.Equal(5.0 / 60, r.PeriodHours, 12);
        Assert.Equal(40 * 120 / 12.0, r.Settlement.TotalLoadPayment, 6);
    }

    [Fact]
    public void Hosting_AddMarketsRegistersBuiltInSolver_AdaptersReplaceIt()
    {
        using (var sp = new ServiceCollection().AddElectrical(b => b.AddMarkets()).BuildServiceProvider())
        {
            Assert.IsType<BranchAndBoundSolver>(sp.GetRequiredService<IMilpSolver>());
            var svc = sp.GetRequiredService<IMarketService>();
            var r = svc.ClearSinglePeriod(TwoBus(), [new SupplyOffer("G1", 1, [new OfferBlock(200, 20)]), new SupplyOffer("G2", 2, [new OfferBlock(200, 50)])],
                [new DemandBid("L2", 2) { FixedMw = 150 }]);
            Assert.Equal(50, r.LmpAt(2), 6);
        }
        using (var sp = new ServiceCollection().AddElectrical(b => b.AddMarkets().AddHighs()).BuildServiceProvider())
        {
            Assert.IsType<HighsSolver>(sp.GetRequiredService<IMilpSolver>());
            Assert.IsType<HighsSolver>(sp.GetRequiredService<IMarketService>().Solver);
        }
        using (var sp = new ServiceCollection().AddElectrical(b => b.AddOrTools().AddMarkets()).BuildServiceProvider())
        {
            Assert.IsType<OrToolsSolver>(sp.GetRequiredService<IMilpSolver>());
            var svc = sp.GetRequiredService<IMarketService>();
            var r = svc.ClearSinglePeriod(TwoBus(), [new SupplyOffer("G1", 1, [new OfferBlock(200, 20)]), new SupplyOffer("G2", 2, [new OfferBlock(200, 50)])],
                [new DemandBid("L2", 2) { FixedMw = 150 }]);
            Assert.Equal(3000, r.Settlement.CongestionRent, 5);
        }
    }
}
