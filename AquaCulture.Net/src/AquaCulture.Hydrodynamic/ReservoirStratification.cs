using AquaCulture.Core.Species;
using AquaCulture.Environment;
using AquaCulture.WaterQuality;

namespace AquaCulture.Hydrodynamic;

/// <summary>A reservoir or lake column with floating net cages (KJA).</summary>
public sealed record ReservoirSettings
{
    /// <summary>Water depth at the cage site (m) and layer thickness (m).</summary>
    public double DepthM { get; init; } = 20;
    public double LayerM { get; init; } = 0.5;
    /// <summary>Light extinction (1/m); ≈ 1.7 / Secchi depth. Eutrophic KJA reservoirs: 0.8–2.</summary>
    public double ExtinctionPerM { get; init; } = 1.2;
    /// <summary>Chlorophyll-a in the mixed layer (µg/L), drives photosynthesis.</summary>
    public double ChlorophyllUgL { get; init; } = 40;
    /// <summary>Water-column respiration at 20 °C (mg O₂/L/d) — high below fish cages because of uneaten feed and feces.</summary>
    public double WaterRespiration20 { get; init; } = 0.35;
    /// <summary>Sediment oxygen demand at 20 °C (g O₂/m²/d).</summary>
    public double SedimentOxygenDemand20 { get; init; } = 2.5;
    /// <summary>Cages: depth range (m) and stocked biomass per m³ of cage volume.</summary>
    public double CageTopM { get; init; } = 0;
    public double CageBottomM { get; init; } = 3;
    public double CageBiomassKgM3 { get; init; } = 20;
    /// <summary>Share of the water surface around the site covered by cages (their respiration is spread over the column's area).</summary>
    public double CageAreaFraction { get; init; } = 0.01;
    public double FishWeightG { get; init; } = 200;
    public SpeciesGroup Group { get; init; } = SpeciesGroup.Fish;
    /// <summary>Share of wind energy that goes into turbulent mixing (DYRESM-type stirring coefficient).</summary>
    public double WindMixingEfficiency { get; init; } = 0.06;
    /// <summary>Background vertical diffusivity below the mixed layer (m²/s).</summary>
    public double BackgroundDiffusivity { get; init; } = 2e-6;
    public double AltitudeM { get; init; } = 110;
}

/// <summary>Hourly snapshot of the column.</summary>
public sealed record ReservoirHour(DateTime Time, double[] TempC, double[] Do, double MixedLayerM, double SchmidtStability, double CageDo, double AnoxicThicknessM,
    double SurfaceHeatFluxWm2)
{
    public double SurfaceTemp => TempC[0];
    public double BottomTemp => TempC[^1];
}

/// <summary>An upwelling ("umbalan" / "arus balik") episode: the cage zone loses its oxygen because hypolimnetic water mixed up.</summary>
public sealed record UpwellingEvent(DateTime Start, DateTime End, double MinCageDo, double MixedLayerM, double AnoxicBeforeM);

/// <summary>Outcome of a run and a 0–100 risk index for the next days.</summary>
public sealed record UpwellingAssessment(IReadOnlyList<ReservoirHour> Hours, IReadOnlyList<UpwellingEvent> Events, double RiskIndex, Core.Text2 Advice);

/// <summary>
/// One-dimensional (vertical) stratification model of a tropical reservoir column with fish cages — the physics behind
/// <i>umbalan</i> (seasonal or storm-driven turnover in Jatiluhur, Cirata, Saguling, Toba, Maninjau): surface heat budget (shortwave
/// with Beer–Lambert extinction, long-wave, sensible and latent heat by bulk formulae), convective mixing, wind-energy deepening of the
/// mixed layer against the potential-energy barrier of the thermocline (DYRESM-type), weak diffusion below; dissolved oxygen per layer with
/// reaeration, photosynthesis, water-column and sediment demand and cage respiration. When cooling, wind or rain deepen the mixed layer
/// into anoxic hypolimnetic water, oxygen in the cage zone collapses. Horizontal transport is not modelled.
/// </summary>
public static class ReservoirStratification
{
    private const double Rho0 = 1000, Cp = 4186, G = 9.81, Sigma = 5.67e-8, RhoAir = 1.2, CpAir = 1005, Latent = 2.45e6;

