using AquaCulture.Core;
using AquaCulture.Core.Farming;

namespace AquaCulture.Environment;

public enum ImpactSeverity { Low, Moderate, High, Severe }

/// <summary>One predicted effect of the weather on a culture unit.</summary>
public sealed record WeatherImpactItem(string Code, ImpactSeverity Severity, Text2 Effect, Text2 Advice);

/// <summary>Predicted pond response to a weather window.</summary>
public sealed record WeatherImpactReport(double WaterTempMinC, double WaterTempMaxC, double TotalRainMm, double SalinityDropPpt, double PhDrop,
    double PhotosynthesisIndex, double StratificationRisk, double MaxWindMs, IReadOnlyList<WeatherImpactItem> Impacts)
{
    public ImpactSeverity Overall => Impacts.Count == 0 ? ImpactSeverity.Low : Impacts.Max(i => i.Severity);
}

/// <summary>Pond description for weather-impact prediction.</summary>
public sealed record PondExposure(double DepthM, double SalinityPpt = 0, CultureSystem System = CultureSystem.EarthenPond, bool AcidSulfateSoil = false,
    double CurrentWaterTempC = double.NaN, bool Covered = false);

/// <summary>
/// Weather impact predictor: water temperature (first-order lag to an equilibrium temperature), rain dilution and pH drop,
/// photosynthesis/DO pressure from cloud cover, thermal stratification on calm hot days, wind and storm risk for cages.
/// </summary>
public static class WeatherImpactPredictor
{
    /// <summary>Hourly water temperature: dTw/dt = (Te − Tw)/τ with τ ∝ depth and Te from air temperature, solar heating and wind cooling.</summary>
    public static double[] WaterTemperature(IReadOnlyList<WeatherObservation> hourly, double depthM, double initialC, bool covered = false)
    {
        var tw = new double[hourly.Count];
        double t = double.IsNaN(initialC) ? hourly.Take(24).Average(h => h.AirTempC) + 1 : initialC;
        double tau = 18 * depthM + 6; // hours
        for (int i = 0; i < hourly.Count; i++)
        {
            var h = hourly[i];
            double te = h.AirTempC + (covered ? 0.002 : 0.006) * h.SolarWm2 - 0.35 * h.WindMs + (h.RainfallMm > 0 ? -1.0 : 0) + 1.2;
            t += (te - t) / tau;
            tw[i] = t;
        }
        return tw;
    }

