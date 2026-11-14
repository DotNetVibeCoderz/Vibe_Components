using Electrical.Pmu.C37118;

namespace Electrical.Pmu;

/// <summary>Frames of several PMU streams sharing one timestamp. <see cref="Missing"/> lists streams that did not arrive within the wait time.</summary>
public sealed record AlignedFrameSet(C37Time Time, IReadOnlyDictionary<ushort, C37DataFrame> Frames, IReadOnlyList<ushort> Missing)
{
    public bool IsComplete => Missing.Count == 0;
}

/// <summary>
/// Phasor data concentrator time alignment: frames from the expected streams (by IDCODE) are grouped by timestamp (SOC/FRACSEC).
/// A set is released as soon as every stream has reported, or when <see cref="WaitTime"/> has elapsed since the first frame of that
/// timestamp arrived (then with <see cref="AlignedFrameSet.Missing"/>). Sets are always released in timestamp order; frames for an
/// already released timestamp are counted in <see cref="LateFrames"/> and dropped. Arrival times are passed in explicitly, so the buffer
/// is deterministic (drive it from a <see cref="TimeProvider"/> in real time).
/// </summary>
public sealed class PdcAlignmentBuffer
{
    private readonly HashSet<ushort> _expected;
    private readonly SortedDictionary<ulong, Pending> _pending = [];
    private readonly Lock _gate = new();
    private ulong? _lastReleased;

    private sealed class Pending(C37Time time, DateTimeOffset first)
    {
        public C37Time Time { get; } = time;
        public DateTimeOffset FirstArrival { get; } = first;
        public Dictionary<ushort, C37DataFrame> Frames { get; } = [];
    }

    /// <param name="expectedIdCodes">Stream IDCODEs to align.</param>
    /// <param name="waitTime">Maximum wait for late streams after the first frame of a timestamp.</param>
    public PdcAlignmentBuffer(IEnumerable<ushort> expectedIdCodes, TimeSpan waitTime)
    {
        _expected = [.. expectedIdCodes];
        if (_expected.Count == 0) throw new ArgumentException("At least one stream is required.", nameof(expectedIdCodes));
        WaitTime = waitTime;
    }

    public TimeSpan WaitTime { get; }
    public IReadOnlyCollection<ushort> ExpectedStreams => _expected;
    /// <summary>Frames dropped because their timestamp had already been released.</summary>
    public long LateFrames { get; private set; }
    /// <summary>Frames from streams that are not expected.</summary>
    public long UnknownFrames { get; private set; }
    /// <summary>Timestamps waiting for frames.</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Adds a frame that arrived at <paramref name="arrival"/> and returns the sets released by it (in timestamp order).</summary>
    public IReadOnlyList<AlignedFrameSet> Add(C37DataFrame frame, DateTimeOffset arrival)
    {
        lock (_gate)
        {
            if (!_expected.Contains(frame.IdCode)) { UnknownFrames++; return Release(arrival); }
            ulong key = frame.Time.Key;
            if (_lastReleased is { } last && key <= last) { LateFrames++; return Release(arrival); }
            if (!_pending.TryGetValue(key, out var p)) _pending[key] = p = new Pending(frame.Time, arrival);
            p.Frames[frame.IdCode] = frame;
            return Release(arrival);
        }
    }

    /// <summary>Releases sets whose wait time has expired at <paramref name="now"/>.</summary>
    public IReadOnlyList<AlignedFrameSet> Flush(DateTimeOffset now) { lock (_gate) return Release(now); }

    /// <summary>Releases everything still pending (end of stream).</summary>
    public IReadOnlyList<AlignedFrameSet> FlushAll() { lock (_gate) return Release(DateTimeOffset.MaxValue); }

    private List<AlignedFrameSet> Release(DateTimeOffset now)
    {
        var output = new List<AlignedFrameSet>();
        // Oldest first: a complete set waits behind an older incomplete one until that one expires (timestamp order is kept).
        while (_pending.Count > 0)
        {
            var (key, p) = _pending.First();
            bool complete = p.Frames.Count == _expected.Count;
            bool expired = now == DateTimeOffset.MaxValue || now - p.FirstArrival >= WaitTime;
            if (!complete && !expired) break;
            _pending.Remove(key);
            _lastReleased = key;
            var missing = _expected.Where(id => !p.Frames.ContainsKey(id)).Order().ToArray();
            output.Add(new AlignedFrameSet(p.Time, new Dictionary<ushort, C37DataFrame>(p.Frames), missing));
        }
        return output;
    }
}
