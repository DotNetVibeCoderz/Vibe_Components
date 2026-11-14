using Electrical.Core.Network;
using Electrical.Markets;
using Electrical.Optimization;

namespace Electrical.Cli;

public static partial class Extra
{
    static partial void DispatchMarkets(Args cli, ref Task<int>? result)
    {
        result ??= cli.Command switch
        {
            "acopf" => MarketCommands.AcOpfCmd(cli),
            "scuc" => MarketCommands.Scuc(cli),
            "market" => MarketCommands.Market(cli),
            _ => null,
        };
        DispatchEmt(cli, ref result);
    }

    static partial void DispatchEmt(Args cli, ref Task<int>? result);
}

public static partial class Commands
{
    static partial void MoreHelpMarkets(ref string text)
    {
        text += """

              acopf [--limit 5-6:42] [--no-limits]    AC optimal power flow (interior point): cost, dispatch, LMP decomposition
              scuc [--limit 8-9:57.5] [--reserve 0.1] [--solver highs|builtin] [--gap 1e-4] [--time 30]   24 h SCUC (MILP), hourly LMPs
              market [--limit 8-9:60]                 Single-period market clearing from offers and bids: LMPs, settlement, FTR
            """;
        MoreHelpEmt(ref text);
    }

    static partial void MoreHelpEmt(ref string text);
}

