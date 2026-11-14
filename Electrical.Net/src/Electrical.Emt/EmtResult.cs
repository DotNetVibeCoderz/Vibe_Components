namespace Electrical.Emt;

/// <summary>
/// Uniformly sampled output channel (node voltage in V or branch current in A). Sample k is at time
/// <c>StartTime + k·TimeStep</c> (s).
/// </summary>
public sealed class EmtChannel
{
    public EmtChannel(string name, string unit, double startTime, double timeStep, double[] values)
    {
        if (timeStep <= 0) throw new ArgumentOutOfRangeException(nameof(timeStep));
        Name = name; Unit = unit; StartTime = startTime; TimeStep = timeStep; Values = values;
    }

    public string Name { get; }

    /// <summary>"V" or "A".</summary>
    public string Unit { get; }

    /// <summary>Time of the first sample (s).</summary>
    public double StartTime { get; }

    /// <summary>Sampling interval (s).</summary>
    public double TimeStep { get; }

    public double[] Values { get; }

    public int Count => Values.Length;

    public double this[int index] => Values[index];

    /// <summary>Time of sample <paramref name="index"/> (s).</summary>
    public double TimeAt(int index) => StartTime + index * TimeStep;

    /// <summary>Linearly interpolated value at time <paramref name="t"/> (s), clamped to the record.</summary>
    public double At(double t)
    {
        double p = (t - StartTime) / TimeStep;
        if (p <= 0) return Values[0];
        if (p >= Count - 1) return Values[^1];
        int i = (int)p;
        double f = p - i;
        return Values[i] + f * (Values[i + 1] - Values[i]);
    }

    /// <summary>Samples with t0 ≤ t ≤ t1 (s).</summary>
    public ReadOnlySpan<double> Window(double t0 = double.NegativeInfinity, double t1 = double.PositiveInfinity)
    {
        int i0 = Math.Max(0, (int)Math.Ceiling((t0 - StartTime) / TimeStep - 1e-9));
        int i1 = Math.Min(Count - 1, (int)Math.Floor((t1 - StartTime) / TimeStep + 1e-9));
        return i1 < i0 ? [] : Values.AsSpan(i0, i1 - i0 + 1);
    }

    /// <summary>RMS over [t0, t1) (s) — use an integer number of cycles for AC quantities.</summary>
    public double Rms(double t0, double t1)
    {
        var w = Window(t0, t1);
        if (w.Length > 1) w = w[..^1]; // half-open window: n samples per cycle
        double s = 0;
        foreach (var v in w) s += v * v;
        return w.Length == 0 ? 0 : Math.Sqrt(s / w.Length);
    }

    /// <summary>RMS over the last <paramref name="cycles"/> cycles of a <paramref name="frequencyHz"/> signal.</summary>
    public double RmsLastCycles(double frequencyHz, int cycles = 1)
    {
        double tEnd = TimeAt(Count - 1);
        return Rms(tEnd - cycles / frequencyHz, tEnd);
    }

    /// <summary>Largest absolute value in [t0, t1] (s).</summary>
    public double Peak(double t0 = double.NegativeInfinity, double t1 = double.PositiveInfinity)
    {
        double m = 0;
        foreach (var v in Window(t0, t1)) m = Math.Max(m, Math.Abs(v));
        return m;
    }

    /// <summary>Time (s) of the largest absolute value in [t0, t1].</summary>
    public double TimeOfPeak(double t0 = double.NegativeInfinity, double t1 = double.PositiveInfinity)
    {
        int i0 = Math.Max(0, (int)Math.Ceiling((t0 - StartTime) / TimeStep - 1e-9));
        int i1 = Math.Min(Count - 1, (int)Math.Floor((t1 - StartTime) / TimeStep + 1e-9));
        int best = i0; double m = -1;
        for (int i = i0; i <= i1; i++) if (Math.Abs(Values[i]) > m) { m = Math.Abs(Values[i]); best = i; }
        return TimeAt(best);
    }

    public double Max() => Values.Max();
    public double Min() => Values.Min();

