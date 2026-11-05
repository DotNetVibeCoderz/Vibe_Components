using Agritech.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agritech.Crops;

/// <summary>Site description used for land-suitability evaluation.</summary>
public sealed record SiteConditions(
    double MeanTempC,
    double SeasonalRainfallMm,
    double SoilPh,
    double ElevationM = 0,
    bool IrrigationAvailable = false,
    double? MinTempC = null,
    double? MaxTempC = null);

/// <summary>Suitability result in FAO land-suitability classes.</summary>
public sealed record CropSuitability(CropDefinition Crop, double Score, SuitabilityClass Class, IReadOnlyList<string> LimitingFactors);

public enum SuitabilityClass
{
    /// <summary>Highly suitable (≥ 0.8).</summary>
    S1,
    /// <summary>Moderately suitable (≥ 0.6).</summary>
    S2,
    /// <summary>Marginally suitable (≥ 0.4).</summary>
    S3,
    /// <summary>Not suitable.</summary>
    N
}

/// <summary>
/// Fuzzy land-suitability recommender (FAO framework style): each factor gets a trapezoidal membership
/// score and the overall score is the geometric mean, so one strongly limiting factor dominates.
/// </summary>
public sealed class CropRecommender(CropCatalog catalog)
{
    public CropRecommender() : this(CropCatalog.Default) { }

    public IReadOnlyList<CropSuitability> Recommend(SiteConditions site, int top = 5) =>
        catalog.All.Select(c => Evaluate(c, site)).OrderByDescending(s => s.Score).Take(top).ToList();

    public CropSuitability Evaluate(CropDefinition crop, SiteConditions site)
    {
        var limits = new List<string>();
        double temp = Trapezoid(site.MeanTempC, crop.AbsoluteTempMinC, crop.OptimalTempMinC, crop.OptimalTempMaxC, crop.AbsoluteTempMaxC);
        if (temp < 0.6) limits.Add($"temperature {site.MeanTempC:F1}°C (optimum {crop.OptimalTempMinC}-{crop.OptimalTempMaxC}°C)");

        double rain = site.IrrigationAvailable && site.SeasonalRainfallMm < crop.RainfallOptMinMm
            ? 0.9
            : Trapezoid(site.SeasonalRainfallMm, crop.RainfallMinMm, crop.RainfallOptMinMm, crop.RainfallOptMaxMm, crop.RainfallMaxMm);
        if (rain < 0.6) limits.Add($"rainfall {site.SeasonalRainfallMm:F0} mm (optimum {crop.RainfallOptMinMm}-{crop.RainfallOptMaxMm} mm)");

        double ph = Trapezoid(site.SoilPh, crop.PhMin, crop.PhOptMin, crop.PhOptMax, crop.PhMax);
        if (ph < 0.6) limits.Add($"soil pH {site.SoilPh:F1} (optimum {crop.PhOptMin}-{crop.PhOptMax})");

        double elev = site.ElevationM <= crop.ElevationMaxM ? 1 : Math.Max(0, 1 - (site.ElevationM - crop.ElevationMaxM) / 500);
        if (elev < 0.6) limits.Add($"elevation {site.ElevationM:F0} m (max {crop.ElevationMaxM} m)");

        // Geometric mean with a small floor so ranking still distinguishes unsuitable crops.
        double score = Math.Pow(Math.Max(temp, 0.01) * Math.Max(rain, 0.01) * Math.Max(ph, 0.01) * Math.Max(elev, 0.01), 0.25);
        var cls = score >= 0.8 ? SuitabilityClass.S1 : score >= 0.6 ? SuitabilityClass.S2 : score >= 0.4 ? SuitabilityClass.S3 : SuitabilityClass.N;
        return new CropSuitability(crop, Math.Round(score, 4), cls, limits);
    }

    /// <summary>Trapezoidal membership: 0 outside [a,d], 1 within [b,c], linear in between.</summary>
    public static double Trapezoid(double x, double a, double b, double c, double d)
    {
        if (x <= a || x >= d) return 0;
        if (x >= b && x <= c) return 1;
        return x < b ? (x - a) / (b - a) : (d - x) / (d - c);
    }
}

public static class CropsBuilderExtensions
{
    public static AgritechBuilder AddCrops(this AgritechBuilder builder, Action<CropCatalog>? configure = null)
    {
        if (!builder.TryAddModule("Crops")) return builder;
        var catalog = new CropCatalog();
        configure?.Invoke(catalog);
        builder.Services.TryAddSingleton(catalog);
        builder.Services.TryAddSingleton(sp => new CropRecommender(sp.GetRequiredService<CropCatalog>()));
        return builder;
    }
}
