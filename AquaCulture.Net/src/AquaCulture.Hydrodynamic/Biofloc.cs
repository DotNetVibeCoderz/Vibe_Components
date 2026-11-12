using AquaCulture.Core.Data;
using AquaCulture.WaterQuality;

namespace AquaCulture.Hydrodynamic;

/// <summary>
/// Stoichiometry of ammonia removal per gram of TAN-N (Ebeling, Timmons &amp; Bisogni 2006, Aquaculture 257: 346–358).
/// </summary>
public static class NitrogenStoichiometry
{
    // heterotrophic bacteria fed with carbohydrate (sucrose)
    public const double HeteroCarbohydrate = 15.17;   // g carbohydrate per g TAN-N
    public const double HeteroOxygen = 4.71;          // g O₂ per g TAN-N
    public const double HeteroAlkalinity = 3.57;      // g CaCO₃ per g TAN-N
    public const double HeteroBiomass = 8.07;         // g VSS per g TAN-N
    // autotrophic nitrification (TAN → NO₃)
    public const double NitrifOxygen = 4.18;
    public const double NitrifAlkalinity = 7.05;
    public const double NitrifBiomass = 0.20;
    public const double NitrifNitrate = 0.976;
    /// <summary>TAN excreted per kg of feed and per unit of protein fraction (kg TAN-N / kg feed / protein fraction).</summary>
    public const double TanPerFeedProtein = 0.092;
}

/// <summary>Settings of a biofloc (zero/limited-exchange) culture unit.</summary>
public sealed record BioflocParameters
{
    public double VolumeM3 { get; init; } = 1000;
    public double TemperatureC { get; init; } = 29;
    public double DissolvedOxygen { get; init; } = 5.5;
    public double FeedProteinFraction { get; init; } = 0.35;
    /// <summary>Digestible carbohydrate in the feed (fraction of feed mass) that heterotrophs can use.</summary>
    public double FeedCarbohydrateFraction { get; init; } = 0.25;
    /// <summary>Sugar fraction of the carbon source (molasses ≈ 0.5, sucrose 1, tapioca flour ≈ 0.8).</summary>
    public double CarbonSourceSugar { get; init; } = 0.5;
    /// <summary>Maximum heterotrophic TAN uptake (1/d) and half-saturation (mg N/L).</summary>
    public double HeteroMaxRate { get; init; } = 3.0;
    public double HeteroHalfSat { get; init; } = 0.3;
    /// <summary>Mature nitrification capacity (mg N/L/d) for ammonia (AOB) and nitrite (NOB) oxidation, at 20 °C.</summary>
    public double AobCapacity { get; init; } = 2.0;
    public double NobCapacity { get; init; } = 2.0;
    public double NitrifHalfSat { get; init; } = 0.5;
    public double OxygenHalfSat { get; init; } = 1.0;
    public double NitrifTheta { get; init; } = 1.07;
    /// <summary>Daily growth rate of the nitrifier communities while the system matures (AOB establish faster than NOB, hence the nitrite peak).</summary>
    public double AobMaturation { get; init; } = 0.25;
    public double NobMaturation { get; init; } = 0.15;
    /// <summary>Solids removed per day by settling chambers / clarifiers (fraction of TSS).</summary>
    public double SludgeRemovalPerDay { get; init; } = 0.05;
    /// <summary>Water exchange (fraction of volume per day).</summary>
    public double ExchangePerDay { get; init; } = 0.0;
    /// <summary>Settled floc volume per TSS (mL/L per mg/L), Imhoff cone, typically 1/10–1/20.</summary>
    public double FlocVolumePerTss { get; init; } = 1.0 / 12;
    /// <summary>Alkalinity (mg/L CaCO₃) at which nitrification is halved; below ≈ 50 mg/L pH drops and nitrifiers slow down.</summary>
    public double AlkalinityHalfSat { get; init; } = 25;
    /// <summary>When set, sodium bicarbonate is dosed each morning to bring alkalinity back to this level (mg/L CaCO₃).</summary>
    public double? BicarbonateTargetMgL { get; init; }
}

/// <summary>Daily state of a biofloc unit.</summary>
public sealed record BioflocDay(int Day, double FeedKg, double CarbonKg, double Tan, double Nitrite, double Nitrate, double TssMgL, double FlocVolumeMlL,
    double AlkalinityMgL, double OxygenDemandKg, double AobMaturity, double NobMaturity, double CnRatio, double SludgeKg, double BicarbonateKg = 0)
{
    public double Nh3(double ph, double salinity) => WaterChemistry.UnionizedAmmonia(Tan, 29, ph, salinity);
}

