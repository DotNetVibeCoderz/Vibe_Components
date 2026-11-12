using AquaCulture.Core.Species;

namespace AquaCulture.Growth;

/// <summary>Daily environment driving the growth simulation.</summary>
/// <param name="TemperatureC">Mean water temperature.</param>
/// <param name="DissolvedOxygen">Daily minimum (dawn) DO, mg/L.</param>
/// <param name="Nh3">Un-ionised ammonia NH₃-N, mg/L.</param>
/// <param name="SalinityPpt">Salinity.</param>
/// <param name="FeedRatio">Ration fed relative to the feeding table (1 = full table).</param>
/// <param name="ExtraMortality">Additional daily mortality rate (e.g. disease outbreak), 0–1.</param>
public readonly record struct DailyEnvironment(double TemperatureC, double DissolvedOxygen = 6, double Nh3 = 0, double SalinityPpt = double.NaN, double FeedRatio = 1, double ExtraMortality = 0);

/// <summary>State of the stock at the end of a culture day.</summary>
public sealed record DailyGrowthState(
    int Day, DateOnly Date, double AbwG, double Survivors, double BiomassKg, double DailyGainG, double FeedKg, double CumulativeFeedKg,
    double Fcr, double DailyMortalityRate, double TemperatureFactor, double OxygenFactor, double AmmoniaFactor, double DensityFactor);

/// <summary>Initial conditions of a simulation.</summary>
public sealed record StockState(SpeciesProfile Species, DateOnly StartDate, double AbwG, double Count, double VolumeM3, int StartDay = 0);

/// <summary>Result of a growth simulation.</summary>
public sealed class GrowthTrajectory(StockState start, IReadOnlyList<DailyGrowthState> days)
{
    public StockState Start { get; } = start;
    public IReadOnlyList<DailyGrowthState> Days { get; } = days;
    public DailyGrowthState Final => Days[^1];
    public double SurvivalRate => Final.Survivors / Start.Count;

    /// <summary>First day the ABW reaches a target, or null.</summary>
    public DailyGrowthState? FirstReaching(double targetWeightG) => Days.FirstOrDefault(d => d.AbwG >= targetWeightG);
}

/// <summary>
/// AquaGrowth engine: daily thermal-unit growth (TGC) modulated by temperature, oxygen, ammonia, salinity, crowding and ration,
/// with stress-dependent mortality and table-based feed consumption. Deterministic for a given environment.
/// </summary>
public sealed class GrowthSimulator
{
    public GrowthSimulator(SpeciesProfile species) => Species = species;

    public SpeciesProfile Species { get; }

    /// <summary>Optional TGC override (calibrated from farm sampling).</summary>
    public double? Tgc { get; init; }

    /// <summary>Base daily mortality implied by the species' typical cycle survival.</summary>
    public double BaseMortality => -Math.Log(Species.TypicalSurvival) / Species.TypicalCycleDays;

    /// <summary>Daily mortality rate under a given environment.</summary>
    public double MortalityRate(in DailyEnvironment e, double abwG)
    {
        var s = Species;
        double m = BaseMortality;
        // early stages (fry / post-larvae) are more fragile
        if (abwG < s.MarketWeightG * 0.02) m *= 1.8;
        if (e.DissolvedOxygen < s.DoCritical) m += 0.05 * (1 + (s.DoCritical - e.DissolvedOxygen) / s.DoCritical * 4);
        else if (e.DissolvedOxygen < s.DoOptimal) m += 0.002 * (s.DoOptimal - e.DissolvedOxygen) / (s.DoOptimal - s.DoCritical);
        if (e.Nh3 > s.Nh3Max * 2) m += 0.01 * (e.Nh3 / s.Nh3Max - 2);
        if (!s.Temperature.IsTolerable(e.TemperatureC)) m += 0.08;
        else if (!s.Temperature.IsOptimal(e.TemperatureC)) m += 0.0015;
        if (!double.IsNaN(e.SalinityPpt) && !s.Salinity.IsTolerable(e.SalinityPpt)) m += 0.05;
        return Math.Clamp(m + e.ExtraMortality, 0, 1);
    }

