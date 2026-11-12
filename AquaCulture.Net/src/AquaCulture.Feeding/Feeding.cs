using AquaCulture.Core;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;
using AquaCulture.Growth;

namespace AquaCulture.Feeding;

/// <summary>A feed product.</summary>
public sealed record FeedProduct(string Id, string Name, double ProteinPct, double PelletMm, double PricePerKg, double MinAbwG, double MaxAbwG,
    double ExpectedFcr, SpeciesGroup Group = SpeciesGroup.Fish, double LipidPct = 6);

/// <summary>Generic feed range (indicative Indonesian prices, IDR/kg). Replace with your supplier's list.</summary>
public static class FeedCatalog
{
    public static readonly IReadOnlyList<FeedProduct> Products =
    [
        new("fish-starter", "Fish starter crumble 40%", 40, 0.8, 16_500, 0.5, 10, 1.0),
        new("fish-nursery", "Fish nursery 36%", 36, 1.5, 14_000, 5, 40, 1.15),
        new("fish-grower", "Fish grower 32%", 32, 3, 12_000, 30, 250, 1.35),
        new("fish-finisher", "Fish finisher 28%", 28, 4, 10_500, 150, 2000, 1.55),
        new("fish-floating-30", "Floating pellet 30% (economy)", 30, 3, 10_000, 40, 2000, 1.6),
        new("marine-40", "Marine fish pellet 45%", 45, 4, 21_000, 20, 2000, 1.4),
        new("shrimp-pl", "Shrimp PL powder 42%", 42, 0.3, 26_000, 0.001, 0.5, 0.8, SpeciesGroup.Shrimp),
        new("shrimp-crumble", "Shrimp crumble 40%", 40, 0.8, 22_000, 0.3, 3, 1.0, SpeciesGroup.Shrimp),
        new("shrimp-grower", "Shrimp grower pellet 36%", 36, 1.6, 18_500, 2, 15, 1.25, SpeciesGroup.Shrimp),
        new("shrimp-finisher", "Shrimp finisher pellet 32%", 32, 2.2, 16_500, 10, 60, 1.4, SpeciesGroup.Shrimp),
    ];

    public static FeedProduct Get(string id) => Products.First(p => p.Id == id);
}

/// <summary>Feed conversion ratios.</summary>
public static class Fcr
{
    /// <summary>FCR = feed given / biomass gain.</summary>
    public static double Calculate(double feedGivenKg, double biomassGainKg) => biomassGainKg <= 0 ? double.NaN : feedGivenKg / biomassGainKg;

    /// <summary>Biological FCR including the biomass of dead animals in the gain.</summary>
    public static double Biological(double feedKg, double finalBiomassKg, double initialBiomassKg, double deadBiomassKg) =>
        Calculate(feedKg, finalBiomassKg + deadBiomassKg - initialBiomassKg);

    /// <summary>Economic FCR: feed per kg actually harvested.</summary>
    public static double Economic(double feedKg, double harvestedKg, double stockedBiomassKg) => Calculate(feedKg, harvestedKg - stockedBiomassKg);

    /// <summary>FCR of a culture cycle (harvested + standing biomass).</summary>
    public static double ForCycle(CultureCycle c) => Calculate(c.TotalFeedKg, c.HarvestedKg + (c.IsActive ? c.EstimatedBiomassKg : 0) - c.InitialBiomassKg);
}

/// <summary>A planned meal.</summary>
public sealed record Meal(TimeOnly Time, double Kg);

/// <summary>Daily feeding plan.</summary>
public sealed record FeedingPlan(double DailyKg, double RatePercentBw, IReadOnlyList<Meal> Meals, FeedProduct? Feed, double PelletMm, IReadOnlyList<Text2> Adjustments)
{
    public int MealsPerDay => Meals.Count;
    public double DailyCost => Feed is null ? double.NaN : DailyKg * Feed.PricePerKg;
}

/// <summary>Feeding tables, meal schedules and daily rations.</summary>
public static class FeedingCalculator
{
    /// <summary>Meals per day by size and group.</summary>
    public static int MealsPerDay(SpeciesProfile s, double abwG)
    {
        double rel = abwG / s.MarketWeightG;
        if (s.Group == SpeciesGroup.Shrimp) return rel < 0.05 ? 6 : rel < 0.3 ? 5 : 4;
        return rel < 0.02 ? 6 : rel < 0.15 ? 4 : rel < 0.6 ? 3 : 2;
    }