    /// <summary>Freshwater density (kg/m³) at temperature (UNESCO-type polynomial for pure water).</summary>
    public static double Density(double t) => 1000 * (1 - (t + 288.9414) / (508929.2 * (t + 68.12963)) * (t - 3.9863) * (t - 3.9863));

    private static double Q(double tempC, double rhPct, double pHpa)
    {
        double es = 6.112 * Math.Exp(17.67 * tempC / (tempC + 243.5));
        double e = es * Math.Clamp(rhPct, 0, 100) / 100;
        return 0.622 * e / (pHpa - 0.378 * e);
    }

    /// <summary>Net surface heat flux (W/m², positive into the water) excluding the penetrating part of shortwave.</summary>
    public static (double SurfaceNet, double Shortwave) HeatFlux(WeatherObservation w, double surfaceC)
    {
        double sw = Math.Max(0, w.SolarWm2) * (1 - 0.08);
        double cloud = double.IsNaN(w.CloudPct) ? 0.5 : w.CloudPct / 100;
        double ta = w.AirTempC + 273.15, ts = surfaceC + 273.15;
        double lwIn = 0.937e-5 * ta * ta * (1 + 0.17 * cloud * cloud) * Sigma * Math.Pow(ta, 4);
        double lwOut = 0.97 * Sigma * Math.Pow(ts, 4);
        double u = Math.Sqrt(w.WindMs * w.WindMs + 4);          // + 2 m/s "gustiness": free convection keeps exchanging heat in calm air
        double sensible = RhoAir * CpAir * 1.3e-3 * u * (w.AirTempC - surfaceC);
        double latent = RhoAir * Latent * 1.5e-3 * u * (Q(w.AirTempC, w.HumidityPct, w.PressureHPa) - Q(surfaceC, 100, w.PressureHPa));
        // rain arrives near the wet-bulb temperature (≈ air − 2 °C) and cools the surface
        double rain = w.RainfallMm / 1000 / 3600 * Rho0 * Cp * (w.AirTempC - 2 - surfaceC);
        return (lwIn - lwOut + sensible + latent + rain, sw);
    }

    /// <summary>Schmidt stability (J/m²): work needed to mix the column to uniform density (per unit area, uniform basin).</summary>
    public static double SchmidtStability(double[] temp, double dz)
    {
        int n = temp.Length;
        var rho = temp.Select(Density).ToArray();
        double mean = rho.Average(), zc = n * dz / 2, s = 0;
        for (int i = 0; i < n; i++) s += (i * dz + dz / 2 - zc) * (rho[i] - mean) * dz;
        return G * s;
    }

