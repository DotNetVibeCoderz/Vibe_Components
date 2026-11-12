using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Environment;
using AquaCulture.Hydrodynamic;
using AquaCulture.Native;

namespace AquaCulture.Forecasting;

/// <summary>Overnight dissolved-oxygen forecast.</summary>
public sealed record DoForecast(double PredictedMin, DateTime At, double ProbabilityBelowCritical, double NightDeclinePerHour, DateTime? AeratorStartBy,
    string Method, Text2 Advice, IReadOnlyList<(DateTime Time, double Do)> Curve);

/// <summary>
/// Dawn DO-crash forecasting: blends the observed evening decline (linear extrapolation of readings since dusk) with the
/// pond oxygen-budget model driven by tonight's weather. Returns the latest time to start aerators to stay above the critical DO.
/// </summary>
public static class DoCrashForecaster
{
    /// <param name="aeration">Planned aerator rule for the oxygen-budget part (e.g. <see cref="PondOxygenBudget.SetPoint"/>); null = aerators off.</param>
    public static DoForecast Forecast(TimeSeries recentDo, SpeciesProfile species, OxygenBudgetParameters? pond = null,
        IReadOnlyList<WeatherObservation>? tonight = null, DateTime? now = null, Func<DateTime, double, bool>? aeration = null)
    {
        var t0 = now ?? recentDo.Last?.Time ?? DateTime.UtcNow;
        var dawn = t0.Hour < 6 ? t0.Date.AddHours(6) : t0.Date.AddDays(1).AddHours(6);
        double hoursToDawn = (dawn - t0).TotalHours;

        // observed decline from readings in the last 4 hours after 17:00
        var night = recentDo.Points.Where(p => (t0 - p.Time).TotalHours <= 4 && (p.Time.Hour >= 17 || p.Time.Hour < 6)).ToList();
        double current = recentDo.Last?.Value ?? double.NaN;
        double slope = double.NaN, resid = 0.4;
        if (night.Count >= 3)
        {
            var xs = night.Select(p => (p.Time - t0).TotalHours).ToArray(); var ys = night.Select(p => p.Value).ToArray();
            var fit = Stats.LinearRegression(xs, ys);
            slope = fit.Slope; current = fit.Predict(0);
            resid = Math.Max(0.15, Math.Sqrt(ys.Select((y, i) => Math.Pow(y - fit.Predict(xs[i]), 2)).Sum() / Math.Max(1, ys.Length - 2)));
        }

        var curve = new List<(DateTime, double)>();
        double modelMin = double.NaN; string method;
        if (pond is not null && tonight is { Count: > 0 })
        {
            var w = tonight.Where(x => x.Time >= t0 && x.Time <= dawn).ToList();
            if (w.Count > 0)
            {
                var sim = PondOxygenBudget.Simulate(pond, w, current, aeration);
                curve.AddRange(sim.Hours.Select(h => (h.Time, h.Do)));
                modelMin = sim.MinDo;
            }
        }
        // night respiration slows as DO falls (and plankton respiration declines), so the evening slope is damped (e-folding about 5 h)
        const double tau = 5.0;
        double Damped(double h) => tau * (1 - Math.Exp(-h / tau));
        double trendMin = double.IsNaN(slope) ? double.NaN : current + Math.Min(0, slope) * Damped(hoursToDawn);
        double predicted;
        if (!double.IsNaN(modelMin) && !double.IsNaN(trendMin)) { predicted = 0.5 * modelMin + 0.5 * trendMin; method = "blend(trend, oxygen-budget)"; }
        else if (!double.IsNaN(modelMin)) { predicted = modelMin; method = "oxygen-budget"; }
        else if (!double.IsNaN(trendMin)) { predicted = trendMin; method = "trend"; }
        else { predicted = current - 2.5; method = "rule-of-thumb"; }
        predicted = Math.Max(0, predicted);
        if (curve.Count == 0 && !double.IsNaN(slope))
            for (double h = 0; h <= hoursToDawn; h += 1) curve.Add((t0.AddHours(h), Math.Max(0, current + Math.Min(0, slope) * Damped(h))));

        double sd = resid * Math.Sqrt(1 + hoursToDawn / 4);
        double z = (species.DoCritical - predicted) / sd;
        double pBelow = NormalCdf(z);
        double rate = double.IsNaN(slope) ? (current - predicted) / Math.Max(1, hoursToDawn) * -1 : slope;
        DateTime? startBy = null;
        if (predicted < species.DoOptimal && rate < 0)
        {
            // first hour at which the damped trend crosses the growth optimum
            double hCross = 0;
            if (current > species.DoOptimal)
            {
                double need = (current - species.DoOptimal) / -rate;
                hCross = need >= tau ? hoursToDawn : -tau * Math.Log(1 - need / tau);
            }
            startBy = t0.AddHours(Math.Clamp(hCross, 0, hoursToDawn));
        }
        var advice = pBelow > 0.5
            ? new Text2($"High risk: DO may fall to {predicted:0.0} mg/L by dawn (critical {species.DoCritical}). Start all aerators by {startBy:HH:mm} and do not feed tonight.",
                        $"Risiko tinggi: DO bisa turun ke {predicted:0.0} mg/L menjelang subuh (kritis {species.DoCritical}). Nyalakan semua aerator paling lambat {startBy:HH:mm} dan jangan beri pakan malam ini.")
            : pBelow > 0.15
                ? new Text2($"Moderate risk: dawn DO ≈ {predicted:0.0} mg/L. Run aerators from {startBy:HH:mm} and check at 03:00.",
                            $"Risiko sedang: DO subuh ≈ {predicted:0.0} mg/L. Jalankan aerator mulai {startBy:HH:mm} dan cek pukul 03.00.")
                : new Text2($"Low risk: dawn DO ≈ {predicted:0.0} mg/L.", $"Risiko rendah: DO subuh ≈ {predicted:0.0} mg/L.");
        return new DoForecast(Math.Round(predicted, 2), dawn, Math.Round(pBelow, 3), double.IsNaN(slope) ? double.NaN : Math.Round(slope, 3), startBy, method, advice, curve);
    }

