using System.Numerics;
using System.Runtime.InteropServices;

namespace Agritech.Core.Data;

/// <summary>Allocation-free descriptive statistics over spans (SIMD accelerated where possible).</summary>
public static class Stats
{
    public static double Sum(ReadOnlySpan<double> x)
    {
        double total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<double>.Count * 2)
        {
            var acc = Vector<double>.Zero;
            var vecs = MemoryMarshal.Cast<double, Vector<double>>(x);
            foreach (var v in vecs) acc += v;
            total = Vector.Sum(acc);
            i = vecs.Length * Vector<double>.Count;
        }
        for (; i < x.Length; i++) total += x[i];
        return total;
    }

    public static double Mean(ReadOnlySpan<double> x) => x.IsEmpty ? double.NaN : Sum(x) / x.Length;

    public static double Variance(ReadOnlySpan<double> x, bool sample = true)
    {
        if (x.Length < 2) return 0;
        double m = Mean(x), ss = 0;
        foreach (var v in x) { double d = v - m; ss += d * d; }
        return ss / (sample ? x.Length - 1 : x.Length);
    }

    public static double StdDev(ReadOnlySpan<double> x, bool sample = true) => Math.Sqrt(Variance(x, sample));

    public static (double Min, double Max) MinMax(ReadOnlySpan<double> x)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in x) { if (v < min) min = v; if (v > max) max = v; }
        return (min, max);
    }

    /// <summary>Percentile with linear interpolation (p in [0,100]).</summary>
    public static double Percentile(ReadOnlySpan<double> x, double p)
    {
        if (x.IsEmpty) return double.NaN;
        var copy = x.ToArray();
        Array.Sort(copy);
        double rank = Guard.InRange(p, 0, 100) / 100.0 * (copy.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return copy[lo] + (copy[hi] - copy[lo]) * (rank - lo);
    }

    public static double Median(ReadOnlySpan<double> x) => Percentile(x, 50);

    public static double Correlation(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        Guard.SameLength(x.Length, y.Length);
        double mx = Mean(x), my = Mean(y), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>Ordinary least squares y = a + b·x.</summary>
    public static (double Intercept, double Slope, double R2) LinearFit(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        Guard.SameLength(x.Length, y.Length);
        double mx = Mean(x), my = Mean(y), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        double b = sxx == 0 ? 0 : sxy / sxx;
        double r2 = sxx == 0 || syy == 0 ? 0 : sxy * sxy / (sxx * syy);
        return (my - b * mx, b, r2);
    }

    public static double Rmse(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted)
    {
        Guard.SameLength(actual.Length, predicted.Length);
        double s = 0;
        for (int i = 0; i < actual.Length; i++) { double d = actual[i] - predicted[i]; s += d * d; }
        return Math.Sqrt(s / actual.Length);
    }

    public static double ZScore(double value, double mean, double std) => std == 0 ? 0 : (value - mean) / std;

    /// <summary>Otsu's optimal threshold for a set of values (e.g. vegetation index) using a 256-bin histogram.</summary>
    public static double OtsuThreshold(ReadOnlySpan<double> values)
    {
        var (min, max) = MinMax(values);
        if (!(max > min)) return min;
        Span<int> hist = stackalloc int[256];
        double scale = 255.0 / (max - min);
        foreach (var v in values) if (!double.IsNaN(v)) hist[(int)((v - min) * scale)]++;
        int total = 0; double sumAll = 0;
        for (int i = 0; i < 256; i++) { total += hist[i]; sumAll += i * (double)hist[i]; }
        double sumB = 0, best = -1; int wB = 0, threshold = 0;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            int wF = total - wB; if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t; }
        }
        return min + (threshold + 0.5) / scale;
    }
}