/// <summary>
/// Biofloc nitrogen dynamics: TAN from feed is taken up by heterotrophic bacteria as long as carbohydrate is available (C:N control),
/// the rest is nitrified by AOB → NO₂ and NOB → NO₃ (Monod in substrate and DO, θ-temperature correction) by communities that mature
/// over the first weeks — reproducing the start-up nitrite peak. Solids accumulate from bacterial biomass and feces and are removed by
/// settling; alkalinity is consumed by both pathways and, when it runs low, slows nitrification (optional automatic bicarbonate dosing).
/// Hourly Euler integration.
/// </summary>
public static class BioflocModel
{
    /// <summary>Carbon-to-nitrogen ratio of the daily inputs (feed + carbon source).</summary>
    public static double CnRatio(double feedKg, double carbonSourceKg, double proteinFraction, double sugarFraction = 0.5) =>
        feedKg <= 0 ? double.NaN : (feedKg * 0.45 + carbonSourceKg * sugarFraction * 0.42) / (feedKg * proteinFraction / 6.25);

    /// <summary>Carbon source (kg) that lifts the input C:N ratio to <paramref name="targetCn"/> (practical biofloc targets 12–20).</summary>
    public static double CarbonSourceForCn(double feedKg, double proteinFraction, double targetCn = 15, double sugarFraction = 0.5)
    {
        double n = feedKg * proteinFraction / 6.25, feedC = feedKg * 0.45;
        return Math.Max(0, (targetCn * n - feedC) / (sugarFraction * 0.42));
    }

    /// <summary>Carbon source (kg) that immobilises all the TAN excreted from a day's feed through heterotrophic uptake (15.17 g CH per g N).</summary>
    public static double CarbonSourceForFullAssimilation(double feedKg, double proteinFraction, double sugarFraction = 0.5) =>
        feedKg * proteinFraction * NitrogenStoichiometry.TanPerFeedProtein * NitrogenStoichiometry.HeteroCarbohydrate / sugarFraction;

    /// <summary>
    /// Simulates <paramref name="days"/> days. <paramref name="feedKg"/> and <paramref name="carbonKg"/> give the daily feed and carbon source.
    /// Start with immature nitrifiers (<paramref name="maturity0"/> ≈ 0.02) for a new pond, or 1 for an established one.
    /// </summary>
    public static IReadOnlyList<BioflocDay> Simulate(BioflocParameters p, int days, Func<int, double> feedKg, Func<int, double, double> carbonKg,
        double tan0 = 0.2, double no2_0 = 0.05, double no3_0 = 1, double tss0 = 30, double alk0 = 150, double maturity0 = 0.02)
    {
        double L = p.VolumeM3 * 1000;
        double tan = tan0, no2 = no2_0, no3 = no3_0, tss = tss0, alk = alk0, aob = maturity0, nob = maturity0 * 0.5, carb = 0;
        double theta = Math.Pow(p.NitrifTheta, p.TemperatureC - 20);
        double doTerm = p.DissolvedOxygen / (p.OxygenHalfSat + p.DissolvedOxygen);
        var list = new List<BioflocDay>(days);
        for (int d = 0; d < days; d++)
        {
            double feed = feedKg(d), carbon = carbonKg(d, tan), bicarb = 0;
            if (p.BicarbonateTargetMgL is { } target && alk < target)
            {
                bicarb = WaterChemistry.SodiumBicarbonateKg(target - alk, p.VolumeM3);
                alk = target;
            }
            double tanIn = feed * p.FeedProteinFraction * NitrogenStoichiometry.TanPerFeedProtein * 1e6 / L / 24;   // mg N/L/h
            double carbIn = (feed * p.FeedCarbohydrateFraction + carbon * p.CarbonSourceSugar) * 1e6 / L / 24;       // mg CH/L/h
            double fecesIn = feed * 0.25 * 1e6 / L / 24;                                                             // mg TSS/L/h
            double o2Kg = 0, sludgeKg = 0;
            for (int h = 0; h < 24; h++)
            {
                tan += tanIn; carb += carbIn; tss += fecesIn;
                // heterotrophic uptake, limited by kinetics and by carbohydrate
                double hetero = Math.Min(p.HeteroMaxRate / 24 * tan * tan / (p.HeteroHalfSat + tan), carb / NitrogenStoichiometry.HeteroCarbohydrate);
                hetero = Math.Max(0, Math.Min(hetero, tan));
                tan -= hetero; carb -= hetero * NitrogenStoichiometry.HeteroCarbohydrate;
                tss += hetero * NitrogenStoichiometry.HeteroBiomass * 1.25;                    // VSS → TSS
                alk = Math.Max(0, alk - hetero * NitrogenStoichiometry.HeteroAlkalinity);
                o2Kg += hetero * NitrogenStoichiometry.HeteroOxygen * L / 1e6;
                // nitrification
                double alkTerm = Math.Max(0, alk) / (p.AlkalinityHalfSat + Math.Max(0, alk));
                double r1 = Math.Min(tan, p.AobCapacity / 24 * aob * theta * tan / (p.NitrifHalfSat + tan) * doTerm * alkTerm);
                double r2 = Math.Min(no2 + r1, p.NobCapacity / 24 * nob * theta * no2 / (p.NitrifHalfSat + no2) * doTerm * alkTerm);
                tan -= r1; no2 += r1 - r2; no3 += r2 * NitrogenStoichiometry.NitrifNitrate;
                tss += r1 * NitrogenStoichiometry.NitrifBiomass * 1.25;
                alk = Math.Max(0, alk - r1 * NitrogenStoichiometry.NitrifAlkalinity);
                o2Kg += r1 * NitrogenStoichiometry.NitrifOxygen * L / 1e6;
                // removal: settling and exchange; unused carbohydrate decays (respired)
                double removed = tss * p.SludgeRemovalPerDay / 24;
                sludgeKg += removed * L / 1e6; tss -= removed;
                double ex = p.ExchangePerDay / 24;
                tan -= tan * ex; no2 -= no2 * ex; no3 -= no3 * ex; tss -= tss * ex; alk += (alk0 - alk) * ex;
                carb *= 1 - 0.5 / 24;
            }
            // nitrifier communities grow while they have substrate, toward a mature level of 1
            aob += p.AobMaturation * aob * (1 - aob) * Math.Min(1, tan / 0.5 + 0.1);
            nob += p.NobMaturation * nob * (1 - nob) * Math.Min(1, no2 / 0.3 + 0.1);
            list.Add(new BioflocDay(d, feed, carbon, Math.Round(tan, 3), Math.Round(no2, 3), Math.Round(no3, 2), Math.Round(tss), Math.Round(tss * p.FlocVolumePerTss, 1),
                Math.Round(alk, 1), Math.Round(o2Kg, 2), Math.Round(aob, 3), Math.Round(nob, 3),
                Math.Round(CnRatio(feed, carbon, p.FeedProteinFraction, p.CarbonSourceSugar), 1), Math.Round(sludgeKg, 1), Math.Round(bicarb, 2)));
        }
        return list;
    }

