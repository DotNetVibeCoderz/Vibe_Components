using Electrical.Core.Data;

namespace Electrical.SCADA;

/// <summary>Archived historian sample.</summary>
public readonly record struct HistorianSample(DateTimeOffset Timestamp, double Value, TagQuality Quality = TagQuality.Good);

/// <summary>Compression algorithm applied to incoming samples.</summary>
public enum CompressionMode
{
    /// <summary>Store every sample.</summary>
    None,
    /// <summary>Store a sample when it deviates from the last stored value by more than the deviation.</summary>
    Deadband,
    /// <summary>Swinging-door trending (Bristol 1990, door slopes pivoting on the last archived point): linear interpolation between stored points stays within ± deviation of every raw sample.</summary>
    SwingingDoor,
}

/// <summary>Per-tag historian configuration.</summary>
/// <param name="Mode">Compression algorithm.</param>
/// <param name="Deviation">Compression deviation in engineering units (≥ 0).</param>
/// <param name="MaxInterval">Maximum time between archived samples (forces storage of a heartbeat); null = unlimited.</param>
public sealed record HistorianTagOptions(CompressionMode Mode = CompressionMode.SwingingDoor, double Deviation = 0, TimeSpan? MaxInterval = null);

/// <summary>
/// In-memory process historian with per-tag deadband or swinging-door compression, time-range queries, linear interpolation and
/// resampling into <see cref="TimeSeries"/>. Samples must arrive in non-decreasing time order per tag.
/// </summary>
public sealed class Historian(HistorianTagOptions? defaultOptions = null)
{
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public HistorianTagOptions DefaultOptions { get; } = defaultOptions ?? new();

    /// <summary>Configures compression for a tag (call before the first sample; otherwise <see cref="DefaultOptions"/> apply).</summary>
    public void Configure(string tag, HistorianTagOptions options) { lock (_gate) _channels[tag] = new Channel(options); }

    public IReadOnlyList<string> Tags { get { lock (_gate) return _channels.Keys.ToArray(); } }

    /// <summary>Records tag database changes automatically.</summary>
    public IDisposable Attach(TagDatabase tags)
    {
        void Handler(TagValueChanged e) => Record(e.Tag, e.Timestamp, e.Value, e.Quality);
        tags.Changed += Handler;
        return new Detach(() => tags.Changed -= Handler);
    }

