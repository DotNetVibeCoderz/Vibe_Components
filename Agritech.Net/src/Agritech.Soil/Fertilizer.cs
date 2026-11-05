using Agritech.Core;
using Agritech.Core.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agritech.Soil;

/// <summary>Fertilizer product with nutrient grade (% N, P2O5, K2O, S).</summary>
public sealed record FertilizerProduct(string Id, string Name, double N, double P2O5, double K2O, double S = 0, double PricePerKg = 0);

public static class FertilizerProducts
{
    public static readonly FertilizerProduct Urea = new("urea", "Urea", 46, 0, 0, 0, 2_500);
    public static readonly FertilizerProduct Za = new("za", "ZA (Ammonium sulfate)", 21, 0, 0, 24, 2_100);
    public static readonly FertilizerProduct Sp36 = new("sp36", "SP-36", 0, 36, 0, 5, 2_700);
    public static readonly FertilizerProduct Tsp = new("tsp", "TSP", 0, 46, 0, 0, 3_500);
    public static readonly FertilizerProduct Dap = new("dap", "DAP", 18, 46, 0, 0, 5_000);
    public static readonly FertilizerProduct Kcl = new("kcl", "KCl (MOP)", 0, 0, 60, 0, 4_000);
    public static readonly FertilizerProduct Npk151515 = new("npk-15-15-15", "NPK 15-15-15 (Phonska)", 15, 15, 15, 10, 2_400);
    public static readonly FertilizerProduct[] All = [Urea, Za, Sp36, Tsp, Dap, Kcl, Npk151515];
}

/// <summary>Nutrient requirement in kg/ha.</summary>
public readonly record struct NutrientNeed(double N, double P2O5, double K2O)
{
    public static NutrientNeed operator -(NutrientNeed a, NutrientNeed b) =>
        new(Math.Max(0, a.N - b.N), Math.Max(0, a.P2O5 - b.P2O5), Math.Max(0, a.K2O - b.K2O));
}

public sealed record FertilizerLine(FertilizerProduct Product, double KgPerHa, double TotalKg, double Cost);

public sealed record FertilizerPlan(NutrientNeed Requirement, IReadOnlyList<FertilizerLine> Lines, IReadOnlyList<string> Schedule)
{
    public double TotalCost => Lines.Sum(l => l.Cost);
}

/// <summary>
/// Nutrient-balance fertilizer recommendation: requirement = crop removal at target yield, adjusted by soil-test
/// sufficiency and nutrient-use efficiency, then converted into a product mix.
/// </summary>
public static class FertilizerCalculator
{
    /// <summary>Typical fertilizer recovery efficiencies.</summary>
    public const double NEfficiency = 0.5, PEfficiency = 0.25, KEfficiency = 0.6;

    /// <param name="removalPerTonne">Crop nutrient removal per tonne of yield (kg N, P2O5, K2O).</param>
    public static NutrientNeed Requirement(SoilSample soil, double targetYieldTonHa, NutrientNeed removalPerTonne)
    {
        double n = removalPerTonne.N * targetYieldTonHa * Factor(SoilHealth.RateN(soil)) / NEfficiency * 0.5; // ~50% met by soil mineralisation
        double p = removalPerTonne.P2O5 * targetYieldTonHa * Factor(SoilHealth.RateP(soil)) / PEfficiency * 0.4;
        double k = removalPerTonne.K2O * targetYieldTonHa * Factor(SoilHealth.RateK(soil)) / KEfficiency * 0.6;
        return new NutrientNeed(Math.Round(n, 1), Math.Round(p, 1), Math.Round(k, 1));
    }

    private static double Factor(NutrientRating r) => r switch
    {
        NutrientRating.VeryLow => 1.3,
        NutrientRating.Low => 1.15,
        NutrientRating.Medium => 1.0,
        NutrientRating.High => 0.6,
        _ => 0.3,
    };

