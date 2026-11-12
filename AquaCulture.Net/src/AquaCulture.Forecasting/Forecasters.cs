using AquaCulture.Core.Data;
using AquaCulture.Core.Hosting;

namespace AquaCulture.Forecasting;

/// <summary>Point forecast with an approximate 95 % interval.</summary>
public sealed record ForecastResult(string Model, double[] Point, double[] Lower, double[] Upper, double InSampleRmse);

/// <summary>Common interface for univariate forecasters.</summary>
public interface IForecaster
{
    string Name { get; }
    ForecastResult Forecast(ReadOnlySpan<double> history, int horizon);
}

/// <summary>Holt's linear trend (double exponential smoothing). Parameters are optimised by SSE when not given.</summary>
public sealed class HoltLinear(double? alpha = null, double? beta = null) : IForecaster
{
    public string Name => "Holt";

    public ForecastResult Forecast(ReadOnlySpan<double> history, int horizon)
    {
        var y = history.ToArray();
        if (y.Length < 3) throw new ArgumentException("Need at least 3 points.");
        (double a, double b) = (alpha ?? 0.5, beta ?? 0.1);
        if (alpha is null || beta is null)
        {
            var best = Numerics.NelderMead(p => Sse(y, Sig(p[0]), Sig(p[1])).Sse, [0, -2], [1, 1]);
            a = alpha ?? Sig(best.Parameters[0]); b = beta ?? Sig(best.Parameters[1]);
        }
        var (sse, level, trend) = Sse(y, a, b);
        double sd = Math.Sqrt(sse / (y.Length - 2));
        var pt = new double[horizon]; var lo = new double[horizon]; var hi = new double[horizon];
        for (int h = 1; h <= horizon; h++)
        {
            pt[h - 1] = level + h * trend;
            double w = 1.96 * sd * Math.Sqrt(1 + (h - 1) * a * a * (1 + h * b + h * (2 * h - 1) * b * b / 6));
            lo[h - 1] = pt[h - 1] - w; hi[h - 1] = pt[h - 1] + w;
        }
        return new ForecastResult(Name, pt, lo, hi, sd);
    }

    private static double Sig(double x) => 1 / (1 + Math.Exp(-x));

    private static (double Sse, double Level, double Trend) Sse(double[] y, double a, double b)
    {
        double l = y[0], t = y[1] - y[0], sse = 0;
        for (int i = 1; i < y.Length; i++)
        {
            double f = l + t, e = y[i] - f;
            sse += e * e;
            double nl = a * y[i] + (1 - a) * (l + t);
            t = b * (nl - l) + (1 - b) * t; l = nl;
        }
        return (sse, l, t);
    }
}

/// <summary>Additive Holt–Winters with season length m (e.g. 24 for hourly DO, 7 for daily feed).</summary>
public sealed class HoltWinters(int seasonLength, double? alpha = null, double? beta = null, double? gamma = null) : IForecaster
{
    public string Name => $"Holt-Winters(m={seasonLength})";

    public ForecastResult Forecast(ReadOnlySpan<double> history, int horizon)
    {
        var y = history.ToArray();
        int m = seasonLength;
        if (y.Length < 2 * m) throw new ArgumentException("Need at least two full seasons.");
        double[] p = [alpha ?? 0.3, beta ?? 0.05, gamma ?? 0.2];
        if (alpha is null || beta is null || gamma is null)
        {
            var best = Numerics.NelderMead(q => Run(y, m, Sig(q[0]), Sig(q[1]), Sig(q[2]), 0).Sse, [-0.8, -3, -1.4], [1, 1, 1]);
            p = [alpha ?? Sig(best.Parameters[0]), beta ?? Sig(best.Parameters[1]), gamma ?? Sig(best.Parameters[2])];
        }
        var r = Run(y, m, p[0], p[1], p[2], horizon);
        double sd = Math.Sqrt(r.Sse / Math.Max(1, y.Length - m));
        var lo = r.Forecast.Select((v, i) => v - 1.96 * sd * Math.Sqrt(1 + i * 0.05)).ToArray();
        var hi = r.Forecast.Select((v, i) => v + 1.96 * sd * Math.Sqrt(1 + i * 0.05)).ToArray();
        return new ForecastResult(Name, r.Forecast, lo, hi, sd);
    }

    private static double Sig(double x) => 1 / (1 + Math.Exp(-x));

