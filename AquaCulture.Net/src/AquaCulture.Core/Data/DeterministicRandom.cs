namespace AquaCulture.Core.Data;

/// <summary>
/// Fast, reproducible PRNG (xoshiro256** seeded through SplitMix64). Unlike <see cref="Random"/> or <see cref="HashCode"/>,
/// the sequence is identical across processes, runtimes and platforms, so synthetic datasets and Monte-Carlo runs are reproducible.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;
    private double? _spare;

    public DeterministicRandom(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
    }

    /// <summary>Seeds from a string with FNV-1a (stable across processes).</summary>
    public DeterministicRandom(string seed) : this(Fnv1a(seed)) { }

    public static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextUInt64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9, t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3; _s2 ^= t; _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public double Uniform(double min, double max) => min + (max - min) * NextDouble();

    public int Next(int maxExclusive) => (int)(NextDouble() * maxExclusive);

    public int Next(int min, int maxExclusive) => min + Next(maxExclusive - min);

    /// <summary>Standard normal (Marsaglia polar).</summary>
    public double NextGaussian()
    {
        if (_spare is { } s) { _spare = null; return s; }
        double u, v, q;
        do { u = 2 * NextDouble() - 1; v = 2 * NextDouble() - 1; q = u * u + v * v; } while (q >= 1 || q == 0);
        double f = Math.Sqrt(-2 * Math.Log(q) / q);
        _spare = v * f;
        return u * f;
    }

    public double Normal(double mean, double sd) => mean + sd * NextGaussian();

    public double LogNormal(double mean, double cv)
    {
        double sigma2 = Math.Log(1 + cv * cv);
        return mean * Math.Exp(Math.Sqrt(sigma2) * NextGaussian() - sigma2 / 2);
    }

    /// <summary>Poisson draw (Knuth for small λ, normal approximation above 50).</summary>
    public int Poisson(double lambda)
    {
        if (lambda <= 0) return 0;
        if (lambda > 50) return Math.Max(0, (int)Math.Round(Normal(lambda, Math.Sqrt(lambda))));
        double l = Math.Exp(-lambda), p = 1; int k = 0;
        do { k++; p *= NextDouble(); } while (p > l);
        return k - 1;
    }

    /// <summary>Binomial draw (exact for n ≤ 64, normal approximation otherwise).</summary>
    public long Binomial(long n, double p)
    {
        if (n <= 0 || p <= 0) return 0;
        if (p >= 1) return n;
        if (n <= 64) { long k = 0; for (long i = 0; i < n; i++) if (NextDouble() < p) k++; return k; }
        double mean = n * p, sd = Math.Sqrt(n * p * (1 - p));
        return Math.Clamp((long)Math.Round(Normal(mean, sd)), 0, n);
    }
}
