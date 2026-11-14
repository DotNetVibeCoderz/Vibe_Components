using Electrical.Core.Hosting;

namespace Electrical.SCADA;

/// <summary>Alarm states of the ISA-18.2 alarm state model.</summary>
public enum AlarmState
{
    /// <summary>Process normal, alarm acknowledged (state A).</summary>
    Normal,
    /// <summary>Alarm active, not yet acknowledged (state B).</summary>
    Unacknowledged,
    /// <summary>Alarm active and acknowledged (state C).</summary>
    Acknowledged,
    /// <summary>Process returned to normal before the alarm was acknowledged (state D).</summary>
    ReturnToNormalUnacknowledged,
    /// <summary>Alarm temporarily suppressed by the operator (state E); evaluation resumes on unshelve or expiry.</summary>
    Shelved,
}

/// <summary>Alarm priority (ISA-18.2 recommends ≤ 4 levels).</summary>
public enum AlarmPriority { Low = 1, Medium = 2, High = 3, Critical = 4 }

/// <summary>Analog limit that caused an alarm.</summary>
public enum AlarmLimit { LowLow, Low, High, HighHigh }

/// <summary>
/// Analog alarm configuration for one tag. Limits are in engineering units; null disables a limit. <see cref="Deadband"/> (EU) is
/// the hysteresis applied when clearing: a high alarm clears below limit − deadband, a low alarm above limit + deadband.
/// HH/LL use <see cref="ExtremePriority"/> (default: one level above <see cref="Priority"/>).
/// </summary>
public sealed record AlarmDefinition(string Tag, double? HighHigh = null, double? High = null, double? Low = null, double? LowLow = null,
    double Deadband = 0, AlarmPriority Priority = AlarmPriority.Medium, AlarmPriority? ExtremePriority = null, string? Description = null)
{
    public AlarmPriority PriorityOf(AlarmLimit limit) => limit is AlarmLimit.HighHigh or AlarmLimit.LowLow
        ? ExtremePriority ?? (AlarmPriority)Math.Min((int)Priority + 1, (int)AlarmPriority.Critical)
        : Priority;

    public double? LimitValue(AlarmLimit limit) => limit switch
    {
        AlarmLimit.HighHigh => HighHigh,
        AlarmLimit.High => High,
        AlarmLimit.Low => Low,
        _ => LowLow,
    };
}

/// <summary>Alarm state transition published on the grid event bus.</summary>
public sealed record AlarmEvent(string Tag, AlarmState PreviousState, AlarmState State, AlarmLimit? Limit, double Value, AlarmPriority Priority,
    string Message, DateTimeOffset Timestamp, string? User = null) : IGridEvent;

/// <summary>Current alarm of a tag.</summary>
public sealed class Alarm
{
    internal Alarm(AlarmDefinition def) => Definition = def;

    public AlarmDefinition Definition { get; }
    public string Tag => Definition.Tag;
    public AlarmState State { get; internal set; } = AlarmState.Normal;
    /// <summary>Most severe active limit (null when the condition is clear).</summary>
    public AlarmLimit? ActiveLimit { get; internal set; }
    /// <summary>Limit that was last alarmed (kept for return-to-normal and shelved display).</summary>
    public AlarmLimit? LastLimit { get; internal set; }
    public double LastValue { get; internal set; } = double.NaN;
    public AlarmPriority Priority => LastLimit is { } l ? Definition.PriorityOf(l) : Definition.Priority;
    public DateTimeOffset? RaisedAt { get; internal set; }
    public DateTimeOffset? AcknowledgedAt { get; internal set; }
    public string? AcknowledgedBy { get; internal set; }
    public DateTimeOffset? ShelvedUntil { get; internal set; }
    /// <summary>True while the process condition is in alarm.</summary>
    public bool ConditionActive => ActiveLimit is not null;

    public override string ToString() => $"{Tag} {State} {LastLimit} ({Priority}) value {LastValue:G6}";
}

/// <summary>
/// Alarm manager implementing the ISA-18.2 state model for analog HH/H/L/LL limits with deadband, priorities and shelving.
/// Transitions are returned and published as <see cref="AlarmEvent"/> on the optional <see cref="IGridEventBus"/>.
/// </summary>
public sealed class AlarmManager(IGridEventBus? bus = null, TimeProvider? time = null)
{
    private readonly Dictionary<string, Alarm> _alarms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Raised synchronously for every state transition.</summary>
    public event Action<AlarmEvent>? AlarmChanged;

