namespace Agritech.Soil;

public enum NutrientRating { VeryLow, Low, Medium, High, VeryHigh }

public sealed record IndicatorScore(string Indicator, double Value, double Score, string Interpretation);

public sealed record SoilHealthReport(double Score, string Grade, IReadOnlyList<IndicatorScore> Indicators, IReadOnlyList<string> Recommendations);

/// <summary>
/// Soil health index (0–100) combining chemical and physical indicators with non-linear scoring curves
/// (more-is-better, optimum-range, less-is-better), similar in spirit to the CASH / SMAF frameworks.
/// </summary>
public static class SoilHealth
{
    private static readonly (string Name, double Weight)[] Weights =
    [
        ("pH", 0.18), ("Organic carbon", 0.20), ("CEC", 0.12), ("Nitrogen", 0.12),
        ("Phosphorus", 0.12), ("Potassium", 0.10), ("Salinity (EC)", 0.08), ("Bulk density", 0.08),
    ];

    public static SoilHealthReport Assess(SoilSample s)
    {
        var ind = new List<IndicatorScore>
        {
            new("pH", s.Ph, OptimumRange(s.Ph, 4.0, 6.0, 7.2, 8.8), s.Ph < 5.5 ? "Acidic" : s.Ph > 7.5 ? "Alkaline" : "Optimal"),
            new("Organic carbon", s.OrganicCarbonPct, MoreIsBetter(s.OrganicCarbonPct, 0.5, 3.0), Rate(s.OrganicCarbonPct, 1, 2, 3, 5).ToString()),
            new("CEC", s.CecCmolKg, MoreIsBetter(s.CecCmolKg, 5, 25), Rate(s.CecCmolKg, 5, 16, 25, 40).ToString()),
            new("Nitrogen", s.TotalNitrogenPct, MoreIsBetter(s.TotalNitrogenPct, 0.05, 0.3), RateN(s).ToString()),
            new("Phosphorus", s.AvailablePMgKg, OptimumRange(s.AvailablePMgKg, 2, 20, 50, 120), RateP(s).ToString()),
            new("Potassium", s.ExchangeableKCmolKg, OptimumRange(s.ExchangeableKCmolKg, 0.05, 0.4, 1.0, 2.5), RateK(s).ToString()),
            new("Salinity (EC)", s.EcDsM, LessIsBetter(s.EcDsM, 2, 8), s.EcDsM < 2 ? "Non-saline" : s.EcDsM < 4 ? "Slightly saline" : "Saline"),
            new("Bulk density", s.BulkDensity, LessIsBetter(s.BulkDensity, 1.3, 1.8), s.BulkDensity > 1.6 ? "Compacted" : "Good"),
        };
        double score = 0;
        for (int i = 0; i < ind.Count; i++) score += ind[i].Score * Weights[i].Weight;
        score = Math.Round(score * 100, 1);
        string grade = score >= 80 ? "A (Excellent)" : score >= 65 ? "B (Good)" : score >= 50 ? "C (Fair)" : score >= 35 ? "D (Poor)" : "E (Very poor)";
        return new SoilHealthReport(score, grade, ind, Recommend(s));
    }

    private static List<string> Recommend(SoilSample s)
    {
        var r = new List<string>();
        if (s.Ph < 5.5) r.Add($"Apply agricultural lime (dolomite) ≈ {LimeRequirementTonHa(s, 6.5):F1} t/ha to raise pH to 6.5.");
        if (s.Ph > 7.8) r.Add("Use acidifying fertilizers (ammonium sulfate) or elemental sulfur; avoid lime.");
        if (s.OrganicCarbonPct < 1.5) r.Add("Increase organic matter: compost/manure 5–10 t/ha, crop residue retention, cover crops.");
        if (RateN(s) <= NutrientRating.Low) r.Add("Nitrogen is low: split N applications and consider legume rotation.");
        if (RateP(s) <= NutrientRating.Low) r.Add("Phosphorus is low: apply P fertilizer (SP-36/TSP) at planting.");
        if (RateK(s) <= NutrientRating.Low) r.Add("Potassium is low: apply KCl; return crop residues.");
        if (s.EcDsM >= 4) r.Add("Soil is saline: improve drainage, leach with good-quality water, choose tolerant crops.");
        if (s.BulkDensity > 1.6) r.Add("Compaction detected: deep tillage/subsoiling and controlled traffic.");
        return r;
    }

    /// <summary>Lime requirement (t CaCO3/ha) using a CEC-based buffer approximation for a 20 cm layer.</summary>
    public static double LimeRequirementTonHa(SoilSample s, double targetPh)
    {
        if (s.Ph >= targetPh) return 0;
        // ≈ 0.05 × CEC × ΔpH × depth factor; bounded to sensible field rates.
        double t = 0.05 * Math.Max(s.CecCmolKg, 5) * (targetPh - s.Ph) * s.BulkDensity * 2;
        return Math.Round(Math.Clamp(t, 0, 10), 2);
    }

    public static NutrientRating RateN(SoilSample s) => Rate(s.TotalNitrogenPct, 0.1, 0.2, 0.5, 0.75);
    public static NutrientRating RateP(SoilSample s) => Rate(s.AvailablePMgKg, 5, 10, 15, 25);
    public static NutrientRating RateK(SoilSample s) => Rate(s.ExchangeableKCmolKg, 0.1, 0.3, 0.5, 1.0);

    public static NutrientRating Rate(double v, double low, double medium, double high, double veryHigh) =>
        v < low ? NutrientRating.VeryLow : v < medium ? NutrientRating.Low : v < high ? NutrientRating.Medium : v < veryHigh ? NutrientRating.High : NutrientRating.VeryHigh;

    public static double MoreIsBetter(double x, double low, double high) => Math.Clamp((x - low) / (high - low), 0, 1) * 0.9 + (x >= low ? 0.1 : 0);
    public static double LessIsBetter(double x, double good, double bad) => x <= good ? 1 : x >= bad ? 0 : 1 - (x - good) / (bad - good);

    public static double OptimumRange(double x, double a, double b, double c, double d)
    {
        if (x <= a || x >= d) return 0;
        if (x >= b && x <= c) return 1;
        return x < b ? (x - a) / (b - a) : (d - x) / (d - c);
    }
}