    /// <summary>
    /// Runs the column hour by hour. Starts from <paramref name="initialTempC"/> (uniform) and fully oxygenated, unless profiles are given.
    /// </summary>
    public static IReadOnlyList<ReservoirHour> Simulate(ReservoirSettings r, IReadOnlyList<WeatherObservation> weather, double initialTempC = 28,
        double[]? tempProfile = null, double[]? doProfile = null)
    {
        int n = (int)Math.Round(r.DepthM / r.LayerM);
        double dz = r.LayerM, dt = 3600;
        var t = tempProfile is { } tp && tp.Length == n ? (double[])tp.Clone() : Enumerable.Repeat(initialTempC, n).ToArray();
        var o = doProfile is { } dp && dp.Length == n ? (double[])dp.Clone() : t.Select(x => WaterChemistry.OxygenSaturation(x, 0, r.AltitudeM)).ToArray();
        int cageTop = (int)(r.CageTopM / dz), cageBottom = Math.Min(n, (int)Math.Ceiling(r.CageBottomM / dz));
        var hours = new List<ReservoirHour>(weather.Count);
        foreach (var w in weather)
        {
            // ── heat ──
            var (net, sw) = HeatFlux(w, t[0]);
            double surfaceShare = 0.45;                                   // near-infrared absorbed in the top layer
            for (int i = 0; i < n; i++)
            {
                double top = i * dz, bottom = top + dz;
                double absorbed = sw * (1 - surfaceShare) * (Math.Exp(-r.ExtinctionPerM * top) - Math.Exp(-r.ExtinctionPerM * bottom));
                if (i == 0) absorbed += sw * surfaceShare + net;
                t[i] += absorbed * dt / (Rho0 * Cp * dz);
            }
            // ── oxygen sources and sinks ──
            for (int i = 0; i < n; i++)
            {
                double mid = i * dz + dz / 2, theta = Math.Pow(1.047, t[i] - 20);
                double light = sw * Math.Exp(-r.ExtinctionPerM * mid);
                double photo = 0.006 * r.ChlorophyllUgL * light / (light + 150) * theta;                  // mg/L/h (GPP of a eutrophic reservoir: a few mg/L/d at the surface)
                double resp = r.WaterRespiration20 / 24 * theta;
                if (i == n - 1) resp += r.SedimentOxygenDemand20 / 24 / dz * theta;
                if (i >= cageTop && i < cageBottom)
                    resp += r.CageBiomassKgM3 * r.CageAreaFraction * PondOxygenBudget.AnimalRespiration(r.Group, r.FishWeightG, t[i]) / 1000;   // kg/m³ × mg/kg/h → mg/L/h
                double oxygenLimit = o[i] / (o[i] + 0.5);                                                    // respiration slows near anoxia
                o[i] = Math.Max(0, o[i] + photo - resp * oxygenLimit);
            }
            double cs = WaterChemistry.OxygenSaturation(t[0], 0, r.AltitudeM);
            o[0] = Math.Max(0, o[0] + PondOxygenBudget.WindTransferVelocity(w.WindMs) / 24 / dz * (cs - o[0]));
            // ── convective mixing: merge layers until density increases downward ──
            int mixed = ConvectiveMix(t, o);
            // ── wind deepening against the stability of the layers below ──
            double rho0 = Density(t[0]);
            double ustar = Math.Sqrt(RhoAir * 1.3e-3 * w.WindMs * w.WindMs / rho0);
            double energy = r.WindMixingEfficiency * rho0 * Math.Pow(ustar, 3) * dt;      // J/m²
            while (mixed < n)
            {
                double cost = MixingCost(t, mixed + 1, dz) - MixingCost(t, mixed, dz);
                if (cost > energy) break;
                energy -= Math.Max(0, cost);
                MixTop(t, o, mixed + 1);
                mixed++;
            }
            // ── weak diffusion below the mixed layer ──
            double k = r.BackgroundDiffusivity * dt / (dz * dz);
            for (int it = 0; it < 1; it++)
            {
                var t2 = (double[])t.Clone(); var o2 = (double[])o.Clone();
                for (int i = Math.Max(1, mixed); i < n - 1; i++)
                {
                    t2[i] += k * (t[i - 1] - 2 * t[i] + t[i + 1]);
                    o2[i] += k * (o[i - 1] - 2 * o[i] + o[i + 1]);
                }
                Array.Copy(t2, t, n); Array.Copy(o2, o, n);
            }
            double cageDo = 0; for (int i = cageTop; i < cageBottom; i++) cageDo += o[i];
            cageDo /= Math.Max(1, cageBottom - cageTop);
            double anoxic = o.Count(x => x < 1) * dz;
            hours.Add(new ReservoirHour(w.Time, t.Select(x => Math.Round(x, 3)).ToArray(), o.Select(x => Math.Round(x, 3)).ToArray(), mixed * dz,
                Math.Round(SchmidtStability(t, dz), 1), Math.Round(cageDo, 3), anoxic, Math.Round(net + sw, 1)));
        }
        return hours;
    }

    private static int ConvectiveMix(double[] t, double[] o)
    {
        int k = 1;
        while (k < t.Length && Density(t[k]) < Density(MeanOf(t, k)) - 1e-7) { k++; MixTop(t, o, k); }
        MixTop(t, o, k);
        return k;
    }

    private static double MeanOf(double[] a, int k) { double s = 0; for (int i = 0; i < k; i++) s += a[i]; return s / k; }

