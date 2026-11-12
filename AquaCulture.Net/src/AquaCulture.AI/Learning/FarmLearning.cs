using System.Globalization;
using AquaCulture.AI.Ml;
using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Environment;
using AquaCulture.Feeding;
using AquaCulture.Hydrodynamic;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace AquaCulture.AI.Learning;

/// <summary>One hourly observation of a pond's oxygen regime (from sensors or a simulation).</summary>
public sealed record DoObservation(DateTime Time, double Do, double WaterTempC, double SolarWm2, bool AeratorOn);

/// <summary>Builds hourly <see cref="DoObservation"/> histories: from stored series, or simulated with the pond oxygen budget.</summary>
public static class DoHistory
{
    /// <summary>
    /// Aligns hourly DO, water temperature, solar radiation and aerator series (each resampled to the hour by mean; missing hours are
    /// interpolated from <see cref="TimeSeries.ValueAt"/>). Aerator is "on" when its mean current is above <paramref name="aeratorOnThreshold"/>.
    /// </summary>
    public static IReadOnlyList<DoObservation> FromSeries(TimeSeries dissolvedOxygen, TimeSeries waterTemp, TimeSeries? solar = null, TimeSeries? aerator = null,
        double aeratorOnThreshold = 0.5)
    {
        if (dissolvedOxygen.Count < 2) return [];
        var doH = dissolvedOxygen.Resample(TimeSpan.FromHours(1));
        var tH = waterTemp.Count > 0 ? waterTemp.Resample(TimeSpan.FromHours(1)) : null;
        var sH = solar is { Count: > 0 } ? solar.Resample(TimeSpan.FromHours(1)) : null;
        var aH = aerator is { Count: > 0 } ? aerator.Resample(TimeSpan.FromHours(1)) : null;
        return doH.Points.Select(p => new DoObservation(p.Time, p.Value, tH?.ValueAt(p.Time) ?? double.NaN,
            sH?.ValueAt(p.Time) ?? ClearSky(p.Time), (aH?.ValueAt(p.Time) ?? 0) > aeratorOnThreshold)).ToList();
    }

    /// <summary>Clear-sky proxy when no radiation sensor exists (tropical latitude): 0 at night, a half-sine 06:00–18:00 peaking at 900 W/m².</summary>
    public static double ClearSky(DateTime t)
    {
        double h = t.Hour + t.Minute / 60.0;
        return h is <= 6 or >= 18 ? 0 : 900 * Math.Sin(Math.PI * (h - 6) / 12);
    }

    /// <summary>
    /// A deterministic synthetic pond history: tropical weather with cloudy spells (<see cref="SyntheticWeather"/>), the hourly oxygen
    /// budget with biomass growing linearly over the period, aerators on a DO set point, and sensor noise (σ = 0.1 mg/L).
    /// </summary>
    public static IReadOnlyList<DoObservation> Simulate(int days, ulong seed = 3, double biomassStartKg = 1500, double biomassEndKg = 4000,
        double volumeM3 = 2600, double depthM = 1.3, double aeratorKw = 4.4, double doSetPoint = 4.0, SpeciesGroup group = SpeciesGroup.Shrimp,
        double chlorophyllUgL = 110, DateTime? start = null)
    {
        var t0 = start ?? new DateTime(2026, 1, 1);
        var weather = SyntheticWeather.Generate(-6.3, t0, days, seed);
        var rng = new DeterministicRandom(seed * 7919 + 1);
        var list = new List<DoObservation>(days * 24);
        double d0 = 6.5;
        for (int day = 0; day < days; day++)
        {
            double biomass = biomassStartKg + (biomassEndKg - biomassStartKg) * day / Math.Max(1, days - 1);
            var p = new OxygenBudgetParameters
            {
                VolumeM3 = volumeM3, DepthM = depthM, BiomassKg = biomass, MeanWeightG = 5 + 15.0 * day / Math.Max(1, days), Group = group,
                ChlorophyllUgL = chlorophyllUgL * (0.8 + 0.4 * rng.NextDouble()), AeratorKw = aeratorKw,
            };
            var slice = weather.Skip(day * 24).Take(24).ToList();
            var r = PondOxygenBudget.Simulate(p, slice, d0, PondOxygenBudget.SetPoint(doSetPoint));
            foreach (var h in r.Hours)
                list.Add(new DoObservation(h.Time, Math.Max(0, h.Do + 0.1 * rng.NextGaussian()), h.WaterTempC, slice.FirstOrDefault(w => w.Time == h.Time)?.SolarWm2 ?? 0, h.AeratorOn));
            d0 = r.Hours[^1].Do;
        }
        return list;
    }
}