    private static (double Sse, double[] Forecast) Run(double[] y, int m, double a, double b, double g, int horizon)
    {
        double l = y.Take(m).Average(), t = (y.Skip(m).Take(m).Average() - l) / m;
        var s = new double[m];
        for (int i = 0; i < m; i++) s[i] = y[i] - l;
        double sse = 0;
        for (int i = m; i < y.Length; i++)
        {
            double f = l + t + s[i % m], e = y[i] - f; sse += e * e;
            double nl = a * (y[i] - s[i % m]) + (1 - a) * (l + t);
            t = b * (nl - l) + (1 - b) * t;
            s[i % m] = g * (y[i] - nl) + (1 - g) * s[i % m];
            l = nl;
        }
        var fc = new double[horizon];
        for (int h = 1; h <= horizon; h++) fc[h - 1] = l + h * t + s[(y.Length + h - 1) % m];
        return (sse, fc);
    }
}

/// <summary>Autoregressive AR(p) fitted by Yule–Walker (Levinson–Durbin) on the demeaned series.</summary>
public sealed class Autoregressive(int order = 3) : IForecaster
{
    public string Name => $"AR({order})";
    public double[] Coefficients { get; private set; } = [];

    public ForecastResult Forecast(ReadOnlySpan<double> history, int horizon)
    {
        var y = history.ToArray();
        int p = Math.Min(order, y.Length / 3);
        double mean = y.Average();
        var x = y.Select(v => v - mean).ToArray();
        var r = new double[p + 1];
        for (int k = 0; k <= p; k++) { double s = 0; for (int i = k; i < x.Length; i++) s += x[i] * x[i - k]; r[k] = s / x.Length; }
        var phi = LevinsonDurbin(r, p, out double sigma2);
        Coefficients = phi;
        var ext = x.ToList();
        var pt = new double[horizon];
        for (int h = 0; h < horizon; h++)
        {
            double f = 0;
            for (int j = 0; j < p; j++) f += phi[j] * ext[^(j + 1)];
            ext.Add(f); pt[h] = f + mean;
        }
        // psi-weights for interval widths
        var psi = new double[horizon]; psi[0] = 1;
        for (int h = 1; h < horizon; h++) for (int j = 0; j < Math.Min(h, p); j++) psi[h] += phi[j] * psi[h - j - 1];
        double cum = 0;
        var lo = new double[horizon]; var hi = new double[horizon];
        for (int h = 0; h < horizon; h++) { cum += psi[h] * psi[h]; double w = 1.96 * Math.Sqrt(sigma2 * cum); lo[h] = pt[h] - w; hi[h] = pt[h] + w; }
        return new ForecastResult(Name, pt, lo, hi, Math.Sqrt(sigma2));
    }

    private static double[] LevinsonDurbin(double[] r, int p, out double sigma2)
    {
        var a = new double[p]; var tmp = new double[p];
        double e = r[0];
        for (int k = 0; k < p; k++)
        {
            double acc = r[k + 1];
            for (int j = 0; j < k; j++) acc -= a[j] * r[k - j];
            double kk = e == 0 ? 0 : acc / e;
            Array.Copy(a, tmp, p);
            a[k] = kk;
            for (int j = 0; j < k; j++) a[j] = tmp[j] - kk * tmp[k - 1 - j];
            e *= 1 - kk * kk;
        }
        sigma2 = Math.Max(e, 1e-12);
        return a;
    }
}

/// <summary>Selects the forecaster with the lowest hold-out RMSE.</summary>
public static class AutoForecaster
{
    public static ForecastResult Forecast(ReadOnlySpan<double> history, int horizon, int? seasonLength = null)
    {
        var y = history.ToArray();
        var models = new List<IForecaster> { new HoltLinear(), new Autoregressive(3) };
        if (seasonLength is { } m && y.Length >= 3 * m) models.Add(new HoltWinters(m));
        int hold = Math.Max(1, Math.Min(horizon, y.Length / 4));
        IForecaster best = models[0]; double bestErr = double.MaxValue;
        foreach (var f in models)
        {
            try
            {
                var fc = f.Forecast(y.AsSpan(0, y.Length - hold), hold);
                double err = Stats.Rmse(y.AsSpan(y.Length - hold), fc.Point);
                if (err < bestErr) { bestErr = err; best = f; }
            }
            catch (ArgumentException) { }
        }
        return best.Forecast(y, horizon);
    }
}

public static class ForecastingBuilderExtensions
{
    public static AquaBuilder AddForecasting(this AquaBuilder b) => b.TryAddModule("Forecasting", _ => { });
}
