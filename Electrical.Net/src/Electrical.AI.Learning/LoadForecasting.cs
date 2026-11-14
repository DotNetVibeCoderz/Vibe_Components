using System.Globalization;
using Electrical.Core.Data;
using Electrical.Forecasting;

namespace Electrical.AI.Learning;

/// <summary>Feature set for <see cref="LoadFeatureBuilder"/>.</summary>
public sealed record LoadFeatureOptions
{
    /// <summary>Lags in samples (default 1, 24 and 168 for hourly data: previous hour, same hour yesterday, same hour last week).</summary>
    public IReadOnlyList<int> Lags { get; init; } = [1, 24, 168];
    /// <summary>Hour-of-day and day-of-week Fourier terms, weekend flag.</summary>
    public bool Calendar { get; init; } = true;
    /// <summary>Holiday dates (local calendar); the flag is 1 on these days.</summary>
    public IReadOnlyCollection<DateOnly> Holidays { get; init; } = [];
    /// <summary>Balance temperature for cooling degrees [°C].</summary>
    public double CoolingBalanceC { get; init; } = 24;
    /// <summary>Balance temperature for heating degrees [°C].</summary>
    public double HeatingBalanceC { get; init; } = 18;
}

/// <summary>
/// Feature engineering for hourly (or any regular step) load: lagged loads, calendar (sin/cos hour of day and day of week, weekend,
/// holiday flag) and weather (temperature, cooling/heating degrees, temperature 24 samples earlier). The target is the load [MW] at t.
/// </summary>
public sealed class LoadFeatureBuilder(LoadFeatureOptions? options = null)
{
    public LoadFeatureOptions Options { get; } = options ?? new LoadFeatureOptions();

    /// <summary>Largest lag, i.e. the number of leading samples that cannot form a feature row.</summary>
    public int MaxLag => Options.Lags.Count == 0 ? 0 : Options.Lags.Max();

    /// <summary>Feature names for a model with/without temperature.</summary>
    public string[] FeatureNames(bool temperature)
    {
        var n = new List<string>();
        foreach (var l in Options.Lags) n.Add($"lag{l}");
        if (Options.Calendar) n.AddRange(["hour_sin", "hour_cos", "hour2_sin", "hour2_cos", "dow_sin", "dow_cos", "weekend", "holiday", "hour"]);
        if (temperature) n.AddRange(["temp", "cdd", "hdd", "temp_lag24"]);
        return [.. n];
    }

    /// <summary>
    /// Feature row for sample <paramref name="t"/> of <paramref name="values"/> (lags read <c>values[t − lag]</c>, which may be
    /// predictions during recursive forecasting). <paramref name="temperature"/> is aligned with <paramref name="values"/>.
    /// </summary>
    public double[] Row(IReadOnlyList<double> values, int t, DateTimeOffset time, IReadOnlyList<double>? temperature)
    {
        var x = new List<double>(24);
        foreach (var l in Options.Lags) x.Add(values[t - l]);
        if (Options.Calendar)
        {
            double h = time.Hour + time.Minute / 60.0;
            double d = (int)time.DayOfWeek + h / 24;
            x.Add(Math.Sin(2 * Math.PI * h / 24)); x.Add(Math.Cos(2 * Math.PI * h / 24));
            x.Add(Math.Sin(4 * Math.PI * h / 24)); x.Add(Math.Cos(4 * Math.PI * h / 24));
            x.Add(Math.Sin(2 * Math.PI * d / 7)); x.Add(Math.Cos(2 * Math.PI * d / 7));
            x.Add(time.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1 : 0);
            x.Add(Options.Holidays.Contains(DateOnly.FromDateTime(time.DateTime)) ? 1 : 0);
            x.Add(h);
        }
        if (temperature is not null)
        {
            double tc = temperature[t];
            x.Add(tc);
            x.Add(Math.Max(0, tc - Options.CoolingBalanceC));
            x.Add(Math.Max(0, Options.HeatingBalanceC - tc));
            x.Add(t >= 24 ? temperature[t - 24] : tc);
        }
        return [.. x];
    }