/// <summary>Feature vector for "DO at t + h" given the history up to hour t.</summary>
public static class DoFeatures
{
    public const int Lookback = 24;
    public const int Count = 15;

    public static readonly string[] Names =
    [
        "do_t", "do_t-1", "do_t-2", "do_t-3", "do_t-6", "do_t-24+h", "slope_3h", "temp_t", "solar_mean_3h", "solar_sum_24h_kWh",
        "aerator_frac_3h", "hour_sin_now", "hour_cos_now", "hour_sin_target", "hour_cos_target",
    ];

    /// <summary>Features at index <paramref name="t"/> (needs <see cref="Lookback"/> hours of history before it).</summary>
    public static float[] Build(IReadOnlyList<DoObservation> h, int t, int horizon)
    {
        if (t < Lookback) throw new ArgumentOutOfRangeException(nameof(t), $"Need {Lookback} hours of history.");
        double Do(int k) => h[t - k].Do;
        double solar3 = (h[t].SolarWm2 + h[t - 1].SolarWm2 + h[t - 2].SolarWm2) / 3;
        double solar24 = 0; for (int k = 0; k < 24; k++) solar24 += h[t - k].SolarWm2 / 1000;
        double aer = (h[t].AeratorOn ? 1 : 0) + (h[t - 1].AeratorOn ? 1 : 0) + (h[t - 2].AeratorOn ? 1 : 0);
        double hourNow = h[t].Time.Hour + h[t].Time.Minute / 60.0, hourTarget = hourNow + horizon;
        double sameHourYesterday = horizon <= 24 ? h[t - 24 + horizon].Do : Do(0);
        double temp = double.IsNaN(h[t].WaterTempC) ? 29 : h[t].WaterTempC;
        return
        [
            (float)Do(0), (float)Do(1), (float)Do(2), (float)Do(3), (float)Do(6), (float)sameHourYesterday, (float)((Do(0) - Do(3)) / 3), (float)temp,
            (float)solar3, (float)solar24, (float)(aer / 3), (float)Math.Sin(2 * Math.PI * hourNow / 24), (float)Math.Cos(2 * Math.PI * hourNow / 24),
            (float)Math.Sin(2 * Math.PI * hourTarget / 24), (float)Math.Cos(2 * Math.PI * hourTarget / 24),
        ];
    }
}

/// <summary>Validation of a learned DO forecaster per horizon, against two naive baselines.</summary>
/// <param name="Persistence">RMSE of "DO stays as it is now".</param>
/// <param name="Diurnal">RMSE of "DO equals the value at the same hour yesterday".</param>
public sealed record DoSkill(int HorizonHours, double Rmse, double Persistence, double Diurnal, double P05Residual, double P95Residual)
{
    /// <summary>1 − RMSE / best baseline RMSE: &gt; 0 means the model beats both baselines.</summary>
    public double SkillScore => 1 - Rmse / Math.Min(Persistence, Diurnal);
}

/// <summary>A multi-hour DO forecast with an empirical 90 % band from validation residuals.</summary>
public sealed record DoForecastCurve(string Model, IReadOnlyList<DateTime> Times, double[] Point, double[] Lower, double[] Upper)
{
    public (DateTime Time, double Do) Minimum
    {
        get { int i = Array.IndexOf(Point, Point.Min()); return (Times[i], Point[i]); }
    }

