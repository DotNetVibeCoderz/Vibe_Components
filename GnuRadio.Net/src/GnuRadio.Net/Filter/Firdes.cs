namespace GnuRadio.Filter;

/// <summary>
/// FIR filter design (GNU Radio's <c>filter.firdes</c>). Windowed-sinc designs choose the number of taps
/// from the transition width and the window's attenuation, exactly as GNU Radio does, so taps designed
/// with the same arguments match.
/// </summary>
public static class Firdes
{
    /// <summary>Number of taps a windowed-sinc design needs for the requested transition width (always odd).</summary>
    public static int ComputeTaps(double sampleRate, double transitionWidth, WindowType window, double beta)
    {
        if (transitionWidth <= 0) throw new ArgumentOutOfRangeException(nameof(transitionWidth), "Transition width must be positive.");
        var a = Window.MaxAttenuation(window, beta);
        var ntaps = (int)(a * sampleRate / (22.0 * transitionWidth));
        return (ntaps & 1) == 0 ? ntaps + 1 : ntaps;
    }

    /// <summary>Low-pass with DC gain <paramref name="gain"/>.</summary>
    public static float[] LowPass(double gain, double sampleRate, double cutoff, double transitionWidth,
        WindowType window = WindowType.Hamming, double beta = 6.76)
    {
        CheckCutoff(sampleRate, cutoff);
        var ntaps = ComputeTaps(sampleRate, transitionWidth, window, beta);
        return LowPassWithTaps(gain, sampleRate, cutoff, ntaps, window, beta);
    }

    /// <summary>Low-pass with a fixed number of taps.</summary>
    public static float[] LowPassWithTaps(double gain, double sampleRate, double cutoff, int ntaps,
        WindowType window = WindowType.Hamming, double beta = 6.76)
    {
        var w = Window.Build(window, ntaps, beta);
        var taps = new double[ntaps];
        var m = (ntaps - 1) / 2.0;
        var fwT0 = 2 * Math.PI * cutoff / sampleRate;
        for (var i = 0; i < ntaps; i++)
        {
            var n = i - m;
            taps[i] = (n == 0 ? fwT0 / Math.PI : Math.Sin(n * fwT0) / (n * Math.PI)) * w[i];
        }
        var fmax = taps.Sum();
        return Scale(taps, gain / fmax);
    }

    /// <summary>High-pass with gain <paramref name="gain"/> at fs/2.</summary>
    public static float[] HighPass(double gain, double sampleRate, double cutoff, double transitionWidth,
        WindowType window = WindowType.Hamming, double beta = 6.76)
    {
        CheckCutoff(sampleRate, cutoff);
        var ntaps = ComputeTaps(sampleRate, transitionWidth, window, beta);
        var w = Window.Build(window, ntaps, beta);
        var taps = new double[ntaps];
        var mid = (ntaps - 1) / 2;
        var fwT0 = 2 * Math.PI * cutoff / sampleRate;
        for (var i = 0; i < ntaps; i++)
        {
            var n = i - mid;
            taps[i] = (n == 0 ? 1 - fwT0 / Math.PI : -Math.Sin(n * fwT0) / (n * Math.PI)) * w[i];
        }
        var fmax = taps[mid];
        for (var n = 1; n <= mid; n++) fmax += 2 * taps[n + mid] * Math.Cos(n * Math.PI);
        return Scale(taps, gain / fmax);
    }

    /// <summary>Real band-pass between <paramref name="low"/> and <paramref name="high"/> Hz, unity gain at the band centre.</summary>
    public static float[] BandPass(double gain, double sampleRate, double low, double high, double transitionWidth,
        WindowType window = WindowType.Hamming, double beta = 6.76)
    {
        CheckBand(sampleRate, low, high);
        var ntaps = ComputeTaps(sampleRate, transitionWidth, window, beta);
        var w = Window.Build(window, ntaps, beta);
        var taps = new double[ntaps];
        var mid = (ntaps - 1) / 2;
        var fwT0 = 2 * Math.PI * low / sampleRate;
        var fwT1 = 2 * Math.PI * high / sampleRate;
        for (var i = 0; i < ntaps; i++)
        {
            var n = i - mid;
            taps[i] = (n == 0 ? (fwT1 - fwT0) / Math.PI : (Math.Sin(n * fwT1) - Math.Sin(n * fwT0)) / (n * Math.PI)) * w[i];
        }
        var fmax = taps[mid];
        for (var n = 1; n <= mid; n++) fmax += 2 * taps[n + mid] * Math.Cos(n * (fwT0 + fwT1) * 0.5);
        return Scale(taps, gain / fmax);
    }

