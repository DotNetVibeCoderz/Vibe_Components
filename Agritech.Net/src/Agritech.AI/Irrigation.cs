using Agritech.Crops;
using Agritech.Soil;
using Agritech.Weather;

namespace Agritech.AI;

public sealed record WaterBalanceDay(DateOnly Date, int Dap, double Et0, double Kc, double Ks, double EtcAdj, double Rain, double EffectiveRain,
    double Irrigation, double DepletionMm, double TawMm, double RawMm, double DeepPercolation);

public sealed record IrrigationRecommendation(bool IrrigateNow, double NetDepthMm, double GrossDepthMm, double VolumeM3, DateOnly? NextIrrigation,
    double CurrentDepletionPct, string Reason, IReadOnlyList<WaterBalanceDay> Balance);

/// <summary>
/// FAO-56 single-coefficient daily root-zone water balance (Allen et al. 1998, ch. 8) with irrigation scheduling at the
/// readily-available-water threshold (management allowed depletion).
/// </summary>
public sealed class IrrigationAdvisor(CropDefinition crop, SoilHydraulics soil, WeatherStation station, double efficiency = 0.75)
{
    public double Efficiency { get; } = efficiency;

    /// <summary>Simulates the balance from planting through observed weather, optionally irrigating automatically at RAW.</summary>
    public List<WaterBalanceDay> Simulate(DateOnly planting, IReadOnlyList<DailyWeather> weather, double initialDepletionFraction = 0.2, bool autoIrrigate = false)
    {
        var res = new List<WaterBalanceDay>(weather.Count);
        double rootMin = 0.15;
        double dr = -1;
        foreach (var w in weather)
        {
            int dap = w.Date.DayNumber - planting.DayNumber;
            if (dap < 0) continue;
            double frac = Math.Clamp(dap / (double)(crop.StageDays[0] + crop.StageDays[1]), 0, 1);
            double zr = rootMin + (crop.MaxRootDepthM - rootMin) * frac;
            double taw = soil.TotalAvailableWater(zr);
            double raw = crop.DepletionFraction * taw;
            if (dr < 0) dr = initialDepletionFraction * taw;
            double et0 = w.Et0Mm ?? Evapotranspiration.PenmanMonteith(w, station);
            double kc = crop.CropCoefficient(dap);
            double ks = dr <= raw ? 1 : Math.Max(0, (taw - dr) / ((1 - crop.DepletionFraction) * taw));
            double etc = ks * kc * et0;
            double pe = EffectiveRainfall(w.RainMm);
            double irr = 0;
            dr = dr - pe + etc;
            if (autoIrrigate && dr > raw) { irr = dr; dr = 0; }
            double dp = 0;
            if (dr < 0) { dp = -dr; dr = 0; }
            dr = Math.Min(dr, taw);
            res.Add(new WaterBalanceDay(w.Date, dap, Math.Round(et0, 2), Math.Round(kc, 3), Math.Round(ks, 3), Math.Round(etc, 2), w.RainMm,
                Math.Round(pe, 2), Math.Round(irr, 1), Math.Round(dr, 2), Math.Round(taw, 1), Math.Round(raw, 1), Math.Round(dp, 2)));
        }
        return res;
    }

    /// <summary>USDA-SCS style effective rainfall approximation (daily).</summary>
    public static double EffectiveRainfall(double rainMm) => rainMm <= 2 ? 0 : rainMm <= 50 ? rainMm * 0.8 - 1.6 : 38.4 + (rainMm - 50) * 0.2;

    /// <summary>Recommends irrigation from history plus forecast.</summary>
    public IrrigationRecommendation Recommend(DateOnly planting, IReadOnlyList<DailyWeather> history, IReadOnlyList<DailyWeather> forecast, double areaHa)
    {
        var bal = Simulate(planting, history);
        if (bal.Count == 0) return new(false, 0, 0, 0, null, 0, "Crop not yet planted.", bal);
        var today = bal[^1];
        double depletionPct = today.TawMm == 0 ? 0 : today.DepletionMm / today.TawMm * 100;
        double forecastRain = forecast.Take(3).Sum(f => EffectiveRainfall(f.RainMm));
        if (today.DepletionMm >= today.RawMm && forecastRain < today.DepletionMm * 0.5)
        {
            double net = today.DepletionMm;
            double gross = net / Efficiency;
            return new(true, Math.Round(net, 1), Math.Round(gross, 1), Math.Round(Core.Units.MmToCubicMetresPerHa(gross, areaHa), 1), today.Date,
                Math.Round(depletionPct, 1), $"Root-zone depletion {today.DepletionMm:F1} mm exceeds readily available water {today.RawMm:F1} mm; " +
                $"only {forecastRain:F1} mm effective rain expected in 3 days.", bal);
        }
        // Project forward with forecast to find next irrigation date.
        double dr = today.DepletionMm;
        DateOnly? next = null;
        foreach (var f in forecast)
        {
            int dap = f.Date.DayNumber - planting.DayNumber;
            double et0 = f.Et0Mm ?? Evapotranspiration.PenmanMonteith(f, station);
            dr = Math.Max(0, dr - EffectiveRainfall(f.RainMm) + crop.CropCoefficient(dap) * et0);
            if (dr >= today.RawMm) { next = f.Date; break; }
        }
        string reason = today.DepletionMm >= today.RawMm
            ? $"Soil is past the stress threshold but {forecastRain:F1} mm effective rain is forecast — wait and re-check."
            : $"Soil water adequate (depletion {depletionPct:F0}% of TAW, threshold {crop.DepletionFraction * 100:F0}%).";
        return new(false, 0, 0, 0, next, Math.Round(depletionPct, 1), reason, bal);
    }
}
