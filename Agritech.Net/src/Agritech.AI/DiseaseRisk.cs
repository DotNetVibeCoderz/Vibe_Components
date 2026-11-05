using Agritech.Crops;

namespace Agritech.AI;

/// <summary>Cardinal temperatures and wetness requirement for a pathogen (Magarey et al. 2005 generic infection model).</summary>
public sealed record PathogenModel(string Id, string Name, string NameId, string[] HostCropIds, double TminC, double ToptC, double TmaxC,
    double WetnessMinHours, double WetnessMaxHours, string Management)
{
    public static readonly PathogenModel RiceBlast = new("rice-blast", "Rice blast (Pyricularia oryzae)", "Blas padi", ["rice"],
        12, 26, 35, 8, 24, "Use resistant varieties, avoid excess N, apply tricyclazole/azoxystrobin at booting if risk persists.");
    public static readonly PathogenModel LateBlight = new("late-blight", "Late blight (Phytophthora infestans)", "Busuk daun kentang/tomat", ["potato", "tomato"],
        3, 18, 27, 6, 12, "Protectant fungicide (mancozeb/chlorothalonil) before rain events; destroy volunteers; ensure drainage.");
    public static readonly PathogenModel Anthracnose = new("anthracnose", "Anthracnose (Colletotrichum spp.)", "Antraknosa / patek", ["chili", "banana", "coffee"],
        12, 27, 35, 4, 24, "Harvest timely, remove infected fruit, apply propineb/azoxystrobin during prolonged wet periods.");
    public static readonly PathogenModel DownyMildewMaize = new("maize-downy-mildew", "Maize downy mildew (Peronosclerospora maydis)", "Bulai jagung", ["maize"],
        10, 24, 32, 4, 16, "Metalaxyl seed treatment, resistant hybrids, rogue infected seedlings, plant simultaneously.");
    public static readonly PathogenModel BlackPod = new("cacao-black-pod", "Cacao black pod (Phytophthora palmivora)", "Busuk buah kakao", ["cacao"],
        15, 25, 32, 6, 24, "Frequent harvesting, sanitation pruning, copper fungicide in the wet season.");

    public static readonly PathogenModel[] All = [RiceBlast, LateBlight, Anthracnose, DownyMildewMaize, BlackPod];

    /// <summary>Temperature response f(T) ∈ [0,1] (beta function).</summary>
    public double TemperatureResponse(double t)
    {
        if (t <= TminC || t >= TmaxC) return 0;
        double a = (TmaxC - t) / (TmaxC - ToptC);
        double b = (t - TminC) / (ToptC - TminC);
        return a * Math.Pow(b, (ToptC - TminC) / (TmaxC - ToptC));
    }

    /// <summary>Required wetness duration (h) at temperature T; ∞ if outside cardinal range.</summary>
    public double RequiredWetness(double t)
    {
        double f = TemperatureResponse(t);
        return f <= 0 ? double.PositiveInfinity : Math.Min(WetnessMaxHours, WetnessMinHours / f);
    }
}

public enum RiskLevel { Low, Moderate, High, VeryHigh }

public sealed record DiseaseRiskResult(PathogenModel Pathogen, RiskLevel Level, double Index, string Explanation);

/// <summary>Daily wetness/temperature conditions (leaf wetness hours, mean temperature during wetness, RH).</summary>
public readonly record struct InfectionConditions(DateOnly Date, double LeafWetnessHours, double TempDuringWetnessC, double RhMeanPct);

/// <summary>Weather-driven disease risk assessment.</summary>
public static class DiseaseRiskModel
{
    /// <summary>Infection index for one day: wetness hours relative to the temperature-dependent requirement.</summary>
    public static double DailyIndex(PathogenModel p, InfectionConditions c) =>
        double.IsInfinity(p.RequiredWetness(c.TempDuringWetnessC)) ? 0 : Math.Min(2, c.LeafWetnessHours / p.RequiredWetness(c.TempDuringWetnessC));

