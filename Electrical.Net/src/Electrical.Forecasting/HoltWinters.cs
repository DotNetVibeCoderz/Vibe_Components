using Electrical.Core.Data;

namespace Electrical.Forecasting;

/// <summary>
/// Additive Holt–Winters exponential smoothing with one or two seasonal cycles (Taylor 2003 double-seasonal form,
/// e.g. daily = 24 and weekly = 168 for hourly load):
/// <code>
/// l_t = α(y_t − d_{t−s1} − w_{t−s2}) + (1−α)(l_{t−1} + b_{t−1})
/// b_t = β(l_t − l_{t−1}) + (1−β) b_{t−1}
/// d_t = γ(y_t − l_t − w_{t−s2}) + (1−γ) d_{t−s1}
/// w_t = ω(y_t − l_t − d_{t−s1}) + (1−ω) w_{t−s2}
/// ŷ_{t+h} = l_t + h·b_t + d_{t−s1+h} + w_{t−s2+h}
/// </code>
/// Values are unit-agnostic (MW in, MW out).
/// </summary>
public sealed class HoltWinters
{
    private double _level, _trend;
    private double[] _s1 = [], _s2 = [];
    private int _t; // number of observations consumed
    private bool _fitted;

    /// <param name="seasonLength">First seasonal period in samples (e.g. 24 for hourly data with daily seasonality).</param>
    /// <param name="secondSeasonLength">Optional second period (e.g. 168 for weekly); 0 disables it. Must be a multiple of the first.</param>
    public HoltWinters(int seasonLength, int secondSeasonLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seasonLength, 1);
        if (secondSeasonLength != 0 && (secondSeasonLength <= seasonLength || secondSeasonLength % seasonLength != 0))
            throw new ArgumentException("Second season must be a multiple of the first.", nameof(secondSeasonLength));
        SeasonLength = seasonLength;
        SecondSeasonLength = secondSeasonLength;
    }

    public int SeasonLength { get; }
    public int SecondSeasonLength { get; }
    /// <summary>Level smoothing α ∈ (0, 1).</summary>
    public double Alpha { get; init; } = 0.2;
    /// <summary>Trend smoothing β ∈ [0, 1).</summary>
    public double Beta { get; init; } = 0.01;
    /// <summary>First-season smoothing γ ∈ [0, 1).</summary>
    public double Gamma { get; init; } = 0.2;
    /// <summary>Second-season smoothing ω ∈ [0, 1).</summary>
    public double Omega { get; init; } = 0.2;

    /// <summary>Sum of squared one-step-ahead errors of the last <see cref="Fit"/> (after the initialization window).</summary>
    public double Sse { get; private set; }

    /// <summary>One-step-ahead in-sample predictions of the last <see cref="Fit"/> (NaN inside the initialization window).</summary>
    public double[] Fitted { get; private set; } = [];

    /// <summary>Initializes from the first full cycle(s) and runs the recursions over <paramref name="y"/>.</summary>
    public HoltWinters Fit(ReadOnlySpan<double> y)
    {
        int s1 = SeasonLength, s2 = SecondSeasonLength, sMax = Math.Max(s1, s2);
        if (y.Length < 2 * sMax) throw new ArgumentException($"Need at least two full cycles ({2 * sMax} samples).", nameof(y));
        int cycles = y.Length / sMax;
        double m1 = Mean(y[..sMax]), m2 = Mean(y.Slice(sMax, sMax));
        _level = m1;
        _trend = (m2 - m1) / sMax;
        // first season: average deviation of each phase from its cycle mean over the available full cycles of length s1
        _s1 = new double[s1];
        int c1 = Math.Min(y.Length / s1, Math.Max(2, cycles * (sMax / s1)));
        for (int c = 0; c < c1; c++)
        {
            double cm = Mean(y.Slice(c * s1, s1));
            for (int j = 0; j < s1; j++) _s1[j] += (y[c * s1 + j] - cm) / c1;
        }
        _s2 = new double[Math.Max(s2, 1)];
        if (s2 > 0)
        {
            int c2 = Math.Min(cycles, 3);
            for (int c = 0; c < c2; c++)
            {
                double cm = Mean(y.Slice(c * s2, s2));
                for (int j = 0; j < s2; j++) _s2[j] += (y[c * s2 + j] - cm - _s1[j % s1]) / c2;
            }
        }
        // level at the start of the series: shift by trend so that recursions begin at t = 0
        _level -= _trend * ((sMax - 1) / 2.0 + 1);
        Fitted = new double[y.Length];
        Sse = 0;
        _t = 0;
        for (int t = 0; t < y.Length; t++)
        {
            double pred = _level + _trend + _s1[t % s1] + (s2 > 0 ? _s2[t % s2] : 0);
            Fitted[t] = t < sMax ? double.NaN : pred;
            if (t >= sMax) Sse += (y[t] - pred) * (y[t] - pred);
            Update(y[t], t);
        }
        _t = y.Length;
        _fitted = true;
        return this;
    }

    private void Update(double yt, int t)
    {
        int s1 = SeasonLength, s2 = SecondSeasonLength;
        double d = _s1[t % s1], w = s2 > 0 ? _s2[t % s2] : 0;
        double prevLevel = _level;
        _level = Alpha * (yt - d - w) + (1 - Alpha) * (_level + _trend);
        _trend = Beta * (_level - prevLevel) + (1 - Beta) * _trend;
        _s1[t % s1] = Gamma * (yt - _level - w) + (1 - Gamma) * d;
        if (s2 > 0) _s2[t % s2] = Omega * (yt - _level - d) + (1 - Omega) * w;
    }

    /// <summary>Forecasts the next <paramref name="horizon"/> samples after the fitted data.</summary>
    public double[] Forecast(int horizon)
    {
        if (!_fitted) throw new InvalidOperationException("Call Fit first.");
        var f = new double[horizon];
        int s1 = SeasonLength, s2 = SecondSeasonLength;
        for (int h = 1; h <= horizon; h++)
        {
            int t = _t + h - 1;
            f[h - 1] = _level + h * _trend + _s1[t % s1] + (s2 > 0 ? _s2[t % s2] : 0);
        }
        return f;
    }

    /// <summary>Fits to a time series and returns a forecast series starting at its end.</summary>
    public TimeSeries Forecast(TimeSeries history, int horizon)
    {
        Fit(history.Values);
        return new TimeSeries(history.End, history.Step, Forecast(horizon), "Holt-Winters forecast", history.Unit);
    }

    /// <summary>
    /// Fits with smoothing parameters chosen by a coarse grid search minimizing in-sample one-step SSE
    /// (α, γ, ω ∈ {0.05, 0.1, 0.2, 0.4}, β ∈ {0, 0.01}).
    /// </summary>
    public static HoltWinters FitOptimized(ReadOnlySpan<double> y, int seasonLength, int secondSeasonLength = 0)
    {
        double[] grid = [0.05, 0.1, 0.2, 0.4];
        double[] betas = [0, 0.01];
        double[] omegas = secondSeasonLength > 0 ? grid : [0];
        HoltWinters? best = null;
        foreach (double a in grid)
            foreach (double b in betas)
                foreach (double g in grid)
                    foreach (double o in omegas)
                    {
                        var m = new HoltWinters(seasonLength, secondSeasonLength) { Alpha = a, Beta = b, Gamma = g, Omega = o }.Fit(y);
                        if (best is null || m.Sse < best.Sse) best = m;
                    }
        return best!;
    }

    private static double Mean(ReadOnlySpan<double> x)
    {
        double s = 0;
        foreach (var v in x) s += v;
        return s / x.Length;
    }
}
