using Agritech.Core.Data;
using Agritech.Core.Geo;
using Agritech.Core.Hosting;

namespace Agritech.Marketplace;

public enum Side { Sell, Buy }

/// <summary>A limit order (offer from a farmer or bid from a buyer). Price in currency per kg.</summary>
public sealed record Order(string Id, Side Side, string Trader, string Commodity, string Grade, double QuantityKg, double PricePerKg,
    DateTimeOffset Time, GeoCoordinate? Location = null)
{
    public double RemainingKg { get; set; } = QuantityKg;
}

public sealed record Trade(string BuyOrderId, string SellOrderId, string Buyer, string Seller, string Commodity, double QuantityKg, double PricePerKg,
    double TransportCostPerKg, DateTimeOffset Time)
{
    public double Value => QuantityKg * PricePerKg;
}

/// <summary>
/// Continuous double-auction order book with price–time priority per (commodity, grade). Optional transport-cost awareness:
/// a bid only matches an offer if bid ≥ offer + distance × cost/km/kg.
/// </summary>
public sealed class OrderBook(double transportCostPerKgPerKm = 0)
{
    private readonly List<Order> _bids = [], _asks = [];
    private readonly List<Trade> _trades = [];
    private int _seq;

    public IReadOnlyList<Trade> Trades => _trades;
    public IEnumerable<Order> Bids(string commodity) => _bids.Where(b => b.Commodity == commodity && b.RemainingKg > 0).OrderByDescending(b => b.PricePerKg).ThenBy(b => b.Time);
    public IEnumerable<Order> Asks(string commodity) => _asks.Where(a => a.Commodity == commodity && a.RemainingKg > 0).OrderBy(a => a.PricePerKg).ThenBy(a => a.Time);

    public Order Submit(Side side, string trader, string commodity, double kg, double price, string grade = "A", GeoCoordinate? location = null, DateTimeOffset? time = null)
    {
        var o = new Order($"{(side == Side.Buy ? "B" : "S")}{Interlocked.Increment(ref _seq):00000}", side, trader, commodity, grade, kg, price, time ?? DateTimeOffset.UtcNow, location);
        (side == Side.Buy ? _bids : _asks).Add(o);
        Match(o);
        return o;
    }

    private double Transport(Order a, Order b) =>
        transportCostPerKgPerKm <= 0 || a.Location is null || b.Location is null ? 0 : GeoMath.Distance(a.Location.Value, b.Location.Value) / 1000 * transportCostPerKgPerKm;

    private void Match(Order incoming)
    {
        var book = (incoming.Side == Side.Buy ? Asks(incoming.Commodity) : Bids(incoming.Commodity)).Where(o => o.Grade == incoming.Grade).ToList();
        foreach (var resting in book)
        {
            if (incoming.RemainingKg <= 0) break;
            var (buy, sell) = incoming.Side == Side.Buy ? (incoming, resting) : (resting, incoming);
            double tc = Transport(buy, sell);
            if (buy.PricePerKg < sell.PricePerKg + tc) { if (tc == 0) break; else continue; }
            double qty = Math.Min(incoming.RemainingKg, resting.RemainingKg);
            incoming.RemainingKg -= qty; resting.RemainingKg -= qty;
            // Trade executes at the resting order's price (standard price-time priority).
            _trades.Add(new Trade(buy.Id, sell.Id, buy.Trader, sell.Trader, incoming.Commodity, qty, resting.PricePerKg, Math.Round(tc, 2), DateTimeOffset.UtcNow));
        }
    }

    public bool Cancel(string orderId)
    {
        var o = _bids.Concat(_asks).FirstOrDefault(x => x.Id == orderId);
        if (o is null) return false;
        o.RemainingKg = 0;
        return true;
    }

    public (double? BestBid, double? BestAsk, double? Spread) Quote(string commodity)
    {
        double? bid = Bids(commodity).FirstOrDefault()?.PricePerKg, ask = Asks(commodity).FirstOrDefault()?.PricePerKg;
        return (bid, ask, bid is not null && ask is not null ? ask - bid : null);
    }
}

public sealed record PriceInsight(double Last, double Ma7, double Ma30, double VolatilityPct, double Trend, IReadOnlyList<double> Forecast, string Signal);