    /// <summary>Sodium bicarbonate (kg) that restores alkalinity to <paramref name="targetMgL"/> as CaCO₃.</summary>
    public static double BicarbonateKg(double currentAlkMgL, double targetMgL, double volumeM3) => WaterChemistry.SodiumBicarbonateKg(targetMgL - currentAlkMgL, volumeM3);
}

// ─────────────────────────────── RAS components ───────────────────────────────

/// <summary>
/// Rotating drum (micro-screen) filter. Removal efficiency is the share of suspended solids larger than the mesh, for a log-normal particle
/// size distribution (median and geometric standard deviation in µm); backwash water is a fraction of the treated flow.
/// </summary>
public sealed record DrumFilter(double MeshMicron = 60, double MedianParticleMicron = 60, double GeometricSd = 2.7, double BackwashFraction = 0.005)
{
    public double Efficiency
    {
        get
        {
            double z = (Math.Log(MeshMicron) - Math.Log(MedianParticleMicron)) / Math.Log(GeometricSd);
            return 1 - Normal.Cdf(z);
        }
    }

    public double BackwashM3PerDay(double flowM3PerHour) => flowM3PerHour * 24 * BackwashFraction;
}

/// <summary>
/// Moving-bed biofilm reactor. Areal TAN removal follows Monod kinetics in TAN (half-saturation ≈ 1 mg/L) with a temperature correction,
/// at up to ≈ 1 g TAN/m²/d at 20 °C for warm-water RAS media (Rusten et al. 2006 ranges). Media area = volume × specific surface × fill.
/// </summary>
public sealed record MbbrReactor(double VolumeM3, double SpecificSurfaceM2PerM3 = 500, double FillFraction = 0.5, double MaxArealRate = 1.0, double HalfSatTan = 1.0,
    double Theta = 1.06)
{
    public double MediaAreaM2 => VolumeM3 * SpecificSurfaceM2PerM3 * FillFraction;

    /// <summary>TAN removed (g/d) at a reactor TAN concentration and temperature.</summary>
    public double TanRemovalGPerDay(double tanMgL, double tempC) =>
        MediaAreaM2 * MaxArealRate * tanMgL / (HalfSatTan + tanMgL) * Math.Pow(Theta, tempC - 20);

    /// <summary>Steady-state system TAN (mg/L) at which removal equals the daily TAN load; NaN if the reactor is too small.</summary>
    public double SteadyStateTan(double tanLoadGPerDay, double tempC)
    {
        double cap = MediaAreaM2 * MaxArealRate * Math.Pow(Theta, tempC - 20);
        return tanLoadGPerDay >= cap ? double.NaN : HalfSatTan * tanLoadGPerDay / (cap - tanLoadGPerDay);
    }

    /// <summary>Reactor volume (m³) needed to hold TAN at <paramref name="targetTanMgL"/> for a daily load.</summary>
    public static double RequiredVolumeM3(double tanLoadGPerDay, double targetTanMgL, double tempC, double specificSurface = 500, double fill = 0.5, double maxRate = 1.0,
        double halfSat = 1.0, double theta = 1.06)
    {
        double perM2 = maxRate * targetTanMgL / (halfSat + targetTanMgL) * Math.Pow(theta, tempC - 20);
        return tanLoadGPerDay / perM2 / (specificSurface * fill);
    }
}