    public static WeatherImpactReport Predict(IReadOnlyList<WeatherObservation> window, PondExposure pond)
    {
        if (window.Count == 0) throw new ArgumentException("Empty weather window.", nameof(window));
        var tw = WaterTemperature(window, pond.DepthM, pond.CurrentWaterTempC, pond.Covered);
        double rain = window.Sum(w => w.RainfallMm);
        double maxRain24 = MaxRolling(window.Select(w => w.RainfallMm).ToArray(), 24);
        double salDrop = pond.SalinityPpt > 0 ? pond.SalinityPpt - pond.SalinityPpt * pond.DepthM * 1000 / (pond.DepthM * 1000 + rain) : 0;
        double phDrop = Math.Min(1.2, maxRain24 / 50 * (pond.AcidSulfateSoil ? 0.6 : 0.25));
        double maxWind = window.Max(w => w.WindMs);
        double clearSum = window.Sum(w => w.SolarWm2), potential = window.Sum(w => SyntheticWeather.ClearSkySolar(-6, w.Time.DayOfYear, w.Time.Hour + 0.5));
        double photo = potential <= 0 ? 1 : Math.Clamp(clearSum / potential, 0, 1.2);
        // stratification: strong daytime heating + calm wind, worse in deep ponds and reservoirs
        double strat = Math.Clamp((window.Where(w => w.SolarWm2 > 500).Select(w => 1 - Math.Min(1, w.WindMs / 3)).DefaultIfEmpty(0).Average()) *
                                  Math.Min(1.5, pond.DepthM / 1.5), 0, 1);

        var items = new List<WeatherImpactItem>();
        double tMax = tw.Max(), tMin = tw.Min();
        if (tMax > 33) items.Add(new("heat", tMax > 35 ? ImpactSeverity.High : ImpactSeverity.Moderate,
            new($"Water may reach {tMax:0.0} °C; oxygen solubility drops and stress rises", $"Air bisa mencapai {tMax:0.0} °C; kelarutan oksigen turun dan stres meningkat"),
            new("Raise the water level, feed early morning, run aerators at night", "Tinggikan air, beri pakan pagi hari, nyalakan aerator malam")));
        if (tMax - tMin > 4) items.Add(new("temp-swing", ImpactSeverity.Moderate,
            new($"Water temperature swings {tMax - tMin:0.0} °C", $"Suhu air berfluktuasi {tMax - tMin:0.0} °C"),
            new("Deepen the pond or add shading; avoid handling", "Perdalam kolam atau beri naungan; hindari penanganan ikan")));
        if (salDrop > 2) items.Add(new("dilution", salDrop > 5 ? ImpactSeverity.High : ImpactSeverity.Moderate,
            new($"{rain:0} mm of rain dilutes salinity by ≈ {salDrop:0.0} ppt", $"Hujan {rain:0} mm menurunkan salinitas ≈ {salDrop:0.0} ppt"),
            new("Drain the fresh surface layer from the top outlet; prepare mineral salt", "Buang lapisan air tawar di permukaan lewat pintu atas; siapkan garam mineral")));
        if (phDrop > 0.25) items.Add(new("acid-runoff", phDrop > 0.6 ? ImpactSeverity.High : ImpactSeverity.Moderate,
            new($"Heavy rain ({maxRain24:0} mm/24 h) may lower pH by ≈ {phDrop:0.0}", $"Hujan lebat ({maxRain24:0} mm/24 jam) dapat menurunkan pH ≈ {phDrop:0.0}"),
            new("Lime the dikes before rain (dolomite 50–100 kg/ha); check pH next morning", "Kapur pematang sebelum hujan (dolomit 50–100 kg/ha); cek pH besok pagi")));
        if (photo < 0.45) items.Add(new("cloudy", photo < 0.3 ? ImpactSeverity.High : ImpactSeverity.Moderate,
            new($"Overcast ({photo:P0} of clear-sky light): phytoplankton produce little oxygen, dawn DO will be low", $"Mendung ({photo:P0} cahaya cerah): fitoplankton menghasilkan sedikit oksigen, DO subuh akan rendah"),
            new("Run aerators from midnight and reduce feed 20–30 %", "Nyalakan aerator sejak tengah malam dan kurangi pakan 20–30 %")));
        if (strat > 0.5) items.Add(new("stratification", pond.System == CultureSystem.FloatingNetCage ? ImpactSeverity.High : ImpactSeverity.Moderate,
            new("Hot, calm conditions favour thermal stratification; a later cold rain can overturn low-oxygen bottom water", "Panas dan tenang memicu stratifikasi; hujan dingin berikutnya dapat membalik air dasar yang miskin oksigen"),
            new(pond.System == CultureSystem.FloatingNetCage ? "Watch for upwelling (umbalan); be ready to move or harvest cages" : "Mix the water column with aerators at night",
                pond.System == CultureSystem.FloatingNetCage ? "Waspadai umbalan; siapkan pemindahan atau panen KJA" : "Aduk kolom air dengan aerator pada malam hari")));
        if (maxWind > 10) items.Add(new("storm", pond.System == CultureSystem.FloatingNetCage ? ImpactSeverity.Severe : ImpactSeverity.Moderate,
            new($"Gusts up to {maxWind:0} m/s", $"Angin kencang hingga {maxWind:0} m/s"),
            new("Secure cage moorings, nets and floats; check power for aerators", "Kencangkan tambat, jaring dan pelampung KJA; pastikan listrik aerator")));
        return new WeatherImpactReport(Math.Round(tMin, 1), Math.Round(tMax, 1), Math.Round(rain, 1), Math.Round(salDrop, 2), Math.Round(phDrop, 2),
            Math.Round(photo, 2), Math.Round(strat, 2), Math.Round(maxWind, 1), items);
    }

    private static double MaxRolling(double[] v, int window)
    {
        double s = 0, max = 0;
        for (int i = 0; i < v.Length; i++) { s += v[i]; if (i >= window) s -= v[i - window]; max = Math.Max(max, s); }
        return max;
    }
}
