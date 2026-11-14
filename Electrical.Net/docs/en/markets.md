# Electricity markets

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/pasar-listrik.md)

Package: `Electrical.Markets` (new in v1.2, depends on `Electrical.Core`, `Electrical.PowerFlow` and `Electrical.Optimization`). It contains
security-constrained unit commitment (SCUC) as a MILP, bid-based day-ahead and real-time market clearing on a DC network, locational marginal
prices (LMPs) with their energy / congestion / loss components, settlement and financial transmission rights (FTRs). The models are built with
[`LinearModel`](solvers.md) and solved by any `IMilpSolver`: the built-in branch and bound by default, or HiGHS / OR-Tools through the adapter
packages.

Units: MW, $/MWh, $ per period (MW × $/MWh × period hours), periods of one hour unless `ScucOptions.PeriodHours` says otherwise.

## Security-constrained unit commitment

`SecurityConstrainedUnitCommitment.Solve` builds a three-binary MILP (commitment u, start-up v, shut-down w; Carrión & Arroyo 2006,
Morales-España et al. 2013):

- piecewise-linear generation cost (offer blocks, or `CostSegments` chords of a quadratic cost between Pmin and Pmax), no-load cost per
  committed period, start-up cost;
- Pmin·u ≤ P ≤ Pmax·u, minimum up/down times (Rajan & Takriti turn-on/turn-off inequalities), ramp limits with start-up/shut-down ramps,
  initial status and output;
- power balance per period and spinning reserve Σ(Pmax·u − P) ≥ f·D + R;
- on a network, DC branch limits through PTDFs and optional N-1 post-contingency limits through LODFs, added lazily (only violated rows,
  re-solved until the dispatch is secure) or all upfront (`LazyNetworkConstraints = false`).

Prices come from a second, linear solve with the commitment fixed (the "restricted pricing" used by ISOs):
LMP<sub>i,t</sub> = λ<sub>t</sub> + f·ρ<sub>t</sub> − Σ<sub>c</sub> μ<sub>c</sub>·PTDF<sub>c,i</sub>.

### Single bus: Wood & Wollenberg

```csharp
using Electrical.Markets;
using Electrical.Optimization;

double[] demand = [450, 420, 400, 400, 450, 550, 700, 850, 950, 1000, 1050, 1050, 1000, 1000, 950, 900, 950, 1050, 1100, 1000, 850, 700, 550, 480];
var ww = UnitCommitment.WoodWollenbergThreeUnits();
var units = ww.Select(u => ScucUnit.FromUcUnit(u)).ToList();
var r = SecurityConstrainedUnitCommitment.Solve(units, demand, new ScucOptions { ReserveFraction = 0.05, CostSegments = 4 });
Console.WriteLine($"{r.Status}: {r.TotalCost:0} $ (production {r.ProductionCost:0}, start-up {r.StartupCost:0}), gap {r.MipGap:P3}");
for (int g = 0; g < r.UnitCount; g++)
    Console.WriteLine($"{units[g].Name}: " + string.Concat(Enumerable.Range(0, r.Periods).Select(t => r.Commitment[g, t] ? '1' : '0')));
Console.WriteLine($"λ at 18:00 = {r.SystemLambda[18]:0.00} $/MWh, reserve {r.SpinningReserveMw[18]:0} MW");

var dp = UnitCommitment.Solve(ww, demand, reserveFraction: 0.05);  // priority-list DP for comparison
Console.WriteLine($"DP {dp.TotalCost:0} $");
```

```
Optimal: 183728 $ (production 182628, start-up 1100), gap 0.001%
Unit 1: 000000111111111111111111
Unit 2: 111111111111111111111100
Unit 3: 111111000111110001110000
λ at 18:00 = 10.79 $/MWh, reserve 100 MW
DP 184368 $
```

183 728 $ is the MILP objective with the 4-segment cost. Re-dispatching the same commitment with the exact quadratic costs gives
**183 665 $**, 703 $ (0.38 %) below the priority-list dynamic program (184 368 $): the MILP keeps unit 3 on in the early hours and cycles it
at the peaks, which the priority list cannot do. A 3-hour case is checked against brute force over all 512 commitments.