    public Alarm Define(AlarmDefinition definition)
    {
        if (definition.High is { } h && definition.HighHigh is { } hh && hh < h) throw new ArgumentException("HighHigh must be ≥ High.");
        if (definition.Low is { } l && definition.LowLow is { } ll && ll > l) throw new ArgumentException("LowLow must be ≤ Low.");
        if (definition.Deadband < 0) throw new ArgumentException("Deadband must be ≥ 0.");
        var a = new Alarm(definition);
        lock (_gate) _alarms[definition.Tag] = a;
        return a;
    }

    public Alarm Get(string tag) { lock (_gate) return _alarms[tag]; }

    public IReadOnlyList<Alarm> All { get { lock (_gate) return _alarms.Values.ToArray(); } }

    /// <summary>Alarms requiring operator attention (not Normal, not Shelved), most severe and oldest first.</summary>
    public IReadOnlyList<Alarm> ActiveAlarms
    {
        get
        {
            lock (_gate)
                return _alarms.Values.Where(a => a.State is not (AlarmState.Normal or AlarmState.Shelved))
                    .OrderByDescending(a => a.Priority).ThenBy(a => a.RaisedAt).ToArray();
        }
    }

    /// <summary>Subscribes to a tag database so every value change is evaluated (bad-quality values are ignored).</summary>
    public IDisposable Attach(TagDatabase tags)
    {
        void Handler(TagValueChanged e) { if (e.Quality is TagQuality.Good or TagQuality.Uncertain) Evaluate(e.Tag, e.Value, e.Timestamp); }
        tags.Changed += Handler;
        return new Detach(() => tags.Changed -= Handler);
    }

    /// <summary>Evaluates a new value for a tag and returns the resulting transitions (empty when nothing changed).</summary>
    public IReadOnlyList<AlarmEvent> Evaluate(string tag, double value, DateTimeOffset? timestamp = null)
    {
        var ts = timestamp ?? _time.GetUtcNow();
        var events = new List<AlarmEvent>(2);
        lock (_gate)
        {
            if (!_alarms.TryGetValue(tag, out var a) || double.IsNaN(value)) return events;
            a.LastValue = value;
            if (a.State == AlarmState.Shelved && a.ShelvedUntil is { } until && ts >= until)
            {
                a.ShelvedUntil = null;
                Transition(a, AlarmState.Normal, value, ts, events, "Shelve expired");
            }
            var newLimit = Condition(a.Definition, a.ActiveLimit, value);
            var oldLimit = a.ActiveLimit;
            a.ActiveLimit = newLimit;
            if (a.State == AlarmState.Shelved) return events;

            if (newLimit is { } nl)
            {
                bool escalated = oldLimit is { } ol && (Severity(nl) > Severity(ol) || Side(nl) != Side(ol));
                if (a.State is AlarmState.Normal or AlarmState.ReturnToNormalUnacknowledged || escalated)
                {
                    a.LastLimit = nl;
                    a.RaisedAt = ts;
                    a.AcknowledgedAt = null; a.AcknowledgedBy = null;
                    Transition(a, AlarmState.Unacknowledged, value, ts, events, $"{nl} alarm");
                }
            }
            else
            {
                if (a.State == AlarmState.Unacknowledged) Transition(a, AlarmState.ReturnToNormalUnacknowledged, value, ts, events, "Return to normal");
                else if (a.State == AlarmState.Acknowledged) Transition(a, AlarmState.Normal, value, ts, events, "Return to normal");
            }
        }
        Publish(events);
        return events;
    }

    /// <summary>Operator acknowledgement: Unacknowledged → Acknowledged, ReturnToNormalUnacknowledged → Normal.</summary>
    public bool Acknowledge(string tag, string? user = null, DateTimeOffset? timestamp = null)
    {
        var ts = timestamp ?? _time.GetUtcNow();
        var events = new List<AlarmEvent>(1);
        lock (_gate)
        {
            if (!_alarms.TryGetValue(tag, out var a)) return false;
            a.AcknowledgedAt = ts; a.AcknowledgedBy = user;
            if (a.State == AlarmState.Unacknowledged) Transition(a, AlarmState.Acknowledged, a.LastValue, ts, events, "Acknowledged", user);
            else if (a.State == AlarmState.ReturnToNormalUnacknowledged) Transition(a, AlarmState.Normal, a.LastValue, ts, events, "Acknowledged", user);
            else return false;
        }
        Publish(events);
        return true;
    }

