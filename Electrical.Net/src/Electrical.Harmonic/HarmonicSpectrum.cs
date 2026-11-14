using System.Numerics;
using Electrical.Core.Units;

namespace Electrical.Harmonic;

/// <summary>
/// Harmonic spectrum of a periodic quantity: RMS magnitude and phase per harmonic order h = 0 (DC) … <see cref="MaxOrder"/>.
/// Units follow the input (V, A, or pu). Phases are in degrees on a cosine reference.
/// </summary>
public sealed class HarmonicSpectrum
{
    private readonly double[] _rms;
    private readonly double[] _phase;

    public HarmonicSpectrum(double fundamentalHz, double[] rmsByOrder, double[]? phaseDegByOrder = null)
    {
        if (rmsByOrder.Length < 2) throw new ArgumentException("Spectrum needs at least the DC and fundamental terms.", nameof(rmsByOrder));
        FundamentalHz = fundamentalHz;
        _rms = rmsByOrder;
        _phase = phaseDegByOrder ?? new double[rmsByOrder.Length];
    }

    public double FundamentalHz { get; }
    public int MaxOrder => _rms.Length - 1;
    /// <summary>RMS magnitude of order h (h = 0 is the DC value, signed values are made absolute).</summary>
    public double this[int order] => order < _rms.Length ? _rms[order] : 0;
    public double Phase(int order) => order < _phase.Length ? _phase[order] : 0;
    public IReadOnlyList<double> Rms => _rms;
    public double Fundamental => _rms[1];

    /// <summary>
    /// Spectrum from uniformly sampled data via FFT. For leakage-free results the record should span an integer number of fundamental cycles.
    /// </summary>
    /// <param name="samples">Instantaneous values.</param>
    /// <param name="sampleRateHz">Sampling frequency [Hz].</param>
    /// <param name="fundamentalHz">Fundamental frequency [Hz].</param>
    /// <param name="maxOrder">Highest harmonic order to extract (limited by Nyquist).</param>
    public static HarmonicSpectrum FromSamples(ReadOnlySpan<double> samples, double sampleRateHz, double fundamentalHz, int maxOrder = 50)
    {
        int n = samples.Length;
        if (n < 4) throw new ArgumentException("Too few samples.", nameof(samples));
        var x = Fft.Forward(samples);
        double binsPerHarmonic = fundamentalHz * n / sampleRateHz;
        int hMax = Math.Min(maxOrder, (int)Math.Floor((n / 2 - 1) / binsPerHarmonic));
        var rms = new double[hMax + 1];
        var ph = new double[hMax + 1];
        rms[0] = Math.Abs(x[0].Real / n);
        for (int h = 1; h <= hMax; h++)
        {
            int k = (int)Math.Round(h * binsPerHarmonic);
            var c = x[k];
            rms[h] = 2 * c.Magnitude / n / Math.Sqrt(2);
            ph[h] = c.Phase * 180 / Math.PI;
        }
        return new HarmonicSpectrum(fundamentalHz, rms, ph);
    }

    /// <summary>Spectrum from a fundamental RMS value and harmonic magnitudes in percent of the fundamental.</summary>
    public static HarmonicSpectrum FromPercent(double fundamentalHz, double fundamentalRms, IEnumerable<(int Order, double Percent, double PhaseDeg)> harmonics)
    {
        var list = harmonics.ToList();
        int hMax = Math.Max(1, list.Count == 0 ? 1 : list.Max(h => h.Order));
        var rms = new double[hMax + 1];
        var ph = new double[hMax + 1];
        rms[1] = fundamentalRms;
        foreach (var (h, p, a) in list) { rms[h] = fundamentalRms * p / 100; ph[h] = a; }
        return new HarmonicSpectrum(fundamentalHz, rms, ph);
    }

    /// <summary>Total RMS value √(ΣI_h²) including DC.</summary>
    public double TotalRms => Math.Sqrt(_rms.Sum(v => v * v));

    /// <summary>RMS of the harmonic content √(Σ_{h≥2} I_h²).</summary>
    public double HarmonicRms => Math.Sqrt(_rms.Skip(2).Sum(v => v * v));

