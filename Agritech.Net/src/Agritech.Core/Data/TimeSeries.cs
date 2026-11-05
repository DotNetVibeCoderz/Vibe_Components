namespace Agritech.Core.Data;

/// <summary>Timestamped sample.</summary>
public readonly record struct Sample<T>(DateTimeOffset Time, T Value);

/// <summary>Time-ordered series with efficient append and range queries.</summary>
public sealed class TimeSeries<T>
{
    private readonly List<Sample<T>> _samples = [];

    public string Name { get; }
    public string? Unit { get; }

    public TimeSeries(string name, string? unit = null) { Name = name; Unit = unit; }

    public int Count => _samples.Count;
    public IReadOnlyList<Sample<T>> Samples => _samples;
    public Sample<T>? Last => _samples.Count == 0 ? null : _samples[^1];

    /// <summary>Adds a sample; out-of-order samples are inserted in place (binary search).</summary>
    public void Add(DateTimeOffset time, T value)
    {
        var s = new Sample<T>(time, value);
        if (_samples.Count == 0 || _samples[^1].Time <= time) { _samples.Add(s); return; }
        _samples.Insert(LowerBound(time), s);
    }

    private int LowerBound(DateTimeOffset t)
    {
        int lo = 0, hi = _samples.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_samples[mid].Time < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Samples within [from, to).</summary>
    public IEnumerable<Sample<T>> Range(DateTimeOffset from, DateTimeOffset to)
    {
        for (int i = LowerBound(from); i < _samples.Count && _samples[i].Time < to; i++) yield return _samples[i];
    }

    /// <summary>Keeps only samples newer than the given cutoff (ring-buffer style retention).</summary>
    public void TrimBefore(DateTimeOffset cutoff)
    {
        int idx = LowerBound(cutoff);
        if (idx > 0) _samples.RemoveRange(0, idx);
    }
}

public static class TimeSeriesExtensions
{
    public static double[] Values(this TimeSeries<double> ts) => ts.Samples.Select(s => s.Value).ToArray();

    /// <summary>Daily aggregation using the supplied reducer (default: mean).</summary>
    public static IReadOnlyList<Sample<double>> ResampleDaily(this TimeSeries<double> ts, Func<double[], double>? reducer = null)
    {
        reducer ??= v => Stats.Mean(v);
        return ts.Samples
            .GroupBy(s => new DateTimeOffset(s.Time.Date, s.Time.Offset))
            .Select(g => new Sample<double>(g.Key, reducer(g.Select(x => x.Value).ToArray())))
            .ToList();
    }

    /// <summary>Simple moving average over a window of N samples.</summary>
    public static double[] MovingAverage(ReadOnlySpan<double> values, int window)
    {
        if (window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        var res = new double[values.Length];
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            if (i >= window) sum -= values[i - window];
            res[i] = sum / Math.Min(i + 1, window);
        }
        return res;
    }

    /// <summary>Centred moving average (window shrinks at the edges) — no phase lag, suited to peak detection.</summary>
    public static double[] CenteredMovingAverage(ReadOnlySpan<double> values, int window)
    {
        if (window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        int half = window / 2;
        var res = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(values.Length - 1, i + half);
            double s = 0;
            for (int k = a; k <= b; k++) s += values[k];
            res[i] = s / (b - a + 1);
        }
        return res;
    }

    /// <summary>Exponential smoothing with factor alpha in (0,1].</summary>
    public static double[] ExponentialSmoothing(ReadOnlySpan<double> values, double alpha)
    {
        Guard.InRange(alpha, double.Epsilon, 1);
        var res = new double[values.Length];
        if (values.IsEmpty) return res;
        res[0] = values[0];
        for (int i = 1; i < values.Length; i++) res[i] = alpha * values[i] + (1 - alpha) * res[i - 1];
        return res;
    }
}
