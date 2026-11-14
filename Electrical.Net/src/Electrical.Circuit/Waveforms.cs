namespace Electrical.Circuit;

/// <summary>Time function of an independent source (value in V or A, time in s).</summary>
public abstract class Waveform
{
    /// <summary>Source value at time <paramref name="t"/> [s].</summary>
    public abstract double Value(double t);

    /// <summary>Constant value.</summary>
    public static Waveform Dc(double value) => new DcWaveform(value);

    /// <summary>SPICE SIN: offset + amplitude·e^(−θ(t−td))·sin(2πf(t−td) + φ) for t ≥ td, offset + amplitude·sin φ before.</summary>
    /// <param name="offset">DC offset.</param>
    /// <param name="amplitude">Peak amplitude.</param>
    /// <param name="frequencyHz">Frequency [Hz].</param>
    /// <param name="delayS">Delay td [s].</param>
    /// <param name="damping">Damping factor θ [1/s].</param>
    /// <param name="phaseDeg">Phase φ [degrees].</param>
    public static Waveform Sine(double offset, double amplitude, double frequencyHz, double delayS = 0, double damping = 0, double phaseDeg = 0) =>
        new SineWaveform(offset, amplitude, frequencyHz, delayS, damping, phaseDeg);

    /// <summary>SPICE PULSE(v1 v2 td tr tf pw per). A period of 0 means a single pulse.</summary>
    public static Waveform Pulse(double v1, double v2, double delayS = 0, double riseS = 1e-9, double fallS = 1e-9, double widthS = double.PositiveInfinity,
        double periodS = 0) => new PulseWaveform(v1, v2, delayS, riseS, fallS, widthS, periodS);

    /// <summary>Piece-wise linear (t, value) points; held constant outside the range.</summary>
    public static Waveform PiecewiseLinear(params (double TimeS, double Value)[] points) => new PwlWaveform(points);

    public static implicit operator Waveform(double value) => Dc(value);
}

internal sealed class DcWaveform(double v) : Waveform
{
    public override double Value(double t) => v;
    public override string ToString() => $"DC {v}";
}

internal sealed class SineWaveform(double vo, double va, double f, double td, double theta, double phaseDeg) : Waveform
{
    public override double Value(double t)
    {
        double ph = phaseDeg * Math.PI / 180;
        if (t < td) return vo + va * Math.Sin(ph);
        double tt = t - td;
        return vo + va * Math.Exp(-theta * tt) * Math.Sin(2 * Math.PI * f * tt + ph);
    }
    public override string ToString() => $"SIN({vo} {va} {f})";
}

internal sealed class PulseWaveform(double v1, double v2, double td, double tr, double tf, double pw, double per) : Waveform
{
    public override double Value(double t)
    {
        if (t < td) return v1;
        double tt = t - td;
        if (per > 0) tt %= per;
        if (tt < tr) return tr <= 0 ? v2 : v1 + (v2 - v1) * tt / tr;
        tt -= tr;
        if (tt < pw) return v2;
        tt -= pw;
        if (tt < tf) return tf <= 0 ? v1 : v2 + (v1 - v2) * tt / tf;
        return v1;
    }
    public override string ToString() => $"PULSE({v1} {v2} {td} {tr} {tf} {pw} {per})";
}

internal sealed class PwlWaveform : Waveform
{
    private readonly (double T, double V)[] _p;
    public PwlWaveform((double, double)[] points)
    {
        if (points.Length == 0) throw new ArgumentException("PWL needs at least one point.");
        _p = points.OrderBy(p => p.Item1).ToArray();
    }
    public override double Value(double t)
    {
        if (t <= _p[0].T) return _p[0].V;
        for (int k = 1; k < _p.Length; k++)
            if (t <= _p[k].T)
            {
                double w = (t - _p[k - 1].T) / (_p[k].T - _p[k - 1].T);
                return _p[k - 1].V + w * (_p[k].V - _p[k - 1].V);
            }
        return _p[^1].V;
    }
}
