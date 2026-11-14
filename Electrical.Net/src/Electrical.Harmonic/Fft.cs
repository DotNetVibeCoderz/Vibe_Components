using System.Numerics;

namespace Electrical.Harmonic;

/// <summary>
/// Fast Fourier transform: iterative radix-2 Cooley–Tukey for power-of-two lengths and Bluestein's chirp-z algorithm for any other length.
/// Convention: X[k] = Σ x[n]·e^(−2πi·kn/N) (forward, unscaled); the inverse divides by N.
/// </summary>
public static class Fft
{
    /// <summary>Forward transform of a complex sequence (returns a new array).</summary>
    public static Complex[] Forward(ReadOnlySpan<Complex> x)
    {
        var a = x.ToArray();
        Transform(a, inverse: false);
        return a;
    }

    /// <summary>Forward transform of a real sequence.</summary>
    public static Complex[] Forward(ReadOnlySpan<double> x)
    {
        var a = new Complex[x.Length];
        for (int i = 0; i < x.Length; i++) a[i] = x[i];
        Transform(a, inverse: false);
        return a;
    }

    /// <summary>Inverse transform (scaled by 1/N).</summary>
    public static Complex[] Inverse(ReadOnlySpan<Complex> x)
    {
        var a = x.ToArray();
        Transform(a, inverse: true);
        for (int i = 0; i < a.Length; i++) a[i] /= a.Length;
        return a;
    }

    /// <summary>Direct O(N²) DFT (reference implementation).</summary>
    public static Complex[] Dft(ReadOnlySpan<Complex> x)
    {
        int n = x.Length;
        var r = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            Complex s = 0;
            for (int j = 0; j < n; j++) s += x[j] * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((long)k * j % n) / n);
            r[k] = s;
        }
        return r;
    }

    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>In-place unscaled transform of any length.</summary>
    public static void Transform(Complex[] a, bool inverse)
    {
        int n = a.Length;
        if (n <= 1) return;
        if (IsPowerOfTwo(n)) Radix2(a, inverse);
        else Bluestein(a, inverse);
    }

    private static void Radix2(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            int half = len / 2;
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < half; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + half] * w;
                    a[i + k] = u + v;
                    a[i + k + half] = u - v;
                    w *= wl;
                }
            }
        }
    }

    private static void Bluestein(Complex[] a, bool inverse)
    {
        int n = a.Length;
        int m = 1;
        while (m < 2 * n - 1) m <<= 1;
        double sign = inverse ? 1 : -1;
        var chirp = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            long kk = (long)k * k % (2L * n); // keep the angle argument small for accuracy
            chirp[k] = Complex.FromPolarCoordinates(1, sign * Math.PI * kk / n);
        }
        var u = new Complex[m];
        var v = new Complex[m];
        for (int k = 0; k < n; k++) u[k] = a[k] * chirp[k];
        v[0] = Complex.Conjugate(chirp[0]);
        for (int k = 1; k < n; k++) v[k] = v[m - k] = Complex.Conjugate(chirp[k]);
        Radix2(u, false);
        Radix2(v, false);
        for (int i = 0; i < m; i++) u[i] *= v[i];
        Radix2(u, true);
        for (int k = 0; k < n; k++) a[k] = u[k] / m * chirp[k];
    }
}
