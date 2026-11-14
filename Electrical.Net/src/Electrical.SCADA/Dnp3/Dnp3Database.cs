namespace Electrical.SCADA.Dnp3;

/// <summary>Event class assignment of a point (Class 0 = static only, no events).</summary>
public enum Dnp3PointClass : byte { Class0 = 0, Class1 = 1, Class2 = 2, Class3 = 3 }

/// <summary>Point types held by a <see cref="Dnp3Database"/>.</summary>
public enum Dnp3PointType { BinaryInput, AnalogInput, BinaryOutput, AnalogOutput }

/// <summary>Current value of a point.</summary>
public sealed record Dnp3Point(Dnp3PointType Type, ushort Index, double Value, Dnp3Flags Flags, DateTimeOffset Time, Dnp3PointClass Class, double Deadband = 0)
{
    public bool State => Value != 0;
    /// <summary>Value of the last event (deadband reference) [engineering units].</summary>
    public double ReportedValue { get; init; } = Value;
}

/// <summary>A buffered event (binary input change g2, analog input change g32).</summary>
public sealed record Dnp3Event(long Id, Dnp3PointType Type, ushort Index, double Value, Dnp3Flags Flags, DateTimeOffset Time, Dnp3PointClass Class);

/// <summary>
/// Outstation point database: binary/analog inputs and outputs by index, with per-class event buffers. Updates of inputs assigned to
/// class 1–3 create events (analogs only when |Δ| exceeds the deadband); a full buffer drops the oldest event and sets
/// <see cref="Overflow"/> (reported as IIN2.3). Thread-safe.
/// </summary>
public sealed class Dnp3Database(int eventBufferSize = 200, TimeProvider? time = null)
{
    private readonly SortedDictionary<ushort, Dnp3Point>[] _points = [new(), new(), new(), new()];
    private readonly List<Dnp3Event>[] _events = [[], [], [], []];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _nextId;

    /// <summary>Maximum events per class.</summary>
    public int EventBufferSize { get; } = eventBufferSize;
    /// <summary>An event was discarded because a class buffer was full (cleared by <see cref="ClearOverflow"/>).</summary>
    public bool Overflow { get; private set; }

    /// <summary>Raised after an event is buffered (used for unsolicited reporting).</summary>
    public event Action<Dnp3Event>? EventAdded;

    public Dnp3Point AddBinaryInput(ushort index, bool value = false, Dnp3PointClass eventClass = Dnp3PointClass.Class1) =>
        Add(new Dnp3Point(Dnp3PointType.BinaryInput, index, value ? 1 : 0, Dnp3Flags.Online, _time.GetUtcNow(), eventClass));

    /// <param name="deadband">Minimum change that produces an event [engineering units].</param>
    public Dnp3Point AddAnalogInput(ushort index, double value = 0, Dnp3PointClass eventClass = Dnp3PointClass.Class2, double deadband = 0) =>
        Add(new Dnp3Point(Dnp3PointType.AnalogInput, index, value, Dnp3Flags.Online, _time.GetUtcNow(), eventClass, deadband));

    public Dnp3Point AddBinaryOutput(ushort index, bool value = false) =>
        Add(new Dnp3Point(Dnp3PointType.BinaryOutput, index, value ? 1 : 0, Dnp3Flags.Online, _time.GetUtcNow(), Dnp3PointClass.Class0));

    public Dnp3Point AddAnalogOutput(ushort index, double value = 0) =>
        Add(new Dnp3Point(Dnp3PointType.AnalogOutput, index, value, Dnp3Flags.Online, _time.GetUtcNow(), Dnp3PointClass.Class0));

    private Dnp3Point Add(Dnp3Point p)
    {
        lock (_gate)
        {
            if (!_points[(int)p.Type].TryAdd(p.Index, p)) throw new ArgumentException($"{p.Type} {p.Index} already exists.");
        }
        return p;
    }

    public bool Contains(Dnp3PointType type, ushort index) { lock (_gate) return _points[(int)type].ContainsKey(index); }

    public Dnp3Point Get(Dnp3PointType type, ushort index)
    {
        lock (_gate) return _points[(int)type].TryGetValue(index, out var p) ? p : throw new KeyNotFoundException($"{type} {index} not found.");
    }

