using System.Numerics;

namespace Electrical.Pmu;

/// <summary>Performance class of IEEE C37.118.1: P (protection, fast) or M (measurement, anti-aliased).</summary>
public enum PmuClass { P, M }

/// <summary>Settings of a <see cref="PhasorEstimator"/>.</summary>
public sealed record PhasorEstimatorOptions
{
    /// <summary>Nominal frequency f₀ [Hz].</summary>
    public double NominalFrequencyHz { get; init; } = 50;
    /// <summary>Waveform sampling rate [Hz]; must be an integer multiple of f₀ (e.g. 50 Hz × 20 = 1000 Hz, 60 Hz × 16 = 960 Hz).</summary>
    public double SampleRateHz { get; init; } = 1000;
    /// <summary>Reporting rate Fs [frames/s].</summary>
    public double ReportingRate { get; init; } = 50;
    public PmuClass Class { get; init; } = PmuClass.P;
    /// <summary>M class: reference filter frequency F_fr [Hz] (default ≈ 0.1365·Fs, the C37.118.1 Table C.1 value 8.19 Hz at Fs = 60).</summary>
    public double? FilterFrequencyHz { get; init; }
    /// <summary>M class: filter order N (even; default ≈ 10.25 nominal cycles × 60/Fs, the Table C.1 value 164 at 960 Hz, Fs = 60).</summary>
    public int? FilterOrder { get; init; }
    /// <summary>
    /// Divide the magnitude by the filter gain at the estimated frequency deviation (the reference model's magnitude compensation,
    /// computed exactly from the filter weights).
    /// </summary>
    public bool MagnitudeCompensation { get; init; } = true;
}

/// <summary>One estimate: time of the window centre [s], RMS phasor (angle vs. the UTC-synchronised nominal cosine), frequency [Hz], ROCOF [Hz/s].</summary>
public readonly record struct PhasorEstimate(double TimeS, Complex Phasor, double FrequencyHz, double RocofHzPerS)
{
    public double Magnitude => Phasor.Magnitude;
    public double AngleDeg => Phasor.Phase * 180 / Math.PI;
}

/// <summary>
/// Synchrophasor estimation per the IEEE C37.118.1-2011 Annex C reference model: quadrature demodulation at f₀ with a symmetric FIR
/// low-pass filter W(k) — P class: 2-cycle triangle W(k) = 1 − 2|k|/(N+2), N = 2(fs/f₀ − 1); M class: Hamming-windowed sinc with
/// F_fr. X(t) = √2/ΣW · Σ x(t + kΔt)·W(k)·e^(−jω₀(t + kΔt)), i.e. the RMS phasor of x = Xm·cos(ω₀t + φ).
/// Frequency and ROCOF come from first/second differences of the phasor angle over ± half a nominal cycle (which also cancels most of the
/// 2f₀ ripple of single-phase estimates); the positive-sequence variant removes that ripple entirely for balanced signals.
/// </summary>
public sealed class PhasorEstimator
{
    private readonly double[] _w;
    private readonly double _gain, _dt, _w0;
    private readonly int _half, _h;

    public PhasorEstimator(PhasorEstimatorOptions? options = null)
    {
        Options = options ?? new PhasorEstimatorOptions();
        var o = Options;
        double spc = o.SampleRateHz / o.NominalFrequencyHz;
        if (Math.Abs(spc - Math.Round(spc)) > 1e-9 || spc < 4) throw new ArgumentException("SampleRateHz must be an integer multiple (≥ 4) of NominalFrequencyHz.");
        int samplesPerCycle = (int)Math.Round(spc);
        _dt = 1 / o.SampleRateHz;
        _w0 = 2 * Math.PI * o.NominalFrequencyHz;
        int n;
        if (o.Class == PmuClass.P)
        {
            n = 2 * (samplesPerCycle - 1);
            _half = n / 2;
            _w = new double[n + 1];
            for (int k = -_half; k <= _half; k++) _w[k + _half] = 1 - 2.0 * Math.Abs(k) / (n + 2);
        }
        else
        {
            double ffr = o.FilterFrequencyHz ?? 0.1365 * o.ReportingRate;
            n = o.FilterOrder ?? 2 * (int)Math.Round(10.25 * 60 / o.ReportingRate * samplesPerCycle / 2);
            if (n % 2 != 0) n++;
            _half = n / 2;
            _w = new double[n + 1];
            double a = 2 * Math.PI * 2 * ffr / o.SampleRateHz;
            for (int k = -_half; k <= _half; k++)
            {
                double s = k == 0 ? 1 : Math.Sin(a * k) / (a * k);
                _w[k + _half] = s * (0.54 + 0.46 * Math.Cos(2 * Math.PI * k / n));
            }
        }
        _gain = _w.Sum();
        _h = samplesPerCycle / 2;
    }

    public PhasorEstimatorOptions Options { get; }
    /// <summary>Filter weights W(−N/2 … N/2).</summary>
    public IReadOnlyList<double> Weights => _w;
    /// <summary>Filter order N (window = N + 1 samples).</summary>
    public int FilterOrder => _w.Length - 1;
    /// <summary>Samples needed on each side of a reporting instant (filter half length + frequency baseline).</summary>
    public int Margin => _half + _h;

    /// <summary>Normalised filter gain at a frequency deviation Δf [Hz] (1 at Δf = 0).</summary>
    public double FilterGain(double deltaHz)
    {
        double s = 0;
        for (int k = -_half; k <= _half; k++) s += _w[k + _half] * Math.Cos(2 * Math.PI * deltaHz * k * _dt);
        return s / _gain;
    }