    /// <summary>Runs <paramref name="days"/> daily steps with an environment provider (day index → conditions).</summary>
    public GrowthTrajectory Simulate(StockState start, int days, Func<int, DateOnly, DailyEnvironment> environment)
    {
        var s = Species;
        double tgc = Tgc ?? s.Tgc;
        double w = start.AbwG, n = start.Count, cumFeed = 0, deadBiomass = 0;
        double initialBiomass = w * n / 1000;
        var list = new List<DailyGrowthState>(days);
        for (int d = 1; d <= days; d++)
        {
            var date = start.StartDate.AddDays(d);
            var e = environment(start.StartDay + d, date);
            double biomassKg = w * n / 1000;
            double fT = GrowthModifiers.Temperature(s, e.TemperatureC);
            double fO = GrowthModifiers.Oxygen(s, e.DissolvedOxygen);
            double fN = GrowthModifiers.Ammonia(s, e.Nh3);
            double fS = double.IsNaN(e.SalinityPpt) ? 1 : GrowthModifiers.Salinity(s, e.SalinityPpt);
            double fD = start.VolumeM3 > 0 ? GrowthModifiers.Density(s, biomassKg / start.VolumeM3) : 1;
            double fF = GrowthModifiers.Feed(e.FeedRatio);
            double f = fT * fO * fN * fS * fD * fF;

            double wNext = Math.Pow(Math.Cbrt(w) + tgc / 1000 * Math.Max(0, e.TemperatureC) * f, 3);
            // Feed: what the gain requires at the species' FCR, worse under stress (maintenance costs rise when growth is limited),
            // capped by appetite (table ration scaled by temperature/oxygen and the ration actually offered).
            double gainKg = (wNext - w) * n / 1000;
            double stress = Math.Max(0.35, Math.Sqrt(fT * fO * fN * fS * fD));
            double appetite = biomassKg * s.FeedingRatePercent(w) / 100 * 1.8 * Math.Min(1, fT * 1.1) * Math.Min(1, 0.4 + 0.6 * fO);
            double feedKg = Math.Min(appetite * Math.Max(0, Math.Min(1.3, e.FeedRatio)), gainKg * s.TypicalFcr / stress + biomassKg * 0.001 * (1 - stress));
            double m = MortalityRate(e, w);
            double dead = n * m;
            deadBiomass += dead * (w + wNext) / 2 / 1000;
            n -= dead;
            cumFeed += feedKg;
            double gain = wNext - w;
            w = wNext;
            double biomassNow = w * n / 1000;
            double produced = biomassNow + deadBiomass - initialBiomass;
            double fcr = produced > 0 ? cumFeed / produced : double.NaN;
            list.Add(new DailyGrowthState(start.StartDay + d, date, w, n, biomassNow, gain, feedKg, cumFeed, fcr, m, fT, fO, fN, fD));
        }
        return new GrowthTrajectory(start, list);
    }

    /// <summary>Simulation under constant conditions.</summary>
    public GrowthTrajectory Simulate(StockState start, int days, DailyEnvironment constant) => Simulate(start, days, (_, _) => constant);

    /// <summary>Simulates until a target weight is reached (or <paramref name="maxDays"/>).</summary>
    public GrowthTrajectory SimulateToWeight(StockState start, double targetWeightG, Func<int, DateOnly, DailyEnvironment> environment, int maxDays = 400)
    {
        var full = Simulate(start, maxDays, environment);
        int idx = full.Days.ToList().FindIndex(d => d.AbwG >= targetWeightG);
        return idx < 0 ? full : new GrowthTrajectory(start, full.Days.Take(idx + 1).ToList());
    }
}