    /// <summary>Standard normal CDF (Abramowitz–Stegun 7.1.26).</summary>
    public static double NormalCdf(double z)
    {
        double t = 1 / (1 + 0.3275911 * Math.Abs(z) / Math.Sqrt(2));
        double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z / 2);
        return z >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }
}

/// <summary>Probabilistic harvest forecast.</summary>
public sealed record HarvestForecast(int Simulations, double TargetWeightG, int DayP10, int DayP50, int DayP90, DateOnly DateP50,
    double WeightAtHorizonP10, double WeightAtHorizonP50, double WeightAtHorizonP90, double BiomassAtHorizonP50Kg, double BiomassAtHorizonP10Kg, double BiomassAtHorizonP90Kg,
    double[] FinalWeights, KernelBackend Backend);

/// <summary>
/// Monte-Carlo harvest forecasting: daily temperature uncertainty in TGC growth (native kernel) and survival uncertainty,
/// giving P10/P50/P90 of harvest date and of biomass at the horizon.
/// </summary>
public static class HarvestForecaster
{
    public static HarvestForecast Forecast(SpeciesProfile species, DateOnly from, double abwG, double population, double targetWeightG,
        IReadOnlyList<double> dailyTempC, int simulations = 500, double tempSd = 1.2, double survivalCv = 0.08, ulong seed = 2026, double? tgc = null, double tgcCv = 0.07)
    {
        double k0 = tgc ?? species.Tgc;
        var temps = dailyTempC.ToArray();
        // cycle-to-cycle growth variability: each simulation draws its own TGC (log-normal, CV = tgcCv)
        var tgcRng = new DeterministicRandom(seed ^ 0x5A5A5A5AUL);
        var tgcs = Enumerable.Range(0, simulations).Select(_ => tgcRng.LogNormal(k0, tgcCv)).ToArray();
        var finals = new double[simulations];
        for (int s = 0; s < simulations; s++) finals[s] = AquaKernels.TgcMonteCarlo(abwG, tgcs[s], temps, 1, tempSd, seed + (ulong)s)[0];

        // day of crossing per simulation (same random stream as the kernel)
        var days = new int[simulations];
        for (int s = 0; s < simulations; s++)
        {
            var rng = new DeterministicRandom(seed + (ulong)s);
            double k = tgcs[s];
            double cube = Math.Cbrt(abwG), target = Math.Cbrt(targetWeightG);
            int d = 0;
            for (; d < temps.Length && cube < target; d++)
            {
                double tt = temps[d] + tempSd * rng.NextGaussian();
                if (tt > 0) cube += k / 1000 * tt;
            }
            days[s] = cube >= target ? d : int.MaxValue;
        }
        var reached = days.Where(x => x != int.MaxValue).Select(x => (double)x).OrderBy(x => x).ToArray();
        int P(double q) => reached.Length == 0 ? -1 : (int)Math.Round(Stats.PercentileSorted(reached, q));

        var surv = new DeterministicRandom(seed ^ 0xA5A5);
        double baseDaily = -Math.Log(species.TypicalSurvival) / species.TypicalCycleDays;
        var biomass = finals.Select(w => w * population * Math.Exp(-baseDaily * temps.Length) * Math.Clamp(surv.LogNormal(1, survivalCv), 0.5, 1.2) / 1000).OrderBy(x => x).ToArray();
        var sortedW = finals.OrderBy(x => x).ToArray();
        int p50 = P(50);
        return new HarvestForecast(simulations, targetWeightG, P(10), p50, P(90), p50 >= 0 ? from.AddDays(p50) : from.AddDays(temps.Length),
            Stats.PercentileSorted(sortedW, 10), Stats.PercentileSorted(sortedW, 50), Stats.PercentileSorted(sortedW, 90),
            Stats.PercentileSorted(biomass, 50), Stats.PercentileSorted(biomass, 10), Stats.PercentileSorted(biomass, 90), finals, AquaKernels.Backend);
    }
}
