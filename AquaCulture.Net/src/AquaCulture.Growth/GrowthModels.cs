using AquaCulture.Core.Species;

namespace AquaCulture.Growth;

/// <summary>Closed-form growth models used in aquaculture.</summary>
public static class GrowthModels
{
    /// <summary>Thermal-unit growth: W₁ = (W₀^⅓ + TGC/1000 · Σ T)³.</summary>
    public static double Tgc(double w0, double tgc, double degreeDays) => Math.Pow(Math.Cbrt(w0) + tgc / 1000 * degreeDays, 3);

    /// <summary>TGC estimated from two weights and the degree-days between them.</summary>
    public static double EstimateTgc(double w0, double w1, double degreeDays) => (Math.Cbrt(w1) - Math.Cbrt(w0)) / degreeDays * 1000;

    /// <summary>Days needed to grow from w0 to w1 at a constant temperature.</summary>
    public static double DaysToWeight(double w0, double w1, double tgc, double tempC) => (Math.Cbrt(w1) - Math.Cbrt(w0)) / (tgc / 1000 * tempC);

    /// <summary>Specific growth rate (% per day).</summary>
    public static double Sgr(double w0, double w1, double days) => 100 * (Math.Log(w1) - Math.Log(w0)) / days;

    /// <summary>Average daily gain (g per day).</summary>
    public static double Adg(double w0, double w1, double days) => (w1 - w0) / days;

    /// <summary>von Bertalanffy length at age t (years): L = L∞ (1 − e^(−K (t − t₀))).</summary>
    public static double VbLength(double lInf, double k, double t0, double ageYears) => lInf * (1 - Math.Exp(-k * (ageYears - t0)));

    /// <summary>von Bertalanffy weight at age via the length–weight relationship.</summary>
    public static double VbWeight(SpeciesProfile s, double ageYears) => s.WeightFromLength(VbLength(s.LInfinityCm, s.VbK, s.VbT0, ageYears));

    /// <summary>Age (years) at which a length is reached.</summary>
    public static double VbAgeAtLength(double lInf, double k, double t0, double lengthCm) =>
        lengthCm >= lInf ? double.PositiveInfinity : t0 - Math.Log(1 - lengthCm / lInf) / k;

    /// <summary>Gompertz growth W(t) = W∞ · exp(−exp(−k (t − ti))), t in days.</summary>
    public static double Gompertz(double wInf, double k, double inflectionDay, double day) => wInf * Math.Exp(-Math.Exp(-k * (day - inflectionDay)));

    /// <summary>Logistic growth W(t) = W∞ / (1 + exp(−k (t − ti))).</summary>
    public static double Logistic(double wInf, double k, double inflectionDay, double day) => wInf / (1 + Math.Exp(-k * (day - inflectionDay)));
}

/// <summary>Environmental growth modifiers (each 0–1).</summary>
public static class GrowthModifiers
{
    /// <summary>1 inside the optimal temperature band, falling linearly to 0 at the tolerance limits.</summary>
    public static double Temperature(SpeciesProfile s, double tempC) => s.Temperature.Suitability(tempC);

    /// <summary>1 at or above the optimal DO, 0 at the critical DO.</summary>
    public static double Oxygen(SpeciesProfile s, double doMgL) =>
        doMgL >= s.DoOptimal ? 1 : doMgL <= s.DoCritical ? 0 : (doMgL - s.DoCritical) / (s.DoOptimal - s.DoCritical);

    /// <summary>1 below the safe NH₃ limit, linear to 0.3 at four times the limit.</summary>
    public static double Ammonia(SpeciesProfile s, double nh3MgL) =>
        nh3MgL <= s.Nh3Max ? 1 : Math.Max(0.3, 1 - 0.7 * (nh3MgL - s.Nh3Max) / (3 * s.Nh3Max));

    /// <summary>Salinity suitability with a floor of 0.3 while inside the tolerance range.</summary>
    public static double Salinity(SpeciesProfile s, double ppt)
    {
        double f = s.Salinity.Suitability(ppt);
        return f <= 0 ? 0 : Math.Max(0.3, f);
    }

    /// <summary>Crowding: 1 below 70 % of the carrying density, linear to 0.5 at 100 % and above.</summary>
    public static double Density(SpeciesProfile s, double biomassKgM3)
    {
        double cap = s.MaxBiomassKgM3, ratio = biomassKgM3 / cap;
        return ratio <= 0.7 ? 1 : Math.Max(0.5, 1 - 0.5 * (ratio - 0.7) / 0.3);
    }

    /// <summary>Feed restriction: growth scales with (ration / satiation)^0.8, capped at 1.</summary>
    public static double Feed(double feedRatio) => feedRatio <= 0 ? 0 : Math.Min(1, Math.Pow(feedRatio, 0.8));
}
