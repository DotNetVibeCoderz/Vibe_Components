namespace Agritech.Core.Data;

/// <summary>Deterministic helpers for generating synthetic agronomic data (demos, tests, scenario runs).</summary>
public static class Rng
{
    /// <summary>Standard normal sample (Box–Muller).</summary>
    public static double NextGaussian(this Random rng, double mean = 0, double std = 1)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return mean + std * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    /// <summary>Gamma(shape, scale) via Marsaglia–Tsang. Useful for synthetic rainfall amounts.</summary>
    public static double NextGamma(this Random rng, double shape, double scale)
    {
        if (shape < 1) return rng.NextGamma(shape + 1, scale) * Math.Pow(rng.NextDouble(), 1.0 / shape);
        double d = shape - 1.0 / 3, c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do { x = rng.NextGaussian(); v = 1 + c * x; } while (v <= 0);
            v = v * v * v;
            double u = rng.NextDouble();
            if (u < 1 - 0.0331 * x * x * x * x || Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v))) return d * v * scale;
        }
    }
}
