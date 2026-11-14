using Electrical.Core.Data;
using Electrical.Core.Hosting;
using Electrical.Forecasting;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class ForecastingTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)); // Monday, WIB

    [Fact]
    public void HoltWinters_PureSeasonal_TinyMape()
    {
        // y = 100 + 20 sin(2πt/24) + 5 cos(4πt/24): additive Holt–Winters reproduces a pure seasonal series almost exactly.
        var y = Enumerable.Range(0, 24 * 21).Select(t => 100 + 20 * Math.Sin(2 * Math.PI * t / 24) + 5 * Math.Cos(4 * Math.PI * t / 24)).ToArray();
        var hw = new HoltWinters(24) { Alpha = 0.2, Beta = 0.0, Gamma = 0.2 }.Fit(y.AsSpan(0, 24 * 19));
        var f = hw.Forecast(48);
        Assert.True(ForecastMetrics.Mape(y.AsSpan(24 * 19), f) < 0.01);
    }

    [Fact]
    public void HoltWinters_DoubleSeasonal_CommercialLoad()
    {
        var load = SyntheticProfiles.Load(LoadProfileKind.Commercial, Start, 35, TimeSpan.FromHours(1), 10);
        var hist = load.Values[..(24 * 28)];
        var hw = HoltWinters.FitOptimized(hist, 24, 168);
        var f = hw.Forecast(24 * 7);
        double mape = ForecastMetrics.Mape(load.Values.AsSpan(24 * 28), f);
        Assert.True(mape < 1.0, $"MAPE {mape:0.###} %");
    }

    [Fact]
    public void Regression_WithTemperature_LearnsCoolingSensitivity()
    {
        var temp = SyntheticProfiles.Temperature(Start, 35, TimeSpan.FromHours(1), meanC: 28, dailyAmplitudeC: 5, noiseC: 1.5);
        var load = SyntheticProfiles.Load(LoadProfileKind.Residential, Start, 35, TimeSpan.FromHours(1), 50, noise: 0.01, temperature: temp,
            coolingPerDegree: 0.03);
        int nTrain = 24 * 28;
        var train = new TimeSeries(Start, load.Step, load.Values[..nTrain]);
        var trainT = new TimeSeries(Start, load.Step, temp.Values[..nTrain]);
        var model = RegressionForecaster.Fit(train, trainT);
        var testT = new TimeSeries(train.End, load.Step, temp.Values[nTrain..]);
        var pred = model.Predict(train.End, load.Step, load.Count - nTrain, testT);
        double mape = ForecastMetrics.Mape(load.Values.AsSpan(nTrain), pred.Values);
        Assert.True(mape < 4, $"MAPE {mape:0.##} %");
        int cdd = Array.IndexOf(model.FeatureNames, "CDD");
        Assert.True(model.Coefficients[cdd] > 0.5, "cooling-degree coefficient should be ≈ 0.03·50 = 1.5 MW/°C");
    }

    [Fact]
    public void SimilarDay_PeriodicProfile_IsReproduced()
    {
        var load = SyntheticProfiles.Load(LoadProfileKind.Industrial, Start, 22, TimeSpan.FromHours(1), 20);
        var hist = new TimeSeries(Start, load.Step, load.Values[..(24 * 21)]);
        var f = SimilarDayForecaster.ForecastNextDay(hist, k: 3);
        Assert.Equal(24, f.Count);
        Assert.True(ForecastMetrics.Mape(load.Values.AsSpan(24 * 21), f.Values) < 0.5);
    }

    [Fact]
    public void Ensemble_And_Facade_Work()
    {
        var load = SyntheticProfiles.Load(LoadProfileKind.Residential, Start, 30, TimeSpan.FromHours(1), 5, noise: 0.03, seed: 7);
        var hist = new TimeSeries(Start, load.Step, load.Values[..(24 * 28)]);
        var actual = load.Values[(24 * 28)..(24 * 28 + 48)];
        var members = new[] { LoadForecastMethod.HoltWinters, LoadForecastMethod.Regression, LoadForecastMethod.SimilarDay }
            .Select(m => LoadForecast.Forecast(hist, 48, m).Values).ToArray();
        var ens = LoadForecast.Forecast(hist, 48, LoadForecastMethod.Ensemble).Values;
        double worst = members.Max(m => ForecastMetrics.Mae(actual, m));
        Assert.True(ForecastMetrics.Mae(actual, ens) <= worst + 1e-12);
        Assert.Equal(EnsembleForecaster.Combine(members), ens);
        Assert.True(ForecastMetrics.Mape(actual, ens) < 6);

        var sp = new ServiceCollection().AddElectrical(b => b.AddForecasting()).BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IForecastingService>());
    }

    [Fact]
    public void Metrics_KnownValues()
    {
        double[] a = [100, 200], p = [110, 190];
        var e = ForecastMetrics.Evaluate(a, p);
        Assert.Equal(7.5, e.Mape, 10);   // (10 % + 5 %)/2
        Assert.Equal(10, e.Mae, 10);
        Assert.Equal(10, e.Rmse, 10);
        Assert.Equal(100 * 10 / 150.0, e.NMae, 10);
        Assert.Equal(0, e.Bias, 10);
        Assert.Equal(5, ForecastMetrics.NMae(a, p, reference: 200), 10);
    }

    [Fact]
    public void Solar_ClearSky_And_PvForecast()
    {
        // Jakarta (−6.2°, 106.8°) near the March equinox at local solar noon: zenith ≈ |φ − δ| ≈ 6–8°,
        // Haurwitz GHI ≈ 1098·cos Z·exp(−0.057/cos Z) ≈ 1030 W/m².
        var noon = new DateTimeOffset(2024, 3, 20, 12, 0, 0, TimeSpan.FromHours(7)).AddMinutes(-7 - 8); // ≈ solar noon (lon 106.8 vs 105 + EoT)
        double z = SolarForecast.SolarZenithDeg(noon, -6.2, 106.8);
        Assert.InRange(z, 4, 9);
        Assert.InRange(SolarForecast.ClearSkyGhi(noon, -6.2, 106.8), 1015, 1040);
        Assert.Equal(0, SolarForecast.ClearSkyGhi(noon.AddHours(12), -6.2, 106.8));
        Assert.Equal(0.25, SolarForecast.CloudFactor(1), 12);

        var cc = SyntheticProfiles.CloudCover(Start, 2, TimeSpan.FromHours(1), 0.3);
        var pv = SolarForecast.Forecast(cc, -6.2, 106.8, capacityMw: 10);
        Assert.True(pv.Max() is > 5 and < 10);
        Assert.Equal(0, pv[0]); // midnight
    }

    [Fact]
    public void Wind_PowerCurve_And_Forecast()
    {
        var c = new WindPowerCurve(3, 12, 25, 2);
        Assert.Equal(0, c.Power(2.9));
        Assert.Equal(0, c.Power(3));
        Assert.Equal(2, c.Power(12));
        Assert.Equal(2, c.Power(20));
        Assert.Equal(0, c.Power(26));
        Assert.Equal(2 * (512 - 27) / (1728.0 - 27), c.Power(8), 12);
        var ws = SyntheticProfiles.WindSpeed(Start, 3, TimeSpan.FromHours(1), 8);
        var f = WindForecast.Forecast(ws, c, turbines: 10, losses: 0.1);
        Assert.True(f.Max() <= 18 + 1e-9);
        Assert.Equal(ws[^1], WindForecast.Persistence(ws, 6)[0]);
    }
}