    /// <summary>First hour where the lower band falls below <paramref name="threshold"/> (an early warning), or null.</summary>
    public DateTime? FirstRiskBelow(double threshold)
    {
        for (int i = 0; i < Lower.Length; i++) if (Lower[i] < threshold) return Times[i];
        return null;
    }
}

/// <summary>Common shape of the learned DO forecasters (gradient-boosted here, LSTM in AquaCulture.AI.Torch).</summary>
public interface IDoForecaster
{
    string Name { get; }
    int Horizon { get; }
    IReadOnlyList<DoSkill> Skill { get; }
    DoForecastCurve Forecast(IReadOnlyList<DoObservation> recent);
}

/// <summary>Naive baselines for a validation window, shared by the learned forecasters.</summary>
public static class DoBaselines
{
    public static (double Persistence, double Diurnal) Rmse(IReadOnlyList<DoObservation> h, int from, int to, int horizon)
    {
        double p = 0, d = 0; int n = 0;
        for (int t = Math.Max(from, DoFeatures.Lookback); t + horizon < to; t++)
        {
            double y = h[t + horizon].Do;
            p += Math.Pow(y - h[t].Do, 2);
            d += Math.Pow(y - h[t + horizon - 24].Do, 2);
            n++;
        }
        return n == 0 ? (double.NaN, double.NaN) : (Math.Sqrt(p / n), Math.Sqrt(d / n));
    }

    public static double Quantile(List<double> v, double q)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        double pos = q * (v.Count - 1); int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return v[lo] + (v[hi] - v[lo]) * (pos - lo);
    }
}

/// <summary>
/// Gradient-boosted DO forecaster (ML.NET FastTree), one model per horizon (direct multi-step), trained on a pond's own hourly history.
/// The last 20 % of the history (chronologically) is held out to measure <see cref="Skill"/> against persistence and same-hour-yesterday,
/// and to set the empirical 90 % band; the models are then refitted on the whole history.
/// </summary>
public sealed class GbtDoForecaster : IDoForecaster
{
    private sealed class Row
    {
        [VectorType(DoFeatures.Count)] public float[] Features { get; set; } = [];
        public float Label { get; set; }
    }

    private sealed class Score { [ColumnName("Score")] public float Value { get; set; } }

    private readonly MLContext _ml = new(seed: 7);
    private readonly List<PredictionEngine<Row, Score>> _engines = [];
    private readonly List<ITransformer> _models = [];
    private readonly List<DoSkill> _skill = [];

    public GbtDoForecaster(int horizon = 12, int trees = 120, int leaves = 16, double learningRate = 0.1)
    {
        Horizon = horizon; Trees = trees; Leaves = leaves; LearningRate = learningRate;
    }

    public string Name => "GBT (ML.NET FastTree)";
    public int Horizon { get; }
    public int Trees { get; }
    public int Leaves { get; }
    public double LearningRate { get; }
    public IReadOnlyList<DoSkill> Skill => _skill;

    public GbtDoForecaster Train(IReadOnlyList<DoObservation> history)
    {
        if (history.Count < DoFeatures.Lookback + Horizon + 48) throw new ArgumentException("Need at least three days of hourly history.");
        int split = (int)(history.Count * 0.8);
        _models.Clear(); _engines.Clear(); _skill.Clear();
        for (int h = 1; h <= Horizon; h++)
        {
            var validation = Fit(Rows(history, DoFeatures.Lookback, split, h));
            var resid = new List<double>();
            double se = 0; int n = 0;
            using (var eng = _ml.Model.CreatePredictionEngine<Row, Score>(validation))
                for (int t = Math.Max(split, DoFeatures.Lookback); t + h < history.Count; t++)
                {
                    double e = history[t + h].Do - eng.Predict(new Row { Features = DoFeatures.Build(history, t, h) }).Value;
                    resid.Add(e); se += e * e; n++;
                }
            var (pers, diurnal) = DoBaselines.Rmse(history, split, history.Count, h);
            _skill.Add(new DoSkill(h, Math.Round(Math.Sqrt(se / Math.Max(1, n)), 4), Math.Round(pers, 4), Math.Round(diurnal, 4),
                Math.Round(DoBaselines.Quantile(resid, 0.05), 4), Math.Round(DoBaselines.Quantile(resid, 0.95), 4)));
            var full = Fit(Rows(history, DoFeatures.Lookback, history.Count, h));
            _models.Add(full);
            _engines.Add(_ml.Model.CreatePredictionEngine<Row, Score>(full));
        }
        return this;
    }