    /// <summary>Builds a product mix. With <paramref name="useCompound"/> a compound NPK covers the limiting nutrient first, topped up with straights.</summary>
    public static FertilizerPlan Plan(NutrientNeed need, double areaHa = 1, bool useCompound = true)
    {
        var lines = new List<FertilizerLine>();
        var remaining = need;
        if (useCompound)
        {
            var npk = FertilizerProducts.Npk151515;
            double limiting = Math.Min(need.N, Math.Min(need.P2O5, need.K2O));
            if (limiting > 0)
            {
                double kg = limiting / (npk.N / 100);
                lines.Add(Line(npk, kg, areaHa));
                remaining -= new NutrientNeed(kg * npk.N / 100, kg * npk.P2O5 / 100, kg * npk.K2O / 100);
            }
        }
        if (remaining.P2O5 > 0) lines.Add(Line(FertilizerProducts.Sp36, remaining.P2O5 / 0.36, areaHa));
        if (remaining.K2O > 0) lines.Add(Line(FertilizerProducts.Kcl, remaining.K2O / 0.60, areaHa));
        if (remaining.N > 0) lines.Add(Line(FertilizerProducts.Urea, remaining.N / 0.46, areaHa));

        var schedule = new List<string>
        {
            "Basal (0–7 DAP): all P and K products + 1/3 of urea.",
            "Top-dress 1 (21–28 DAP): 1/3 of urea.",
            "Top-dress 2 (35–45 DAP / before flowering): remaining 1/3 of urea.",
        };
        return new FertilizerPlan(need, lines, schedule);
    }

    private static FertilizerLine Line(FertilizerProduct p, double kgHa, double areaHa)
    {
        kgHa = Math.Round(kgHa, 1);
        return new FertilizerLine(p, kgHa, Math.Round(kgHa * areaHa, 1), Math.Round(kgHa * areaHa * p.PricePerKg));
    }
}

/// <summary>Per-field nutrient ledger (inputs vs. removals) to track partial nutrient balance over seasons.</summary>
public sealed class NutrientLedger
{
    private readonly List<(DateOnly Date, string Source, NutrientNeed Delta)> _entries = [];

    public IReadOnlyList<(DateOnly Date, string Source, NutrientNeed Delta)> Entries => _entries;

    public void ApplyFertilizer(DateOnly date, FertilizerProduct p, double kgPerHa) =>
        _entries.Add((date, p.Name, new NutrientNeed(kgPerHa * p.N / 100, kgPerHa * p.P2O5 / 100, kgPerHa * p.K2O / 100)));

    public void ApplyManure(DateOnly date, double tonPerHa, double nPct = 1.5, double p2o5Pct = 1.0, double k2oPct = 1.5) =>
        _entries.Add((date, "Manure/compost", new NutrientNeed(tonPerHa * 10 * nPct, tonPerHa * 10 * p2o5Pct, tonPerHa * 10 * k2oPct)));

    public void RecordHarvest(DateOnly date, double yieldTonHa, NutrientNeed removalPerTonne) =>
        _entries.Add((date, "Harvest removal", new NutrientNeed(-removalPerTonne.N * yieldTonHa, -removalPerTonne.P2O5 * yieldTonHa, -removalPerTonne.K2O * yieldTonHa)));

    /// <summary>Net balance kg/ha (positive = surplus).</summary>
    public (double N, double P2O5, double K2O) Balance()
    {
        double n = 0, p = 0, k = 0;
        foreach (var e in _entries) { n += e.Delta.N; p += e.Delta.P2O5; k += e.Delta.K2O; }
        return (Math.Round(n, 1), Math.Round(p, 1), Math.Round(k, 1));
    }
}

public static class SoilBuilderExtensions
{
    public static AgritechBuilder AddSoil(this AgritechBuilder builder)
    {
        builder.TryAddModule("Soil");
        builder.Services.TryAddTransient<NutrientLedger>();
        return builder;
    }
}
