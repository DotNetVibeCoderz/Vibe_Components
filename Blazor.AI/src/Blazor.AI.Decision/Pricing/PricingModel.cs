using System.Globalization;
using Blazor.AI.Visualization;

namespace Blazor.AI.Decision;

public sealed class PricingInputs
{
    public double UnitCost { get; set; } = 42_000;

    public double ReferencePrice { get; set; } = 75_000;

    public double ReferenceDemand { get; set; } = 320;

    public double CompetitorPrice { get; set; } = 79_000;

    /// <summary>Constant price elasticity of demand (must be below -1 for a finite optimum).</summary>
    public double Elasticity { get; set; } = -2.2;

    public int InventoryWeeks { get; set; } = 6;

    /// <summary>Price must keep at least this margin over cost (0.2 = 20%).</summary>
    public double MinMargin { get; set; } = 0.2;

    /// <summary>Price may exceed the competitor by at most this share (0.1 = 10%).</summary>
    public double MaxAboveCompetitor { get; set; } = 0.1;
}

public sealed record PricingResult(double OptimalPrice, double FinalPrice, double Units, double Profit, IReadOnlyList<string> Adjustments, IReadOnlyList<ChartSeries> Curve);

/// <summary>
/// With demand q(p) = q0·(p/p0)^e and e &lt; -1, profit (p − c)·q(p) is maximised at p* = c·e/(1+e) (the Lerner rule).
/// Guardrails then clamp the price for margin, competitor positioning and inventory pressure.
/// </summary>
public static class PricingModel
{
    public static PricingResult Optimise(PricingInputs inputs)
    {
        var e = Math.Min(inputs.Elasticity, -1.05);
        var cost = Math.Max(0.01, inputs.UnitCost);
        var optimal = cost * e / (1 + e);
        var price = optimal;
        var adjustments = new List<string>();

        var floor = cost * (1 + inputs.MinMargin);
        if (price < floor)
        {
            adjustments.Add($"Raised to the {inputs.MinMargin:P0} margin floor ({Fmt(floor)}).");
            price = floor;
        }

        var ceiling = inputs.CompetitorPrice * (1 + inputs.MaxAboveCompetitor);
        if (inputs.CompetitorPrice > 0 && price > ceiling)
        {
            adjustments.Add($"Capped at {inputs.MaxAboveCompetitor:P0} above the competitor ({Fmt(ceiling)}).");
            price = ceiling;
        }

        if (inputs.InventoryWeeks > 10)
        {
            // Overstock: discount up to 15% to accelerate sell-through, but never below the margin floor.
            var discount = Math.Min(0.15, (inputs.InventoryWeeks - 10) * 0.015);
            var discounted = Math.Max(floor, price * (1 - discount));
            if (discounted < price)
            {
                adjustments.Add($"{inputs.InventoryWeeks} weeks of stock: discounted {discount:P0} to clear inventory.");
                price = discounted;
            }
        }
        else if (inputs.InventoryWeeks <= 2)
        {
            var premium = Math.Min(ceiling > 0 ? ceiling : price * 1.05, price * 1.05);
            if (premium > price)
            {
                adjustments.Add("Low stock: 5% scarcity premium (within the competitor cap).");
                price = premium;
            }
        }

        price = Math.Round(price / 500) * 500;
        var units = Demand(inputs, e, price);
        var curve = Enumerable.Range(0, 60)
            .Select(i => cost * 1.02 + i * (Math.Max(optimal, inputs.CompetitorPrice) * 1.6 - cost) / 59)
            .Select(p => new ChartPoint(p, (p - cost) * Demand(inputs, e, p), $"price {Fmt(p)}"))
            .ToArray();

        return new PricingResult(optimal, price, units, (price - cost) * units, adjustments, [new ChartSeries("Profit", curve) { Area = true }]);
    }

    private static double Demand(PricingInputs inputs, double e, double price) =>
        inputs.ReferenceDemand * Math.Pow(price / Math.Max(0.01, inputs.ReferencePrice), e);

    private static string Fmt(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