`ScucUnit` fields: `Name`, `Bus`, `PminMw`, `PmaxMw`, `Cost` (quadratic) or `Blocks` + `NoLoadCost`, `StartupCost`, `MinUpHours`,
`MinDownHours`, `RampUpMwPerHour`, `RampDownMwPerHour`, `StartupRampMw`, `ShutdownRampMw`, `InitialHours` (+h on / −h off),
`InitialOutputMw`, `FixedCommitment`. Factories: `ScucUnit.FromUcUnit`, `ScucUnit.FromGenerator`, `SecurityConstrainedUnitCommitment.UnitsOf(net)`.

### On a network, with N-1

```csharp
using Electrical.Core.Network;
using Electrical.Optimization.Highs;

var net = TestCases.Case9();
double peak = net.TotalLoadMw;
double[] demand = [.. Enumerable.Range(0, 24).Select(h => peak * (0.6 + 0.4 * Math.Max(0, Math.Sin((h - 6) * Math.PI / 16))))];
int line89 = net.Branches.FindIndex(b => b.From == 8 && b.To == 9);
var ring = Enumerable.Range(0, net.Branches.Count).Where(l => net.Branches[l].R > 0).ToList();   // the six 230 kV lines

var options = new ScucOptions
{
    ReserveFraction = 0.1,
    RatingOverrides = new Dictionary<int, double> { [line89] = 57.5 },   // MW
    Solver = new HighsSolver(),                                         // see "LP / MILP solvers"
};
var r = SecurityConstrainedUnitCommitment.Solve(net, demand, options);  // units from the network's generators
Console.WriteLine($"{r.Status}: {r.TotalCost:0} $, {r.NetworkIterations} constraint-generation rounds, {r.NetworkConstraints} network rows");
int t = 14;
Console.WriteLine("LMP 14:00: " + string.Join("  ", r.BusIds.Select((b, i) => $"{b}:{r.Lmp[i, t]:0.00}")));
var c = r.LmpComponents[t];                                     // DcLmpComponents: energy + congestion (+ loss = 0)
Console.WriteLine($"bus 9: {c.LmpAt(9):0.00} = {c.Energy[8]:0.00} + {c.Congestion[8]:0.00}");
foreach (var b in r.Binding.Where(b => b.Hour == t))
    Console.WriteLine($"{b.Kind} branch {b.Branch} (outage {b.Contingency}): {b.FlowMw:0.0}/{b.LimitMw:0.0} MW, μ {b.ShadowPrice:0.00}");

// N-1: the six 230 kV lines rated 130 MW and each of their outages secured through LODFs
var secure = SecurityConstrainedUnitCommitment.Solve(net, demand, options with
{
    RatingOverrides = ring.ToDictionary(l => l, _ => 130.0), Contingencies = ring,
});
Console.WriteLine($"N-1 secure: {secure.Status}, {secure.TotalCost:0} $, " +
                  $"{secure.Binding.Count(b => b.Kind == "ContingencyFlow")} binding contingency rows");
```

```
Optimal: 94257 $, 2 constraint-generation rounds, 11 network rows
LMP 14:00: 1:27.00  2:21.39  3:23.62  4:27.00  5:25.81  6:23.62  7:22.32  8:21.39  9:28.10
bus 9: 28.10 = 27.00 + 1.10
Flow branch 7 (outage -1): 57.5/57.5 MW, μ 8.79
N-1 secure: Optimal, 93647 $, 9 binding contingency rows
```

