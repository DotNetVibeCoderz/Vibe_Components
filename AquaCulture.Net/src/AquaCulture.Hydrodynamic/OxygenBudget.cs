using AquaCulture.Core.Species;
using AquaCulture.Environment;
using AquaCulture.WaterQuality;

namespace AquaCulture.Hydrodynamic;

/// <summary>Inputs of the pond oxygen budget.</summary>
public sealed record OxygenBudgetParameters
{
    public required double VolumeM3 { get; init; }
    public required double DepthM { get; init; }
    public double BiomassKg { get; init; }
    public double MeanWeightG { get; init; } = 100;
    public SpeciesGroup Group { get; init; } = SpeciesGroup.Fish;
    /// <summary>Chlorophyll-a (µg/L) as a proxy for phytoplankton density.</summary>
    public double ChlorophyllUgL { get; init; } = 80;
    /// <summary>Sediment oxygen demand (g O₂ m⁻² day⁻¹).</summary>
    public double SedimentOxygenDemand { get; init; } = 1.5;
    public double SalinityPpt { get; init; }
    public double AltitudeM { get; init; }
    /// <summary>Installed aeration (kW).</summary>
    public double AeratorKw { get; init; }
    /// <summary>Standard oxygen transfer efficiency (kg O₂ per kWh; paddle wheel ≈ 1.8).</summary>
    public double SaeKgPerKwh { get; init; } = 1.8;
}

public sealed record OxygenHour(DateTime Time, double Do, double WaterTempC, double Saturation, double Photosynthesis, double Respiration, double Reaeration, double Aeration, bool AeratorOn);

public sealed record OxygenBudgetResult(IReadOnlyList<OxygenHour> Hours, double MinDo, DateTime MinTime, double MaxDo, double AeratorKwh)
{
    public bool FallsBelow(double threshold) => MinDo < threshold;
}

/// <summary>
/// Zero-dimensional hourly pond oxygen budget (Boyd-style): phytoplankton photosynthesis and respiration, fish/shrimp respiration,
/// sediment oxygen demand, wind-driven surface reaeration and mechanical aeration. Predicts the dawn DO minimum and the
/// latest safe aerator start time.
/// </summary>
public static class PondOxygenBudget
{
    /// <summary>
    /// Animal oxygen consumption (mg O₂ kg⁻¹ h⁻¹) by size and temperature (allometric exponent −0.2, θ = 1.07).
    /// Reference values at 20 °C and 100 g: shrimp 200, fish 150 — about 600 and 300 mg kg⁻¹ h⁻¹ for 12 g shrimp and 100 g tilapia at 30 °C.
    /// </summary>
    public static double AnimalRespiration(SpeciesGroup group, double weightG, double tempC)
    {
        double r20 = (group == SpeciesGroup.Shrimp ? 200 : 150) * Math.Pow(Math.Max(weightG, 0.01) / 100, -0.2);
        return r20 * Math.Pow(1.07, tempC - 20);
    }

    /// <summary>Surface transfer velocity K_L (m/day) from wind at 10 m (Banks &amp; Herrera).</summary>
    public static double WindTransferVelocity(double windMs) =>
        Math.Max(0.1, 0.728 * Math.Sqrt(windMs) - 0.317 * windMs + 0.0372 * windMs * windMs);

    /// <summary>Simulates DO hour by hour. <paramref name="aeratorOn"/> decides when the aerators run.</summary>
    public static OxygenBudgetResult Simulate(OxygenBudgetParameters p, IReadOnlyList<WeatherObservation> hourly, double initialDo,
        Func<DateTime, double, bool>? aeratorOn = null, IReadOnlyList<double>? waterTemps = null)
    {
        aeratorOn ??= (_, _) => false;
        var tw = waterTemps ?? WeatherImpactPredictor.WaterTemperature(hourly, p.DepthM, double.NaN);
        double c = initialDo, kwh = 0;
        var hours = new List<OxygenHour>(hourly.Count);
        double peakSolar = Math.Max(400, hourly.Max(h => h.SolarWm2));
        foreach (var (h, i) in hourly.Select((h, i) => (h, i)))
        {
            bool on = p.AeratorKw > 0 && aeratorOn(h.Time, c);
            var step = StepHour(p, h, tw[i], c, on ? 1 : 0, peakSolar);
            kwh += step.AeratorKwh;
            c = step.Do;
            hours.Add(step.Hour);
        }
        var min = hours.MinBy(x => x.Do)!;
        return new OxygenBudgetResult(hours, min.Do, min.Time, hours.Max(x => x.Do), kwh);
    }

