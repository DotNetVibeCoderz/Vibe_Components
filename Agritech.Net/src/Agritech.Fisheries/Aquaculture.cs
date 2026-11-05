using Agritech.Core.Hosting;

namespace Agritech.Fisheries;

/// <summary>Aquaculture species parameters (tropical production systems).</summary>
public sealed record AquaSpecies(string Id, string Name, string NameId, double OptTempMinC, double OptTempMaxC, double MinDoMgL, double PhMin, double PhMax,
    double MaxUnionizedAmmoniaMgL, double TypicalFcr, double Tgc, double MaxDensityKgM3, double SalinityPpt = 0)
{
    public static readonly AquaSpecies Tilapia = new("tilapia", "Nile tilapia", "Nila", 26, 32, 3, 6.5, 8.5, 0.05, 1.5, 1.8, 30);
    public static readonly AquaSpecies Catfish = new("catfish", "African catfish", "Lele", 25, 30, 2, 6.5, 8.5, 0.1, 1.0, 2.2, 100);
    public static readonly AquaSpecies Pangasius = new("pangasius", "Striped catfish", "Patin", 26, 30, 2.5, 6.5, 8.5, 0.1, 1.6, 2.0, 60);
    public static readonly AquaSpecies Shrimp = new("vannamei", "Whiteleg shrimp", "Udang vaname", 28, 32, 4, 7.5, 8.5, 0.03, 1.4, 0.9, 3, 15);
    public static readonly AquaSpecies Milkfish = new("milkfish", "Milkfish", "Bandeng", 25, 32, 3, 7.0, 8.5, 0.05, 1.8, 1.5, 2, 15);
    public static readonly AquaSpecies Gourami = new("gourami", "Giant gourami", "Gurame", 25, 30, 3, 6.5, 8.0, 0.05, 1.8, 0.9, 15);
    public static readonly AquaSpecies[] All = [Tilapia, Catfish, Pangasius, Shrimp, Milkfish, Gourami];
}

public sealed record WaterSample(DateTimeOffset Time, double TempC, double DoMgL, double Ph, double TanMgL, double SalinityPpt = 0, double? TransparencyCm = null);

public sealed record WaterQualityReport(double UnionizedAmmoniaMgL, double DoSaturationMgL, double DoPercentSaturation, IReadOnlyList<string> Issues, string Status);

/// <summary>Water chemistry for aquaculture.</summary>
public static class WaterQuality
{
    /// <summary>Unionised ammonia fraction (Emerson et al. 1975): f = 1 / (10^(pKa − pH) + 1), pKa = 0.09018 + 2729.92/T(K).</summary>
    public static double UnionizedAmmoniaFraction(double tempC, double ph)
    {
        double pka = 0.09018 + 2729.92 / (tempC + 273.15);
        return 1 / (Math.Pow(10, pka - ph) + 1);
    }

    /// <summary>Dissolved oxygen saturation (mg/L) — freshwater polynomial with salinity and altitude correction.</summary>
    public static double DoSaturation(double tempC, double salinityPpt = 0, double altitudeM = 0)
    {
        double t = tempC;
        double fresh = 14.652 - 0.41022 * t + 0.007991 * t * t - 0.000077774 * t * t * t;
        double salCorr = 1 - salinityPpt * 0.0056; // ≈ −0.56% per ppt in the 0–35 ppt range
        double pressure = Math.Exp(-altitudeM / 8434.5);
        return Math.Max(0, fresh * salCorr * pressure);
    }

    public static WaterQualityReport Assess(WaterSample s, AquaSpecies sp, double altitudeM = 0)
    {
        double nh3 = s.TanMgL * UnionizedAmmoniaFraction(s.TempC, s.Ph);
        double sat = DoSaturation(s.TempC, s.SalinityPpt, altitudeM);
        var issues = new List<string>();
        if (s.DoMgL < sp.MinDoMgL) issues.Add($"DO {s.DoMgL:F1} mg/L below {sp.MinDoMgL} — run aerators, reduce feeding.");
        if (s.TempC < sp.OptTempMinC || s.TempC > sp.OptTempMaxC) issues.Add($"Temperature {s.TempC:F1} °C outside optimum {sp.OptTempMinC}-{sp.OptTempMaxC} °C.");
        if (s.Ph < sp.PhMin || s.Ph > sp.PhMax) issues.Add($"pH {s.Ph:F1} outside {sp.PhMin}-{sp.PhMax}" + (s.Ph < sp.PhMin ? " — apply agricultural lime." : " — partial water exchange, reduce photosynthesis load."));
        if (nh3 > sp.MaxUnionizedAmmoniaMgL) issues.Add($"Toxic NH3 {nh3:F3} mg/L (> {sp.MaxUnionizedAmmoniaMgL}) — exchange water, stop feeding 24 h, add probiotics.");
        if (s.TransparencyCm is { } tr && tr < 25) issues.Add($"Transparency {tr} cm — dense plankton bloom, night-time DO crash risk.");
        string status = issues.Count == 0 ? "Good" : issues.Any(i => i.StartsWith("DO") || i.StartsWith("Toxic")) ? "Critical" : "Warning";
        return new WaterQualityReport(Math.Round(nh3, 4), Math.Round(sat, 2), Math.Round(s.DoMgL / sat * 100, 1), issues, status);
    }
}

public sealed record FeedingPlan(double BiomassKg, double FeedingRatePct, double DailyFeedKg, int MealsPerDay, double PelletSizeMm, string Note);

public sealed record GrowthProjection(int Day, double AvgWeightG, double Survivors, double BiomassKg, double CumulativeFeedKg);

