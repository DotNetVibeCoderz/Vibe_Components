using AquaCulture.Core;
using AquaCulture.Core.Species;

namespace AquaCulture.WaterQuality;

public enum ParameterStatus { Optimal, Acceptable, Stress, Critical }

public enum WaterQualityClass { Excellent, Good, Fair, Poor, Critical }

/// <summary>Score of one parameter against the species tolerance.</summary>
public sealed record ParameterScore(WaterParameter Parameter, double Value, double Score, ParameterStatus Status, ToleranceRange Range);

/// <summary>Species-aware assessment of a water sample.</summary>
public sealed record WaterQualityAssessment
{
    public required SpeciesProfile Species { get; init; }
    public required WaterSample Sample { get; init; }
    public required IReadOnlyList<ParameterScore> Scores { get; init; }
    /// <summary>Water Quality Index 0–100 (weighted, capped by any critical parameter).</summary>
    public double Wqi { get; init; }
    public WaterQualityClass Class { get; init; }
    /// <summary>Un-ionised ammonia NH₃-N (mg/L) when TAN, temperature and pH are known.</summary>
    public double? UnionizedAmmonia { get; init; }
    public double? DoSaturationPct { get; init; }
    public double? FreeCo2 { get; init; }
    public IReadOnlyList<Text2> Issues { get; init; } = [];

    public ParameterScore? this[WaterParameter p] => Scores.FirstOrDefault(s => s.Parameter == p);
    public bool IsHealthy => Class is WaterQualityClass.Excellent or WaterQualityClass.Good;

    public static Text2 ClassName(WaterQualityClass c) => c switch
    {
        WaterQualityClass.Excellent => new("Excellent", "Sangat baik"),
        WaterQualityClass.Good => new("Good", "Baik"),
        WaterQualityClass.Fair => new("Fair", "Cukup"),
        WaterQualityClass.Poor => new("Poor", "Buruk"),
        _ => new("Critical", "Kritis"),
    };

    public static Text2 StatusName(ParameterStatus s) => s switch
    {
        ParameterStatus.Optimal => new("Optimal", "Optimal"),
        ParameterStatus.Acceptable => new("Acceptable", "Layak"),
        ParameterStatus.Stress => new("Stress", "Stres"),
        _ => new("Critical", "Kritis"),
    };
}

/// <summary>
/// Scores each measured parameter 0–100 against species tolerance bands (piecewise-linear suitability)
/// and combines them into a weighted Water Quality Index. Ammonia is scored as un-ionised NH₃ when temperature and pH are known.
/// </summary>
public static class WaterQualityIndex
{
    /// <summary>Tolerance band used for a parameter and species.</summary>
    public static ToleranceRange RangeFor(WaterParameter p, SpeciesProfile s, bool ammoniaAsNh3 = false) => p switch
    {
        WaterParameter.Temperature => s.Temperature,
        WaterParameter.Ph => s.Ph,
        WaterParameter.DissolvedOxygen => new(s.DoCritical, s.DoOptimal, 12, 20),
        WaterParameter.Orp => new(-50, 100, 400, 550),
        WaterParameter.Salinity => s.Salinity,
        WaterParameter.Tds => s.Habitat == Habitat.Freshwater ? new(-1, 0, 1000, 3000) : new(-1, 0, 60_000, 80_000),
        WaterParameter.Tss => new(-1, 0, 80, 200),
        WaterParameter.Alkalinity => s.Alkalinity,
        WaterParameter.Ammonia => ammoniaAsNh3 ? new(-1, 0, s.Nh3Max, s.Nh3Max * 4) : new(-1, 0, s.TanMax, s.TanMax * 3),
        WaterParameter.Nitrite => new(-1, 0, s.NitriteMax, s.NitriteMax * 4),
        WaterParameter.Nitrate => new(-1, 0, 50, 200),
        WaterParameter.Phosphate => new(-1, 0, 0.5, 2),
        WaterParameter.Bod => new(-1, 0, 10, 30),
        WaterParameter.Cod => new(-1, 0, 40, 120),
        WaterParameter.Turbidity => new(-1, 0, s.TurbidityMax, s.TurbidityMax * 2),
        _ => new(double.MinValue, double.MinValue, double.MaxValue, double.MaxValue),
    };