    public static DiseaseRiskResult Assess(PathogenModel p, IReadOnlyList<InfectionConditions> recentDays)
    {
        if (recentDays.Count == 0) return new(p, RiskLevel.Low, 0, "No data.");
        // Exponentially weighted: recent days matter more.
        double idx = 0, wsum = 0;
        for (int i = 0; i < recentDays.Count; i++)
        {
            double w = Math.Pow(0.75, recentDays.Count - 1 - i);
            idx += w * DailyIndex(p, recentDays[i]); wsum += w;
        }
        idx /= wsum;
        int infectionDays = recentDays.Count(d => DailyIndex(p, d) >= 1);
        var level = idx >= 1.2 || infectionDays >= 3 ? RiskLevel.VeryHigh : idx >= 0.8 ? RiskLevel.High : idx >= 0.45 ? RiskLevel.Moderate : RiskLevel.Low;
        string expl = $"{infectionDays} of {recentDays.Count} days met infection conditions (index {idx:F2}). " +
                      (level >= RiskLevel.High ? p.Management : "Continue routine scouting.");
        return new(p, level, Math.Round(idx, 3), expl);
    }

    /// <summary>Estimates leaf wetness hours from daily RH and rain when no wetness sensor exists (RH ≥ 90% heuristic).</summary>
    public static double EstimateLeafWetnessHours(double rhMeanPct, double rainMm) =>
        Math.Clamp((rhMeanPct - 70) / 30 * 14 + (rainMm > 0.5 ? 4 + Math.Min(6, rainMm / 5) : 0), 0, 24);

    /// <summary>Smith Period for potato/tomato late blight: two consecutive days with Tmin ≥ 10 °C and ≥ 11 h RH ≥ 90 %.</summary>
    public static bool IsSmithPeriod((double TminC, double HoursRh90) day1, (double TminC, double HoursRh90) day2) =>
        day1.TminC >= 10 && day2.TminC >= 10 && day1.HoursRh90 >= 11 && day2.HoursRh90 >= 11;

    public static IEnumerable<PathogenModel> ForCrop(CropDefinition crop) => PathogenModel.All.Where(p => p.HostCropIds.Contains(crop.Id));
}

/// <summary>Insect pest phenology driven by degree days.</summary>
public sealed record PestModel(string Id, string Name, string NameId, string[] HostCropIds, double BaseTempC, double UpperTempC, double DegreeDaysPerGeneration, string Management)
{
    public static readonly PestModel FallArmyworm = new("faw", "Fall armyworm (Spodoptera frugiperda)", "Ulat grayak jagung", ["maize", "rice", "sugarcane"],
        10.9, 35, 559, "Scout whorls twice weekly; action threshold ≈ 20% damaged plants; use Bt/emamectin; conserve natural enemies.");
    public static readonly PestModel BrownPlanthopper = new("bph", "Brown planthopper (Nilaparvata lugens)", "Wereng batang cokelat", ["rice"],
        10, 35, 380, "Avoid excess N and early broad-spectrum insecticides; resistant varieties; light traps for monitoring.");
    public static readonly PestModel FruitFly = new("fruit-fly", "Oriental fruit fly (Bactrocera dorsalis)", "Lalat buah", ["chili", "tomato", "banana"],
        12, 35, 340, "Methyl-eugenol traps, sanitation of fallen fruit, protein bait sprays.");
    public static readonly PestModel CoffeeBerryBorer = new("cbb", "Coffee berry borer (Hypothenemus hampei)", "Penggerek buah kopi", ["coffee"],
        14.9, 32, 330, "Complete harvest (rampasan), alcohol-based traps, Beauveria bassiana.");

    public static readonly PestModel[] All = [FallArmyworm, BrownPlanthopper, FruitFly, CoffeeBerryBorer];

    public double DailyDegreeDays(double tmin, double tmax) => Phenology.GrowingDegreeDays(tmin, tmax, BaseTempC, UpperTempC);

    /// <summary>Generations completed given accumulated degree days.</summary>
    public double Generations(double accumulatedDd) => accumulatedDd / DegreeDaysPerGeneration;
}
