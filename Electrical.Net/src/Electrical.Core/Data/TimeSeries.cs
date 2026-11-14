using System.Numerics.Tensors;

namespace Electrical.Core.Data;

/// <summary>Regularly sampled time series (start + fixed step), e.g. a 15-minute load profile.</summary>
public sealed class TimeSeries
{
    public TimeSeries(DateTimeOffset start, TimeSpan step, double[] values, string? name = null, string? unit = null)
    {
        if (step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(step));
        Start = start; Step = step; Values = values; Name = name; Unit = unit;
    }

    public DateTimeOffset Start { get; }
    public TimeSpan Step { get; }
    public double[] Values { get; }
    public string? Name { get; init; }
    public string? Unit { get; init; }
    public int Count => Values.Length;
    public DateTimeOffset End => Start + Step * Count;

    public DateTimeOffset TimeAt(int i) => Start + Step * i;
    public double this[int i] => Values[i];

    public double Sum() => TensorPrimitives.Sum<double>(Values);
    public double Mean() => Count == 0 ? double.NaN : Sum() / Count;
    public double Max() => Count == 0 ? double.NaN : TensorPrimitives.Max<double>(Values);
    public double Min() => Count == 0 ? double.NaN : TensorPrimitives.Min<double>(Values);

    /// <summary>Energy [unit·h] assuming values are power (trapezoid-free rectangular integration).</summary>
    public double Energy() => Sum() * Step.TotalHours;

    /// <summary>Load factor = mean / peak.</summary>
    public double LoadFactor() => Max() is var m && m > 0 ? Mean() / m : 0;

    public TimeSeries Scale(double k)
    {
        var v = new double[Count];
        TensorPrimitives.Multiply<double>(Values, k, v);
        return new TimeSeries(Start, Step, v, Name, Unit);
    }

    public TimeSeries Add(TimeSeries other)
    {
        if (other.Count != Count || other.Step != Step) throw new ArgumentException("Series must be aligned.");
        var v = new double[Count];
        TensorPrimitives.Add<double>(Values, other.Values, v);
        return new TimeSeries(Start, Step, v, Name, Unit);
    }

    /// <summary>Averages into coarser buckets (e.g. 15-min → hourly).</summary>
    public TimeSeries Resample(TimeSpan newStep)
    {
        int k = (int)Math.Round(newStep / Step);
        if (k <= 1) return this;
        int n = Count / k;
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = TensorPrimitives.Sum<double>(Values.AsSpan(i * k, k)) / k;
        return new TimeSeries(Start, newStep, v, Name, Unit);
    }

    /// <summary>Values sorted descending (load-duration curve).</summary>
    public double[] DurationCurve()
    {
        var v = (double[])Values.Clone();
        Array.Sort(v);
        Array.Reverse(v);
        return v;
    }
}

/// <summary>Basic statistics used across modules.</summary>
public static class Stats
{
    public static double Mean(ReadOnlySpan<double> x) => x.Length == 0 ? double.NaN : TensorPrimitives.Sum(x) / x.Length;

    public static double StdDev(ReadOnlySpan<double> x)
    {
        if (x.Length < 2) return 0;
        double m = Mean(x), s = 0;
        foreach (var v in x) s += (v - m) * (v - m);
        return Math.Sqrt(s / (x.Length - 1));
    }

    public static double Rms(ReadOnlySpan<double> x) => x.Length == 0 ? 0 : Math.Sqrt(TensorPrimitives.SumOfSquares(x) / x.Length);

    public static double Percentile(ReadOnlySpan<double> x, double p)
    {
        if (x.Length == 0) return double.NaN;
        var a = x.ToArray();
        Array.Sort(a);
        double pos = Math.Clamp(p, 0, 1) * (a.Length - 1);
        int lo = (int)pos;
        int hi = Math.Min(lo + 1, a.Length - 1);
        return a[lo] + (a[hi] - a[lo]) * (pos - lo);
    }

    /// <summary>Mean absolute percentage error [%] (skips zero actuals).</summary>
    public static double Mape(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted)
    {
        double s = 0; int n = 0;
        for (int i = 0; i < actual.Length; i++)
            if (actual[i] != 0) { s += Math.Abs((actual[i] - predicted[i]) / actual[i]); n++; }
        return n == 0 ? 0 : 100 * s / n;
    }

    public static double RmsError(ReadOnlySpan<double> actual, ReadOnlySpan<double> predicted)
    {
        double s = 0;
        for (int i = 0; i < actual.Length; i++) s += (actual[i] - predicted[i]) * (actual[i] - predicted[i]);
        return actual.Length == 0 ? 0 : Math.Sqrt(s / actual.Length);
    }
}