    /// <summary>Offers a raw sample to the compressor.</summary>
    public void Record(string tag, DateTimeOffset timestamp, double value, TagQuality quality = TagQuality.Good)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(tag, out var c)) _channels[tag] = c = new Channel(DefaultOptions);
            c.Add(new HistorianSample(timestamp, value, quality));
        }
    }

    /// <summary>Number of raw samples received for a tag.</summary>
    public long ReceivedCount(string tag) { lock (_gate) return _channels.TryGetValue(tag, out var c) ? c.Received : 0; }

    /// <summary>Number of samples archived (including the pending snapshot).</summary>
    public int ArchivedCount(string tag) { lock (_gate) return _channels.TryGetValue(tag, out var c) ? c.Points().Count : 0; }

    /// <summary>Compression ratio = received / archived (≥ 1).</summary>
    public double CompressionRatio(string tag)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(tag, out var c)) return 1;
            int n = c.Points().Count;
            return n == 0 ? 1 : (double)c.Received / n;
        }
    }

    /// <summary>Archived samples with <paramref name="from"/> ≤ t ≤ <paramref name="to"/> (the newest held sample is included as snapshot).</summary>
    public IReadOnlyList<HistorianSample> Query(string tag, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(tag, out var c)) return [];
            return c.Points().Where(s => s.Timestamp >= from && s.Timestamp <= to).ToArray();
        }
    }

    /// <summary>
    /// Value at time <paramref name="t"/> by linear interpolation between archived samples (NaN outside the archived range or across
    /// bad-quality samples).
    /// </summary>
    public double Interpolate(string tag, DateTimeOffset t)
    {
        lock (_gate)
            return _channels.TryGetValue(tag, out var c) ? InterpolateCore(c.Points(), t) : double.NaN;
    }

    /// <summary>Interpolated values at a regular step over [from, to) as a <see cref="TimeSeries"/>.</summary>
    public TimeSeries Resample(string tag, DateTimeOffset from, DateTimeOffset to, TimeSpan step, string? unit = null)
    {
        int n = Math.Max(0, (int)Math.Ceiling((to - from) / step));
        var v = new double[n];
        lock (_gate)
        {
            var pts = _channels.TryGetValue(tag, out var c) ? c.Points() : [];
            for (int i = 0; i < n; i++) v[i] = InterpolateCore(pts, from + step * i);
        }
        return new TimeSeries(from, step, v, tag, unit);
    }

    private static double InterpolateCore(IReadOnlyList<HistorianSample> pts, DateTimeOffset t)
    {
        if (pts.Count == 0 || t < pts[0].Timestamp || t > pts[^1].Timestamp) return double.NaN;
        int lo = 0, hi = pts.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (pts[mid].Timestamp <= t) lo = mid; else hi = mid;
        }
        var a = pts[lo]; var b = pts[hi];
        if (a.Timestamp == t) return a.Quality == TagQuality.Bad ? double.NaN : a.Value;
        if (b.Timestamp == t) return b.Quality == TagQuality.Bad ? double.NaN : b.Value;
        if (a.Quality is not (TagQuality.Good or TagQuality.Uncertain) || b.Quality is not (TagQuality.Good or TagQuality.Uncertain)) return double.NaN;
        double f = (t - a.Timestamp) / (b.Timestamp - a.Timestamp);
        return a.Value + f * (b.Value - a.Value);
    }

    /// <summary>Compressor state of one tag.</summary>
    private sealed class Channel(HistorianTagOptions o)
    {
        private readonly List<HistorianSample> _archive = [];
        private HistorianSample? _held;          // last received, not yet archived
        private double _slopeUpper, _slopeLower;  // door slopes [EU/s] relative to the last archived point
        public long Received;

        public void Add(HistorianSample s)
        {
            Received++;
            if (_archive.Count > 0 && s.Timestamp < (_held ?? _archive[^1]).Timestamp)
                throw new ArgumentException("Historian samples must be in time order.");
            if (o.Mode == CompressionMode.None || _archive.Count == 0) { Archive(s); return; }
            var last = _archive[^1];
            bool forced = s.Quality != (_held ?? last).Quality || double.IsNaN(s.Value) ||
                          o.MaxInterval is { } mi && s.Timestamp - last.Timestamp > mi;

            if (o.Mode == CompressionMode.Deadband)
            {
                if (forced || Math.Abs(s.Value - last.Value) > o.Deviation) { if (_held is { } h && forced) _archive.Add(h); Archive(s); }
                else _held = s;
                return;
            }

            // Swinging door
            if (forced)
            {
                if (_held is { } h) _archive.Add(h);
                Archive(s);
                return;
            }
            double dt = (s.Timestamp - last.Timestamp).TotalSeconds;
            if (dt <= 0) { _held = s; return; }
            double up = (s.Value + o.Deviation - last.Value) / dt, lo = (s.Value - o.Deviation - last.Value) / dt;
            double exact = (s.Value - last.Value) / dt;
            if ((exact > _slopeUpper || exact < _slopeLower) && _held is { } held)
            {
                // The line from the last archived point to the new sample leaves the door corridor of an earlier sample:
                // archive the previous sample and restart the doors from it. Every archived segment therefore stays
                // within ±deviation of all raw samples it spans.
                _archive.Add(held);
                double dt2 = (s.Timestamp - held.Timestamp).TotalSeconds;
                _slopeUpper = dt2 > 0 ? (s.Value + o.Deviation - held.Value) / dt2 : double.PositiveInfinity;
                _slopeLower = dt2 > 0 ? (s.Value - o.Deviation - held.Value) / dt2 : double.NegativeInfinity;
            }
            else
            {
                _slopeUpper = Math.Min(_slopeUpper, up);
                _slopeLower = Math.Max(_slopeLower, lo);
            }
            _held = s;
        }

        private void Archive(HistorianSample s)
        {
            _archive.Add(s);
            _held = null;
            _slopeUpper = double.PositiveInfinity;
            _slopeLower = double.NegativeInfinity;
        }

        /// <summary>Archive plus the held snapshot.</summary>
        public IReadOnlyList<HistorianSample> Points() => _held is { } h ? [.. _archive, h] : _archive;
    }

    private sealed class Detach(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() => Interlocked.Exchange(ref _a, null)?.Invoke();
    }
}