    /// <summary>Real band-stop between <paramref name="low"/> and <paramref name="high"/> Hz, unity gain at DC.</summary>
    public static float[] BandReject(double gain, double sampleRate, double low, double high, double transitionWidth,
        WindowType window = WindowType.Hamming, double beta = 6.76)
    {
        CheckBand(sampleRate, low, high);
        var ntaps = ComputeTaps(sampleRate, transitionWidth, window, beta);
        var w = Window.Build(window, ntaps, beta);
        var taps = new double[ntaps];
        var mid = (ntaps - 1) / 2;
        var fwT0 = 2 * Math.PI * low / sampleRate;
        var fwT1 = 2 * Math.PI * high / sampleRate;
        for (var i = 0; i < ntaps; i++)
        {
            var n = i - mid;
            taps[i] = (n == 0 ? 1.0 + (fwT0 - fwT1) / Math.PI : (Math.Sin(n * fwT0) - Math.Sin(n * fwT1)) / (n * Math.PI)) * w[i];
        }
        return Scale(taps, gain / taps.Sum());
    }

    /// <summary>
    /// Complex band-pass passing [<paramref name="low"/>, <paramref name="high"/>] Hz only (negative frequencies allowed),
    /// for selecting one side of a complex baseband signal.
    /// </summary>
    public static Complex32[] ComplexBandPass(double gain, double sampleRate, double low, double high, double transitionWidth,
        WindowType window = WindowType.Hamming, double beta = 6.76)
    {
        if (high <= low) throw new ArgumentException("High edge must be above low edge.");
        var lp = LowPass(gain, sampleRate, (high - low) / 2, transitionWidth, window, beta);
        var center = (low + high) / 2;
        return Shift(lp, center / sampleRate);
    }

    /// <summary>Multiplies real taps by e^(j2π·f·n), turning a low-pass into a band-pass centred at <paramref name="normalizedFrequency"/>·fs.</summary>
    public static Complex32[] Shift(ReadOnlySpan<float> taps, double normalizedFrequency)
    {
        var result = new Complex32[taps.Length];
        var mid = (taps.Length - 1) / 2.0;
        for (var i = 0; i < taps.Length; i++)
            result[i] = Complex32.FromPolar(taps[i], (float)(2 * Math.PI * normalizedFrequency * (i - mid)));
        return result;
    }

    /// <summary>Root-raised-cosine pulse for matched filtering of PSK/QAM (GNU Radio's <c>firdes.root_raised_cosine</c>).</summary>
    public static float[] RootRaisedCosine(double gain, double sampleRate, double symbolRate, double alpha, int ntaps)
    {
        ntaps |= 1;
        var spb = sampleRate / symbolRate;
        var taps = new double[ntaps];
        var scale = 0.0;
        for (var i = 0; i < ntaps; i++)
        {
            double xindx = i - ntaps / 2;
            var x1 = Math.PI * xindx / spb;
            var x2 = 4 * alpha * xindx / spb;
            var x3 = x2 * x2 - 1;
            double num, den;
            if (Math.Abs(x3) >= 0.000001)
            {
                num = i != ntaps / 2
                    ? Math.Cos((1 + alpha) * x1) + Math.Sin((1 - alpha) * x1) / (4 * alpha * xindx / spb)
                    : Math.Cos((1 + alpha) * x1) + (1 - alpha) * Math.PI / (4 * alpha);
                den = x3 * Math.PI;
            }
            else
            {
                if (alpha == 1)
                {
                    taps[i] = -1;
                    scale += -1;
                    continue;
                }
                x3 = (1 - alpha) * x1;
                x2 = (1 + alpha) * x1;
                num = Math.Sin(x2) * (1 + alpha) * Math.PI
                      - Math.Cos(x3) * ((1 - alpha) * Math.PI * spb) / (4 * alpha * xindx)
                      + Math.Sin(x3) * spb * spb / (4 * alpha * xindx * xindx);
                den = -32 * Math.PI * alpha * alpha * xindx / spb;
            }
            taps[i] = 4 * alpha * num / den;
            scale += taps[i];
        }
        return Scale(taps, gain / scale);
    }

