using Agritech.Core.Hosting;

namespace Agritech.SupplyChain;

/// <summary>Perishable product storage profile.</summary>
public sealed record PerishableProfile(string Product, double ReferenceTempC, double ShelfLifeDaysAtRef, double Q10, double MinSafeTempC, double MaxSafeTempC)
{
    public static readonly PerishableProfile Tomato = new("tomato", 12, 14, 2.5, 10, 15);
    public static readonly PerishableProfile Chili = new("chili", 8, 21, 2.2, 7, 12);
    public static readonly PerishableProfile Lettuce = new("lettuce", 1, 18, 3.0, 0, 4);
    public static readonly PerishableProfile Banana = new("banana", 14, 21, 2.3, 13, 15);
    public static readonly PerishableProfile Fish = new("fish", 0, 12, 3.5, -1, 3);
    public static readonly PerishableProfile Milk = new("milk", 4, 10, 3.0, 1, 6);
}

public sealed record TemperatureLog(DateTimeOffset Time, double TempC);

public sealed record ShelfLifeReport(double ConsumedDays, double RemainingDays, double RemainingPct, IReadOnlyList<(DateTimeOffset From, DateTimeOffset To, double PeakC)> Excursions, string Status);

/// <summary>Q10 (Arrhenius-type) dynamic shelf-life model over a temperature history — the basis of FEFO cold chains.</summary>
public static class ShelfLife
{
    /// <summary>Spoilage rate relative to the reference temperature.</summary>
    public static double RelativeRate(PerishableProfile p, double tempC) => Math.Pow(p.Q10, (tempC - p.ReferenceTempC) / 10);

    public static ShelfLifeReport Evaluate(PerishableProfile p, IReadOnlyList<TemperatureLog> log)
    {
        double consumed = 0;
        var exc = new List<(DateTimeOffset, DateTimeOffset, double)>();
        DateTimeOffset? excStart = null; double peak = double.MinValue;
        for (int i = 0; i < log.Count; i++)
        {
            var cur = log[i];
            bool outOfRange = cur.TempC < p.MinSafeTempC || cur.TempC > p.MaxSafeTempC;
            if (outOfRange) { excStart ??= cur.Time; peak = Math.Max(peak, cur.TempC); }
            else if (excStart is not null) { exc.Add((excStart.Value, cur.Time, peak)); excStart = null; peak = double.MinValue; }
            if (i == 0) continue;
            var prev = log[i - 1];
            double hours = (cur.Time - prev.Time).TotalHours;
            double t = (prev.TempC + cur.TempC) / 2;
            consumed += hours / 24 * RelativeRate(p, t);
        }
        if (excStart is not null) exc.Add((excStart.Value, log[^1].Time, peak));
        double remaining = Math.Max(0, p.ShelfLifeDaysAtRef - consumed);
        double pct = remaining / p.ShelfLifeDaysAtRef * 100;
        string status = remaining <= 0 ? "Expired" : pct < 25 ? "Sell immediately" : exc.Count > 0 ? "Quality risk" : "Good";
        return new ShelfLifeReport(Math.Round(consumed, 2), Math.Round(remaining, 2), Math.Round(pct, 1), exc, status);
    }
}

public sealed record StockItem(string LotId, string Product, double QuantityKg, DateOnly ExpiryDate, string Location);

/// <summary>Warehouse inventory with FEFO (first-expired, first-out) picking.</summary>
public sealed class Inventory
{
    private readonly List<StockItem> _items = [];
    public IReadOnlyList<StockItem> Items => _items;

    public void Receive(StockItem item) => _items.Add(item);

    public double OnHand(string product) => _items.Where(i => i.Product == product).Sum(i => i.QuantityKg);

    /// <summary>Picks quantity using FEFO; returns pick list (lot, kg). Throws if insufficient stock.</summary>
    public IReadOnlyList<(string LotId, double Kg)> Pick(string product, double kg, DateOnly today)
    {
        var avail = _items.Where(i => i.Product == product && i.ExpiryDate >= today).OrderBy(i => i.ExpiryDate).ToList();
        if (avail.Sum(i => i.QuantityKg) < kg) throw new InvalidOperationException($"Insufficient non-expired stock of {product}.");
        var picks = new List<(string, double)>();
        foreach (var it in avail)
        {
            if (kg <= 0) break;
            double take = Math.Min(kg, it.QuantityKg);
            picks.Add((it.LotId, take)); kg -= take;
            int idx = _items.IndexOf(it);
            _items[idx] = it with { QuantityKg = it.QuantityKg - take };
        }
        _items.RemoveAll(i => i.QuantityKg <= 1e-9);
        return picks;
    }

    public IReadOnlyList<StockItem> ExpiringWithin(DateOnly today, int days) => _items.Where(i => i.ExpiryDate <= today.AddDays(days)).OrderBy(i => i.ExpiryDate).ToList();
}

public static class SupplyChainBuilderExtensions
{
    public static AgritechBuilder AddSupplyChain(this AgritechBuilder builder)
    {
        builder.TryAddModule("SupplyChain");
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<TraceabilityLedger>(builder.Services);
        return builder;
    }
}
