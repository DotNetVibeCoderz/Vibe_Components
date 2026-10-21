using System.Numerics;
using Gravicode.Science.GraviNum.Signal;

namespace Blazor.AI.Analytics;

public enum ForecastMethod
{
    HoltWinters,
    Holt,
    SeasonalNaive,
    LinearTrend,
}

public sealed record ForecastResult(ForecastMethod Method, double[] Fitted, double[] Forecast, double[] Lower, double[] Upper, double Mape, double Rmse);

public sealed record Decomposition(double[] Trend, double[] Seasonal, double[] Residual, int Period);

/// <summary>Forecasting, decomposition, autocorrelation, anomaly scoring and signal processing for 1-D series.</summary>
public static class TimeSeriesTools
{
    /// <summary>Forecasts <paramref name="horizon"/> steps with ~95% intervals from in-sample residuals.</summary>
    public static ForecastResult Forecast(IReadOnlyList<double> series, int horizon, ForecastMethod method, int period = 12, double alpha = 0.3, double beta = 0.05, double gamma = 0.2)
    {
        var y = series.ToArray();
        double[] fitted, forecast;
        switch (method)
        {
            case ForecastMethod.SeasonalNaive:
                fitted = y.Select((_, i) => i >= period ? y[i - period] : y[i]).ToArray();
                forecast = Enumerable.Range(0, horizon).Select(h => y[y.Length - period + h % period]).ToArray();
                break;
            case ForecastMethod.LinearTrend:
                var (slope, intercept) = Ols(y);
                fitted = y.Select((_, i) => intercept + slope * i).ToArray();
                forecast = Enumerable.Range(y.Length, horizon).Select(i => intercept + slope * i).ToArray();
                break;
            case ForecastMethod.Holt:
                (fitted, forecast) = Holt(y, horizon, alpha, beta);
                break;
            default:
                (fitted, forecast) = y.Length >= 2 * period ? HoltWinters(y, horizon, period, alpha, beta, gamma) : Holt(y, horizon, alpha, beta);
                break;
        }

        var start = method is ForecastMethod.SeasonalNaive or ForecastMethod.HoltWinters ? Math.Min(period, y.Length - 1) : 1;
        var residuals = Enumerable.Range(start, y.Length - start).Select(i => y[i] - fitted[i]).ToArray();
        var sigma = residuals.Length < 2 ? 0 : Math.Sqrt(residuals.Sum(r => r * r) / (residuals.Length - 1));
        var lower = forecast.Select((f, h) => f - 1.96 * sigma * Math.Sqrt(1 + h * 0.15)).ToArray();
        var upper = forecast.Select((f, h) => f + 1.96 * sigma * Math.Sqrt(1 + h * 0.15)).ToArray();
        var mape = MlTrainer.Mape(y[start..], fitted[start..]);
        var rmse = residuals.Length == 0 ? 0 : Math.Sqrt(residuals.Average(r => r * r));
        return new ForecastResult(method, fitted, forecast, lower, upper, mape, rmse);
    }

    /// <summary>Classical additive decomposition: centred moving-average trend, average seasonal profile, residual.</summary>
    public static Decomposition Decompose(IReadOnlyList<double> series, int period = 12)
    {
        var y = series.ToArray();
        var n = y.Length;
        var trend = new double[n];
        var half = period / 2;
        for (var i = 0; i < n; i++)
        {
            if (i < half || i >= n - half)
            {
                trend[i] = double.NaN;
                continue;
            }

            double sum = 0;
            if (period % 2 == 0)
            {
                sum = 0.5 * y[i - half] + 0.5 * y[i + half];
                for (var k = i - half + 1; k < i + half; k++) sum += y[k];
                trend[i] = sum / period;
            }
            else
            {
                for (var k = i - half; k <= i + half; k++) sum += y[k];
                trend[i] = sum / period;
            }
        }

        var seasonalProfile = new double[period];
        for (var s = 0; s < period; s++)
        {
            var values = Enumerable.Range(0, n).Where(i => i % period == s && !double.IsNaN(trend[i])).Select(i => y[i] - trend[i]).ToArray();
            seasonalProfile[s] = values.Length == 0 ? 0 : values.Average();
        }

        var adjust = seasonalProfile.Average();
        var seasonal = Enumerable.Range(0, n).Select(i => seasonalProfile[i % period] - adjust).ToArray();
        var residual = Enumerable.Range(0, n).Select(i => double.IsNaN(trend[i]) ? double.NaN : y[i] - trend[i] - seasonal[i]).ToArray();
        return new Decomposition(trend, seasonal, residual, period);
    }

    public static double[] Autocorrelation(IReadOnlyList<double> series, int maxLag = 24)
    {
        var y = series.ToArray();
        var mean = y.Average();
        var denominator = y.Sum(v => (v - mean) * (v - mean));
        return Enumerable.Range(0, Math.Min(maxLag, y.Length - 1) + 1)
            .Select(lag => denominator == 0 ? 0 : Enumerable.Range(0, y.Length - lag).Sum(i => (y[i] - mean) * (y[i + lag] - mean)) / denominator)
            .ToArray();
    }