    private ITransformer Fit(IEnumerable<Row> rows)
    {
        var data = _ml.Data.LoadFromEnumerable(rows);
        var trainer = _ml.Regression.Trainers.FastTree(labelColumnName: "Label", featureColumnName: "Features", numberOfLeaves: Leaves,
            numberOfTrees: Trees, minimumExampleCountPerLeaf: 10, learningRate: LearningRate);
        return trainer.Fit(data);
    }

    private static IEnumerable<Row> Rows(IReadOnlyList<DoObservation> h, int from, int to, int horizon)
    {
        for (int t = from; t + horizon < to; t++)
            yield return new Row { Features = DoFeatures.Build(h, t, horizon), Label = (float)h[t + horizon].Do };
    }

    /// <summary>Forecasts the next <see cref="Horizon"/> hours from the most recent ≥ 24 hours.</summary>
    public DoForecastCurve Forecast(IReadOnlyList<DoObservation> recent)
    {
        if (_engines.Count == 0) throw new InvalidOperationException("Train the forecaster first.");
        int t = recent.Count - 1;
        var times = new List<DateTime>(); var pt = new double[Horizon]; var lo = new double[Horizon]; var hi = new double[Horizon];
        for (int h = 1; h <= Horizon; h++)
        {
            double y = Math.Max(0, _engines[h - 1].Predict(new Row { Features = DoFeatures.Build(recent, t, h) }).Value);
            times.Add(recent[t].Time.AddHours(h));
            pt[h - 1] = Math.Round(y, 3);
            lo[h - 1] = Math.Round(Math.Max(0, y + _skill[h - 1].P05Residual), 3);
            hi[h - 1] = Math.Round(y + _skill[h - 1].P95Residual, 3);
        }
        return new DoForecastCurve(Name, times, pt, lo, hi);
    }

    /// <summary>Saves one ML.NET model per horizon plus the skill table into a folder.</summary>
    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var schema = _ml.Data.LoadFromEnumerable(Array.Empty<Row>()).Schema;
        for (int i = 0; i < _models.Count; i++) _ml.Model.Save(_models[i], schema, Path.Combine(folder, $"do-h{i + 1:00}.zip"));
        File.WriteAllLines(Path.Combine(folder, "skill.csv"), _skill.Select(s => string.Join(',', new[] { s.HorizonHours, s.Rmse, s.Persistence, s.Diurnal, s.P05Residual, s.P95Residual }
            .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).Prepend("h,rmse,persistence,diurnal,p05,p95"));
    }

    public static GbtDoForecaster Load(string folder)
    {
        var lines = File.ReadAllLines(Path.Combine(folder, "skill.csv")).Skip(1).ToList();
        var f = new GbtDoForecaster(lines.Count);
        foreach (var l in lines)
        {
            var v = l.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            f._skill.Add(new DoSkill((int)v[0], v[1], v[2], v[3], v[4], v[5]));
            var m = f._ml.Model.Load(Path.Combine(folder, $"do-h{(int)v[0]:00}.zip"), out _);
            f._models.Add(m);
            f._engines.Add(f._ml.Model.CreatePredictionEngine<Row, Score>(m));
        }
        return f;
    }
}

/// <summary>Daily water conditions of a pond, as needed by <see cref="FarmGrowthLearner"/> (from the store, a twin or manual logs).</summary>
public readonly record struct DailyWater(double TemperatureC, double DawnDo, double Nh3, double SalinityPpt = double.NaN);