    /// <summary>Feeding schedule between 06:00 and 18:00 for fish; shrimp also get a 22:00 meal. Morning and evening portions are slightly larger.</summary>
    public static IReadOnlyList<Meal> Schedule(SpeciesProfile s, double dailyKg, int meals)
    {
        var times = new List<TimeOnly>();
        int day = s.Group == SpeciesGroup.Shrimp && meals >= 4 ? meals - 1 : meals;
        for (int i = 0; i < day; i++) times.Add(new TimeOnly(day == 1 ? 9 : 6 + (int)Math.Round(12.0 * i / (day - 1)), 0));
        if (day < meals) times.Add(new TimeOnly(22, 0));
        var weights = times.Select((t, i) => i == 0 || i == times.Count - 1 ? 1.2 : 1.0).ToArray();
        double sum = weights.Sum();
        return times.Select((t, i) => new Meal(t, Math.Round(dailyKg * weights[i] / sum, 3))).ToList();
    }

    /// <summary>
    /// Daily ration from the species feeding table, adjusted for temperature, dawn DO and NH₃, with the best-value feed for the size.
    /// </summary>
    public static FeedingPlan Plan(SpeciesProfile s, double abwG, double population, double tempC, double? dawnDo = null, double? nh3 = null,
        IEnumerable<FeedProduct>? products = null)
    {
        var adj = new List<Text2>();
        double rate = s.FeedingRatePercent(abwG);
        double fT = GrowthModifiers.Temperature(s, tempC);
        if (fT < 1)
        {
            adj.Add(new($"Temperature {tempC:0.0} °C outside the optimum: ration × {fT:0.00}", $"Suhu {tempC:0.0} °C di luar optimum: ransum × {fT:0.00}"));
            rate *= Math.Max(0.2, fT);
        }
        if (dawnDo is { } dox && dox < s.DoOptimal)
        {
            double f = dox < s.DoCritical ? 0 : 0.6 + 0.4 * (dox - s.DoCritical) / (s.DoOptimal - s.DoCritical);
            adj.Add(new($"Dawn DO {dox:0.0} mg/L: ration × {f:0.00}", $"DO subuh {dox:0.0} mg/L: ransum × {f:0.00}"));
            rate *= f;
        }
        if (nh3 is { } n && n > s.Nh3Max)
        {
            adj.Add(new($"NH₃ {n:0.000} mg/L above limit: ration × 0.5", $"NH₃ {n:0.000} mg/L di atas batas: ransum × 0,5"));
            rate *= 0.5;
        }
        double biomass = abwG * population / 1000;
        double daily = biomass * rate / 100;
        var feed = FeedOptimizer.BestFeed(s, abwG, products ?? FeedCatalog.Products);
        int meals = MealsPerDay(s, abwG);
        return new FeedingPlan(Math.Round(daily, 3), Math.Round(rate, 3), Schedule(s, daily, meals), feed, feed?.PelletMm ?? Math.Round(Math.Max(0.3, Math.Cbrt(abwG) * 0.6), 1), adj);
    }
}

/// <summary>Feed cost breakdown of a cycle.</summary>
public sealed record FeedCostAnalysis(double FeedKg, double FeedCost, double BiomassProducedKg, double Fcr, double FeedCostPerKgProduced, double FeedShareOfCost, double AverageFeedPrice);

/// <summary>Least-cost feed selection and blending.</summary>
public static class FeedOptimizer
{
    /// <summary>Expected FCR, penalised 3 % per percentage point of protein below the species requirement.</summary>
    public static double AdjustedFcr(FeedProduct f, SpeciesProfile s) =>
        f.ExpectedFcr * (1 + 0.03 * Math.Max(0, s.ProteinRequirementPct - f.ProteinPct)) * (s.Group == f.Group ? 1 : 1.3);

    /// <summary>Feed cost per kg of biomass gain.</summary>
    public static double CostPerKgGain(FeedProduct f, SpeciesProfile s) => f.PricePerKg * AdjustedFcr(f, s);

    /// <summary>Cheapest feed per kg gain among products suitable for this size and group.</summary>
    public static FeedProduct? BestFeed(SpeciesProfile s, double abwG, IEnumerable<FeedProduct> products) =>
        products.Where(p => p.Group == s.Group && abwG >= p.MinAbwG && abwG <= p.MaxAbwG).MinBy(p => CostPerKgGain(p, s))
        ?? products.Where(p => p.Group == s.Group).MinBy(p => Math.Min(Math.Abs(Math.Log(abwG / p.MinAbwG)), Math.Abs(Math.Log(abwG / p.MaxAbwG))));

    /// <summary>Feeds ranked by cost per kg gain for a size.</summary>
    public static IReadOnlyList<(FeedProduct Feed, double Fcr, double CostPerKgGain)> Rank(SpeciesProfile s, double abwG, IEnumerable<FeedProduct> products) =>
        products.Where(p => p.Group == s.Group && abwG >= p.MinAbwG && abwG <= p.MaxAbwG)
                .Select(p => (p, AdjustedFcr(p, s), CostPerKgGain(p, s))).OrderBy(x => x.Item3).ToList();