    public static double[] MovingAverage(IReadOnlyList<double> series, int window) =>
        Enumerable.Range(0, series.Count).Select(i => i + 1 < window ? double.NaN : Enumerable.Range(i - window + 1, window).Average(k => series[k])).ToArray();

    /// <summary>Rolling z-score anomaly scores (robust: median/MAD of the trailing window).</summary>
    public static double[] RollingZScores(IReadOnlyList<double> series, int window = 30)
    {
        var scores = new double[series.Count];
        for (var i = 0; i < series.Count; i++)
        {
            var from = Math.Max(0, i - window);
            if (i - from < 5)
            {
                continue;
            }

            var history = Enumerable.Range(from, i - from).Select(k => series[k]).Order().ToArray();
            var median = history[history.Length / 2];
            var mad = history.Select(v => Math.Abs(v - median)).Order().ElementAt(history.Length / 2) * 1.4826;
            scores[i] = mad < 1e-12 ? 0 : (series[i] - median) / mad;
        }

        return scores;
    }

    public static (bool[] Flags, double Low, double High) IqrOutliers(IReadOnlyList<double> series, double k = 1.5)
    {
        var sorted = series.Order().ToArray();
        var q1 = Quantile(sorted, 0.25);
        var q3 = Quantile(sorted, 0.75);
        var iqr = q3 - q1;
        var low = q1 - k * iqr;
        var high = q3 + k * iqr;
        return (series.Select(v => v < low || v > high).ToArray(), low, high);
    }

    /// <summary>Single-sided amplitude spectrum via GraviNum's FFT.</summary>
    public static (double[] Frequencies, double[] Magnitudes) Spectrum(IReadOnlyList<double> signal, double sampleRate)
    {
        var magnitudes = Fft.Magnitude(signal).ToArray();
        var frequencies = Fft.FrequencyBins(signal.Count, sampleRate).ToArray();
        var half = Math.Min(magnitudes.Length, frequencies.Length) / 2;
        return (frequencies[..half], magnitudes[..half].Select(m => m * 2 / signal.Count).ToArray());
    }

    /// <summary>Ideal low/high/band-pass filter by zeroing FFT bins (fine for visual demos).</summary>
    public static double[] FftFilter(IReadOnlyList<double> signal, double sampleRate, double? lowCut, double? highCut)
    {
        var spectrum = Fft.ForwardReal(signal);
        var n = spectrum.Length;
        for (var k = 0; k < n; k++)
        {
            var frequency = Math.Min(k, n - k) * sampleRate / n;
            if ((lowCut is { } lo && frequency < lo) || (highCut is { } hi && frequency > hi))
            {
                spectrum[k] = Complex.Zero;
            }
        }

        return Fft.InverseReal(spectrum, signal.Count);
    }

    private static (double[] Fitted, double[] Forecast) Holt(double[] y, int horizon, double alpha, double beta)
    {
        var level = y[0];
        var trend = y.Length > 1 ? y[1] - y[0] : 0;
        var fitted = new double[y.Length];
        fitted[0] = y[0];
        for (var t = 1; t < y.Length; t++)
        {
            fitted[t] = level + trend;
            var previous = level;
            level = alpha * y[t] + (1 - alpha) * (level + trend);
            trend = beta * (level - previous) + (1 - beta) * trend;
        }

        return (fitted, Enumerable.Range(1, horizon).Select(h => level + h * trend).ToArray());
    }

    private static (double[] Fitted, double[] Forecast) HoltWinters(double[] y, int horizon, int m, double alpha, double beta, double gamma)
    {
        var level = y.Take(m).Average();
        var trend = (y.Skip(m).Take(m).Average() - level) / m;
        var seasonal = Enumerable.Range(0, m).Select(i => y[i] - level).ToArray();
        var fitted = new double[y.Length];
        for (var t = 0; t < y.Length; t++)
        {
            var s = seasonal[t % m];
            fitted[t] = level + trend + s;
            if (t < m)
            {
                continue;
            }

            var previous = level;
            level = alpha * (y[t] - s) + (1 - alpha) * (level + trend);
            trend = beta * (level - previous) + (1 - beta) * trend;
            seasonal[t % m] = gamma * (y[t] - level) + (1 - gamma) * s;
        }

        for (var t = 0; t < m; t++)
        {
            fitted[t] = y[t];
        }

        return (fitted, Enumerable.Range(1, horizon).Select(h => level + h * trend + seasonal[(y.Length + h - 1) % m]).ToArray());
    }

    private static (double Slope, double Intercept) Ols(double[] y)
    {
        var n = y.Length;
        double mx = (n - 1) / 2.0, my = y.Average(), sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++)
        {
            sxy += (i - mx) * (y[i] - my);
            sxx += (i - mx) * (i - mx);
        }

        var slope = sxx == 0 ? 0 : sxy / sxx;
        return (slope, my - slope * mx);
    }

    private static double Quantile(double[] sorted, double q)
    {
        var pos = (sorted.Length - 1) * q;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