    /// <summary>Builds the supervised dataset (rows from <see cref="MaxLag"/> to the end, or the given range).</summary>
    public (List<double[]> X, List<double> Y) Build(TimeSeries load, TimeSeries? temperature = null, int from = -1, int to = -1)
    {
        if (temperature is not null && temperature.Count < load.Count) throw new ArgumentException("Temperature must cover the load series.", nameof(temperature));
        int a = Math.Max(MaxLag, from < 0 ? 0 : from), b = to < 0 ? load.Count : Math.Min(to, load.Count);
        var x = new List<double[]>(Math.Max(0, b - a));
        var y = new List<double>(Math.Max(0, b - a));
        for (int t = a; t < b; t++)
        {
            x.Add(Row(load.Values, t, load.TimeAt(t), temperature?.Values));
            y.Add(load[t]);
        }
        return (x, y);
    }
}

/// <summary>Options of <see cref="MlLoadForecaster.Train"/>.</summary>
public sealed record MlLoadForecastOptions
{
    public LoadFeatureOptions Features { get; init; } = new();
    public RegressionTrainingOptions Model { get; init; } = new();
}

/// <summary>
/// Machine-learning load forecaster: a regression model (GraviLearn gradient boosting or ML.NET FastTree) over
/// <see cref="LoadFeatureBuilder"/> features. Multi-step forecasts are recursive: the one-step lag uses earlier predictions inside
/// the horizon; longer lags (24, 168) are actual history as long as the horizon does not exceed them.
/// </summary>
public sealed class MlLoadForecaster
{
    private MlLoadForecaster(IRegressionModel model, LoadFeatureBuilder features, bool usesTemperature)
    {
        Model = model; Features = features; UsesTemperature = usesTemperature;
    }

    public IRegressionModel Model { get; }
    public LoadFeatureBuilder Features { get; }
    public bool UsesTemperature { get; }

    /// <summary>Fits on all feature rows of <paramref name="load"/> [MW] (and aligned temperature [°C], optional).</summary>
    public static MlLoadForecaster Train(TimeSeries load, TimeSeries? temperature = null, MlLoadForecastOptions? options = null)
    {
        var o = options ?? new MlLoadForecastOptions();
        var fb = new LoadFeatureBuilder(o.Features);
        var (x, y) = fb.Build(load, temperature);
        if (x.Count < 10) throw new ArgumentException($"Need more than {fb.MaxLag + 10} samples to train.", nameof(load));
        var model = ModelTrainer.TrainRegressor(x, y, o.Model with { FeatureNames = fb.FeatureNames(temperature is not null) });
        return new MlLoadForecaster(model, fb, temperature is not null);
    }

    /// <summary>Wraps an already trained (e.g. loaded) model.</summary>
    public static MlLoadForecaster FromModel(IRegressionModel model, LoadFeatureOptions? features = null, bool usesTemperature = false) =>
        new(model, new LoadFeatureBuilder(features), usesTemperature);

    /// <summary>
    /// Forecasts <paramref name="horizon"/> samples after <paramref name="history"/>. When the model uses temperature,
    /// <paramref name="temperature"/> must be aligned with the history start and cover history + horizon (a temperature forecast).
    /// </summary>
    public TimeSeries Forecast(TimeSeries history, int horizon, TimeSeries? temperature = null)
    {
        if (UsesTemperature && (temperature is null || temperature.Count < history.Count + horizon))
            throw new ArgumentException("This model needs a temperature series covering history + horizon.", nameof(temperature));
        if (history.Count < Features.MaxLag) throw new ArgumentException($"History must hold at least {Features.MaxLag} samples.", nameof(history));
        var v = new List<double>(history.Values);
        var temp = UsesTemperature ? temperature!.Values : null;
        var f = new double[horizon];
        for (int k = 0; k < horizon; k++)
        {
            int t = history.Count + k;
            double p = Model.Predict(Features.Row(v, t, history.Start + history.Step * t, temp));
            f[k] = p;
            v.Add(p);
        }
        return new TimeSeries(history.End, history.Step, f, $"{Model.Kind} forecast", history.Unit ?? "MW");
    }
}