/// <summary>
/// Packed-column CO₂ stripper (degasser). Removal per pass rises with the volumetric gas:liquid ratio and the packing depth:
/// E = 1 − exp(−k · G:L · depth), with k ≈ 0.15 /m giving the 50–90 % removals reported for G:L 5–10 and 1 m packing (Summerfelt et al. 2003).
/// </summary>
public sealed record Degasser(double GasLiquidRatio = 10, double PackingDepthM = 1.0, double K = 0.15)
{
    public double Efficiency => 1 - Math.Exp(-K * GasLiquidRatio * PackingDepthM);
    public double AirFlowM3PerHour(double waterFlowM3PerHour) => waterFlowM3PerHour * GasLiquidRatio;
}

/// <summary>Steady state of a RAS loop with its treatment train.</summary>
public sealed record RasLoopState(double TanMgL, double Co2MgL, double TssMgL, double TanLoadGPerDay, double Co2LoadKgPerDay, double SolidsLoadKgPerDay,
    double MakeupM3PerDay, bool BiofilterOverloaded);

/// <summary>Steady-state concentrations of a recirculating system for a feed load and a treatment train (well-mixed tanks).</summary>
public static class RasLoop
{
    public static RasLoopState SteadyState(double feedKgPerDay, double proteinFraction, double flowM3PerHour, DrumFilter drum, MbbrReactor mbbr, Degasser degasser,
        double tempC = 28, double makeupFractionPerDay = 0.05, double systemVolumeM3 = 100)
    {
        double tanLoad = feedKgPerDay * proteinFraction * NitrogenStoichiometry.TanPerFeedProtein * 1000;            // g/d
        double co2Load = feedKgPerDay * 0.25 * 1.375;                                                              // kg/d: ≈ 0.25 kg O₂ per kg feed × 1.375 kg CO₂ per kg O₂
        double solidsLoad = feedKgPerDay * 0.25;                                                                   // kg TSS/d
        double qDay = flowM3PerHour * 24, makeup = systemVolumeM3 * makeupFractionPerDay;
        // TAN: daily load = MBBR removal(TAN) + TAN leaving with make-up water (m³ × mg/L = g); MBBR sees the system concentration
        double Balance(double t) => mbbr.TanRemovalGPerDay(t, tempC) + makeup * t - tanLoad;
        bool over = Balance(50) < 0;
        double tan = over ? 50 : Numerics.Brent(Balance, 0, 50, 1e-9);
        // CO2 and TSS: load / (flow × efficiency + makeup)
        double co2 = co2Load * 1000 / (qDay * degasser.Efficiency + makeup);
        double tss = solidsLoad * 1000 / (qDay * drum.Efficiency + makeup + drum.BackwashM3PerDay(flowM3PerHour));
        return new RasLoopState(Math.Round(tan, 3), Math.Round(co2, 2), Math.Round(tss, 2), Math.Round(tanLoad), Math.Round(co2Load, 2), Math.Round(solidsLoad, 2),
            Math.Round(makeup + drum.BackwashM3PerDay(flowM3PerHour), 1), over);
    }
}

internal static class Normal
{
    /// <summary>Standard normal CDF (Abramowitz–Stegun 26.2.17, |error| &lt; 7.5e-8).</summary>
    public static double Cdf(double z)
    {
        double t = 1 / (1 + 0.2316419 * Math.Abs(z));
        double d = 0.3989423 * Math.Exp(-z * z / 2);
        double p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
        return z > 0 ? 1 - p : p;
    }
}
