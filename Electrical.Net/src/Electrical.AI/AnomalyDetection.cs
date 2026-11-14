using Electrical.Core.Data;

namespace Electrical.AI;

/// <summary>A flagged sample.</summary>
/// <param name="Index">Sample index in the analysed series.</param>
/// <param name="Score">Detector statistic (modified z-score, EWMA z, or CUSUM sum).</param>
/// <param name="Direction">+1 above normal, −1 below.</param>
public sealed record Anomaly(int Index, double Value, double Score, int Direction, string Method);

/// <summary>
/// Robust z-score (Iglewicz &amp; Hoaglin 1993): M_i = 0.6745·(x_i − median)/MAD, flagged when |M_i| &gt; 3.5. Insensitive to the outliers
/// it looks for, unlike the classical z-score. With a window the median/MAD are computed over a centred rolling window (seasonal data).
/// </summary>
public static class RobustZScore
{
    public const double DefaultThreshold = 3.5;

    /// <summary>Median of a sample (NaNs ignored).</summary>
    public static double Median(ReadOnlySpan<double> x)
    {
        var a = x.ToArray().Where(v => !double.IsNaN(v)).ToArray();
        if (a.Length == 0) return double.NaN;
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
    }

    /// <summary>Median absolute deviation from the median.</summary>
    public static double Mad(ReadOnlySpan<double> x)
    {
        double m = Median(x);
        var d = new double[x.Length];
        for (int i = 0; i < x.Length; i++) d[i] = Math.Abs(x[i] - m);
        return Median(d);
    }

    /// <summary>Modified z-scores of all samples (global median/MAD, or a centred window of <paramref name="window"/> samples when &gt; 0).</summary>
    public static double[] Scores(ReadOnlySpan<double> x, int window = 0)
    {
        var s = new double[x.Length];
        if (window <= 0 || window >= x.Length)
        {
            double m = Median(x), mad = Mad(x);
            for (int i = 0; i < x.Length; i++) s[i] = Score(x[i], m, mad, x);
            return s;
        }
        int half = window / 2;
        for (int i = 0; i < x.Length; i++)
        {
            int lo = Math.Max(0, Math.Min(i - half, x.Length - window));
            var w = x.Slice(lo, window);
            s[i] = Score(x[i], Median(w), Mad(w), w);
        }
        return s;
    }

    private static double Score(double v, double median, double mad, ReadOnlySpan<double> sample)
    {
        if (mad > 1e-12) return 0.6745 * (v - median) / mad;
        // MAD = 0 (more than half the samples identical): fall back to the mean absolute deviation (1.253314·MeanAD ≈ σ)
        double meanAd = 0;
        foreach (var x in sample) meanAd += Math.Abs(x - median);
        meanAd /= Math.Max(1, sample.Length);
        return meanAd > 1e-12 ? (v - median) / (1.253314 * meanAd) : 0;
    }

    public static IReadOnlyList<Anomaly> Detect(ReadOnlySpan<double> x, double threshold = DefaultThreshold, int window = 0)
    {
        var s = Scores(x, window);
        var list = new List<Anomaly>();
        for (int i = 0; i < s.Length; i++)
            if (Math.Abs(s[i]) > threshold) list.Add(new Anomaly(i, x[i], s[i], Math.Sign(s[i]), "robust-z"));
        return list;
    }

    /// <summary>
    /// Seasonal variant for periodic data (e.g. daily load, period = samples per day): residual r_i = x_i − median of the samples at the same
    /// phase of the period, then robust z-scores of the residuals.
    /// </summary>
    public static IReadOnlyList<Anomaly> DetectSeasonal(ReadOnlySpan<double> x, int period, double threshold = DefaultThreshold)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        var r = new double[x.Length];
        var arr = x.ToArray();
        for (int p = 0; p < Math.Min(period, x.Length); p++)
        {
            var idx = Enumerable.Range(0, (x.Length - p + period - 1) / period).Select(j => p + j * period).ToArray();
            double m = Median(idx.Select(i => arr[i]).ToArray());
            foreach (int i in idx) r[i] = arr[i] - m;
        }
        var s = Scores(r);
        var list = new List<Anomaly>();
        for (int i = 0; i < s.Length; i++)
            if (Math.Abs(s[i]) > threshold) list.Add(new Anomaly(i, x[i], s[i], Math.Sign(s[i]), "robust-z-seasonal"));
        return list;
    }

    public static IReadOnlyList<Anomaly> Detect(TimeSeries series, double threshold = DefaultThreshold, int window = 0) =>
        Detect(series.Values, threshold, window);
}

