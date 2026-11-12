using System.Numerics;
using System.Runtime.InteropServices;

namespace AquaCulture.Core.Data;

/// <summary>Summary statistics of a sample.</summary>
public readonly record struct Summary(int Count, double Min, double Max, double Mean, double StdDev)
{
    public double CoefficientOfVariation => Mean == 0 ? double.NaN : StdDev / Mean;
}

/// <summary>Allocation-free, SIMD-accelerated statistics over spans. NaN values are skipped by the <c>Nan*</c> methods.</summary>
public static class Stats
{
    /// <summary>Sum using <see cref="Vector{T}"/> lanes.</summary>
    public static double Sum(ReadOnlySpan<double> values)
    {
        var acc = Vector<double>.Zero;
        int i = 0, w = Vector<double>.Count;
        var vecs = MemoryMarshal.Cast<double, Vector<double>>(values);
        foreach (var v in vecs) acc += v;
        i = vecs.Length * w;
        double s = Vector.Sum(acc);
        for (; i < values.Length; i++) s += values[i];
        return s;
    }

    public static double Mean(ReadOnlySpan<double> values) => values.Length == 0 ? double.NaN : Sum(values) / values.Length;

    /// <summary>Sample variance (n − 1), two-pass for numerical stability.</summary>
    public static double Variance(ReadOnlySpan<double> values)
    {
        if (values.Length < 2) return double.NaN;
        double m = Mean(values), ss = 0;
        foreach (var v in values) { double d = v - m; ss += d * d; }
        return ss / (values.Length - 1);
    }

    public static double StdDev(ReadOnlySpan<double> values) => Math.Sqrt(Variance(values));

    /// <summary>Summary skipping NaN (Welford).</summary>
    public static Summary Summarize(ReadOnlySpan<double> values)
    {
        int n = 0; double mean = 0, m2 = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            n++;
            double d = v - mean;
            mean += d / n;
            m2 += d * (v - mean);
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return n == 0 ? new Summary(0, double.NaN, double.NaN, double.NaN, double.NaN)
                      : new Summary(n, min, max, mean, n > 1 ? Math.Sqrt(m2 / (n - 1)) : 0);
    }

    /// <summary>Linear-interpolated percentile (p in 0–100) of a copy of the data.</summary>
    public static double Percentile(ReadOnlySpan<double> values, double p)
    {
        if (values.Length == 0) return double.NaN;
        var copy = values.ToArray();
        Array.Sort(copy);
        return PercentileSorted(copy, p);
    }

    public static double PercentileSorted(ReadOnlySpan<double> sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        double rank = Math.Clamp(p, 0, 100) / 100 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static double Median(ReadOnlySpan<double> values) => Percentile(values, 50);

    /// <summary>Pearson correlation.</summary>
    public static double Correlation(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length || x.Length < 2) return double.NaN;
        double mx = Mean(x), my = Mean(y), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>Ordinary least squares y = a + b·x.</summary>
    public static LinearFit LinearRegression(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length || x.Length < 2) throw new ArgumentException("Need at least two paired points.");
        double mx = Mean(x), my = Mean(y), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        double b = sxy / sxx, a = my - b * mx;
        double r2 = syy == 0 ? 1 : sxy * sxy / (sxx * syy);
        double sse = 0;
        for (int i = 0; i < x.Length; i++) { double e = y[i] - (a + b * x[i]); sse += e * e; }
        double se = x.Length > 2 ? Math.Sqrt(sse / (x.Length - 2) / sxx) : double.NaN;
        return new LinearFit(a, b, r2, se, x.Length);
    }

    /// <summary>Root-mean-square error.</summary>
    public static double Rmse(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted)
    {
        double s = 0;
        for (int i = 0; i < actual.Length; i++) { double d = actual[i] - predicted[i]; s += d * d; }
        return Math.Sqrt(s / actual.Length);
    }

    /// <summary>Mean absolute percentage error (%), skipping zero actuals.</summary>
    public static double Mape(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted)
    {
        double s = 0; int n = 0;
        for (int i = 0; i < actual.Length; i++) if (actual[i] != 0) { s += Math.Abs((actual[i] - predicted[i]) / actual[i]); n++; }
        return n == 0 ? double.NaN : 100 * s / n;
    }
}

/// <summary>Result of a simple linear regression.</summary>
public readonly record struct LinearFit(double Intercept, double Slope, double RSquared, double SlopeStdError, int N)
{
    public double Predict(double x) => Intercept + Slope * x;
}
