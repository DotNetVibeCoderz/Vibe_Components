using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Optimization;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Markets;

/// <summary>Point-to-point FTR obligation of <paramref name="Mw"/> from <paramref name="SourceBus"/> to <paramref name="SinkBus"/> (bus ids).</summary>
public sealed record FtrObligation(string Name, int SourceBus, int SinkBus, double Mw);

/// <summary>FTR settlement of one market period.</summary>
/// <param name="Payouts">Payout per FTR [$] = MW × (LMP_sink − LMP_source) × hours.</param>
/// <param name="CongestionRent">Congestion rent collected in the period [$].</param>
public sealed record FtrSettlement(double[] Payouts, double CongestionRent)
{
    public double TotalPayout => Payouts.Sum();
    /// <summary>Congestion rent left after paying the FTRs [$] (negative = revenue shortfall).</summary>
    public double Surplus => CongestionRent - TotalPayout;
    /// <summary>True when the congestion rent covers the payouts (revenue adequacy).</summary>
    public bool RevenueAdequate => Surplus >= -1e-6 * Math.Max(1, Math.Abs(CongestionRent));
}

/// <summary>
/// Financial transmission rights (obligations). Payout = MW·(LMP_sink − LMP_source). If the FTR set is simultaneously feasible on the same DC
/// network model and limits used to clear the market, the payouts never exceed the congestion rent (revenue adequacy; Hogan 1992).
/// </summary>
public static class FinancialTransmissionRights
{
    public static double Payout(FtrObligation ftr, MarketClearingResult market) =>
        ftr.Mw * (market.LmpAt(ftr.SinkBus) - market.LmpAt(ftr.SourceBus)) * market.PeriodHours;

    public static FtrSettlement Settle(IReadOnlyList<FtrObligation> ftrs, MarketClearingResult market) =>
        new(ftrs.Select(f => Payout(f, market)).ToArray(), market.Settlement.CongestionRent);

    /// <summary>Base-case DC flows [MW] per branch caused by the FTR injections (source +MW, sink −MW).</summary>
    public static double[] Flows(PowerNetwork net, IReadOnlyList<FtrObligation> ftrs)
    {
        var ptdf = DcPowerFlow.Ptdf(net);
        var f = new double[net.Branches.Count];
        foreach (var ftr in ftrs)
        {
            int s = net.IndexOf(ftr.SourceBus), k = net.IndexOf(ftr.SinkBus);
            for (int l = 0; l < f.Length; l++) f[l] += ftr.Mw * (ptdf[l, s] - ptdf[l, k]);
        }
        return f;
    }

    /// <summary>Simultaneous feasibility test: FTR flows within the (base-case and contingency) limits of <paramref name="options"/>.</summary>
    public static bool IsSimultaneouslyFeasible(PowerNetwork net, IReadOnlyList<FtrObligation> ftrs, ScucOptions? options = null)
    {
        options ??= ScucOptions.Default;
        var f = Flows(net, ftrs);
        var lodf = options.Contingencies.Count > 0 ? DcPowerFlow.Lodf(net) : null;
        for (int l = 0; l < f.Length; l++)
        {
            double rate = options.Rating(net, l);
            if (!net.Branches[l].InService || rate <= 0) continue;
            if (Math.Abs(f[l]) > rate * options.RatingFactor + 1e-6) return false;
            foreach (int k in options.Contingencies)
            {
                if (k == l || lodf is null || double.IsNaN(lodf[l, k])) continue;
                if (Math.Abs(f[l] + lodf[l, k] * f[k]) > rate * options.EmergencyRatingFactor + 1e-6) return false;
            }
        }
        return true;
    }
}

/// <summary>DI facade for market studies (uses the registered <see cref="IMilpSolver"/>).</summary>
public interface IMarketService
{
    IMilpSolver Solver { get; }
    ScucResult SecurityConstrainedUnitCommitment(PowerNetwork? net, IReadOnlyList<ScucUnit> units, double[,] busDemandMw, ScucOptions? options = null);
    MarketClearingResult ClearSinglePeriod(PowerNetwork? net, IReadOnlyList<SupplyOffer> offers, IReadOnlyList<DemandBid> bids, MarketOptions? options = null);
    DayAheadResult ClearDayAhead(PowerNetwork? net, IReadOnlyList<SupplyOffer> offers, IReadOnlyList<IReadOnlyList<DemandBid>> bidsPerHour,
        bool commit = true, MarketOptions? options = null);
}

internal sealed class MarketService(IMilpSolver solver, ILpSolver lpSolver) : IMarketService
{
    public IMilpSolver Solver => solver;

    private ScucOptions With(ScucOptions? o)
    {
        var x = o ?? ScucOptions.Default;
        return x.Solver is null ? x with { Solver = solver, LpSolver = x.LpSolver ?? lpSolver } : x;
    }

    private MarketOptions With(MarketOptions? o)
    {
        var x = o ?? MarketOptions.Default;
        return x with { Scuc = With(x.Scuc) };
    }

    public ScucResult SecurityConstrainedUnitCommitment(PowerNetwork? net, IReadOnlyList<ScucUnit> units, double[,] busDemandMw, ScucOptions? options = null) =>
        Markets.SecurityConstrainedUnitCommitment.Solve(net, units, busDemandMw, With(options));
    public MarketClearingResult ClearSinglePeriod(PowerNetwork? net, IReadOnlyList<SupplyOffer> offers, IReadOnlyList<DemandBid> bids, MarketOptions? options = null) =>
        MarketClearing.ClearSinglePeriod(net, offers, bids, With(options));
    public DayAheadResult ClearDayAhead(PowerNetwork? net, IReadOnlyList<SupplyOffer> offers, IReadOnlyList<IReadOnlyList<DemandBid>> bidsPerHour,
        bool commit = true, MarketOptions? options = null) => MarketClearing.ClearDayAhead(net, offers, bidsPerHour, commit, With(options));
}

public static class MarketsBuilderExtensions
{
    /// <summary>Registers <see cref="IMarketService"/> and the built-in <see cref="BranchAndBoundSolver"/> as default <see cref="IMilpSolver"/> /
    /// <see cref="ILpSolver"/> (unless a solver is already registered, e.g. by <c>AddHighs()</c> / <c>AddOrTools()</c>, which may also be called
    /// afterwards to replace it). Also adds the Optimization module.</summary>
    public static ElectricalBuilder AddMarkets(this ElectricalBuilder b)
    {
        b.AddOptimization();
        if (b.TryAddModule("Markets"))
        {
            b.Services.TryAddSingleton<IMilpSolver, BranchAndBoundSolver>();
            b.Services.TryAddSingleton<ILpSolver, SimplexSolver>();
            b.Services.TryAddSingleton<IMarketService, MarketService>();
        }
        return b;
    }
}
