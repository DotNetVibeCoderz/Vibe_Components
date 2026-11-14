using Electrical.Core.Data;
using Electrical.Core.Numerics;

namespace Electrical.Forecasting;

/// <summary>
/// Multiple linear regression load model with calendar and weather features, fitted by (ridge-regularized) normal equations
/// (XᵀX + λI)β = Xᵀy solved with <see cref="DenseMatrix{T}"/>. Features: intercept, 4 daily Fourier harmonics, weekend flag,
/// weekend × 2 harmonics, and (when temperature is given) T, cooling degrees max(0, T − 24 °C), heating degrees max(0, 18 °C − T).
/// </summary>
public sealed class RegressionForecaster
{
    private const int Harmonics = 4;

    private RegressionForecaster(double[] coefficients, bool useTemperature)
    {
        Coefficients = coefficients;
        UsesTemperature = useTemperature;
    }

    /// <summary>Fitted coefficients, aligned with <see cref="FeatureNames"/>.</summary>
    public double[] Coefficients { get; }
    public bool UsesTemperature { get; }

    public string[] FeatureNames => Names(UsesTemperature);

    private static string[] Names(bool temp)
    {
        var n = new List<string> { "intercept" };
        for (int k = 1; k <= Harmonics; k++) { n.Add($"sin{k}"); n.Add($"cos{k}"); }
        n.Add("weekend");
        for (int k = 1; k <= 2; k++) { n.Add($"weekend·sin{k}"); n.Add($"weekend·cos{k}"); }
        if (temp) { n.Add("T"); n.Add("CDD"); n.Add("HDD"); }
        return [.. n];
    }

    /// <summary>Feature vector for local time <paramref name="t"/> and optional temperature [°C].</summary>
    public static double[] Features(DateTimeOffset t, double? temperatureC)
    {
        var x = new List<double>(20) { 1 };
        double h = t.Hour + t.Minute / 60.0 + t.Second / 3600.0;
        for (int k = 1; k <= Harmonics; k++)
        {
            x.Add(Math.Sin(2 * Math.PI * k * h / 24));
            x.Add(Math.Cos(2 * Math.PI * k * h / 24));
        }
        double we = t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1 : 0;
        x.Add(we);
        for (int k = 1; k <= 2; k++)
        {
            x.Add(we * Math.Sin(2 * Math.PI * k * h / 24));
            x.Add(we * Math.Cos(2 * Math.PI * k * h / 24));
        }
        if (temperatureC is double tc)
        {
            x.Add(tc);
            x.Add(Math.Max(0, tc - 24));
            x.Add(Math.Max(0, 18 - tc));
        }
        return [.. x];
    }

    /// <summary>Fits the model to a load history [MW] and an aligned temperature series [°C] (optional).</summary>
    /// <param name="ridge">Tikhonov regularization added to the diagonal (except the intercept).</param>
    public static RegressionForecaster Fit(TimeSeries load, TimeSeries? temperature = null, double ridge = 1e-6)
    {
        bool useT = temperature is not null;
        int p = Names(useT).Length;
        var xtx = new DenseMatrix<double>(p, p);
        var xty = new double[p];
        for (int i = 0; i < load.Count; i++)
        {
            var x = Features(load.TimeAt(i), useT ? temperature![i] : null);
            for (int a = 0; a < p; a++)
            {
                xty[a] += x[a] * load[i];
                for (int b = 0; b < p; b++) xtx[a, b] += x[a] * x[b];
            }
        }
        for (int a = 1; a < p; a++) xtx[a, a] += ridge * load.Count;
        return new RegressionForecaster(xtx.Solve(xty), useT);
    }

    /// <summary>Prediction [MW] at one instant.</summary>
    public double Predict(DateTimeOffset t, double? temperatureC = null)
    {
        if (UsesTemperature && temperatureC is null) throw new ArgumentException("This model needs a temperature.", nameof(temperatureC));
        var x = Features(t, UsesTemperature ? temperatureC : null);
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += x[i] * Coefficients[i];
        return s;
    }

    /// <summary>Forecast series of <paramref name="count"/> samples (temperature forecast must cover them when the model uses it).</summary>
    public TimeSeries Predict(DateTimeOffset start, TimeSpan step, int count, TimeSeries? temperature = null)
    {
        var v = new double[count];
        for (int i = 0; i < count; i++) v[i] = Predict(start + step * i, temperature is null ? null : temperature[i]);
        return new TimeSeries(start, step, v, "Regression forecast", "MW");
    }
}

