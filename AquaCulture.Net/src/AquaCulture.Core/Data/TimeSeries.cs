namespace AquaCulture.Core.Data;

/// <summary>A timestamped observation.</summary>
public readonly record struct TimePoint(DateTime Time, double Value);

/// <summary>Ordered time series with resampling, rolling statistics and interpolation.</summary>
public sealed class TimeSeries
{
    private readonly List<TimePoint> _points = [];

    public TimeSeries(string name = "", string unit = "") { Name = name; Unit = unit; }

    public TimeSeries(IEnumerable<TimePoint> points, string name = "", string unit = "") : this(name, unit)
    {
        foreach (var p in points) Add(p.Time, p.Value);
    }

    public string Name { get; }
    public string Unit { get; }
    public int Count => _points.Count;
    public IReadOnlyList<TimePoint> Points => _points;
    public TimePoint this[int index] => _points[index];
    public TimePoint? Last => _points.Count == 0 ? null : _points[^1];

    /// <summary>Adds a point, keeping the series sorted (appending in order is O(1)).</summary>
    public void Add(DateTime time, double value)
    {
        var p = new TimePoint(time, value);
        if (_points.Count == 0 || _points[^1].Time <= time) { _points.Add(p); return; }
        int idx = _points.BinarySearch(p, Comparer<TimePoint>.Create((a, b) => a.Time.CompareTo(b.Time)));
        _points.Insert(idx < 0 ? ~idx : idx, p);
    }

    public double[] Values() { var v = new double[_points.Count]; for (int i = 0; i < v.Length; i++) v[i] = _points[i].Value; return v; }

    public TimeSeries Between(DateTime from, DateTime to) => new(_points.Where(p => p.Time >= from && p.Time <= to), Name, Unit);

    /// <summary>Linear interpolation at a time (clamped at the ends).</summary>
    public double ValueAt(DateTime t)
    {
        if (_points.Count == 0) return double.NaN;
        if (t <= _points[0].Time) return _points[0].Value;
        if (t >= _points[^1].Time) return _points[^1].Value;
        int lo = 0, hi = _points.Count - 1;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (_points[mid].Time <= t) lo = mid; else hi = mid; }
        var a = _points[lo]; var b = _points[hi];
        double f = (t - a.Time).TotalSeconds / (b.Time - a.Time).TotalSeconds;
        return a.Value + f * (b.Value - a.Value);
    }

    /// <summary>Buckets the series into fixed intervals, aggregating with <paramref name="agg"/> (mean by default).</summary>
    public TimeSeries Resample(TimeSpan interval, Func<IReadOnlyList<double>, double>? agg = null)
    {
        agg ??= v => v.Average();
        var result = new TimeSeries(Name, Unit);
        if (_points.Count == 0) return result;
        long ticks = interval.Ticks;
        foreach (var g in _points.GroupBy(p => p.Time.Ticks / ticks))
            result.Add(new DateTime(g.Key * ticks, _points[0].Time.Kind), agg(g.Select(p => p.Value).ToList()));
        return result;
    }

    /// <summary>Trailing moving average over <paramref name="window"/> points.</summary>
    public TimeSeries MovingAverage(int window)
    {
        var result = new TimeSeries(Name, Unit);
        double sum = 0;
        for (int i = 0; i < _points.Count; i++)
        {
            sum += _points[i].Value;
            if (i >= window) sum -= _points[i - window].Value;
            result.Add(_points[i].Time, sum / Math.Min(i + 1, window));
        }
        return result;
    }

    /// <summary>Rate of change per hour between the last two points.</summary>
    public double RecentSlopePerHour(int lookback = 4)
    {
        if (_points.Count < 2) return 0;
        int start = Math.Max(0, _points.Count - lookback);
        var xs = new double[_points.Count - start]; var ys = new double[xs.Length];
        for (int i = start; i < _points.Count; i++) { xs[i - start] = (_points[i].Time - _points[start].Time).TotalHours; ys[i - start] = _points[i].Value; }
        return xs.Length < 2 || xs[^1] == 0 ? 0 : Stats.LinearRegression(xs, ys).Slope;
    }
}