    /// <summary>Assesses a sample for a species. <paramref name="altitudeM"/> refines DO saturation.</summary>
    public static WaterQualityAssessment Assess(WaterSample sample, SpeciesProfile species, double altitudeM = 0)
    {
        double? nh3 = null, doSat = null, co2 = null;
        if (sample.Ammonia is { } tan && sample.TemperatureC is { } t && sample.Ph is { } ph)
            nh3 = WaterChemistry.UnionizedAmmonia(tan, t, ph, sample.SalinityPpt ?? 0);
        if (sample.DissolvedOxygen is { } dox && sample.TemperatureC is { } t2)
            doSat = WaterChemistry.OxygenPercentSaturation(dox, t2, sample.SalinityPpt ?? 0, altitudeM);
        if (sample.Ph is { } ph2 && sample.Alkalinity is { } alk && sample.TemperatureC is { } t3)
            co2 = WaterChemistry.FreeCo2(ph2, alk, t3);

        var scores = new List<ParameterScore>();
        var issues = new List<Text2>();
        double wsum = 0, ssum = 0;
        bool critical = false;
        foreach (var (p, raw) in sample.Measured())
        {
            bool asNh3 = p == WaterParameter.Ammonia && nh3 is not null;
            double value = asNh3 ? nh3!.Value : raw;
            var range = RangeFor(p, species, asNh3);
            double suit = range.Suitability(value);
            double score = Math.Round(100 * suit, 1);
            var status = range.IsOptimal(value) ? ParameterStatus.Optimal : suit >= 0.6 ? ParameterStatus.Acceptable : suit > 0 ? ParameterStatus.Stress : ParameterStatus.Critical;
            scores.Add(new ParameterScore(p, raw, score, status, range));
            double w = WaterParameters.Info[p].Weight;
            wsum += w; ssum += w * score;
            if (status == ParameterStatus.Critical && w >= 0.06) critical = true;
            if (status is ParameterStatus.Stress or ParameterStatus.Critical) issues.Add(Describe(p, raw, value, range, asNh3, status));
        }
        // DO supersaturation risk (gas bubble disease)
        if (doSat is > 150) issues.Add(new($"DO supersaturation {doSat:0}% — gas bubble disease risk", $"DO lewat jenuh {doSat:0}% — risiko penyakit gelembung gas"));
        if (co2 is > 20) issues.Add(new($"Free CO₂ {co2:0} mg/L is high (limit ~20)", $"CO₂ bebas {co2:0} mg/L tinggi (batas ~20)"));

        double wqi = wsum == 0 ? double.NaN : ssum / wsum;
        if (critical) wqi = Math.Min(wqi, 30);
        var cls = double.IsNaN(wqi) ? WaterQualityClass.Fair : wqi >= 90 ? WaterQualityClass.Excellent : wqi >= 75 ? WaterQualityClass.Good
            : wqi >= 55 ? WaterQualityClass.Fair : wqi >= 35 ? WaterQualityClass.Poor : WaterQualityClass.Critical;
        return new WaterQualityAssessment
        {
            Species = species, Sample = sample, Scores = scores, Wqi = Math.Round(wqi, 1), Class = cls,
            UnionizedAmmonia = nh3 is null ? null : Math.Round(nh3.Value, 4), DoSaturationPct = doSat is null ? null : Math.Round(doSat.Value, 1),
            FreeCo2 = co2 is null ? null : Math.Round(co2.Value, 1), Issues = issues,
        };
    }

    private static Text2 Describe(WaterParameter p, double raw, double value, ToleranceRange r, bool asNh3, ParameterStatus status)
    {
        var info = WaterParameters.Info[p];
        string sevEn = status == ParameterStatus.Critical ? "critical" : "stressful", sevId = status == ParameterStatus.Critical ? "kritis" : "membuat stres";
        bool low = value < r.OptLow;
        if (asNh3)
            return new($"Toxic NH₃ {value:0.000} mg/L is {sevEn} (safe ≤ {r.OptHigh:0.###}; TAN {raw:0.##})",
                       $"NH₃ beracun {value:0.000} mg/L {sevId} (aman ≤ {r.OptHigh:0.###}; TAN {raw:0.##})");
        string dirEn = low ? "low" : "high", dirId = low ? "rendah" : "tinggi";
        string bound = low ? $"{r.OptLow:0.##}" : $"{r.OptHigh:0.##}";
        return new($"{info.Name.En} {raw:0.##} {info.Unit} is {dirEn} and {sevEn} (optimal {(low ? "≥" : "≤")} {bound})",
                   $"{info.Name.Id} {raw:0.##} {info.Unit} {dirId} dan {sevId} (optimal {(low ? "≥" : "≤")} {bound})");
    }

    /// <summary>
    /// CCME Water Quality Index (Canadian Council of Ministers of the Environment) over a set of samples:
    /// F1 scope (failed parameters), F2 frequency (failed tests) and F3 amplitude (excursion), WQI = 100 − √(F1²+F2²+F3²)/1.732.
    /// Objectives are the species optimal ranges.
    /// </summary>
    public static double Ccme(IReadOnlyList<WaterSample> samples, SpeciesProfile species)
    {
        var tests = new List<(WaterParameter P, double Excursion)>();
        var all = new HashSet<WaterParameter>();
        var failedParams = new HashSet<WaterParameter>();
        int total = 0;
        foreach (var s in samples)
            foreach (var (p, v) in s.Measured())
            {
                var r = RangeFor(p, species);
                all.Add(p); total++;
                double exc = v < r.OptLow ? (r.OptLow == 0 ? 0 : r.OptLow / Math.Max(v, 1e-6) - 1) : v > r.OptHigh ? v / Math.Max(r.OptHigh, 1e-6) - 1 : 0;
                if (exc > 0) { failedParams.Add(p); tests.Add((p, exc)); }
            }
        if (total == 0) return double.NaN;
        double f1 = 100.0 * failedParams.Count / all.Count;
        double f2 = 100.0 * tests.Count / total;
        double nse = tests.Sum(t => t.Excursion) / total;
        double f3 = nse / (0.01 * nse + 0.01);
        return Math.Round(100 - Math.Sqrt(f1 * f1 + f2 * f2 + f3 * f3) / 1.732, 1);
    }
}