internal static class MarketCommands
{
    /// <summary>Applies "--limit from-to:MW" to the network's RateA.</summary>
    private static Dictionary<int, double>? Limit(Args cli, PowerNetwork net)
    {
        if (cli.Get("limit") is not { } lim) return null;
        var parts = lim.Split(':');
        var ends = parts[0].Split('-').Select(int.Parse).ToArray();
        int idx = net.Branches.FindIndex(b => (b.From == ends[0] && b.To == ends[1]) || (b.From == ends[1] && b.To == ends[0]));
        if (idx < 0) throw new ArgumentException($"Branch {parts[0]} not found.");
        return new Dictionary<int, double> { [idx] = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) };
    }

    /// <summary>HiGHS by default (presolve + cuts handle unit-commitment relaxations well); the built-in branch-and-bound is for small models.</summary>
    private static IMilpSolver Solver(Args cli) => cli.Get("solver", "highs").ToLowerInvariant() switch
    {
        "highs" => new Electrical.Optimization.Highs.HighsSolver(),
        "builtin" => new BranchAndBoundSolver(),
        var s => throw new ArgumentException($"Unknown solver '{s}' (highs, builtin)."),
    };

    public static Task<int> AcOpfCmd(Args cli)
    {
        var net = cli.Network("case9");
        var r = AcOpf.Solve(net, new AcOpfOptions { RatingOverrides = Limit(cli, net), EnforceLineLimits = !cli.Flag("no-limits") });
        var dc = DcOpf.Solve(net, new DcOpfOptions { RatingOverrides = Limit(cli, net) });
        Console.WriteLine($"AC-OPF on {net.Name}: {(r.Converged ? "optimal" : $"NOT converged ({r.Status})")} in {r.Iterations} it, cost {r.TotalCost:0.00} $/h (DC-OPF {dc.TotalCost:0.00} $/h)");
        for (int k = 0; k < r.PgMw.Length; k++) Console.WriteLine($"  Gen {k + 1}: P {r.PgMw[k],8:0.00} MW  Q {r.QgMvar[k],8:0.00} Mvar");
        var d = r.Decompose();
        Console.WriteLine("  Bus    Vm[pu]   LMP[$/MWh]  energy    loss  congestion  voltage");
        for (int i = 0; i < r.BusIds.Length; i++)
            Console.WriteLine($"  {r.BusIds[i],-5} {r.Vm[i],7:0.0000} {r.Lmp[i],11:0.000} {d.Energy[i],8:0.000} {d.Loss[i],7:0.000} {d.Congestion[i],11:0.000} {d.Voltage[i],8:0.000}");
        foreach (var b in r.Binding) Console.WriteLine($"  Binding: {b.Description} (shadow price {b.ShadowPrice:0.000})");
        return Commands.Ok();
    }

    public static Task<int> Scuc(Args cli)
    {
        var net = cli.Network("case9");
        double peak = net.TotalLoadMw;
        double[] demand = [.. Enumerable.Range(0, 24).Select(h => Math.Round(peak * (0.6 + 0.4 * Math.Max(0, Math.Sin((h - 6) * Math.PI / 16))), 1))];
        var r = SecurityConstrainedUnitCommitment.Solve(net, demand, new ScucOptions
        {
            ReserveFraction = cli.Num("reserve", 0.1), RatingOverrides = Limit(cli, net),
            // The built-in branch-and-bound is meant for small models: stop at a 0.1 % gap / 30 s unless asked otherwise.
            SolverOptions = new SolverOptions { RelativeGap = cli.Num("gap", 1e-4), TimeLimit = TimeSpan.FromSeconds(cli.Num("time", 30)) },
            Solver = Solver(cli), LpSolver = Solver(cli) as ILpSolver,
        });
        Console.WriteLine($"SCUC on {net.Name} (24 h, {Solver(cli).Name}): {r.Status}, total cost {r.TotalCost:0.00} $ (production {r.ProductionCost:0.00}, startup {r.StartupCost:0.00}), gap {r.MipGap:P3}");
        int units = r.Units.Count;
        Console.WriteLine("  Hour  Demand  " + string.Join(" ", Enumerable.Range(1, units).Select(u => $"  U{u}[MW]")) + "   λ[$/MWh]  LMP min..max");
        for (int t = 0; t < demand.Length; t++)
        {
            var lmps = Enumerable.Range(0, r.BusIds.Length).Select(i => r.Lmp[i, t]).ToArray();
            Console.WriteLine($"  {t,4} {demand[t],7:0.0}  " + string.Join(" ", Enumerable.Range(0, units).Select(u => r.Commitment[u, t] ? $"{r.DispatchMw[u, t],8:0.0}" : "     off")) +
                              $"   {r.SystemLambda[t],8:0.00}  {lmps.Min():0.00}..{lmps.Max():0.00}");
        }
        foreach (var b in r.Binding.Take(5)) Console.WriteLine($"  Binding: {b.Kind} hour {b.Hour} branch {b.Branch} ({b.FlowMw:0.0}/{b.LimitMw:0.0} MW, μ {b.ShadowPrice:0.00})");
        return Commands.Ok();
    }

    public static Task<int> Market(Args cli)
    {
        var net = cli.Network("case9");
        // Offers: three price blocks per generator around its marginal cost; bids: 90 % fixed + 10 % elastic at 60 $/MWh.
        var offers = net.Generators.Select((g, k) =>
        {
            double third = g.Pmax / 3;
            return new SupplyOffer(g.Name ?? $"G{k + 1}", g.Bus,
            [
                new OfferBlock(third, Math.Round(g.Cost.Marginal(third / 2), 2)),
                new OfferBlock(third, Math.Round(g.Cost.Marginal(1.5 * third), 2)),
                new OfferBlock(third, Math.Round(g.Cost.Marginal(2.5 * third), 2)),
            ]);
        }).ToList();
        var bids = net.Loads.Select(l => new DemandBid($"Load {l.Bus}", l.Bus) { FixedMw = 0.9 * l.P, Blocks = [new OfferBlock(0.1 * l.P, 60)] }).ToList();
        if (Limit(cli, net) is { } lim) foreach (var (idx, mw) in lim) net.Branches[idx].RateA = mw;
        var r = MarketClearing.ClearSinglePeriod(net, offers, bids);
        var s = r.Settlement;
        Console.WriteLine($"Market clearing on {net.Name}: {r.Status}, system λ {r.SystemLambda:0.00} $/MWh");
        for (int k = 0; k < offers.Count; k++) Console.WriteLine($"  {offers[k].Name,-4} bus {offers[k].Bus}: {r.OfferDispatchMw[k],7:0.0} MW, revenue {s.GeneratorRevenue[k],9:0.00} $");
        for (int k = 0; k < bids.Count; k++) Console.WriteLine($"  {bids[k].Name,-8}: {r.BidClearedMw[k],7:0.0} MW, pays {s.LoadPayment[k],9:0.00} $");
        Console.WriteLine("  LMP [$/MWh]: " + string.Join("  ", r.BusIds.Select((b, i) => $"{b}:{r.Lmp[i]:0.00}")));
        foreach (var b in r.Binding) Console.WriteLine($"  Binding: {b.Kind} branch {b.Branch} ({b.FlowMw:0.0}/{b.LimitMw:0.0} MW, μ {b.ShadowPrice:0.00} $/MWh)");
        Console.WriteLine($"  Congestion rent {s.CongestionRent:0.00} $ (Σ μ·flow {s.CongestionRentFromShadowPrices:0.00} $), consumer surplus (elastic bids) {s.ElasticConsumerSurplus:0.00} $, producer surplus {s.ProducerSurplus:0.00} $");
        if (r.Binding.Count > 0)
        {
            int src = offers[0].Bus, sink = bids.MaxBy(b => r.LmpAt(b.Bus))!.Bus;
            var ftr = new FtrObligation($"FTR {src}→{sink}", src, sink, 50);
            Console.WriteLine($"  FTR 50 MW {src}→{sink} pays {FinancialTransmissionRights.Payout(ftr, r):0.00} $");
        }
        return Commands.Ok();
    }
}
