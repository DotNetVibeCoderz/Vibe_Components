using AquaCulture.Core;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Hosting;
using AquaCulture.Feeding;
using AquaCulture.WaterQuality;

namespace AquaCulture.Analytics;

/// <summary>Water Health Index summary over a period.</summary>
public sealed record WaterHealthSummary(double Index, double PercentGoodOrBetter, int Samples, WaterParameter? WorstParameter, double WorstParameterScore);

/// <summary>Cost breakdown of a cycle.</summary>
public sealed record CostBreakdown(double Seed, double Feed, double Energy, double Labor, double Chemicals, double Other)
{
    public double Total => Seed + Feed + Energy + Labor + Chemicals + Other;
    public IEnumerable<(Text2 Name, double Amount)> Items =>
    [
        (new("Seed", "Benih"), Seed), (new("Feed", "Pakan"), Feed), (new("Energy", "Energi"), Energy),
        (new("Labour", "Tenaga kerja"), Labor), (new("Chemicals & probiotics", "Kimia & probiotik"), Chemicals), (new("Other", "Lainnya"), Other),
    ];
}

/// <summary>Key performance indicators of a culture cycle.</summary>
public sealed record CycleKpis
{
    public required string CycleId { get; init; }
    public required string Species { get; init; }
    public int DayOfCulture { get; init; }
    /// <summary>Average body weight (g).</summary>
    public double Abw { get; init; }
    /// <summary>Average daily gain (g/day).</summary>
    public double Adg { get; init; }
    /// <summary>Specific growth rate (%/day).</summary>
    public double Sgr { get; init; }
    /// <summary>Survival rate (%).</summary>
    public double Sr { get; init; }
    public double MortalityPct { get; init; }
    public double BiomassKg { get; init; }
    public double HarvestedKg { get; init; }
    public double Fcr { get; init; }
    public double ProductivityKgM2 { get; init; }
    public double ProductivityTonHa => ProductivityKgM2 * 10;
    public double Revenue { get; init; }
    public CostBreakdown Costs { get; init; } = new(0, 0, 0, 0, 0, 0);
    public double Profit => Revenue - Costs.Total;
    public double MarginPct => Revenue > 0 ? 100 * Profit / Revenue : double.NaN;
    /// <summary>Price per kg at which revenue equals cost.</summary>
    public double BreakEvenPrice { get; init; }
    public double Roi => Costs.Total > 0 ? Profit / Costs.Total : double.NaN;
    /// <summary>Benefit–cost ratio.</summary>
    public double Bcr => Costs.Total > 0 ? Revenue / Costs.Total : double.NaN;
    public WaterHealthSummary? WaterHealth { get; init; }
    public IReadOnlyList<KpiBenchmark> Benchmarks { get; init; } = [];
}

public enum BenchmarkRating { Excellent, OnTarget, BelowTarget, Poor }

public sealed record KpiBenchmark(string Kpi, double Value, double Target, BenchmarkRating Rating, Text2 Comment);

/// <summary>KPI calculator: FCR, SR, ABW, ADG, SGR, biomass, productivity, profit, mortality and Water Health Index.</summary>
public static class KpiCalculator
{
    public static WaterHealthSummary WaterHealth(IEnumerable<WaterQualityAssessment> assessments)
    {
        var list = assessments.ToList();
        if (list.Count == 0) return new WaterHealthSummary(double.NaN, double.NaN, 0, null, double.NaN);
        var worst = list.SelectMany(a => a.Scores).GroupBy(s => s.Parameter).Select(g => (g.Key, Avg: g.Average(x => x.Score))).MinBy(x => x.Avg);
        return new WaterHealthSummary(Math.Round(list.Average(a => a.Wqi), 1), Math.Round(100.0 * list.Count(a => a.IsHealthy) / list.Count, 1), list.Count, worst.Key, Math.Round(worst.Avg, 1));
    }