/// <summary>Commodity price analytics: moving averages, volatility, seasonality and Holt's linear-trend forecast.</summary>
public static class PriceIntelligence
{
    public static PriceInsight Analyze(IReadOnlyList<double> dailyPrices, int horizon = 14)
    {
        if (dailyPrices.Count < 2) throw new ArgumentException("Need at least two prices.");
        var arr = dailyPrices.ToArray();
        double ma7 = Stats.Mean(arr.AsSpan(Math.Max(0, arr.Length - 7)));
        double ma30 = Stats.Mean(arr.AsSpan(Math.Max(0, arr.Length - 30)));
        var returns = new double[arr.Length - 1];
        for (int i = 1; i < arr.Length; i++) returns[i - 1] = Math.Log(arr[i] / arr[i - 1]);
        double vol = Stats.StdDev(returns) * Math.Sqrt(30) * 100;
        var (forecast, trend) = Holt(arr, 0.4, 0.2, horizon);
        string signal = ma7 > ma30 * 1.03 ? "Rising — consider holding stock / staggered selling"
            : ma7 < ma30 * 0.97 ? "Falling — consider selling soon or forward contracts" : "Stable";
        return new PriceInsight(arr[^1], Math.Round(ma7, 2), Math.Round(ma30, 2), Math.Round(vol, 2), Math.Round(trend, 3), forecast, signal);
    }

    /// <summary>Holt's linear exponential smoothing.</summary>
    public static (double[] Forecast, double Trend) Holt(ReadOnlySpan<double> y, double alpha, double beta, int horizon)
    {
        double level = y[0], trend = y.Length > 1 ? y[1] - y[0] : 0;
        for (int i = 1; i < y.Length; i++)
        {
            double prevLevel = level;
            level = alpha * y[i] + (1 - alpha) * (level + trend);
            trend = beta * (level - prevLevel) + (1 - beta) * trend;
        }
        var f = new double[horizon];
        for (int h = 0; h < horizon; h++) f[h] = Math.Round(level + (h + 1) * trend, 2);
        return (f, trend);
    }

    /// <summary>Monthly seasonal index (100 = average) from dated prices.</summary>
    public static IReadOnlyDictionary<int, double> SeasonalIndex(IEnumerable<(DateOnly Date, double Price)> prices)
    {
        var list = prices.ToList();
        double mean = list.Average(p => p.Price);
        return list.GroupBy(p => p.Date.Month).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => Math.Round(g.Average(p => p.Price) / mean * 100, 1));
    }

    /// <summary>Farm-gate fair price = production cost per kg × (1 + margin), floored at a reference market price share.</summary>
    public static double FairFarmGatePrice(double totalCost, double yieldKg, double marginPct = 20, double? marketPrice = null, double minMarketShare = 0.6)
    {
        double cost = totalCost / Math.Max(1, yieldKg) * (1 + marginPct / 100);
        return Math.Round(marketPrice is { } m ? Math.Max(cost, m * minMarketShare) : cost, 2);
    }

    /// <summary>Synthetic seasonal price series (harvest-season dips) for demos.</summary>
    public static List<(DateOnly Date, double Price)> SyntheticSeries(DateOnly start, int days, double basePrice = 6000, int seed = 9)
    {
        var rng = new Random(seed);
        var res = new List<(DateOnly, double)>();
        double p = basePrice;
        for (int d = 0; d < days; d++)
        {
            var date = start.AddDays(d);
            double seasonal = 1 - 0.12 * Math.Cos(2 * Math.PI * (date.DayOfYear - 90) / 365.0);
            p = p * 0.97 + basePrice * seasonal * 0.03 + rng.NextGaussian(0, basePrice * 0.008);
            res.Add((date, Math.Round(p, 0)));
        }
        return res;
    }
}

public static class MarketplaceBuilderExtensions
{
    public static AgritechBuilder AddMarketplace(this AgritechBuilder builder, double transportCostPerKgPerKm = 0)
    {
        builder.TryAddModule("Marketplace");
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(builder.Services, new OrderBook(transportCostPerKgPerKm));
        return builder;
    }
}