    /// <summary>Total harmonic distortion THD = √(Σ_{h≥2} X_h²) / X_1 [%].</summary>
    public double ThdPercent => Fundamental > 0 ? 100 * HarmonicRms / Fundamental : 0;

    /// <summary>Total demand distortion TDD = √(Σ_{h≥2} I_h²) / I_L [%] (IEEE 519), I_L = maximum demand load current (same unit).</summary>
    public double TddPercent(double maximumDemandCurrent) => 100 * HarmonicRms / maximumDemandCurrent;

    /// <summary>Individual harmonic in percent of the fundamental.</summary>
    public double IndividualPercent(int order) => Fundamental > 0 ? 100 * this[order] / Fundamental : 0;

    /// <summary>Transformer K-factor (UL 1561) K = Σ (I_h/I_rms)²·h², h ≥ 1.</summary>
    public double KFactor
    {
        get
        {
            double num = 0, den = 0;
            for (int h = 1; h <= MaxOrder; h++) { num += _rms[h] * _rms[h] * h * h; den += _rms[h] * _rms[h]; }
            return den > 0 ? num / den : 1;
        }
    }

    /// <summary>Reconstructs <paramref name="count"/> samples at <paramref name="sampleRateHz"/>: x(t) = DC + Σ √2·X_h·cos(2πhft + φ_h).</summary>
    public double[] Synthesize(double sampleRateHz, int count)
    {
        var y = new double[count];
        for (int k = 0; k < count; k++)
        {
            double t = k / sampleRateHz, s = _rms[0];
            for (int h = 1; h <= MaxOrder; h++)
                if (_rms[h] != 0) s += Math.Sqrt(2) * _rms[h] * Math.Cos(2 * Math.PI * h * FundamentalHz * t + _phase[h] * Math.PI / 180);
            y[k] = s;
        }
        return y;
    }
}

/// <summary>Waveform and power-quality indices.</summary>
public static class PowerQuality
{
    /// <summary>RMS of samples.</summary>
    public static double Rms(ReadOnlySpan<double> samples)
    {
        double s = 0;
        foreach (var v in samples) s += v * v;
        return Math.Sqrt(s / samples.Length);
    }

    /// <summary>Crest factor = peak / RMS (√2 for a sine).</summary>
    public static double CrestFactor(ReadOnlySpan<double> samples)
    {
        double peak = 0;
        foreach (var v in samples) peak = Math.Max(peak, Math.Abs(v));
        return peak / Rms(samples);
    }

    /// <summary>Voltage unbalance factor VUF = |V2|/|V1| [%] (IEC 61000-4-30, via <see cref="ThreePhase.UnbalanceFactor"/>).</summary>
    public static double VoltageUnbalancePercent(ThreePhase voltages) => 100 * voltages.UnbalanceFactor();

    /// <summary>NEMA MG-1 line-voltage unbalance rate LVUR = max deviation from mean / mean [%] (magnitudes only).</summary>
    public static double NemaUnbalancePercent(double vab, double vbc, double vca)
    {
        double avg = (vab + vbc + vca) / 3;
        return 100 * Math.Max(Math.Abs(vab - avg), Math.Max(Math.Abs(vbc - avg), Math.Abs(vca - avg))) / avg;
    }

    /// <summary>
    /// Motor derating factor for voltage unbalance per NEMA MG-1 Fig. 14-1 (1.0 up to 1 %, ≈0.95 at 2 %, 0.88 at 3 %, 0.82 at 4 %, 0.75 at 5 %).
    /// </summary>
    public static double NemaDerating(double unbalancePercent)
    {
        (double u, double f)[] t = [(0, 1.0), (1, 1.0), (2, 0.95), (3, 0.88), (4, 0.82), (5, 0.75)];
        if (unbalancePercent >= 5) return 0.75;
        for (int i = 1; i < t.Length; i++)
            if (unbalancePercent <= t[i].u)
                return t[i - 1].f + (t[i].f - t[i - 1].f) * (unbalancePercent - t[i - 1].u) / (t[i].u - t[i - 1].u);
        return 0.75;
    }

    /// <summary>Phasor (complex RMS) of a harmonic from a spectrum.</summary>
    public static Complex Phasor(HarmonicSpectrum s, int order) => Complex.FromPolarCoordinates(s[order], s.Phase(order) * Math.PI / 180);
}
