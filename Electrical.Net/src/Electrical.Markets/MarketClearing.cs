using Electrical.Core.Network;
using Electrical.Optimization;

namespace Electrical.Markets;

/// <summary>Supply offer of one generator: price-quantity blocks from 0 MW plus optional commitment data.</summary>
/// <param name="Name">Offer / unit name.</param>
/// <param name="Bus">Bus id of the unit (ignored without a network).</param>
/// <param name="Blocks">Offer blocks [MW, $/MWh] (cleared cheapest first; the total is the maximum output).</param>
public sealed record SupplyOffer(string Name, int Bus, IReadOnlyList<OfferBlock> Blocks)
{
    /// <summary>Minimum stable output while committed [MW].</summary>
    public double MinOutputMw { get; init; }
    /// <summary>No-load cost per committed hour [$/h].</summary>
    public double NoLoadCost { get; init; }
    /// <summary>Startup cost [$].</summary>
    public double StartupCost { get; init; }
    public int MinUpHours { get; init; } = 1;
    public int MinDownHours { get; init; } = 1;
    /// <summary>Ramp limits [MW/h].</summary>
    public double RampUpMwPerHour { get; init; } = double.PositiveInfinity;
    public double RampDownMwPerHour { get; init; } = double.PositiveInfinity;
    /// <summary>Initial status (+h on / −h off for h hours).</summary>
    public int InitialHours { get; init; } = 24;
    /// <summary>Current output [MW] (real-time ramp reference; NaN = unconstrained).</summary>
    public double InitialOutputMw { get; init; } = double.NaN;
    /// <summary>Commitment used by single-period / given-commitment clearing.</summary>
    public bool Committed { get; init; } = true;

    /// <summary>Total offered quantity [MW].</summary>
    public double MaxOutputMw => Blocks.Sum(b => b.QuantityMw);

    /// <summary>As-offered energy cost of producing p MW for one hour [$/h] (blocks filled cheapest first).</summary>
    public double EnergyCost(double pMw)
    {
        double rest = pMw, cost = 0;
        foreach (var b in Blocks.OrderBy(b => b.Price))
        {
            double q = Math.Min(rest, b.QuantityMw);
            if (q <= 0) break;
            cost += q * b.Price;
            rest -= q;
        }
        return cost;
    }
}

/// <summary>Demand bid at one bus: an inelastic part plus price-responsive blocks.</summary>
/// <param name="Name">Load-serving entity / load name.</param>
/// <param name="Bus">Bus id (ignored without a network).</param>
public sealed record DemandBid(string Name, int Bus)
{
    /// <summary>Fixed (price-taking) demand [MW].</summary>
    public double FixedMw { get; init; }
    /// <summary>Price-responsive blocks [MW, $/MWh willingness to pay].</summary>
    public IReadOnlyList<OfferBlock> Blocks { get; init; } = [];
}

/// <summary>Options for <see cref="MarketClearing"/>.</summary>
public sealed record MarketOptions
{
    /// <summary>Network, reserve, solver and pricing options (shared with SCUC).</summary>
    public ScucOptions Scuc { get; init; } = new();
    /// <summary>Valuation of fixed demand used for the consumer surplus [$/MWh].</summary>
    public double ValueOfLostLoad { get; init; } = 10_000;

    public static MarketOptions Default { get; } = new();
}