/// <summary>Result of <see cref="LoadForecastBenchmark.Run"/>.</summary>
/// <param name="Mape">MAPE [%] per method over the test days (day-ahead, rolling origin).</param>
public sealed record LoadForecastBenchmarkResult(IReadOnlyDictionary<string, double> Mape, int TrainSamples, int TestDays, double[] Actual,
    IReadOnlyDictionary<string, double[]> Forecasts)
{
    public override string ToString() => string.Join(", ", Mape.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value:0.00} %")));
}

/// <summary>Options of <see cref="LoadForecastBenchmark.Run"/>.</summary>
public sealed record LoadForecastBenchmarkOptions
{
    /// <summary>Samples per day (24 for hourly data).</summary>
    public int SamplesPerDay { get; init; } = 24;
    /// <summary>Days at the end of the series used as the test set (forecast one day ahead each, with the origin rolled forward).</summary>
    public int TestDays { get; init; } = 7;
    /// <summary>ML models to compare (the Holt–Winters baseline is always included).</summary>
    public IReadOnlyList<RegressionAlgorithm> Algorithms { get; init; } = [RegressionAlgorithm.GradientBoosting, RegressionAlgorithm.MlNetFastTree];
    public RegressionTrainingOptions Model { get; init; } = new();
    public LoadFeatureOptions Features { get; init; } = new();
}

/// <summary>
/// Time-ordered train/test comparison of ML forecasters against the double-seasonal Holt–Winters baseline (<see cref="HoltWinters.FitOptimized"/>,
/// daily + weekly seasons) and, when temperature is given, the linear calendar/weather <see cref="RegressionForecaster"/>. Everything before the test window trains the ML models once; each test day is then forecast 24 h ahead from
/// the actual history up to its midnight (Holt–Winters refitted at each origin). Temperature, when given, is used as a perfect forecast.
/// </summary>
public static class LoadForecastBenchmark
{
    public static LoadForecastBenchmarkResult Run(TimeSeries load, TimeSeries? temperature = null, LoadForecastBenchmarkOptions? options = null)
    {
        var o = options ?? new LoadForecastBenchmarkOptions();
        int spd = o.SamplesPerDay, split = load.Count - o.TestDays * spd;
        if (split <= 2 * 7 * spd) throw new ArgumentException("Need at least two weeks of training data before the test window.", nameof(load));
        var train = Slice(load, 0, split);
        var tempTrain = temperature is null ? null : Slice(temperature, 0, split);
        var actual = load.Values.AsSpan(split).ToArray();
        var forecasts = new Dictionary<string, double[]>();
        var mape = new Dictionary<string, double>();

        var hw = new double[actual.Length];
        for (int d = 0; d < o.TestDays; d++)
        {
            var model = HoltWinters.FitOptimized(load.Values.AsSpan(0, split + d * spd), spd, 7 * spd);
            Array.Copy(model.Forecast(spd), 0, hw, d * spd, spd);
        }
        forecasts["Holt-Winters"] = hw;

        if (temperature is not null)
        {
            // Second baseline: the existing calendar + weather linear regression (Electrical.Forecasting.RegressionForecaster).
            var reg = RegressionForecaster.Fit(train, tempTrain);
            var tf = Slice(temperature, split, actual.Length);
            forecasts["Regression"] = reg.Predict(load.TimeAt(split), load.Step, actual.Length, tf).Values;
        }

        foreach (var alg in o.Algorithms)
        {
            var f = MlLoadForecaster.Train(train, tempTrain, new MlLoadForecastOptions { Features = o.Features, Model = o.Model with { Algorithm = alg } });
            var pred = new double[actual.Length];
            for (int d = 0; d < o.TestDays; d++)
            {
                var hist = Slice(load, 0, split + d * spd);
                var day = f.Forecast(hist, spd, temperature);
                Array.Copy(day.Values, 0, pred, d * spd, spd);
            }
            forecasts[f.Model.Kind] = pred;
        }
        foreach (var (k, v) in forecasts) mape[k] = ForecastMetrics.Mape(actual, v);
        return new LoadForecastBenchmarkResult(mape, split, o.TestDays, actual, forecasts);
    }

    internal static TimeSeries Slice(TimeSeries s, int from, int count) =>
        new(s.TimeAt(from), s.Step, s.Values.AsSpan(from, count).ToArray(), s.Name, s.Unit);
}
