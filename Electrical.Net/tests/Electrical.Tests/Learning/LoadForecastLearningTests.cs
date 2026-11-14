using Electrical.AI.Learning;
using Electrical.Core.Data;
using Electrical.Forecasting;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class LoadForecastLearningTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));

    /// <summary>
    /// Residential profile with strong temperature dependence (3 % of peak per °C above 24 °C) and day-to-day weather swings: the
    /// Holt–Winters baseline cannot anticipate a hot day, the ML models see the (perfect) temperature forecast.
    /// </summary>
    internal static (TimeSeries Load, TimeSeries Temp) TemperatureProfile(int days = 49)
    {
        var temp = SyntheticProfiles.Temperature(Start, days, TimeSpan.FromHours(1), meanC: 28, dailyAmplitudeC: 4, noiseC: 1.0, seed: 21);
        var load = SyntheticProfiles.Load(LoadProfileKind.Residential, Start, days, TimeSpan.FromHours(1), peakMw: 10, noise: 0.01, seed: 22,
            temperature: temp, coolingPerDegree: 0.03);
        return (load, temp);
    }

    [Fact]
    public void FeatureBuilder_LagsCalendarAndTemperature()
    {
        var (load, temp) = TemperatureProfile(10);
        var fb = new LoadFeatureBuilder(new LoadFeatureOptions { Holidays = [new DateOnly(2026, 1, 13)] });
        var (x, y) = fb.Build(load, temp);
        Assert.Equal(load.Count - 168, x.Count);
        var names = fb.FeatureNames(true);
        Assert.Equal(names.Length, x[0].Length);
        int t = 200;
        var row = x[t - 168];
        Assert.Equal(load[t - 1], row[Array.IndexOf(names, "lag1")]);
        Assert.Equal(load[t - 24], row[Array.IndexOf(names, "lag24")]);
        Assert.Equal(load[t - 168], row[Array.IndexOf(names, "lag168")]);
        Assert.Equal(temp[t], row[Array.IndexOf(names, "temp")]);
        Assert.Equal(y[t - 168], load[t]);
        // 2026-01-13 is the holiday: sample 8 days × 24 h after the start.
        Assert.Equal(1.0, x[8 * 24 - 168][Array.IndexOf(names, "holiday")]);
        Assert.Equal(0.0, x[7 * 24 - 168][Array.IndexOf(names, "holiday")]);
    }

    [Fact]
    public void MlForecasters_BeatHoltWinters_OnTemperatureDependentLoad()
    {
        var (load, temp) = TemperatureProfile();
        var r = LoadForecastBenchmark.Run(load, temp, new LoadForecastBenchmarkOptions { TestDays = 7 });
        output.WriteLine(r.ToString());
        double hw = r.Mape["Holt-Winters"];
        Assert.True(r.Mape["GraviLearn gradient boosting"] < hw, r.ToString());
        Assert.True(r.Mape["ML.NET FastTree"] < hw, r.ToString());
        Assert.True(r.Mape["GraviLearn gradient boosting"] < 3, r.ToString());
        Assert.True(r.Mape["GraviLearn gradient boosting"] < r.Mape["Regression"], r.ToString());
    }

    [Fact]
    public void GradientBoosting_IsDeterministic_AndRoundTripsThroughJson()
    {
        var (load, temp) = TemperatureProfile(28);
        var opts = new MlLoadForecastOptions { Model = new RegressionTrainingOptions { Trees = 60 } };
        var a = MlLoadForecaster.Train(load, temp, opts);
        var b = MlLoadForecaster.Train(load, temp, opts);
        var (x, _) = a.Features.Build(load, temp);
        for (int i = 0; i < x.Count; i += 37) Assert.Equal(a.Model.Predict(x[i]), b.Model.Predict(x[i]));

        string path = Path.Combine(Path.GetTempPath(), $"eln-gb-{Environment.ProcessId}.json");
        try
        {
            a.Model.Save(path);
            var loaded = ModelTrainer.LoadRegressor(path);
            Assert.IsType<TreeEnsembleRegressor>(loaded);
            for (int i = 0; i < x.Count; i += 37) Assert.Equal(a.Model.Predict(x[i]), loaded.Predict(x[i]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MlNetFastTree_RoundTripsThroughZip()
    {
        var (load, temp) = TemperatureProfile(28);
        var f = MlLoadForecaster.Train(load, temp, new MlLoadForecastOptions
        {
            Model = new RegressionTrainingOptions { Algorithm = RegressionAlgorithm.MlNetFastTree, Trees = 60 },
        });
        var (x, _) = f.Features.Build(load, temp);
        string path = Path.Combine(Path.GetTempPath(), $"eln-ft-{Environment.ProcessId}.zip");
        try
        {
            f.Model.Save(path);
            var loaded = ModelTrainer.LoadRegressor(path);
            Assert.IsType<MlNetRegressor>(loaded);
            Assert.Equal(f.Model.FeatureCount, loaded.FeatureCount);
            var p1 = f.Model.Predict(x); var p2 = loaded.Predict(x);
            for (int i = 0; i < x.Count; i++) Assert.Equal(p1[i], p2[i], 6);
            // Forecast with the loaded model.
            var fc = MlLoadForecaster.FromModel(loaded, usesTemperature: true).Forecast(LoadForecastBenchmark_Slice(load, 21 * 24), 24, temp);
            Assert.Equal(24, fc.Count);
            Assert.All(fc.Values, v => Assert.InRange(v, 2, 20));
        }
        finally { File.Delete(path); }
    }

    private static TimeSeries LoadForecastBenchmark_Slice(TimeSeries s, int count) =>
        new(s.Start, s.Step, s.Values.AsSpan(0, count).ToArray(), s.Name, s.Unit);
}