    private static void MixTop(double[] t, double[] o, int k)
    {
        double mt = MeanOf(t, k), mo = MeanOf(o, k);
        for (int i = 0; i < k; i++) { t[i] = mt; o[i] = mo; }
    }

    /// <summary>Potential energy (J/m², relative) of the top <paramref name="k"/> layers if they were fully mixed, minus as they are.</summary>
    private static double MixingCost(double[] t, int k, double dz)
    {
        double mean = MeanOf(t, k), rhoMixed = Density(mean), pe = 0;
        for (int i = 0; i < k; i++) pe += (rhoMixed - Density(t[i])) * G * -(i * dz + dz / 2) * dz;   // heights are −depth
        return pe;                                                                                   // > 0 for a stable column
    }

    /// <summary>Upwelling episodes: cage-zone DO below <paramref name="criticalDo"/> while the mixed layer reaches previously anoxic depths.</summary>
    public static IReadOnlyList<UpwellingEvent> DetectUpwelling(IReadOnlyList<ReservoirHour> hours, double criticalDo = 3, double layerM = 0.5)
    {
        var events = new List<UpwellingEvent>();
        UpwellingEvent? open = null;
        double anoxicBefore = 0;
        for (int i = 1; i < hours.Count; i++)
        {
            var h = hours[i];
            // anoxic water entrained: the anoxic layer thinned by at least 1 m within 12 hours (night-time minima alone do not count)
            bool entrained = hours[Math.Max(0, i - 12)].AnoxicThicknessM - h.AnoxicThicknessM >= 1;
            if (open is null && h.CageDo < criticalDo && entrained)
            {
                anoxicBefore = hours[Math.Max(0, i - 24)].AnoxicThicknessM;
                open = new UpwellingEvent(h.Time, h.Time, h.CageDo, h.MixedLayerM, anoxicBefore);
            }
            else if (open is not null)
            {
                if (h.CageDo >= criticalDo) { events.Add(open with { End = h.Time }); open = null; }
                else open = open with { End = h.Time, MinCageDo = Math.Min(open.MinCageDo, h.CageDo), MixedLayerM = Math.Max(open.MixedLayerM, h.MixedLayerM) };
            }
        }
        if (open is not null) events.Add(open);
        return events;
    }

    /// <summary>
    /// Runs a forecast and rates the upwelling risk 0–100: the minimum cage DO and the share of the column that is anoxic and could be
    /// entrained (stability low relative to the anoxic store).
    /// </summary>
    public static UpwellingAssessment Assess(ReservoirSettings r, IReadOnlyList<WeatherObservation> forecast, double[] tempProfile, double[] doProfile, double criticalDo = 3)
    {
        var hours = Simulate(r, forecast, tempProfile[0], tempProfile, doProfile);
        var events = DetectUpwelling(hours, criticalDo, r.LayerM);
        double minDo = hours.Min(h => h.CageDo);
        double anoxicShare = doProfile.Count(x => x < 1) / (double)doProfile.Length;
        double stability = SchmidtStability(tempProfile, r.LayerM);
        double fragility = 1 / (1 + stability / 50);                                   // weak stratification → easily overturned
        double risk = Math.Clamp(100 * Math.Max(Math.Clamp((criticalDo + 1 - minDo) / (criticalDo + 1), 0, 1), anoxicShare * fragility * 1.5), 0, 100);
        var advice = risk >= 60
            ? new Core.Text2("High upwelling risk: harvest or move fish to shallower cages, stop feeding, prepare aeration and monitor DO at cage depth hourly.",
                             "Risiko umbalan tinggi: panen atau pindahkan ikan ke keramba dangkal, hentikan pakan, siapkan aerasi, dan pantau DO di kedalaman keramba tiap jam.")
            : risk >= 30
                ? new Core.Text2("Moderate risk: reduce feeding, avoid restocking, watch for cool, windy or rainy nights.",
                                 "Risiko sedang: kurangi pakan, tunda penebaran, waspadai malam yang dingin, berangin, atau hujan.")
                : new Core.Text2("Low risk.", "Risiko rendah.");
        return new UpwellingAssessment(hours, events, Math.Round(risk), advice);
    }
}