    /// <summary>Times (s) of rising and falling zero crossings in [t0, t1], linearly interpolated.</summary>
    public IReadOnlyList<double> ZeroCrossings(double t0 = double.NegativeInfinity, double t1 = double.PositiveInfinity)
    {
        var list = new List<double>();
        int i0 = Math.Max(1, (int)Math.Ceiling((t0 - StartTime) / TimeStep - 1e-9));
        int i1 = Math.Min(Count - 1, (int)Math.Floor((t1 - StartTime) / TimeStep + 1e-9));
        for (int i = i0; i <= i1; i++)
        {
            double a = Values[i - 1], b = Values[i];
            if ((a < 0 && b >= 0) || (a > 0 && b <= 0))
                list.Add(TimeAt(i - 1) + TimeStep * a / (a - b));
        }
        return list;
    }

    /// <summary>Element-wise difference of two channels with the same sampling.</summary>
    public static EmtChannel operator -(EmtChannel a, EmtChannel b)
    {
        if (a.Count != b.Count) throw new ArgumentException("Channel lengths differ.");
        var v = new double[a.Count];
        for (int i = 0; i < v.Length; i++) v[i] = a.Values[i] - b.Values[i];
        return new EmtChannel($"{a.Name}-{b.Name}", a.Unit, a.StartTime, a.TimeStep, v);
    }

    public override string ToString() => $"{Name} [{Unit}] {Count} samples @ {TimeStep:g4} s";
}

/// <summary>Result of an EMT run: recorded node voltages and branch currents plus solver statistics.</summary>
public sealed class EmtResult
{
    private readonly Dictionary<string, EmtChannel> _voltages;
    private readonly Dictionary<string, EmtChannel> _currents;

    internal EmtResult(double[] time, Dictionary<string, EmtChannel> voltages, Dictionary<string, EmtChannel> currents,
        int steps, int factorizations, string backend, TimeSpan elapsed)
    {
        Time = time; _voltages = voltages; _currents = currents; Steps = steps; Factorizations = factorizations; Backend = backend; Elapsed = elapsed;
    }

    /// <summary>Recorded sample times (s).</summary>
    public double[] Time { get; }

    /// <summary>Number of solution steps taken (CDA half-steps counted individually).</summary>
    public int Steps { get; }

    /// <summary>Number of sparse LU factorizations (1 + topology changes).</summary>
    public int Factorizations { get; }

    /// <summary>Kernel backend used: "native (Rust)" or "managed".</summary>
    public string Backend { get; }

    /// <summary>Wall-clock time of the run.</summary>
    public TimeSpan Elapsed { get; }

    public IEnumerable<string> NodeNames => _voltages.Keys;
    public IEnumerable<string> CurrentNames => _currents.Keys;

    /// <summary>Node-to-ground voltage (V).</summary>
    public EmtChannel Voltage(string node)
    {
        if (EmtNetwork.IsGround(node)) return new EmtChannel(node, "V", Time.Length > 0 ? Time[0] : 0, Time.Length > 1 ? Time[1] - Time[0] : 1, new double[Time.Length]);
        return _voltages.TryGetValue(node, out var c) ? c : throw new KeyNotFoundException($"Node '{node}' not recorded.");
    }

    /// <summary>Voltage between two nodes (V).</summary>
    public EmtChannel Voltage(string plus, string minus) => Voltage(plus) - Voltage(minus);

    /// <summary>
    /// Branch current (A). Two-terminal elements: positive From → To (sources: current delivered into the node). Multiphase elements:
    /// <c>name[p]</c> (k-end current into the element, phase p); Bergeron lines also <c>name.m[p]</c> (m-end current into the line);
    /// transformers: <c>name</c> = secondary current out of S1, <c>name.p</c> = primary current into P1.
    /// </summary>
    public EmtChannel Current(string element) =>
        _currents.TryGetValue(element, out var c) ? c : throw new KeyNotFoundException($"Current '{element}' not recorded.");

    public bool TryGetCurrent(string element, out EmtChannel? channel) => _currents.TryGetValue(element, out channel);
}
