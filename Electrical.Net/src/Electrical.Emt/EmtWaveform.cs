namespace Electrical.Emt;

/// <summary>Time function of a source: value (V or A) as a function of simulation time t (s).</summary>
public sealed class EmtWaveform
{
    private readonly Func<double, double> _f;

    private EmtWaveform(Func<double, double> f, string description)
    {
        _f = f;
        Description = description;
    }

    /// <summary>Human-readable description, e.g. "sine 100 V 50 Hz 0°".</summary>
    public string Description { get; }

    /// <summary>Value at time <paramref name="t"/> (s).</summary>
    public double At(double t) => _f(t);

    /// <summary>Constant value from t = 0 (a step applied at t = 0⁺ to the de-energized network).</summary>
    public static EmtWaveform Dc(double value) => new(_ => value, $"dc {value}");

    /// <summary>Step of <paramref name="value"/> applied at <paramref name="startTime"/> (s); zero before.</summary>
    public static EmtWaveform Step(double value, double startTime = 0) => new(t => t >= startTime ? value : 0.0, $"step {value} @ {startTime} s");

    /// <summary>
    /// Sinusoid <c>amplitude·cos(2πf·t + φ)</c> (peak value, Hz, degrees). With the cosine convention a phasor
    /// <c>amplitude∠φ</c> corresponds exactly to this waveform. Zero before <paramref name="startTime"/> (s).
    /// </summary>
    public static EmtWaveform Cosine(double amplitude, double frequencyHz, double phaseDeg = 0, double startTime = 0)
    {
        double w = 2 * Math.PI * frequencyHz, ph = phaseDeg * Math.PI / 180;
        return new(t => t >= startTime ? amplitude * Math.Cos(w * t + ph) : 0.0, $"cos {amplitude} {frequencyHz} Hz {phaseDeg}°");
    }

    /// <summary>Sinusoid <c>amplitude·sin(2πf·t + φ)</c> (peak value, Hz, degrees); zero before <paramref name="startTime"/> (s).</summary>
    public static EmtWaveform Sine(double amplitude, double frequencyHz, double phaseDeg = 0, double startTime = 0)
    {
        double w = 2 * Math.PI * frequencyHz, ph = phaseDeg * Math.PI / 180;
        return new(t => t >= startTime ? amplitude * Math.Sin(w * t + ph) : 0.0, $"sin {amplitude} {frequencyHz} Hz {phaseDeg}°");
    }

    /// <summary>Linear ramp from 0 at <paramref name="startTime"/> to <paramref name="value"/> after <paramref name="riseTime"/> (s).</summary>
    public static EmtWaveform Ramp(double value, double riseTime, double startTime = 0) =>
        new(t => t < startTime ? 0.0 : t >= startTime + riseTime ? value : value * (t - startTime) / riseTime, $"ramp {value} in {riseTime} s");

    /// <summary>Arbitrary deterministic function of time (s).</summary>
    public static EmtWaveform Custom(Func<double, double> f, string description = "custom")
    {
        ArgumentNullException.ThrowIfNull(f);
        return new(f, description);
    }

    public override string ToString() => Description;
}
