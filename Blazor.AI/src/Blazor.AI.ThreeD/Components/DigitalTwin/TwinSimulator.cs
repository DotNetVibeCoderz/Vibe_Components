using System.Globalization;
using System.Text;

namespace Blazor.AI.ThreeD;

/// <summary>Sensor behaviour: a baseline with noise, slow drift and optional daily cycle.</summary>
public sealed record SensorSpec(string Id, string Name, string Unit, double Baseline, double Noise, double Min, double Max)
{
    /// <summary>Change per tick (degradation, growth).</summary>
    public double Drift { get; init; }

    /// <summary>Amplitude of a sinusoidal cycle (e.g. day/night), period in ticks.</summary>
    public double CycleAmplitude { get; init; }

    public int CyclePeriod { get; init; } = 120;

    /// <summary>Readings above this are a warning (null: no upper limit).</summary>
    public double? WarnHigh { get; init; }

    /// <summary>Readings below this are a warning (null: no lower limit).</summary>
    public double? WarnLow { get; init; }

    /// <summary>Asset the sensor belongs to (machine, room, zone, pond, road).</summary>
    public string? Asset { get; init; }
}

/// <summary>A live sensor with history, EWMA baseline and anomaly score.</summary>
public sealed class Sensor(SensorSpec spec)
{
    private readonly Queue<double> _history = new();
    private double _ewma = spec.Baseline;
    private double _ewvar = spec.Noise * spec.Noise;
    private double _drift;
    private double _fault;
    private int _faultTicks;

    public SensorSpec Spec { get; } = spec;

    public double Value { get; private set; } = spec.Baseline;

    /// <summary>|z| of the latest reading against the exponentially weighted mean and variance.</summary>
    public double ZScore { get; private set; }

    public IReadOnlyCollection<double> History => _history;

    public bool IsAnomalous => ZScore > 4.5;

    public bool IsWarning => (Spec.WarnHigh is { } hi && Value > hi) || (Spec.WarnLow is { } lo && Value < lo);

    /// <summary>0 = healthy, 1 = warning, 2 = anomalous.</summary>
    public int Level => IsAnomalous ? 2 : IsWarning ? 1 : 0;

    /// <summary>Adds a step fault of <paramref name="magnitude"/> for <paramref name="ticks"/> ticks.</summary>
    public void InjectFault(double magnitude, int ticks)
    {
        _fault = magnitude;
        _faultTicks = ticks;
    }

    /// <summary>Applies an external offset (control action, e.g. irrigation or aeration).</summary>
    public void Nudge(double delta) => Value = Math.Clamp(Value + delta, Spec.Min, Spec.Max);

    internal void Step(int tick, Random rng, double externalOffset)
    {
        _drift += Spec.Drift;
        var cycle = Spec.CycleAmplitude == 0 ? 0 : Spec.CycleAmplitude * Math.Sin(2 * Math.PI * tick / Spec.CyclePeriod);
        var fault = _faultTicks-- > 0 ? _fault : 0;
        var target = Spec.Baseline + _drift + cycle + fault + externalOffset;

        // Mean-reverting walk keeps values smooth instead of white noise.
        Value = Math.Clamp(Value + (target - Value) * 0.35 + Gaussian(rng) * Spec.Noise, Spec.Min, Spec.Max);

        var sd = Math.Max(Math.Sqrt(Math.Max(_ewvar, 1e-9)), Spec.Noise * 1.5);
        ZScore = Math.Abs(Value - _ewma) / sd;
        // Learn slowly while anomalous so a sustained fault stays flagged instead of becoming the new normal.
        var alpha = ZScore > 4.5 ? 0.01 : 0.08;
        var diff = Value - _ewma;
        _ewma += alpha * diff;
        _ewvar = (1 - alpha) * (_ewvar + alpha * diff * diff);

        _history.Enqueue(Value);
        if (_history.Count > 240)
        {
            _history.Dequeue();
        }
    }

    /// <summary>Least-squares slope per tick over the last <paramref name="window"/> readings.</summary>
    public double Slope(int window = 40)
    {
        var y = _history.TakeLast(window).ToArray();
        if (y.Length < 3)
        {
            return 0;
        }

        var meanX = (y.Length - 1) / 2.0;
        var meanY = y.Average();
        double num = 0, den = 0;
        for (var i = 0; i < y.Length; i++)
        {
            num += (i - meanX) * (y[i] - meanY);
            den += (i - meanX) * (i - meanX);
        }

        return den == 0 ? 0 : num / den;
    }

    /// <summary>Linear extrapolation <paramref name="ticks"/> ahead.</summary>
    public double Forecast(int ticks, int window = 40) => Math.Clamp(Value + Slope(window) * ticks, Spec.Min, Spec.Max);

    /// <summary>Ticks until the trend crosses <paramref name="limit"/>; null when it never does.</summary>
    public double? TicksUntil(double limit, int window = 40)
    {
        var slope = Slope(window);
        if (Math.Abs(slope) < 1e-9 || Math.Sign(limit - Value) != Math.Sign(slope))
        {
            return null;
        }

        return (limit - Value) / slope;
    }

    /// <summary>The value without unit.</summary>
    public string Short() => Value.ToString(Math.Abs(Value) >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);

    public string Format() => Value.ToString(Spec.Unit is "%" or "" ? "0.0" : Math.Abs(Value) >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + (Spec.Unit.Length > 0 ? " " + Spec.Unit : string.Empty);

    internal static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
}

/// <summary>A notable event in the twin (fault, alert, action).</summary>
public sealed record TwinEvent(int Tick, string Severity, string Message, string? Asset = null);

/// <summary>
/// Time-stepped sensor simulator for digital twins: noise, drift, cycles and injected faults, with EWMA z-score anomaly
/// detection and linear trend forecasting. Deterministic for a given seed. Not thread-safe; step it from one timer.
/// </summary>
public sealed class TwinSimulator
{
    private readonly Random _rng;
    private readonly Dictionary<string, Sensor> _sensors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TwinEvent> _events = [];