    /// <summary>Snapshot of all points of a type in index order.</summary>
    public IReadOnlyList<Dnp3Point> Points(Dnp3PointType type) { lock (_gate) return [.. _points[(int)type].Values]; }

    /// <summary>Updates a binary input (or output status); creates an event on a state or flag change.</summary>
    public void UpdateBinary(Dnp3PointType type, ushort index, bool value, Dnp3Flags flags = Dnp3Flags.Online, DateTimeOffset? time = null) =>
        Update(type, index, value ? 1 : 0, flags, time);

    public void UpdateBinaryInput(ushort index, bool value, Dnp3Flags flags = Dnp3Flags.Online, DateTimeOffset? time = null) =>
        Update(Dnp3PointType.BinaryInput, index, value ? 1 : 0, flags, time);

    /// <summary>Updates an analog input [engineering units]; creates an event when |Δ| &gt; deadband or the flags change.</summary>
    public void UpdateAnalogInput(ushort index, double value, Dnp3Flags flags = Dnp3Flags.Online, DateTimeOffset? time = null) =>
        Update(Dnp3PointType.AnalogInput, index, value, flags, time);

    public void Update(Dnp3PointType type, ushort index, double value, Dnp3Flags flags = Dnp3Flags.Online, DateTimeOffset? time = null)
    {
        Dnp3Event? ev = null;
        lock (_gate)
        {
            var dict = _points[(int)type];
            if (!dict.TryGetValue(index, out var old)) throw new KeyNotFoundException($"{type} {index} not found.");
            var t = time ?? _time.GetUtcNow();
            flags &= ~Dnp3Flags.State;
            // analog deadband is measured from the last reported value, so slow drifts still report once they leave the band
            bool changed = type is Dnp3PointType.AnalogInput or Dnp3PointType.AnalogOutput
                ? Math.Abs(value - old.ReportedValue) > old.Deadband || old.Flags != flags
                : (value != 0) != old.State || old.Flags != flags;
            dict[index] = old with { Value = value, Flags = flags, Time = t, ReportedValue = changed ? value : old.ReportedValue };
            if (changed && old.Class != Dnp3PointClass.Class0 && type is Dnp3PointType.BinaryInput or Dnp3PointType.AnalogInput)
            {
                var buf = _events[(int)old.Class];
                if (buf.Count >= EventBufferSize) { buf.RemoveAt(0); Overflow = true; }
                ev = new Dnp3Event(++_nextId, type, index, value, flags, t, old.Class);
                buf.Add(ev);
            }
        }
        if (ev is not null) EventAdded?.Invoke(ev);
    }

    /// <summary>Number of buffered events of a class.</summary>
    public int EventCount(Dnp3PointClass c) { lock (_gate) return _events[(int)c].Count; }

    /// <summary>Events of the given classes (oldest first, at most <paramref name="max"/>), optionally of one type only. Events stay buffered until <see cref="Acknowledge"/>.</summary>
    public List<Dnp3Event> PeekEvents(IEnumerable<Dnp3PointClass> classes, int max = int.MaxValue, Dnp3PointType? type = null)
    {
        lock (_gate)
        {
            return classes.Distinct().SelectMany(c => _events[(int)c]).Where(e => type is null || e.Type == type)
                .OrderBy(e => e.Id).Take(max).ToList();
        }
    }

    /// <summary>Removes confirmed events.</summary>
    public void Acknowledge(IEnumerable<long> eventIds)
    {
        var ids = eventIds.ToHashSet();
        lock (_gate) foreach (var b in _events) b.RemoveAll(e => ids.Contains(e.Id));
    }

    public void ClearOverflow() => Overflow = false;

    /// <summary>IIN1.1–1.3 bits for the classes with buffered events.</summary>
    public Dnp3Iin EventIin()
    {
        lock (_gate)
        {
            var iin = Dnp3Iin.None;
            if (_events[1].Count > 0) iin |= Dnp3Iin.Class1Events;
            if (_events[2].Count > 0) iin |= Dnp3Iin.Class2Events;
            if (_events[3].Count > 0) iin |= Dnp3Iin.Class3Events;
            if (Overflow) iin |= Dnp3Iin.EventBufferOverflow;
            return iin;
        }
    }
}
