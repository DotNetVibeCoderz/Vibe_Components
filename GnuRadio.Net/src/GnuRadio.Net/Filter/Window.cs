namespace GnuRadio.Filter;

/// <summary>Window functions for filter design and spectral analysis (GNU Radio's <c>fft.window</c>).</summary>
public enum WindowType
{
    Rectangular,
    Hamming,
    Hann,
    Blackman,
    BlackmanHarris,
    Kaiser,
    FlatTop,
    Bartlett,
    Gaussian,
}

/// <summary>Builds window coefficient arrays and reports their properties.</summary>
public static class Window
{
    /// <summary>Returns <paramref name="length"/> coefficients of the window.</summary>
    /// <param name="type">Window shape.</param>
    /// <param name="length">Number of coefficients.</param>
    /// <param name="beta">Kaiser β, or the Gaussian σ as a fraction of half the length.</param>
    public static float[] Build(WindowType type, int length, double beta = 6.76)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        var w = new float[length];
        if (length == 1)
        {
            w[0] = 1;
            return w;
        }
        var m = length - 1.0;
        for (var n = 0; n < length; n++)
        {
            var x = 2 * Math.PI * n / m;
            w[n] = (float)(type switch
            {
                WindowType.Rectangular => 1.0,
                WindowType.Hamming => 0.54 - 0.46 * Math.Cos(x),
                WindowType.Hann => 0.5 - 0.5 * Math.Cos(x),
                WindowType.Blackman => 0.42 - 0.5 * Math.Cos(x) + 0.08 * Math.Cos(2 * x),
                WindowType.BlackmanHarris => 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x),
                WindowType.FlatTop => 0.21557895 - 0.41663158 * Math.Cos(x) + 0.277263158 * Math.Cos(2 * x)
                                      - 0.083578947 * Math.Cos(3 * x) + 0.006947368 * Math.Cos(4 * x),
                WindowType.Bartlett => 1 - Math.Abs((n - m / 2) / (m / 2)),
                WindowType.Kaiser => BesselI0(beta * Math.Sqrt(1 - Math.Pow(2 * n / m - 1, 2))) / BesselI0(beta),
                WindowType.Gaussian => Math.Exp(-0.5 * Math.Pow((n - m / 2) / (beta * m / 2), 2)),
                _ => 1.0,
            });
        }
        return w;
    }

    /// <summary>
    /// Approximate stop-band attenuation in dB that a windowed-sinc filter achieves with this window;
    /// GNU Radio uses the same figures to choose the number of taps.
    /// </summary>
    public static double MaxAttenuation(WindowType type, double beta = 6.76) => type switch
    {
        WindowType.Rectangular => 21,
        WindowType.Hamming => 53,
        WindowType.Hann => 44,
        WindowType.Blackman => 74,
        WindowType.BlackmanHarris => 92,
        WindowType.Kaiser => beta / 0.1102 + 8.7,
        WindowType.FlatTop => 93,
        WindowType.Bartlett => 27,
        WindowType.Gaussian => 50,
        _ => 21,
    };

    /// <summary>Coherent gain Σw/N, used to correct spectrum amplitudes.</summary>
    public static double CoherentGain(ReadOnlySpan<float> window)
    {
        var s = 0.0;
        foreach (var v in window) s += v;
        return s / window.Length;
    }

    /// <summary>Σw², used to normalise power spectral densities.</summary>
    public static double PowerGain(ReadOnlySpan<float> window)
    {
        var s = 0.0;
        foreach (var v in window) s += v * v;
        return s;
    }

    /// <summary>Kaiser β for a desired attenuation in dB (Kaiser's empirical formula).</summary>
    public static double KaiserBeta(double attenuationDb) =>
        attenuationDb > 50 ? 0.1102 * (attenuationDb - 8.7)
        : attenuationDb >= 21 ? 0.5842 * Math.Pow(attenuationDb - 21, 0.4) + 0.07886 * (attenuationDb - 21)
        : 0;

    /// <summary>Modified Bessel function of the first kind, order zero.</summary>
    public static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var half = x / 2;
        for (var k = 1; k < 50; k++)
        {
            term *= half / k;
            var t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-16) break;
        }
        return sum;
    }
}