    public TwinSimulator(IEnumerable<SensorSpec> sensors, int seed = 42)
    {
        _rng = new Random(seed);
        foreach (var s in sensors)
        {
            _sensors[s.Id] = new Sensor(s);
        }
    }

    public int Tick { get; private set; }

    public IReadOnlyCollection<Sensor> Sensors => _sensors.Values;

    public IReadOnlyList<TwinEvent> Events => _events;

    public Random Random => _rng;

    public Sensor this[string id] => _sensors[id];

    public bool TryGet(string id, out Sensor sensor) => _sensors.TryGetValue(id, out sensor!);

    public IEnumerable<Sensor> ForAsset(string asset) => _sensors.Values.Where(s => string.Equals(s.Spec.Asset, asset, StringComparison.OrdinalIgnoreCase));

    /// <summary>Holds a sensor <paramref name="offset"/> away from its baseline until changed (e.g. a control setpoint).</summary>
    public void SetOffset(string id, double offset) => _offsets[id] = offset;

    public void Log(string severity, string message, string? asset = null)
    {
        _events.Add(new TwinEvent(Tick, severity, message, asset));
        if (_events.Count > 60)
        {
            _events.RemoveAt(0);
        }
    }

    /// <summary>Advances every sensor one tick and logs new anomalies.</summary>
    public void Step(int ticks = 1)
    {
        for (var t = 0; t < ticks; t++)
        {
            Tick++;
            foreach (var s in _sensors.Values)
            {
                var wasAnomalous = s.IsAnomalous;
                s.Step(Tick, _rng, _offsets.GetValueOrDefault(s.Spec.Id));
                if (s.IsAnomalous && !wasAnomalous && Tick > 10)
                {
                    Log("anomaly", string.Create(CultureInfo.InvariantCulture, $"{s.Spec.Name} at {s.Format()} (z = {s.ZScore:0.0})"), s.Spec.Asset);
                }
            }
        }
    }

    /// <summary>Injects a fault into a random sensor (or the named one) and logs it.</summary>
    public Sensor InjectRandomFault(string? id = null, double sigmas = 6, int ticks = 25)
    {
        var s = id is not null && _sensors.TryGetValue(id, out var named) ? named : _sensors.Values.ElementAt(_rng.Next(_sensors.Count));
        var magnitude = Math.Max(s.Spec.Noise, (s.Spec.Max - s.Spec.Min) * 0.03) * sigmas * (s.Spec.WarnLow is not null && s.Spec.WarnHigh is null ? -1 : 1);
        s.InjectFault(magnitude, ticks);
        Log("fault", $"Fault injected: {s.Spec.Name}", s.Spec.Asset);
        return s;
    }

    /// <summary>A compact text snapshot of current readings, trends and events for the AI copilot.</summary>
    public string Snapshot(int maxEvents = 8)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"Tick {Tick}. Readings: ");
        foreach (var s in _sensors.Values)
        {
            b.Append(CultureInfo.InvariantCulture, $"{s.Spec.Name}{(s.Spec.Asset is null ? "" : $" [{s.Spec.Asset}]")} {s.Format()} (trend {s.Slope():+0.###;-0.###}/tick{(s.IsAnomalous ? ", ANOMALY" : s.IsWarning ? ", warning" : "")}); ");
        }

        if (_events.Count > 0)
        {
            b.Append("Recent events: ").AppendJoin("; ", _events.TakeLast(maxEvents).Select(e => $"t{e.Tick} {e.Severity}: {e.Message}"));
        }

        return b.ToString();
    }
}

/// <summary>Shared helpers for the digital-twin components.</summary>
public static class TwinUi
{
    public static string LevelColor(int level) => level switch
    {
        2 => ThreeDPalette.Danger,
        1 => ThreeDPalette.Warning,
        _ => ThreeDPalette.Nila,
    };

    public static string Pct(double v) => v.ToString("P0", CultureInfo.InvariantCulture);

    public static string N(double v, string format = "0.0") => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Local, offline answer for the copilot: cites the anomalous and warning sensors and their trends.</summary>
    public static string LocalDiagnosis(TwinSimulator sim, string question)
    {
        var bad = sim.Sensors.Where(s => s.Level > 0).OrderByDescending(s => s.ZScore).Take(4).ToList();
        var q = question.ToLowerInvariant();
        var focus = sim.Sensors.Where(s => (s.Spec.Asset is { } a && q.Contains(a.ToLowerInvariant())) || q.Contains(s.Spec.Name.ToLowerInvariant())).ToList();
        var subject = focus.Count > 0 ? focus : bad;
        if (subject.Count == 0)
        {
            return "All readings are within normal ranges and no anomalies are active. Keep monitoring; the trends are flat.";
        }

        var b = new StringBuilder();
        foreach (var s in subject.Take(4))
        {
            b.Append(CultureInfo.InvariantCulture, $"- **{s.Spec.Name}**{(s.Spec.Asset is null ? "" : $" ({s.Spec.Asset})")} is {s.Format()}, trend {s.Slope():+0.###;-0.###} per tick{(s.IsAnomalous ? $", anomalous (z {s.ZScore:0.0})" : s.IsWarning ? ", outside the warning band" : "")}.\n");
        }

        b.Append("\nMost likely cause: the sensor").Append(subject.Count > 1 ? "s above deviate" : " above deviates").Append(" from the learned baseline. Inspect that asset first, then compare with its neighbours.");
        return b.ToString();
    }
}