/// <summary>Settlement of one market period. Money in $ for the period (MW × $/MWh × period hours).</summary>
public sealed class MarketSettlement
{
    /// <summary>Σ_t LMP(bus)·P per offer [$].</summary>
    public required double[] GeneratorRevenue { get; init; }
    /// <summary>As-offered cost per offer (energy blocks + no-load + startup) [$].</summary>
    public required double[] GeneratorCost { get; init; }
    /// <summary>Σ_t LMP(bus)·(fixed + cleared) per bid [$].</summary>
    public required double[] LoadPayment { get; init; }
    public double TotalGeneratorRevenue => GeneratorRevenue.Sum();
    public double TotalLoadPayment => LoadPayment.Sum();
    /// <summary>Congestion rent (merchandising surplus) = load payments − generator revenues [$].</summary>
    public double CongestionRent => TotalLoadPayment - TotalGeneratorRevenue;
    /// <summary>Σ over binding branch constraints of shadow price × flow [$] (equals <see cref="CongestionRent"/> at optimality).</summary>
    public required double CongestionRentFromShadowPrices { get; init; }
    /// <summary>Σ (willingness to pay − LMP)·MW, fixed demand valued at <see cref="MarketOptions.ValueOfLostLoad"/> [$].</summary>
    public required double ConsumerSurplus { get; init; }
    /// <summary>
    /// Part of <see cref="ConsumerSurplus"/> from price-responsive bid blocks only [$]. Fixed demand has no bid price, so its share
    /// (valued at VoLL) dominates the total; use this figure to compare market outcomes.
    /// </summary>
    public double ElasticConsumerSurplus { get; init; }
    /// <summary>Σ (revenue − as-offered cost) [$].</summary>
    public double ProducerSurplus => TotalGeneratorRevenue - GeneratorCost.Sum();
    /// <summary>Total surplus = consumer + producer surplus + congestion rent = gross benefit − as-offered cost [$].</summary>
    public double SocialWelfare => ConsumerSurplus + ProducerSurplus + CongestionRent;

    internal static MarketSettlement Sum(IReadOnlyList<MarketSettlement> parts)
    {
        int no = parts[0].GeneratorRevenue.Length, nd = parts.Max(p => p.LoadPayment.Length);
        return new MarketSettlement
        {
            GeneratorRevenue = Enumerable.Range(0, no).Select(i => parts.Sum(p => p.GeneratorRevenue[i])).ToArray(),
            GeneratorCost = Enumerable.Range(0, no).Select(i => parts.Sum(p => p.GeneratorCost[i])).ToArray(),
            LoadPayment = Enumerable.Range(0, nd).Select(i => parts.Sum(p => i < p.LoadPayment.Length ? p.LoadPayment[i] : 0)).ToArray(),
            CongestionRentFromShadowPrices = parts.Sum(p => p.CongestionRentFromShadowPrices),
            ConsumerSurplus = parts.Sum(p => p.ConsumerSurplus),
            ElasticConsumerSurplus = parts.Sum(p => p.ElasticConsumerSurplus),
        };
    }
}

/// <summary>Cleared market for one period.</summary>
public sealed class MarketClearingResult
{
    public required SolveStatus Status { get; init; }
    public required int Period { get; init; }
    /// <summary>Period length [h].</summary>
    public required double PeriodHours { get; init; }
    public required int[] BusIds { get; init; }
    /// <summary>Uniform-price LMP per bus [$/MWh].</summary>
    public required double[] Lmp { get; init; }
    /// <summary>DC decomposition (energy + congestion, loss = 0).</summary>
    public required DcLmpComponents Components { get; init; }
    /// <summary>Reference-bus (energy) price [$/MWh].</summary>
    public required double SystemLambda { get; init; }
    /// <summary>Cleared output per offer [MW].</summary>
    public required double[] OfferDispatchMw { get; init; }
    /// <summary>Commitment per offer.</summary>
    public required bool[] Committed { get; init; }
    /// <summary>Served demand per bid (fixed + cleared price-responsive) [MW].</summary>
    public required double[] BidClearedMw { get; init; }
    /// <summary>Base-case branch flows [MW].</summary>
    public required double[] FlowsMw { get; init; }
    public required IReadOnlyList<MarketConstraint> Binding { get; init; }
    public required MarketSettlement Settlement { get; init; }

    public double LmpAt(int busId) => Lmp[Array.IndexOf(BusIds, busId)];
}

/// <summary>Day-ahead market result: the SCUC plus one cleared period per hour and the summed settlement.</summary>
public sealed class DayAheadResult
{
    public required ScucResult Scuc { get; init; }
    public required IReadOnlyList<MarketClearingResult> Periods { get; init; }
    public required MarketSettlement Settlement { get; init; }
    public SolveStatus Status => Scuc.Status;
}