/// <summary>
/// k-nearest-neighbour similar-day forecaster: the day-ahead profile is the inverse-distance weighted mean of the days that
/// followed the k historical days most similar to the latest one (same weekday/weekend type as the target, distance on the
/// normalized previous-day profile plus, optionally, the daily mean temperature difference).
/// </summary>
public static class SimilarDayForecaster
{
    /// <summary>
    /// Forecasts the day following <paramref name="history"/> (which must consist of whole days).
    /// </summary>
    /// <param name="temperature">Optional daily-aligned temperature [°C] covering history plus the target day.</param>
    /// <param name="temperatureWeight">Weight of 1 °C mean-temperature difference relative to the normalized profile distance.</param>
    public static TimeSeries ForecastNextDay(TimeSeries history, int k = 5, TimeSeries? temperature = null, double temperatureWeight = 0.05)
    {
        int spd = (int)Math.Round(TimeSpan.FromDays(1) / history.Step);
        int days = history.Count / spd;
        if (days < 3) throw new ArgumentException("Need at least 3 days of history.", nameof(history));
        var target = history.Start + TimeSpan.FromDays(days);
        bool targetWeekend = IsWeekend(target);
        double scale = Math.Max(1e-9, history.Max());
        var last = history.Values.AsSpan((days - 1) * spd, spd);
        double targetTemp = temperature is not null && temperature.Count >= (days + 1) * spd ? DayMean(temperature.Values, days, spd) : double.NaN;

        var cand = new List<(double Dist, int Day)>();
        for (int d = 1; d < days; d++)
        {
            if (IsWeekend(history.Start + TimeSpan.FromDays(d)) != targetWeekend) continue;
            var prev = history.Values.AsSpan((d - 1) * spd, spd);
            double s = 0;
            for (int j = 0; j < spd; j++) { double e = (prev[j] - last[j]) / scale; s += e * e; }
            double dist = Math.Sqrt(s / spd);
            if (!double.IsNaN(targetTemp)) dist += temperatureWeight * Math.Abs(DayMean(temperature!.Values, d, spd) - targetTemp);
            cand.Add((dist, d));
        }
        if (cand.Count == 0) for (int d = 1; d < days; d++) cand.Add((1, d));
        var best = cand.OrderBy(c => c.Dist).ThenByDescending(c => c.Day).Take(Math.Max(1, k)).ToList();
        var f = new double[spd];
        double wsum = 0;
        foreach (var (dist, d) in best)
        {
            double w = 1 / (dist + 1e-3);
            wsum += w;
            for (int j = 0; j < spd; j++) f[j] += w * history[d * spd + j];
        }
        for (int j = 0; j < spd; j++) f[j] /= wsum;
        return new TimeSeries(target, history.Step, f, "Similar-day forecast", history.Unit);
    }

    private static bool IsWeekend(DateTimeOffset t) => t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static double DayMean(double[] v, int day, int spd)
    {
        double s = 0;
        for (int j = 0; j < spd; j++) s += v[day * spd + j];
        return s / spd;
    }
}

/// <summary>Combination of several forecasts (simple or error-weighted averaging).</summary>
public static class EnsembleForecaster
{
    /// <summary>Weighted mean of aligned forecasts; equal weights when <paramref name="weights"/> is null.</summary>
    public static double[] Combine(IReadOnlyList<double[]> forecasts, IReadOnlyList<double>? weights = null)
    {
        if (forecasts.Count == 0) throw new ArgumentException("No forecasts.", nameof(forecasts));
        int n = forecasts[0].Length;
        var w = weights?.ToArray() ?? Enumerable.Repeat(1.0, forecasts.Count).ToArray();
        double ws = w.Sum();
        var r = new double[n];
        for (int k = 0; k < forecasts.Count; k++)
        {
            if (forecasts[k].Length != n) throw new ArgumentException("Forecasts must have equal length.", nameof(forecasts));
            for (int i = 0; i < n; i++) r[i] += w[k] / ws * forecasts[k][i];
        }
        return r;
    }

    /// <summary>Weights proportional to 1/error (e.g. validation MAE or RMSE of each member).</summary>
    public static double[] InverseErrorWeights(IReadOnlyList<double> errors) =>
        errors.Select(e => 1 / Math.Max(e, 1e-12)).ToArray();
}

/// <summary>Forecast accuracy summary.</summary>
/// <param name="Mape">Mean absolute percentage error [%].</param>
/// <param name="Rmse">Root-mean-square error [unit of data].</param>
/// <param name="Mae">Mean absolute error [unit of data].</param>
/// <param name="NMae">MAE normalized by the reference (capacity, or mean of actuals) [%].</param>
/// <param name="Bias">Mean error (predicted − actual) [unit of data].</param>
public sealed record ForecastError(double Mape, double Rmse, double Mae, double NMae, double Bias);

/// <summary>Forecast error metrics (MAPE and RMSE from <see cref="Stats"/>).</summary>
public static class ForecastMetrics
{
    public static double Mape(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted) => Stats.Mape(actual, predicted);
    public static double Rmse(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted) => Stats.RmsError(actual, predicted);

    public static double Mae(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted)
    {
        if (actual.Length != predicted.Length) throw new ArgumentException("Length mismatch.");
        double s = 0;
        for (int i = 0; i < actual.Length; i++) s += Math.Abs(actual[i] - predicted[i]);
        return actual.Length == 0 ? 0 : s / actual.Length;
    }

    /// <summary>MAE as % of <paramref name="reference"/> (installed capacity for PV/wind; mean actual when omitted).</summary>
    public static double NMae(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted, double? reference = null)
    {
        double r = reference ?? Stats.Mean(actual);
        return r == 0 ? 0 : 100 * Mae(actual, predicted) / r;
    }

    public static ForecastError Evaluate(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted, double? reference = null)
    {
        double bias = 0;
        for (int i = 0; i < actual.Length; i++) bias += predicted[i] - actual[i];
        return new ForecastError(Mape(actual, predicted), Rmse(actual, predicted), Mae(actual, predicted), NMae(actual, predicted, reference),
            actual.Length == 0 ? 0 : bias / actual.Length);
    }
}