    /// <summary>
    /// Pearson-square blend of two feeds to reach a target protein: returns the fraction of <paramref name="high"/> and the blended price.
    /// </summary>
    public static (double FractionHigh, double FractionLow, double PricePerKg) Blend(FeedProduct high, FeedProduct low, double targetProteinPct)
    {
        if (high.ProteinPct < low.ProteinPct) (high, low) = (low, high);
        if (targetProteinPct >= high.ProteinPct) return (1, 0, high.PricePerKg);
        if (targetProteinPct <= low.ProteinPct) return (0, 1, low.PricePerKg);
        double fh = (targetProteinPct - low.ProteinPct) / (high.ProteinPct - low.ProteinPct);
        return (fh, 1 - fh, fh * high.PricePerKg + (1 - fh) * low.PricePerKg);
    }

    /// <summary>Feed cost analysis for a cycle; <paramref name="otherCosts"/> is everything except feed.</summary>
    public static FeedCostAnalysis Analyze(CultureCycle c, double defaultFeedPrice, double otherCosts = double.NaN)
    {
        double kg = c.TotalFeedKg;
        double cost = c.Feedings.Sum(f => f.Kg * (f.PricePerKg > 0 ? f.PricePerKg : defaultFeedPrice));
        double produced = c.HarvestedKg + (c.IsActive ? c.EstimatedBiomassKg : 0) - c.InitialBiomassKg;
        double other = double.IsNaN(otherCosts) ? c.Expenses.Sum(e => e.Amount) + c.StockedCount * c.SeedPricePerUnit : otherCosts;
        return new FeedCostAnalysis(kg, cost, produced, Fcr.Calculate(kg, produced), produced > 0 ? cost / produced : double.NaN,
            cost + other > 0 ? cost / (cost + other) : double.NaN, kg > 0 ? cost / kg : double.NaN);
    }
}

/// <summary>Observation after a meal (feeding tray/anco for shrimp, surface response for fish).</summary>
/// <param name="LeftoverPct">Share of the last meal left after the check time (0–100).</param>
/// <param name="AppetiteScore">Optional 0 (no response) – 5 (aggressive feeding) surface response.</param>
public sealed record FeedingObservation(double LeftoverPct, int? AppetiteScore = null);

/// <summary>Command for an automatic feeder.</summary>
public sealed record FeederCommand(TimeOnly Time, double Grams, bool Skip, IReadOnlyList<Text2> Reasons);

/// <summary>
/// Closed-loop auto-feeding: adjusts each planned meal from the previous meal's leftovers and live water quality.
/// </summary>
public sealed class AutoFeedingAdvisor(SpeciesProfile species)
{
    /// <summary>Current multiplier applied to the plan (learned from leftovers, 0.5–1.3).</summary>
    public double Multiplier { get; private set; } = 1;

    public FeederCommand Next(Meal planned, FeedingObservation? lastMeal, double? doNow = null, double? tempNow = null, double? nh3Now = null)
    {
        var reasons = new List<Text2>();
        if (lastMeal is { } o)
        {
            double step = o.LeftoverPct > 30 ? -0.20 : o.LeftoverPct > 15 ? -0.10 : o.LeftoverPct < 3 && (o.AppetiteScore ?? 4) >= 3 ? 0.05 : 0;
            if (step != 0)
            {
                Multiplier = Math.Clamp(Multiplier + step, 0.5, 1.3);
                reasons.Add(new($"Leftover {o.LeftoverPct:0}% → plan × {Multiplier:0.00}", $"Sisa pakan {o.LeftoverPct:0}% → rencana × {Multiplier:0.00}"));
            }
        }
        double f = Multiplier;
        if (doNow is { } dox)
        {
            if (dox < species.DoCritical) return new(planned.Time, 0, true, [.. reasons, new($"DO {dox:0.0} mg/L critical: skip meal", $"DO {dox:0.0} mg/L kritis: lewati jadwal")]);
            if (dox < species.DoOptimal) { f *= 0.7; reasons.Add(new($"DO {dox:0.0} mg/L low: −30 %", $"DO {dox:0.0} mg/L rendah: −30 %")); }
        }
        if (tempNow is { } t && !species.Temperature.IsOptimal(t)) { f *= Math.Max(0.3, species.Temperature.Suitability(t)); reasons.Add(new($"Temperature {t:0.0} °C: reduced", $"Suhu {t:0.0} °C: dikurangi")); }
        if (nh3Now is { } n && n > species.Nh3Max) { f *= 0.5; reasons.Add(new($"NH₃ {n:0.000} mg/L: −50 %", $"NH₃ {n:0.000} mg/L: −50 %")); }
        return new(planned.Time, Math.Round(planned.Kg * 1000 * f), false, reasons);
    }
}

public static class FeedingBuilderExtensions
{
    public static AquaBuilder AddFeeding(this AquaBuilder b) => b.TryAddModule("Feeding", _ => { });
}
