using System.Runtime.CompilerServices;

namespace GnuRadio;

/// <summary>
/// xoshiro256** generator with Gaussian and Laplacian helpers. Deterministic for a given seed,
/// allocation-free, and several times faster than <see cref="Random"/> with a seed, which matters for
/// noise sources and channel models that draw millions of values per second.
/// </summary>
public sealed class FastRandom
{
    private ulong _s0, _s1, _s2, _s3;
    private bool _hasSpare;
    private float _spare;

    public FastRandom(long seed = 0)
    {
        var x = seed == 0 ? (ulong)Environment.TickCount64 ^ 0x9E3779B97F4A7C15UL : (ulong)seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    private static ulong SplitMix(ref ulong x)
    {
        var z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextUInt64()
    {
        var result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextSingle() => (NextUInt64() >> 40) * (1.0f / (1 << 24));

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, maxExclusive).</summary>
    public int Next(int maxExclusive) => (int)((NextUInt64() >> 33) * (ulong)maxExclusive >> 31);

    /// <summary>Random bit (0 or 1).</summary>
    public byte NextBit() => (byte)(NextUInt64() >> 63);

    /// <summary>Standard normal deviate (mean 0, variance 1), Marsaglia polar method.</summary>
    public float NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }
        float u, v, s;
        do
        {
            u = 2f * NextSingle() - 1f;
            v = 2f * NextSingle() - 1f;
            s = u * u + v * v;
        } while (s >= 1f || s == 0f);
        var m = MathF.Sqrt(-2f * MathF.Log(s) / s);
        _spare = v * m;
        _hasSpare = true;
        return u * m;
    }

    /// <summary>Circular complex Gaussian with total variance 1 (each component variance ½).</summary>
    public Complex32 NextComplexGaussian()
    {
        const float scale = 0.70710678f;
        return new Complex32(NextGaussian() * scale, NextGaussian() * scale);
    }

    /// <summary>Laplacian deviate with unit variance.</summary>
    public float NextLaplacian()
    {
        var u = NextSingle() - 0.5f;
        return -MathF.Sign(u) * MathF.Log(1f - 2f * MathF.Abs(u) + 1e-12f) / MathF.Sqrt(2f);
    }

    public void NextBytes(Span<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)(NextUInt64() >> 56);
    }

    /// <summary>Fills <paramref name="bits"/> with random 0/1 values, one bit per byte (GNU Radio's "unpacked" format).</summary>
    public void NextBits(Span<byte> bits)
    {
        var i = 0;
        while (i < bits.Length)
        {
            var word = NextUInt64();
            for (var b = 0; b < 64 && i < bits.Length; b++, i++)
            {
                bits[i] = (byte)(word & 1);
                word >>= 1;
            }
        }
    }
}
