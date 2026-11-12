using AquaCulture.Core.Data;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;

namespace AquaCulture.Growth;

/// <summary>Predicted harvest.</summary>
public sealed record HarvestPrediction(DateOnly Date, int DayOfCulture, double AbwG, double Survivors, double BiomassKg, double YieldKgPerM2,
    double SurvivalRate, double Fcr, double CumulativeFeedKg, double Revenue, double FeedCost, double Margin);

/// <summary>Economic inputs for harvest optimisation.</summary>
/// <param name="FeedPricePerKg">Feed price.</param>
/// <param name="DailyFixedCost">Energy, labour and other costs per day.</param>
/// <param name="PriceBySize">Farm-gate price per kg as a function of ABW (g); defaults to the species price.</param>
public sealed record HarvestEconomics(double FeedPricePerKg, double DailyFixedCost = 0, Func<double, double>? PriceBySize = null);

/// <summary>Biomass estimate from sampling with a 95 % confidence interval.</summary>
public sealed record BiomassEstimate(double AbwG, double AbwLowG, double AbwHighG, double Population, double BiomassKg, double BiomassLowKg, double BiomassHighKg);

/// <summary>Harvest date, yield and economically optimal harvest from a growth trajectory.</summary>
public static class HarvestPredictor
{
    /// <summary>Default size-graded shrimp price: count per kg drives price (bigger shrimp, higher price per kg).</summary>
    public static Func<double, double> DefaultPriceCurve(SpeciesProfile s) => s.Group == SpeciesGroup.Shrimp
        ? abw => s.PricePerKg * Math.Clamp(0.55 + 0.45 * abw / s.MarketWeightG, 0.3, 1.35)
        : abw => s.PricePerKg * (abw < s.MarketWeightG * 0.6 ? 0.75 : 1.0);

    /// <summary>Harvest when the target weight is reached (first day ≥ target), or the last simulated day.</summary>
    public static HarvestPrediction AtWeight(GrowthTrajectory t, double targetWeightG, double areaM2, HarvestEconomics? econ = null)
    {
        var d = t.FirstReaching(targetWeightG) ?? t.Final;
        return Evaluate(t, d, areaM2, econ);
    }

    /// <summary>The day that maximises margin (revenue − feed cost − fixed costs) within the trajectory.</summary>
    public static HarvestPrediction Optimal(GrowthTrajectory t, double areaM2, HarvestEconomics econ)
    {
        HarvestPrediction? best = null;
        foreach (var d in t.Days)
        {
            var p = Evaluate(t, d, areaM2, econ);
            if (best is null || p.Margin > best.Margin) best = p;
        }
        return best!;
    }

    private static HarvestPrediction Evaluate(GrowthTrajectory t, DailyGrowthState d, double areaM2, HarvestEconomics? econ)
    {
        var s = t.Start.Species;
        var price = econ?.PriceBySize ?? DefaultPriceCurve(s);
        double revenue = d.BiomassKg * price(d.AbwG);
        double feedCost = d.CumulativeFeedKg * (econ?.FeedPricePerKg ?? 0);
        double fixedCost = (econ?.DailyFixedCost ?? 0) * (d.Day - t.Start.StartDay);
        return new HarvestPrediction(d.Date, d.Day, d.AbwG, d.Survivors, d.BiomassKg, areaM2 > 0 ? d.BiomassKg / areaM2 : double.NaN,
            d.Survivors / t.Start.Count, d.Fcr, d.CumulativeFeedKg, revenue, feedCost, revenue - feedCost - fixedCost);
    }

    /// <summary>
    /// Biomass from a sampling: ABW ± t·SD/√n and a survival estimate (defaults to recorded population / stocked).
    /// </summary>
    public static BiomassEstimate EstimateBiomass(double abwG, double sdG, int sampleSize, double population)
    {
        double t = sampleSize >= 30 ? 1.96 : sampleSize >= 10 ? 2.26 : 2.78;
        double half = sampleSize > 1 ? t * sdG / Math.Sqrt(sampleSize) : abwG * 0.2;
        return new BiomassEstimate(abwG, abwG - half, abwG + half, population, abwG * population / 1000, (abwG - half) * population / 1000, (abwG + half) * population / 1000);
    }

    /// <summary>Biomass estimate from a sample of individual weights.</summary>
    public static BiomassEstimate EstimateBiomass(ReadOnlySpan<double> weightsG, double population)
    {
        var sum = Stats.Summarize(weightsG);
        return EstimateBiomass(sum.Mean, sum.StdDev, sum.Count, population);
    }

    /// <summary>Calibrates the TGC of a cycle from its samplings and a mean temperature.</summary>
    public static double CalibrateTgc(CultureCycle cycle, double meanTempC)
    {
        if (cycle.Samplings.Count == 0) return cycle.Species.Tgc;
        var last = cycle.Samplings.MaxBy(s => s.Date)!;
        int days = cycle.DayOfCulture(last.Date);
        return days <= 0 ? cycle.Species.Tgc : GrowthModels.EstimateTgc(cycle.StockingWeightG, last.AverageWeightG, days * meanTempC);
    }
}

public static class GrowthBuilderExtensions
{
    public static AquaBuilder AddGrowth(this AquaBuilder b) => b.TryAddModule("Growth", _ => { });
}