/// <summary>
/// Uniform-price, bid-based, security-constrained market clearing on a DC network: maximise Σ bid·D − Σ offer·P (− no-load/startup costs)
/// subject to power balance, branch (and optional N-1) limits and, for the day-ahead market, unit commitment constraints. LMPs are the duals of
/// the (fixed-commitment) linear program, decomposed into energy and congestion (<see cref="DcLmpComponents"/>; losses are zero in the DC model).
/// Settlement: generators are paid LMP·P, loads pay LMP·D, the difference is the congestion rent that funds FTRs
/// (<see cref="FinancialTransmissionRights"/>).
/// </summary>
public static class MarketClearing
{
    /// <summary>Single-period clearing with the commitment given by <see cref="SupplyOffer.Committed"/> (a pure LP; e.g. one hour of a day-ahead
    /// market with known commitment). Pass null for a single-bus market.</summary>
    public static MarketClearingResult ClearSinglePeriod(PowerNetwork? net, IReadOnlyList<SupplyOffer> offers, IReadOnlyList<DemandBid> bids,
        MarketOptions? options = null)
    {
        options ??= MarketOptions.Default;
        var units = offers.Select(o => ToUnit(o, [o.Committed], realTime: false, options.Scuc.PeriodHours)).ToList();
        var r = Run(net, offers, units, [bids], options);
        return r.Periods[0];
    }

    /// <summary>Real-time (security-constrained economic dispatch) clearing of one interval of <paramref name="intervalMinutes"/>: committed units
    /// are limited to their ramp capability around <see cref="SupplyOffer.InitialOutputMw"/>; startup and no-load costs are sunk.</summary>
    public static MarketClearingResult ClearRealTime(PowerNetwork? net, IReadOnlyList<SupplyOffer> offers, IReadOnlyList<DemandBid> bids,
        double intervalMinutes = 5, MarketOptions? options = null)
    {
        options ??= MarketOptions.Default;
        double dt = intervalMinutes / 60.0;
        options = options with { Scuc = options.Scuc with { PeriodHours = dt } };
        var units = offers.Select(o => ToUnit(o, [o.Committed], realTime: true, dt)).ToList();
        return Run(net, offers, units, [bids], options).Periods[0];
    }

    /// <summary>
    /// Day-ahead clearing over the hours of <paramref name="bidsPerHour"/>. With <paramref name="commit"/> the commitment is optimised by SCUC
    /// (MILP: no-load, startup, minimum up/down, ramps, reserve, network); otherwise the offers' <see cref="SupplyOffer.Committed"/> flags are used and
    /// the problem is a multi-period LP. Prices come from the fixed-commitment LP.
    /// </summary>
    public static DayAheadResult ClearDayAhead(PowerNetwork? net, IReadOnlyList<SupplyOffer> offers, IReadOnlyList<IReadOnlyList<DemandBid>> bidsPerHour,
        bool commit = true, MarketOptions? options = null)
    {
        options ??= MarketOptions.Default;
        int T = bidsPerHour.Count;
        var units = offers.Select(o => ToUnit(o, commit ? null : Enumerable.Repeat(o.Committed, T).ToArray(), realTime: false,
            options.Scuc.PeriodHours)).ToList();
        return Run(net, offers, units, bidsPerHour, options);
    }

    private static ScucUnit ToUnit(SupplyOffer o, bool[]? fixedCommitment, bool realTime, double dt) => new()
    {
        Name = o.Name, Bus = o.Bus, PminMw = o.MinOutputMw, PmaxMw = o.MaxOutputMw, Blocks = o.Blocks,
        NoLoadCost = realTime ? 0 : o.NoLoadCost, StartupCost = realTime ? 0 : o.StartupCost,
        MinUpHours = realTime ? 1 : o.MinUpHours, MinDownHours = realTime ? 1 : o.MinDownHours,
        RampUpMwPerHour = o.RampUpMwPerHour, RampDownMwPerHour = o.RampDownMwPerHour,
        InitialHours = realTime ? (o.Committed ? 24 : -24) : o.InitialHours,
        InitialOutputMw = realTime ? o.InitialOutputMw : double.NaN,
        StartupRampMw = realTime ? o.MaxOutputMw : double.NaN,
        FixedCommitment = fixedCommitment,
    };