With line 8–9 (branch 7) held at 57.5 MW, the 14:00 LMPs spread from 21.39 $/MWh (buses 2 and 8) to 28.10 $/MWh (bus 9). Lazy constraint generation needed
only 11 of the 24 × 9 possible branch rows. The second run uses different ratings (the six ring lines at 130 MW, 8–9 no longer at 57.5 MW)
and secures every ring outage. This 24-hour network case is solved with HiGHS: the built-in branch and bound handles the single-bus case
above in about a second, but it has no presolve or cuts and is far slower on day-long network models (see [which solver to use](solvers.md#which-solver-to-use)).

`ScucResult` holds `Commitment`, `Startup`, `Shutdown`, `DispatchMw` [unit, period], `Lmp` [bus, period], `SystemLambda`, `LmpComponents`
(one `DcLmpComponents` per period), `FlowsMw` [branch, period], `SpinningReserveMw`, `Binding` (`MarketConstraint`: kind "Flow",
"ContingencyFlow" or "Reserve", hour, branch, outage, shadow price, flow, limit), `Objective`, `ProductionCost`, `StartupCost`, `MipGap`,
`NetworkIterations`, `NetworkConstraints`, and the raw `MilpSolution` / `PricingSolution`.

## Market clearing

`MarketClearing` clears a uniform-price, bid-based market: maximise Σ bid·D − Σ offer·P (minus no-load and start-up costs) subject to power
balance, branch (and optional N-1) limits and, day-ahead, unit commitment constraints.

- `SupplyOffer(Name, Bus, Blocks)`: price-quantity `OfferBlock`s from 0 MW, plus `MinOutputMw`, `NoLoadCost`, `StartupCost`, min up/down,
  ramps, `InitialHours`, `InitialOutputMw`, `Committed`.
- `DemandBid(Name, Bus)`: `FixedMw` (price-taking) plus price-responsive `Blocks` (willingness to pay).
- `ClearSinglePeriod(net, offers, bids)`: one period with the commitment given by `Committed` (a pure LP). `net = null` is a single-bus
  market.
- `ClearRealTime(net, offers, bids, intervalMinutes: 5)`: security-constrained economic dispatch of one interval; committed units are
  limited to their ramp capability around `InitialOutputMw`, start-up and no-load costs are sunk.
- `ClearDayAhead(net, offers, bidsPerHour, commit: true)`: SCUC-based clearing over the hours (with `commit: false` the `Committed` flags
  are used and the problem is a multi-period LP). Returns the `ScucResult`, one `MarketClearingResult` per hour and the summed settlement.

### Two-bus hand check

```csharp
var net = new PowerNetwork { Name = "two-bus", BaseMva = 100 };
net.Buses.Add(new Bus { Id = 1, Type = BusType.Slack });
net.Buses.Add(new Bus { Id = 2 });
net.Branches.Add(new Branch { From = 1, To = 2, X = 0.1, RateA = 100 });     // 100 MW line

SupplyOffer[] offers = [new("G1", 1, [new OfferBlock(200, 20)]), new("G2", 2, [new OfferBlock(200, 50)])];
DemandBid[] bids = [new("L2", 2) { FixedMw = 150 }];
var r = MarketClearing.ClearSinglePeriod(net, offers, bids);

Console.WriteLine($"G1 {r.OfferDispatchMw[0]} MW, G2 {r.OfferDispatchMw[1]} MW, LMP {r.LmpAt(1)} / {r.LmpAt(2)} $/MWh");
var s = r.Settlement;
Console.WriteLine($"loads pay {s.TotalLoadPayment}, generators get {s.TotalGeneratorRevenue}, congestion rent {s.CongestionRent} $");
Console.WriteLine($"energy {r.Components.Energy[1]} + congestion {r.Components.Congestion[1]} at bus 2");

var ftr = FinancialTransmissionRights.Settle([new FtrObligation("F12", 1, 2, 100)], r);
Console.WriteLine($"FTR payout {ftr.TotalPayout} $, surplus {ftr.Surplus} $, adequate {ftr.RevenueAdequate}");
```

```
G1 100 MW, G2 50 MW, LMP 20 / 50 $/MWh
loads pay 7500, generators get 4500, congestion rent 3000 $
energy 20 + congestion 30 at bus 2
FTR payout 3000 $, surplus 0 $, adequate True
```

By hand: the cheap unit is limited to the line rating (100 MW), the 50 MW balance comes from G2, so bus 2 is priced by G2 (50 $/MWh) and
bus 1 by G1 (20 $/MWh). The line's shadow price is 30 $/MWh; the load pays 150 × 50 = 7 500 $, generators receive 100 × 20 + 50 × 50 =
4 500 $, and the 3 000 $ difference (= 100 MW × 30 $/MWh) is the congestion rent, which pays exactly a 100 MW FTR from bus 1 to bus 2.

### Price-responsive demand and welfare

```csharp
SupplyOffer[] offers = [new("A", 0, [new OfferBlock(100, 10), new OfferBlock(100, 30)])];
DemandBid[] bids = [new("D", 0) { Blocks = [new OfferBlock(120, 40), new OfferBlock(60, 25)] }];
var r = MarketClearing.ClearSinglePeriod(null, offers, bids);         // null = single-bus market
var s = r.Settlement;
Console.WriteLine($"cleared {r.BidClearedMw[0]} MW at {r.LmpAt(0)} $/MWh; CS {s.ConsumerSurplus}, PS {s.ProducerSurplus}, welfare {s.SocialWelfare}");
```

```
cleared 120 MW at 30 $/MWh; CS 1200, PS 2000, welfare 3200
```

The 25 $/MWh bid is below the marginal 30 $/MWh offer, so 120 MW clear at 30 $/MWh. `MarketSettlement` reports `GeneratorRevenue`,
`GeneratorCost` (as offered), `LoadPayment`, `CongestionRent` (= load payments − generator revenues), `CongestionRentFromShadowPrices`
(Σ μ·flow, equal at optimality), `ConsumerSurplus` (fixed demand valued at `MarketOptions.ValueOfLostLoad`, 10 000 $/MWh by default),
`ElasticConsumerSurplus` (from the price-responsive bids only, which is the number to compare when fixed demand dominates),
`ProducerSurplus` and `SocialWelfare` = CS + PS + congestion rent = gross benefit − as-offered cost (dominated by the VoLL valuation when
there is fixed demand).

### Day-ahead and real-time

```csharp
var net = TestCases.Case14();
var offers = net.Generators.Select((g, k) => new SupplyOffer($"G{k + 1}", g.Bus,
    [.. new[] { 1 / 6.0, 0.5, 5 / 6.0 }.Select(f => new OfferBlock(g.Pmax / 3, Math.Round(g.Cost.Marginal(f * g.Pmax), 2)))])
    {
        NoLoadCost = k < 2 ? 0 : 100, StartupCost = k < 2 ? 0 : 300, MinOutputMw = k < 2 ? 0 : 10,
        InitialHours = k < 2 ? 24 : -4, MinUpHours = 2,
    }).ToArray();
double[] shape = [0.6, 0.8, 1.0, 1.1];
var bidsPerHour = shape.Select(f => (IReadOnlyList<DemandBid>)net.Loads
    .Select(l => new DemandBid($"L{l.Bus}", l.Bus) { FixedMw = 0.8 * f * l.P, Blocks = [new OfferBlock(0.2 * f * l.P, 45)] }).ToArray()).ToList();
var options = new MarketOptions { Scuc = new ScucOptions { RatingOverrides = new Dictionary<int, double> { [0] = 110, [6] = 30 } } };

var da = MarketClearing.ClearDayAhead(net, offers, bidsPerHour, commit: true, options);
Console.WriteLine($"{da.Status}: λ per hour {string.Join(", ", da.Scuc.SystemLambda.Select(l => l.ToString("0.00")))} $/MWh");
Console.WriteLine($"rent {da.Settlement.CongestionRent:0.00} $, welfare {da.Settlement.SocialWelfare:0} $");
foreach (var p in da.Periods) Console.WriteLine($"h{p.Period}: LMP {p.Lmp.Min():0.00}–{p.Lmp.Max():0.00}, committed {p.Committed.Count(c => c)}");
```

```
Optimal: λ per hour 26.37, 31.06, 34.30, 34.30 $/MWh
rent 4078.62 $, welfare 7231313 $
h0: LMP 6.38–59.72, committed 2
h1: LMP 22.71–45.00, committed 3
h2: LMP 28.10–44.64, committed 3
h3: LMP 27.89–45.00, committed 3
```

The peakers (G3–G5) carry no-load and start-up costs and start offline; the SCUC starts one of them from the second hour. The welfare is
dominated by the fixed demand valued at the 10 000 $/MWh value of lost load.

A real-time interval with a ramp-limited cheap unit:

```csharp
SupplyOffer[] offers =
[
    new("Cheap", 0, [new OfferBlock(200, 15)]) { RampUpMwPerHour = 60, RampDownMwPerHour = 60, InitialOutputMw = 50 },
    new("Peaker", 0, [new OfferBlock(200, 40)]) { InitialOutputMw = 60 },
];
var rt = MarketClearing.ClearRealTime(null, offers, [new DemandBid("L", 0) { FixedMw = 120 }], intervalMinutes: 5);
Console.WriteLine($"{rt.OfferDispatchMw[0]} / {rt.OfferDispatchMw[1]} MW, price {rt.LmpAt(0)} $/MWh, load pays {rt.Settlement.TotalLoadPayment} $");
// 55 / 65 MW, price 40 $/MWh, load pays 400 $
```

The cheap unit can only move 60 MW/h × 5/60 h = 5 MW, so the peaker is marginal and sets the price; money is per 5-minute interval.

## LMP components

Every cleared period carries `DcLmpComponents(BusIds, Lmp, Energy, Congestion, Loss)`: energy is the price at the reference (slack) bus,
congestion = −Σ μ<sub>c</sub>·PTDF<sub>c,i</sub> over binding branch constraints, and loss is zero in the lossless DC model. For AC prices with
a real loss component use `AcOpf` + `LmpDecomposition` ([Optimization](optimization.md#lmp-decomposition)).

## Financial transmission rights

`FtrObligation(Name, SourceBus, SinkBus, Mw)` pays MW × (LMP<sub>sink</sub> − LMP<sub>source</sub>) × hours (negative when prices are
reversed). `FinancialTransmissionRights.Settle(ftrs, market)` returns the payouts, the congestion rent, the surplus and `RevenueAdequate`.
`Flows(net, ftrs)` gives the DC flows the FTR injections would cause and `IsSimultaneouslyFeasible(net, ftrs, options)` checks them against
the base-case (and contingency) limits. A simultaneously feasible FTR set is always funded by the congestion rent (revenue adequacy, Hogan
1992); the test suite checks this with random FTR sets on case14 and shows that a 10× scaled, infeasible set is flagged.

## Dependency injection

```csharp
using var sp = new ServiceCollection().AddElectrical(b => b.AddMarkets()).BuildServiceProvider();
var market = sp.GetRequiredService<IMarketService>();           // built-in branch and bound
SupplyOffer[] offers = [new("G1", 1, [new OfferBlock(200, 20)])];
var r = market.ClearSinglePeriod(null, offers, [new DemandBid("L", 1) { FixedMw = 80 }]);
Console.WriteLine($"{market.Solver.Name}: {r.LmpAt(0)} $/MWh");
```

`AddMarkets()` also adds the Optimization module. Add `.AddHighs()` or `.AddOrTools()` to replace the solver (see [LP / MILP solvers](solvers.md)).

## Validation

| Check | Reference | Result |
|---|---|---|
| 24 h Wood & Wollenberg units, 5 % reserve | Priority-list DP (W&W §4.2): 184 368 $ | MILP commitment 183 665 $ at exact cost; MILP objective within 0.2 % of its exact cost |
| 3-hour commitment | Brute force over 512 commitments | identical objective |
| Ramp and minimum up/down limits | Constraint definitions | every hour satisfied |
| case9, one line limited to 70 % of its peak flow | Lazy vs all-rows formulation | identical cost; LMPs separate; LMP = energy + congestion to 1e-9 |
| case9, N-1 on the six 230 kV lines | Post-contingency DC flows (LODF) | all within limits; 120 MW ratings reported infeasible |
| case14 SCUC with a 120 MW line | HiGHS and OR-Tools | same objective and LMPs (1e-3) |
| Two-bus market | Hand calculation | LMP 20 / 50 $/MWh, rent 3 000 $ = FTR payout |
| Elastic demand, single bus | Hand calculation | 120 MW at 30 $/MWh, CS 1 200 $, PS 2 000 $ |
| case14 market | Σ μ·flow, welfare identity | rent = Σ μ·flow; CS + PS + rent = benefit − cost |
| Real-time 5-minute interval | Ramp-limited hand calculation | 55 / 65 MW, price 40 $/MWh |

Gallery: [Security-constrained unit commitment](gallery.md) (`scuc`) and [Electricity market clearing](gallery.md) (`market`).
CLI: `eln scuc`, `eln market` ([CLI](cli.md)).

## Limitations

DC network model (no losses or voltages in the market; AC prices come from `AcOpf`). Units are thermal-style with piecewise-linear costs;
no storage, combined-cycle configurations, demand response with payback or ancillary-service products other than spinning reserve. FTRs are
obligations (no options). The built-in branch and bound is meant for small and medium models; use HiGHS or OR-Tools for N-1 day-ahead
problems.

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/pasar-listrik.md)*