    /// <summary>
    /// KPIs of a cycle at <paramref name="asOf"/>. Expenses are categorised by their Category text (seed/benih, feed/pakan,
    /// energy/listrik/solar, labor/tenaga, chemical/probiotik/kapur, other). Feed cost is taken from feed records when priced.
    /// </summary>
    public static CycleKpis Calculate(CultureCycle c, Pond pond, DateOnly asOf, double? salePricePerKg = null, double defaultFeedPrice = 0,
        IEnumerable<WaterQualityAssessment>? water = null)
    {
        var s = c.Species;
        int doc = Math.Max(1, c.DayOfCulture(c.ClosedDate ?? asOf));
        double abw = c.LatestAbwG;
        double adg = (abw - c.StockingWeightG) / Math.Max(1, c.DayOfCulture(c.LatestSamplingDate));
        double sgr = c.LatestSamplingDate > c.StockingDate ? 100 * (Math.Log(abw) - Math.Log(c.StockingWeightG)) / c.DayOfCulture(c.LatestSamplingDate) : 0;
        long alive = c.IsActive ? c.EstimatedPopulation : 0;
        double sr = 100.0 * (alive + c.HarvestedCount) / c.StockedCount;
        double biomass = c.IsActive ? c.EstimatedBiomassKg : 0;
        double produced = c.HarvestedKg + biomass;
        double fcr = Fcr.ForCycle(c);
        double price = salePricePerKg ?? s.PricePerKg;
        double revenue = c.Harvests.Sum(h => h.Kg * h.PricePerKg) + biomass * (c.IsActive ? price : 0);

        var byCat = new double[6]; // seed, feed, energy, labour, chemicals, other
        foreach (var e in c.Expenses) byCat[Classify(e.Category)] += e.Amount;
        byCat[0] += c.StockedCount * c.SeedPricePerUnit;
        byCat[1] += c.Feedings.Sum(f => f.Kg * (f.PricePerKg > 0 ? f.PricePerKg : defaultFeedPrice));
        var costs = new CostBreakdown(byCat[0], byCat[1], byCat[2], byCat[3], byCat[4], byCat[5]);

        var benchmarks = new List<KpiBenchmark>
        {
            Bench("FCR", fcr, s.TypicalFcr, lowerIsBetter: true, new("feed per kg gain", "pakan per kg pertambahan")),
            Bench("SR", sr, s.TypicalSurvival * 100, lowerIsBetter: false, new("survival", "kelulushidupan")),
        };
        double expectedAdg = (s.MarketWeightG - s.StockingWeightG) / s.TypicalCycleDays;
        benchmarks.Add(Bench("ADG", adg, expectedAdg, lowerIsBetter: false, new("growth per day", "pertumbuhan per hari")));

        return new CycleKpis
        {
            CycleId = c.Id, Species = s.Id, DayOfCulture = doc, Abw = Math.Round(abw, 2), Adg = Math.Round(adg, 3), Sgr = Math.Round(sgr, 3),
            Sr = Math.Round(sr, 1), MortalityPct = Math.Round(100 - sr, 1), BiomassKg = Math.Round(biomass, 1), HarvestedKg = Math.Round(c.HarvestedKg, 1),
            Fcr = Math.Round(fcr, 2), ProductivityKgM2 = pond.AreaM2 > 0 ? Math.Round(produced / pond.AreaM2, 3) : double.NaN,
            Revenue = Math.Round(revenue), Costs = costs, BreakEvenPrice = produced > 0 ? Math.Round(costs.Total / produced) : double.NaN,
            WaterHealth = water is null ? null : WaterHealth(water), Benchmarks = benchmarks,
        };
    }

    /// <summary>Expense category index: 0 seed, 1 feed, 2 energy, 3 labour, 4 chemicals/probiotics, 5 other (English and Indonesian keywords).</summary>
    public static int Classify(string category)
    {
        static bool Has(string c, params string[] k) => k.Any(x => c.Contains(x, StringComparison.OrdinalIgnoreCase));
        return Has(category, "seed", "benih", "benur", "fry", "fingerling") ? 0
            : Has(category, "feed", "pakan") ? 1
            : Has(category, "energy", "listrik", "electric", "solar", "fuel", "bbm", "diesel") ? 2
            : Has(category, "labor", "labour", "tenaga", "gaji", "wage", "upah") ? 3
            : Has(category, "chemical", "probiotic", "probiotik", "kapur", "lime", "obat", "kimia", "vitamin", "mineral", "molase") ? 4 : 5;
    }

    private static KpiBenchmark Bench(string name, double value, double target, bool lowerIsBetter, Text2 what)
    {
        if (double.IsNaN(value)) return new(name, value, target, BenchmarkRating.BelowTarget, new($"No data for {what.En}", $"Belum ada data {what.Id}"));
        double ratio = lowerIsBetter ? target / value : value / target;
        var rating = ratio >= 1.1 ? BenchmarkRating.Excellent : ratio >= 0.95 ? BenchmarkRating.OnTarget : ratio >= 0.8 ? BenchmarkRating.BelowTarget : BenchmarkRating.Poor;
        string en = rating switch { BenchmarkRating.Excellent => "better than", BenchmarkRating.OnTarget => "on par with", BenchmarkRating.BelowTarget => "slightly worse than", _ => "well below" };
        string id = rating switch { BenchmarkRating.Excellent => "lebih baik dari", BenchmarkRating.OnTarget => "setara dengan", BenchmarkRating.BelowTarget => "sedikit di bawah", _ => "jauh di bawah" };
        return new(name, Math.Round(value, 2), Math.Round(target, 2), rating, new($"{name} {value:0.##} is {en} the reference {target:0.##} ({what.En})", $"{name} {value:0.##} {id} acuan {target:0.##} ({what.Id})"));
    }

    /// <summary>Farm-level roll-up of active and closed cycles.</summary>
    public static FarmSummary Summarize(Farm farm, DateOnly asOf, double defaultFeedPrice = 0)
    {
        var kpis = farm.Cycles.Select(c => (Cycle: c, Kpi: Calculate(c, farm.Pond(c.PondId), asOf, null, defaultFeedPrice))).ToList();
        return new FarmSummary(farm.Ponds.Count, farm.ActiveCycles.Count(), Math.Round(kpis.Where(k => k.Cycle.IsActive).Sum(k => k.Kpi.BiomassKg), 1),
            Math.Round(kpis.Sum(k => k.Kpi.HarvestedKg), 1), Math.Round(kpis.Sum(k => k.Kpi.Revenue)), Math.Round(kpis.Sum(k => k.Kpi.Costs.Total)),
            kpis.Where(k => !double.IsNaN(k.Kpi.Fcr)).Select(k => k.Kpi.Fcr).DefaultIfEmpty(double.NaN).Average(),
            kpis.Select(k => k.Kpi).ToList());
    }
}

public sealed record FarmSummary(int Ponds, int ActiveCycles, double StandingBiomassKg, double HarvestedKg, double Revenue, double Costs, double MeanFcr, IReadOnlyList<CycleKpis> Cycles)
{
    public double Profit => Revenue - Costs;
}

public static class AnalyticsBuilderExtensions
{
    public static AquaBuilder AddAnalytics(this AquaBuilder b) => b.TryAddModule("Analytics", _ => { });
}