    private static DayAheadResult Run(PowerNetwork? net, IReadOnlyList<SupplyOffer> offers, IReadOnlyList<ScucUnit> units,
        IReadOnlyList<IReadOnlyList<DemandBid>> bidsPerHour, MarketOptions options)
    {
        int T = bidsPerHour.Count, nb = net?.BusCount ?? 1;
        int Pos(int bus) => net is null ? 0 : net.IndexOf(bus);
        var busDemand = new double[nb, T];
        var elastic = new List<ElasticBlock>();
        var owner = new List<(int Bid, int Period)>();
        for (int t = 0; t < T; t++)
            for (int b = 0; b < bidsPerHour[t].Count; b++)
            {
                var bid = bidsPerHour[t][b];
                busDemand[Pos(bid.Bus), t] += bid.FixedMw;
                foreach (var blk in bid.Blocks)
                {
                    if (blk.QuantityMw <= 0) continue;
                    elastic.Add(new ElasticBlock(t, Pos(bid.Bus), blk.QuantityMw, blk.Price));
                    owner.Add((b, t));
                }
            }
        var scuc = SecurityConstrainedUnitCommitment.SolveCore(net, units, busDemand, elastic, options.Scuc);
        double dt = options.Scuc.PeriodHours;
        var periods = new List<MarketClearingResult>();
        for (int t = 0; t < T; t++)
        {
            var bids = bidsPerHour[t];
            int no = offers.Count;
            var lmp = Enumerable.Range(0, nb).Select(i => scuc.Lmp[i, t]).ToArray();
            double LmpOf(int bus) => lmp[Pos(bus)];
            var dispatch = Enumerable.Range(0, no).Select(g => scuc.DispatchMw[g, t]).ToArray();
            var committed = Enumerable.Range(0, no).Select(g => scuc.Commitment[g, t]).ToArray();
            var cleared = bids.Select(b => b.FixedMw).ToArray();
            double cs = 0;
            for (int k = 0; k < elastic.Count; k++)
            {
                if (owner[k].Period != t) continue;
                double mw = scuc.ElasticDemandMw[k];
                cleared[owner[k].Bid] += mw;
                cs += (elastic[k].Price - lmp[elastic[k].BusPos]) * mw * dt;
            }
            double elasticCs = cs;
            for (int b = 0; b < bids.Count; b++) cs += (options.ValueOfLostLoad - LmpOf(bids[b].Bus)) * bids[b].FixedMw * dt;
            var revenue = new double[no];
            var cost = new double[no];
            for (int g = 0; g < no; g++)
            {
                revenue[g] = LmpOf(offers[g].Bus) * dispatch[g] * dt;
                cost[g] = units[g].Blocks is null ? 0 : offers[g].EnergyCost(dispatch[g]) * dt;
                if (committed[g]) cost[g] += units[g].NoLoadCost * dt;
                if (scuc.Startup[g, t]) cost[g] += units[g].StartupCost;
            }
            var payment = new double[bids.Count];
            for (int b = 0; b < bids.Count; b++) payment[b] = LmpOf(bids[b].Bus) * cleared[b] * dt;
            var binding = scuc.Binding.Where(c => c.Hour == t).ToList();
            double rentMu = binding.Where(c => c.Kind != "Reserve").Sum(c => c.ShadowPrice * c.FlowMw) * dt;
            var flows = new double[scuc.FlowsMw.GetLength(0)];
            for (int l = 0; l < flows.Length; l++) flows[l] = scuc.FlowsMw[l, t];
            periods.Add(new MarketClearingResult
            {
                Status = scuc.Status, Period = t, PeriodHours = dt, BusIds = scuc.BusIds, Lmp = lmp, Components = scuc.LmpComponents[t],
                SystemLambda = scuc.SystemLambda[t], OfferDispatchMw = dispatch, Committed = committed, BidClearedMw = cleared, FlowsMw = flows,
                Binding = binding,
                Settlement = new MarketSettlement
                {
                    GeneratorRevenue = revenue, GeneratorCost = cost, LoadPayment = payment, CongestionRentFromShadowPrices = rentMu,
                    ConsumerSurplus = cs, ElasticConsumerSurplus = elasticCs,
                },
            });
        }
        return new DayAheadResult { Scuc = scuc, Periods = periods, Settlement = MarketSettlement.Sum(periods.Select(p => p.Settlement).ToList()) };
    }
}