    /// <summary>
    /// One hour of the budget: photosynthesis, plankton/animal/sediment respiration, wind reaeration and mechanical aeration at
    /// <paramref name="aeratorFraction"/> of the installed power (0–1, a variable-speed drive or the share of paddle wheels running).
    /// <paramref name="peakSolar"/> is the day's peak radiation used to scale the light response (W/m²).
    /// </summary>
    public static (double Do, double AeratorKwh, OxygenHour Hour) StepHour(OxygenBudgetParameters p, WeatherObservation h, double waterTempC, double doNow,
        double aeratorFraction, double peakSolar = 900)
    {
        double t = waterTempC, c = doNow, f = Math.Clamp(aeratorFraction, 0, 1);
        double cs = WaterChemistry.OxygenSaturation(t, p.SalinityPpt, p.AltitudeM);
        double theta = Math.Pow(1.047, t - 20);
        peakSolar = Math.Max(400, peakSolar);
        double light = h.SolarWm2 / (h.SolarWm2 + 250) * (1 + 250 / peakSolar);
        double photo = 0.018 * p.ChlorophyllUgL * Math.Min(1.1, light) * theta;                                 // mg/L/h
        double plankton = 0.004 * p.ChlorophyllUgL * theta;
        double animals = p.BiomassKg * AnimalRespiration(p.Group, p.MeanWeightG, t) / (p.VolumeM3 * 1000);      // mg/L/h
        double sod = p.SedimentOxygenDemand / 24 / p.DepthM * theta;
        double reaer = WindTransferVelocity(h.WindMs) / 24 / p.DepthM * (cs - c);
        double aer = 0, kwh = 0;
        if (p.AeratorKw > 0 && f > 0)
        {
            double cs20 = WaterChemistry.OxygenSaturation(20, 0);
            double aotr = f * p.AeratorKw * p.SaeKgPerKwh * Math.Max(0, cs - c) / cs20 * Math.Pow(1.024, t - 20) * 0.9; // kg/h
            aer = aotr * 1e6 / (p.VolumeM3 * 1000);
            kwh = f * p.AeratorKw;
        }
        double resp = plankton + animals + sod;
        c = Math.Max(0, c + photo - resp + reaer + aer);
        return (c, kwh, new OxygenHour(h.Time, Math.Round(c, 3), Math.Round(t, 2), Math.Round(cs, 2), photo, resp, reaer, aer, f > 0));
    }

    /// <summary>Aerators run whenever DO falls below a set-point (DO-controlled aeration).</summary>
    public static Func<DateTime, double, bool> SetPoint(double doSetPoint) => (_, d) => d < doSetPoint;

    /// <summary>Aerators run between two hours of the day (e.g. 22 → 6).</summary>
    public static Func<DateTime, double, bool> Schedule(int startHour, int endHour) =>
        (t, _) => startHour <= endHour ? t.Hour >= startHour && t.Hour < endHour : t.Hour >= startHour || t.Hour < endHour;
}

/// <summary>RAS design summary.</summary>
public sealed record RasDesign(double TanProductionGPerDay, double OxygenDemandKgPerDay, double Co2ProductionKgPerDay, double SolidsKgPerDay,
    double FlowM3PerHour, double TurnoversPerHour, double BiofilterAreaM2, double MediaVolumeM3, double MakeupWaterM3PerDay);

/// <summary>Recirculating aquaculture system mass balance (Timmons &amp; Ebeling).</summary>
public static class RasMassBalance
{
    /// <param name="feedKgPerDay">Daily feed.</param>
    /// <param name="proteinPct">Feed protein (%).</param>
    /// <param name="tankVolumeM3">Culture volume.</param>
    /// <param name="targetTanMgL">Maximum TAN in the tank.</param>
    /// <param name="biofilterEfficiency">TAN removed per pass (0–1).</param>
    /// <param name="areaTanRemoval">Biofilter areal TAN removal (g m⁻² day⁻¹, MBBR ≈ 0.4).</param>
    /// <param name="specificSurface">Media specific surface area (m² m⁻³).</param>
    /// <param name="makeupPctPerDay">New water per day (% of system volume).</param>
    public static RasDesign Design(double feedKgPerDay, double proteinPct, double tankVolumeM3, double targetTanMgL = 1.0, double biofilterEfficiency = 0.5,
        double areaTanRemoval = 0.4, double specificSurface = 500, double makeupPctPerDay = 5)
    {
        double tan = feedKgPerDay * 1000 * proteinPct / 100 * 0.092;                 // g/day
        double o2 = feedKgPerDay * 0.5;                                                // kg/day (fish + nitrification)
        double co2 = o2 * 1.375;
        double solids = feedKgPerDay * 0.27;
        double flowM3Day = tan / (targetTanMgL * biofilterEfficiency);                 // g/day ÷ g/m³
        double area = tan / areaTanRemoval;
        return new RasDesign(tan, o2, co2, solids, flowM3Day / 24, flowM3Day / 24 / tankVolumeM3, area, area / specificSurface, tankVolumeM3 * 1.3 * makeupPctPerDay / 100);
    }
}