/// <summary>
/// EWMA control chart (Roberts 1959; Montgomery, "Statistical Quality Control"): z_t = λx_t + (1 − λ)z_{t−1}; alarm when
/// |z_t − μ| &gt; L·σ·√(λ/(2 − λ)·(1 − (1 − λ)^{2t})). μ and σ are learned (robustly: median and 1.4826·MAD) from the first
/// <see cref="Warmup"/> samples unless supplied. Good at small persistent shifts (drifting sensors).
/// </summary>
public sealed class EwmaDetector(double lambda = 0.2, double l = 3.0, int warmup = 24)
{
    private readonly List<double> _warm = [];
    private double _z;
    private int _t;

    public double Lambda { get; } = lambda is > 0 and <= 1 ? lambda : throw new ArgumentOutOfRangeException(nameof(lambda));
    public double L { get; } = l;
    public int Warmup { get; } = warmup;
    public double? Mean { get; private set; }
    public double? Sigma { get; private set; }
    /// <summary>Current EWMA statistic.</summary>
    public double Statistic => _z;

    /// <summary>Uses known in-control parameters (skips the warm-up).</summary>
    public EwmaDetector WithBaseline(double mean, double sigma) { Mean = mean; Sigma = sigma; _z = mean; return this; }

    /// <summary>Feeds one sample; returns the standardized EWMA deviation (NaN during warm-up) and whether it is out of control.</summary>
    public (bool Alarm, double Z) Update(double x)
    {
        if (Mean is null)
        {
            _warm.Add(x);
            if (_warm.Count < Math.Max(2, Warmup)) return (false, double.NaN);
            double med = RobustZScore.Median(_warm.ToArray());
            double sd = 1.4826 * RobustZScore.Mad(_warm.ToArray());
            if (sd <= 1e-12) sd = Math.Max(1e-12, Stats.StdDev(_warm.ToArray()));
            WithBaseline(med, sd);
            return (false, double.NaN);
        }
        _t++;
        _z = Lambda * x + (1 - Lambda) * _z;
        double width = Sigma!.Value * Math.Sqrt(Lambda / (2 - Lambda) * (1 - Math.Pow(1 - Lambda, 2 * _t)));
        double zs = width > 0 ? (_z - Mean.Value) / width : 0;
        return (Math.Abs(zs) > L, zs);
    }

    /// <summary>Runs the detector over a series and returns the alarmed samples.</summary>
    public IReadOnlyList<Anomaly> Detect(ReadOnlySpan<double> x)
    {
        var list = new List<Anomaly>();
        for (int i = 0; i < x.Length; i++)
        {
            var (alarm, z) = Update(x[i]);
            if (alarm) list.Add(new Anomaly(i, x[i], z, Math.Sign(z), "ewma"));
        }
        return list;
    }
}

/// <summary>
/// Two-sided tabular CUSUM (Page 1954) on standardized samples: S⁺ = max(0, S⁺ + z − k), S⁻ = max(0, S⁻ − z − k), alarm when either exceeds
/// h (defaults k = 0.5, h = 5 → detects 1σ shifts with ARL₀ ≈ 465). The sums reset after an alarm. Baseline learned robustly from the warm-up.
/// </summary>
public sealed class CusumDetector(double k = 0.5, double h = 5.0, int warmup = 24)
{
    private readonly List<double> _warm = [];

    public double K { get; } = k;
    public double H { get; } = h;
    public int Warmup { get; } = warmup;
    public double? Mean { get; private set; }
    public double? Sigma { get; private set; }
    public double SPlus { get; private set; }
    public double SMinus { get; private set; }

    public CusumDetector WithBaseline(double mean, double sigma) { Mean = mean; Sigma = sigma; return this; }

    /// <summary>Feeds one sample; returns +1 (upward shift), −1 (downward shift) or 0.</summary>
    public int Update(double x)
    {
        if (Mean is null)
        {
            _warm.Add(x);
            if (_warm.Count < Math.Max(2, Warmup)) return 0;
            double sd = 1.4826 * RobustZScore.Mad(_warm.ToArray());
            if (sd <= 1e-12) sd = Math.Max(1e-12, Stats.StdDev(_warm.ToArray()));
            WithBaseline(RobustZScore.Median(_warm.ToArray()), sd);
            return 0;
        }
        double z = (x - Mean.Value) / Sigma!.Value;
        SPlus = Math.Max(0, SPlus + z - K);
        SMinus = Math.Max(0, SMinus - z - K);
        if (SPlus > H) { SPlus = 0; SMinus = 0; return 1; }
        if (SMinus > H) { SPlus = 0; SMinus = 0; return -1; }
        return 0;
    }

    public IReadOnlyList<Anomaly> Detect(ReadOnlySpan<double> x)
    {
        var list = new List<Anomaly>();
        for (int i = 0; i < x.Length; i++)
        {
            double sp = SPlus, sm = SMinus;
            int d = Update(x[i]);
            if (d != 0) list.Add(new Anomaly(i, x[i], d > 0 ? sp : -sm, d, "cusum"));
        }
        return list;
    }
}