    /// <summary>Uncompensated filtered phasor with the window centred on sample <paramref name="center"/>; sample n is at t₀ + n/fs.</summary>
    public Complex RawPhasor(ReadOnlySpan<double> x, int center, double t0S)
    {
        if (center - _half < 0 || center + _half >= x.Length) throw new ArgumentOutOfRangeException(nameof(center), "Window exceeds the sample buffer.");
        double re = 0, im = 0;
        for (int k = -_half; k <= _half; k++)
        {
            int i = center + k;
            double t = t0S + i * _dt;
            double v = x[i] * _w[k + _half];
            re += v * Math.Cos(_w0 * t);
            im -= v * Math.Sin(_w0 * t);
        }
        return new Complex(re, im) * (Math.Sqrt(2) / _gain);
    }

    /// <summary>Estimate (phasor, frequency, ROCOF) centred on sample <paramref name="center"/>.</summary>
    public PhasorEstimate EstimateAt(ReadOnlySpan<double> x, int center, double t0S)
    {
        var xm = RawPhasor(x, center - _h, t0S);
        var xc = RawPhasor(x, center, t0S);
        var xp = RawPhasor(x, center + _h, t0S);
        return Finish(xm, xc, xp, t0S + center * _dt);
    }

    /// <summary>Positive-sequence estimate X₁ = (Xa + a·Xb + a²·Xc)/3 centred on sample <paramref name="center"/>.</summary>
    public PhasorEstimate EstimatePositiveSequenceAt(ReadOnlySpan<double> a, ReadOnlySpan<double> b, ReadOnlySpan<double> c, int center, double t0S)
    {
        var op = Complex.FromPolarCoordinates(1, 2 * Math.PI / 3);
        Complex Pos(ReadOnlySpan<double> xa, ReadOnlySpan<double> xb, ReadOnlySpan<double> xc, int n) =>
            (RawPhasor(xa, n, t0S) + op * RawPhasor(xb, n, t0S) + op * op * RawPhasor(xc, n, t0S)) / 3;
        return Finish(Pos(a, b, c, center - _h), Pos(a, b, c, center), Pos(a, b, c, center + _h), t0S + center * _dt);
    }

    private PhasorEstimate Finish(Complex xm, Complex xc, Complex xp, double t)
    {
        double tau = _h * _dt;
        double d1 = (xp * Complex.Conjugate(xc)).Phase, d0 = (xc * Complex.Conjugate(xm)).Phase;
        double df = (d1 + d0) / (2 * Math.PI * 2 * tau);
        double rocof = (d1 - d0) / (2 * Math.PI * tau * tau);
        var x = xc;
        if (Options.MagnitudeCompensation)
        {
            double g = FilterGain(df);
            if (g > 0.1) x /= g;
        }
        return new PhasorEstimate(t, x, Options.NominalFrequencyHz + df, rocof);
    }

    /// <summary>
    /// Estimates at every reporting instant (integer multiples of 1/Fs in absolute time) whose full window lies inside the buffer.
    /// </summary>
    public IReadOnlyList<PhasorEstimate> Estimate(ReadOnlySpan<double> samples, double t0S)
    {
        var list = new List<PhasorEstimate>();
        foreach (int c in ReportingCenters(samples.Length, t0S)) list.Add(EstimateAt(samples, c, t0S));
        return list;
    }

    /// <summary>Positive-sequence estimates at every reporting instant.</summary>
    public IReadOnlyList<PhasorEstimate> EstimatePositiveSequence(ReadOnlySpan<double> a, ReadOnlySpan<double> b, ReadOnlySpan<double> c, double t0S)
    {
        int len = Math.Min(a.Length, Math.Min(b.Length, c.Length));
        var list = new List<PhasorEstimate>();
        foreach (int n in ReportingCenters(len, t0S)) list.Add(EstimatePositiveSequenceAt(a, b, c, n, t0S));
        return list;
    }

    private List<int> ReportingCenters(int length, double t0S)
    {
        var centers = new List<int>();
        double fs = Options.SampleRateHz, rr = Options.ReportingRate;
        long first = (long)Math.Ceiling((t0S + Margin * _dt) * rr - 1e-9);
        for (long m = first; ; m++)
        {
            int c = (int)Math.Round((m / rr - t0S) * fs);
            if (c + Margin >= length) break;
            if (c - Margin >= 0) centers.Add(c);
        }
        return centers;
    }

    /// <summary>Total vector error |X̂ − X| / |X| (fraction; ×100 for %).</summary>
    public static double Tve(Complex estimate, Complex reference) => (estimate - reference).Magnitude / reference.Magnitude;

    /// <summary>
    /// Samples x(t) = √2·|X|·cos(2πf·t + ∠X) at <paramref name="sampleRateHz"/> starting at <paramref name="t0S"/>
    /// (test-signal helper; <paramref name="phasorRms"/> is the RMS phasor at t = 0 relative to a cosine at f).
    /// </summary>
    public static double[] Sinusoid(Complex phasorRms, double frequencyHz, double sampleRateHz, int count, double t0S = 0)
    {
        var x = new double[count];
        double a = Math.Sqrt(2) * phasorRms.Magnitude, ph = phasorRms.Phase;
        for (int i = 0; i < count; i++) x[i] = a * Math.Cos(2 * Math.PI * frequencyHz * (t0S + i / sampleRateHz) + ph);
        return x;
    }
}
