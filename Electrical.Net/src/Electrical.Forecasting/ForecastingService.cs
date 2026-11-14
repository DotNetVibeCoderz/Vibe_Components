using Electrical.Core.Data;
using Electrical.Core.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Forecasting;

/// <summary>Load forecasting method selector for <see cref="IForecastingService"/>.</summary>
public enum LoadForecastMethod { HoltWinters, Regression, SimilarDay, Ensemble }

/// <summary>DI facade over the forecasting algorithms.</summary>
public interface IForecastingService
{
    /// <summary>Forecasts the next <paramref name="horizon"/> samples of a load history [MW].</summary>
    TimeSeries ForecastLoad(TimeSeries history, int horizon, LoadForecastMethod method = LoadForecastMethod.HoltWinters,
        TimeSeries? temperature = null);

    ForecastError Evaluate(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted, double? reference = null);
}

internal sealed class ForecastingService : IForecastingService
{
    public TimeSeries ForecastLoad(TimeSeries history, int horizon, LoadForecastMethod method = LoadForecastMethod.HoltWinters,
        TimeSeries? temperature = null) => LoadForecast.Forecast(history, horizon, method, temperature);

    public ForecastError Evaluate(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted, double? reference = null) =>
        ForecastMetrics.Evaluate(actual, predicted, reference);
}

/// <summary>One-call load forecasting front end used by the service, CLI and gallery.</summary>
public static class LoadForecast
{
    /// <summary>
    /// Forecasts <paramref name="horizon"/> samples after <paramref name="history"/> [MW]. For <see cref="LoadForecastMethod.Regression"/>
    /// the optional temperature [°C] must cover history + horizon. Similar-day forecasts are repeated day by day.
    /// </summary>
    public static TimeSeries Forecast(TimeSeries history, int horizon, LoadForecastMethod method, TimeSeries? temperature = null)
    {
        int spd = (int)Math.Round(TimeSpan.FromDays(1) / history.Step);
        switch (method)
        {
            case LoadForecastMethod.HoltWinters:
            {
                int weekly = history.Count >= 14 * spd ? 7 * spd : 0;
                var hw = HoltWinters.FitOptimized(history.Values, spd, weekly);
                return new TimeSeries(history.End, history.Step, hw.Forecast(horizon), "Holt-Winters forecast", history.Unit);
            }
            case LoadForecastMethod.Regression:
            {
                TimeSeries? histT = null, futT = null;
                if (temperature is not null && temperature.Count >= history.Count + horizon)
                {
                    histT = new TimeSeries(temperature.Start, temperature.Step, temperature.Values[..history.Count]);
                    futT = new TimeSeries(history.End, temperature.Step, temperature.Values[history.Count..(history.Count + horizon)]);
                }
                return RegressionForecaster.Fit(history, histT).Predict(history.End, history.Step, horizon, futT);
            }
            case LoadForecastMethod.SimilarDay:
            {
                var values = new List<double>(history.Values);
                var result = new double[horizon];
                int produced = 0;
                while (produced < horizon)
                {
                    int whole = values.Count / spd * spd;
                    var h = new TimeSeries(history.Start, history.Step, values.Take(whole).ToArray());
                    var day = SimilarDayForecaster.ForecastNextDay(h, 5, temperature);
                    for (int j = 0; j < spd && produced < horizon; j++) result[produced++] = day[j];
                    values.RemoveRange(whole, values.Count - whole);
                    values.AddRange(day.Values);
                }
                return new TimeSeries(history.End, history.Step, result, "Similar-day forecast", history.Unit);
            }
            default:
            {
                var a = Forecast(history, horizon, LoadForecastMethod.HoltWinters, temperature).Values;
                var b = Forecast(history, horizon, LoadForecastMethod.Regression, temperature).Values;
                var c = Forecast(history, horizon, LoadForecastMethod.SimilarDay, temperature).Values;
                return new TimeSeries(history.End, history.Step, EnsembleForecaster.Combine([a, b, c]), "Ensemble forecast", history.Unit);
            }
        }
    }
}

public static class ForecastingBuilderExtensions
{
    /// <summary>Registers <see cref="IForecastingService"/>.</summary>
    public static ElectricalBuilder AddForecasting(this ElectricalBuilder b)
    {
        if (b.TryAddModule("Forecasting")) b.Services.TryAddSingleton<IForecastingService, ForecastingService>();
        return b;
    }
}