    /// <summary>Acknowledges all unacknowledged alarms; returns how many were acknowledged.</summary>
    public int AcknowledgeAll(string? user = null, DateTimeOffset? timestamp = null) =>
        All.Count(a => a.State is AlarmState.Unacknowledged or AlarmState.ReturnToNormalUnacknowledged && Acknowledge(a.Tag, user, timestamp));

    /// <summary>Shelves an alarm for <paramref name="duration"/> (null = until <see cref="Unshelve"/>).</summary>
    public void Shelve(string tag, TimeSpan? duration = null, string? user = null, DateTimeOffset? timestamp = null)
    {
        var ts = timestamp ?? _time.GetUtcNow();
        var events = new List<AlarmEvent>(1);
        lock (_gate)
        {
            var a = _alarms[tag];
            a.ShelvedUntil = duration is { } d ? ts + d : null;
            if (a.State != AlarmState.Shelved) Transition(a, AlarmState.Shelved, a.LastValue, ts, events, "Shelved", user);
        }
        Publish(events);
    }

    /// <summary>Returns a shelved alarm to service; an active condition re-annunciates as Unacknowledged.</summary>
    public void Unshelve(string tag, string? user = null, DateTimeOffset? timestamp = null)
    {
        var ts = timestamp ?? _time.GetUtcNow();
        var events = new List<AlarmEvent>(1);
        lock (_gate)
        {
            var a = _alarms[tag];
            if (a.State != AlarmState.Shelved) return;
            a.ShelvedUntil = null;
            if (a.ActiveLimit is { } l)
            {
                a.LastLimit = l; a.RaisedAt = ts;
                Transition(a, AlarmState.Unacknowledged, a.LastValue, ts, events, $"{l} alarm (unshelved)", user);
            }
            else Transition(a, AlarmState.Normal, a.LastValue, ts, events, "Unshelved", user);
        }
        Publish(events);
    }

    private static int Severity(AlarmLimit l) => l is AlarmLimit.HighHigh or AlarmLimit.LowLow ? 2 : 1;
    private static int Side(AlarmLimit l) => l is AlarmLimit.High or AlarmLimit.HighHigh ? 1 : -1;

    /// <summary>Most severe limit violated by <paramref name="v"/>, honouring deadband hysteresis relative to the currently active limit.</summary>
    private static AlarmLimit? Condition(AlarmDefinition d, AlarmLimit? active, double v)
    {
        bool Over(double? lim, AlarmLimit kind) =>
            lim is { } x && (v >= x || (active is { } a && Side(a) == 1 && Severity(a) >= Severity(kind) && v > x - d.Deadband));
        bool Under(double? lim, AlarmLimit kind) =>
            lim is { } x && (v <= x || (active is { } a && Side(a) == -1 && Severity(a) >= Severity(kind) && v < x + d.Deadband));
        if (Over(d.HighHigh, AlarmLimit.HighHigh)) return AlarmLimit.HighHigh;
        if (Under(d.LowLow, AlarmLimit.LowLow)) return AlarmLimit.LowLow;
        if (Over(d.High, AlarmLimit.High)) return AlarmLimit.High;
        if (Under(d.Low, AlarmLimit.Low)) return AlarmLimit.Low;
        return null;
    }

    private static void Transition(Alarm a, AlarmState to, double value, DateTimeOffset ts, List<AlarmEvent> events, string message, string? user = null)
    {
        var from = a.State;
        a.State = to;
        events.Add(new AlarmEvent(a.Tag, from, to, a.LastLimit, value, a.Priority, $"{a.Tag}: {message}", ts, user));
    }

    private void Publish(List<AlarmEvent> events)
    {
        foreach (var e in events)
        {
            AlarmChanged?.Invoke(e);
            bus?.Publish(e);
        }
    }

    private sealed class Detach(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() => Interlocked.Exchange(ref _a, null)?.Invoke();
    }
}