    /// <summary>Gaussian pulse for GMSK/GFSK (GNU Radio's <c>firdes.gaussian</c>).</summary>
    public static float[] Gaussian(double gain, double samplesPerSymbol, double bt, int ntaps)
    {
        var taps = new double[ntaps];
        var dt = 1.0 / samplesPerSymbol;
        var s = 1.0 / (Math.Sqrt(Math.Log(2.0)) / (2 * Math.PI * bt));
        var t0 = -0.5 * ntaps;
        var scale = 0.0;
        for (var i = 0; i < ntaps; i++)
        {
            t0++;
            var ts = s * dt * t0;
            taps[i] = Math.Exp(-0.5 * ts * ts);
            scale += taps[i];
        }
        return Scale(taps, gain / scale);
    }

    /// <summary>Hilbert transformer (90° phase shift) (GNU Radio's <c>firdes.hilbert</c>).</summary>
    public static float[] Hilbert(int ntaps = 65, WindowType window = WindowType.Rectangular, double beta = 6.76)
    {
        if ((ntaps & 1) == 0) throw new ArgumentException("Hilbert filters need an odd number of taps.", nameof(ntaps));
        var w = Window.Build(window, ntaps, beta);
        var taps = new float[ntaps];
        var h = (ntaps - 1) / 2;
        var gain = 0f;
        for (var i = 1; i <= h; i++)
        {
            if ((i & 1) == 1)
            {
                var x = 1f / i;
                taps[h + i] = x * w[h + i];
                taps[h - i] = -x * w[h - i];
                gain = taps[h + i] - gain;
            }
        }
        gain = 2 * MathF.Abs(gain);
        for (var i = 0; i < ntaps; i++) taps[i] /= gain;
        return taps;
    }

    /// <summary>Matched filter for a known complex sequence: conjugated and time-reversed.</summary>
    public static Complex32[] MatchedFilter(ReadOnlySpan<Complex32> symbols)
    {
        var taps = new Complex32[symbols.Length];
        for (var i = 0; i < symbols.Length; i++) taps[i] = symbols[symbols.Length - 1 - i].Conjugate();
        return taps;
    }

    /// <summary>Taps of a rectangular moving-sum of <paramref name="length"/> samples scaled by <paramref name="scale"/>.</summary>
    public static float[] Boxcar(int length, float scale = 1f)
    {
        var taps = new float[length];
        Array.Fill(taps, scale);
        return taps;
    }

    /// <summary>Magnitude response |H(f)| in dB at <paramref name="points"/> frequencies from 0 to fs/2.</summary>
    public static float[] MagnitudeResponseDb(ReadOnlySpan<float> taps, int points = 512)
    {
        var result = new float[points];
        for (var k = 0; k < points; k++)
        {
            var w = Math.PI * k / (points - 1);
            double re = 0, im = 0;
            for (var n = 0; n < taps.Length; n++)
            {
                re += taps[n] * Math.Cos(w * n);
                im -= taps[n] * Math.Sin(w * n);
            }
            result[k] = (float)(10 * Math.Log10(re * re + im * im + 1e-20));
        }
        return result;
    }

    /// <summary>Magnitude response of complex taps over [-fs/2, fs/2) in dB.</summary>
    public static float[] MagnitudeResponseDb(ReadOnlySpan<Complex32> taps, int points = 512)
    {
        var result = new float[points];
        for (var k = 0; k < points; k++)
        {
            var w = Math.PI * (2.0 * k / points - 1);
            double re = 0, im = 0;
            for (var n = 0; n < taps.Length; n++)
            {
                var (s, c) = Math.SinCos(-w * n);
                re += taps[n].Real * c - taps[n].Imaginary * s;
                im += taps[n].Real * s + taps[n].Imaginary * c;
            }
            result[k] = (float)(10 * Math.Log10(re * re + im * im + 1e-20));
        }
        return result;
    }

    private static float[] Scale(double[] taps, double gain)
    {
        var result = new float[taps.Length];
        for (var i = 0; i < taps.Length; i++) result[i] = (float)(taps[i] * gain);
        return result;
    }

    private static void CheckCutoff(double sampleRate, double cutoff)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (cutoff <= 0 || cutoff > sampleRate / 2)
            throw new ArgumentOutOfRangeException(nameof(cutoff), "Cutoff must be in (0, fs/2].");
    }

    private static void CheckBand(double sampleRate, double low, double high)
    {
        if (low <= 0 || high <= low || high > sampleRate / 2)
            throw new ArgumentOutOfRangeException(nameof(high), "Band edges must satisfy 0 < low < high <= fs/2.");
    }
}