/// <summary>A culture unit (pond, tank, cage) with its stock.</summary>
public sealed class Pond(string id, AquaSpecies species, double areaM2, double depthM)
{
    public string Id { get; } = id;
    public AquaSpecies Species { get; } = species;
    public double AreaM2 { get; } = areaM2;
    public double DepthM { get; } = depthM;
    public double VolumeM3 => AreaM2 * DepthM;
    public int StockedCount { get; set; }
    public double AvgWeightG { get; set; }
    public double SurvivalRate { get; set; } = 0.9;
    public double Biomass => StockedCount * SurvivalRate * AvgWeightG / 1000;
    public double DensityKgM3 => Biomass / VolumeM3;
}

/// <summary>Feed and growth optimisation using the Thermal Growth Coefficient (TGC) model.</summary>
public static class FeedOptimizer
{
    /// <summary>Base feeding rate (% body weight/day) by fish size — standard tropical feeding table.</summary>
    public static double BaseFeedingRate(double avgWeightG) => avgWeightG switch
    {
        < 5 => 10, < 20 => 6, < 50 => 4.5, < 100 => 3.5, < 200 => 3, < 400 => 2.2, < 800 => 1.7, _ => 1.3,
    };

    public static double PelletSize(double avgWeightG) => avgWeightG switch { < 5 => 0.8, < 20 => 1.5, < 50 => 2, < 150 => 3, < 400 => 4, _ => 6 };

    /// <summary>Feeding plan adjusted for temperature and dissolved oxygen (feeding when DO is low wastes feed and pollutes).</summary>
    public static FeedingPlan Plan(Pond pond, WaterSample water)
    {
        var sp = pond.Species;
        double rate = BaseFeedingRate(pond.AvgWeightG);
        double tFactor = water.TempC < sp.OptTempMinC - 4 ? 0.5 : water.TempC < sp.OptTempMinC ? 0.8 : water.TempC > sp.OptTempMaxC + 2 ? 0.6 : 1.0;
        double doFactor = water.DoMgL < sp.MinDoMgL ? 0.0 : water.DoMgL < sp.MinDoMgL + 1 ? 0.6 : 1.0;
        double nh3 = water.TanMgL * WaterQuality.UnionizedAmmoniaFraction(water.TempC, water.Ph);
        double nFactor = nh3 > sp.MaxUnionizedAmmoniaMgL ? 0.5 : 1.0;
        double eff = rate * tFactor * doFactor * nFactor;
        double feed = pond.Biomass * eff / 100;
        int meals = pond.AvgWeightG < 20 ? 5 : pond.AvgWeightG < 100 ? 4 : 3;
        string note = doFactor == 0 ? "Feeding suspended: dissolved oxygen too low."
            : (tFactor < 1 || doFactor < 1 || nFactor < 1) ? $"Ration reduced to {eff / rate * 100:F0}% due to water conditions." : "Full ration.";
        return new FeedingPlan(Math.Round(pond.Biomass, 1), Math.Round(eff, 2), Math.Round(feed, 2), meals, PelletSize(pond.AvgWeightG), note);
    }

    /// <summary>Weight after <paramref name="days"/> at mean temperature T: W^(1/3) = W0^(1/3) + TGC·T·days/1000.</summary>
    public static double ProjectWeight(double w0G, double tgc, double tempC, int days) =>
        Math.Pow(Math.Pow(w0G, 1.0 / 3) + tgc * tempC * days / 1000, 3);

    /// <summary>Days to reach target weight.</summary>
    public static int DaysToTarget(double w0G, double targetG, double tgc, double tempC) =>
        (int)Math.Ceiling((Math.Pow(targetG, 1.0 / 3) - Math.Pow(w0G, 1.0 / 3)) * 1000 / (tgc * tempC));

    /// <summary>Daily growth/feed projection to harvest weight with constant daily mortality.</summary>
    public static IReadOnlyList<GrowthProjection> Project(Pond pond, double meanTempC, double targetWeightG, double cycleSurvival = 0.85, int maxDays = 365)
    {
        var res = new List<GrowthProjection>();
        int days = Math.Min(maxDays, DaysToTarget(pond.AvgWeightG, targetWeightG, pond.Species.Tgc, meanTempC));
        double dailySurv = Math.Pow(cycleSurvival, 1.0 / Math.Max(1, days));
        double n = pond.StockedCount, w = pond.AvgWeightG, feed = 0;
        for (int d = 1; d <= days; d++)
        {
            double wNext = ProjectWeight(w, pond.Species.Tgc, meanTempC, 1);
            n *= dailySurv;
            double gainKg = n * (wNext - w) / 1000;
            feed += gainKg * pond.Species.TypicalFcr;
            w = wNext;
            if (d % 7 == 0 || d == days) res.Add(new GrowthProjection(d, Math.Round(w, 1), Math.Round(n), Math.Round(n * w / 1000, 1), Math.Round(feed, 1)));
        }
        return res;
    }

    /// <summary>Recommended stocking count for a target harvest weight within carrying capacity.</summary>
    public static int RecommendedStocking(Pond pond, double targetWeightG, double expectedSurvival = 0.85) =>
        (int)(pond.Species.MaxDensityKgM3 * 0.8 * pond.VolumeM3 * 1000 / targetWeightG / expectedSurvival);
}

public static class FisheriesBuilderExtensions
{
    public static AgritechBuilder AddFisheries(this AgritechBuilder builder) { builder.TryAddModule("Fisheries"); return builder; }
}