/// <summary>
/// Learns growth from a farm's own culture cycles: every pair of consecutive samplings becomes a training row (daily growth in % body weight
/// against mean temperature, dawn DO, NH₃, density and the feed actually given relative to the feeding table). The farm rows are combined
/// with the synthetic AquaGrowth prior (down-weighted by repetition) so that a few cycles adjust rather than replace the model.
/// </summary>
public static class FarmGrowthLearner
{
    /// <summary>Rows from cycles; <paramref name="water"/> gives the conditions of a pond on a date (null when unknown — the interval is skipped).</summary>
    public static Dataset FromCycles(IEnumerable<CultureCycle> cycles, Func<string, DateOnly, DailyWater?> water, double volumeM3PerPond = 2600)
    {
        var d = new Dataset(PondState.FeatureNames);
        foreach (var c in cycles)
        {
            var s = c.Species;
            var samples = c.Samplings.OrderBy(x => x.Date).Prepend(new SamplingRecord(c.StockingDate, c.StockingWeightG, 0)).ToList();
            for (int i = 1; i < samples.Count; i++)
            {
                var a = samples[i - 1]; var b = samples[i];
                int days = b.Date.DayNumber - a.Date.DayNumber;
                if (days < 3 || b.AverageWeightG <= a.AverageWeightG * 0.98) continue;
                var w = Enumerable.Range(0, days).Select(k => water(c.PondId, a.Date.AddDays(k))).Where(x => x is not null).Select(x => x!.Value).ToList();
                if (w.Count < days / 2) continue;
                long deaths = c.Mortalities.Where(m => m.Date <= a.Date).Sum(m => m.Count);
                double pop = Math.Max(1, c.StockedCount - deaths);
                double biomassKgM3 = pop * a.AverageWeightG / 1000 / volumeM3PerPond;
                double fed = c.Feedings.Where(f => f.Date >= a.Date && f.Date < b.Date).Sum(f => f.Kg);
                double meanT = w.Average(x => x.TemperatureC);
                double table = Enumerable.Range(0, days).Sum(k => FeedingCalculator.Plan(s, a.AverageWeightG * Math.Pow(b.AverageWeightG / a.AverageWeightG, k / (double)days), pop, meanT).DailyKg);
                var st = new PondState(a.AverageWeightG, meanT, w.Average(x => x.DawnDo), w.Average(x => x.Nh3), biomassKgM3 / s.MaxBiomassKgM3,
                    table > 0 && fed > 0 ? Math.Clamp(fed / table, 0.2, 1.5) : 1, w.Select(x => x.SalinityPpt).DefaultIfEmpty(double.NaN).Average(), c.DayOfCulture(a.Date));
                double pctPerDay = (Math.Pow(b.AverageWeightG / a.AverageWeightG, 1.0 / days) - 1) * 100;
                d.Add(st.Features(s), pctPerDay);
            }
        }
        return d;
    }

    /// <summary>Trains a <see cref="GrowthPredictor"/> on the synthetic prior plus the farm rows repeated <paramref name="farmWeight"/> times.</summary>
    public static GrowthPredictor Fit(SpeciesProfile species, Dataset farm, int farmWeight = 5, int priorSamples = 1200, ulong seed = 17)
    {
        var all = GrowthPredictor.SyntheticDataset(species, priorSamples, seed);
        for (int r = 0; r < farmWeight; r++)
            for (int i = 0; i < farm.Count; i++) all.Add(farm.X[i], farm.Y[i]);
        var p = new GrowthPredictor(species, 10, seed);
        p.Train(all);
        return p;
    }

    /// <summary>RMSE (in % body weight per day) of a predictor on farm rows: use a held-out cycle to check that local learning helps.</summary>
    public static double Rmse(GrowthPredictor p, Dataset rows) =>
        rows.Count == 0 ? double.NaN : Math.Sqrt(Enumerable.Range(0, rows.Count).Average(i => Math.Pow(p.PredictRaw(rows.X[i]) - rows.Y[i], 2)));
}
