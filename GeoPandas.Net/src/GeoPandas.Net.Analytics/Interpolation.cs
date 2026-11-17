using GeoPandas.Net.Geometry.Index;

namespace GeoPandas.Net.Analytics;

public enum VariogramModel { Spherical, Exponential, Gaussian, Linear }

/// <summary>Fitted (or user-defined) semivariogram γ(h).</summary>
public sealed record Variogram(VariogramModel Model, double Nugget, double Sill, double Range)
{
    /// <summary>Partial sill (sill − nugget).</summary>
    public double PartialSill => Sill - Nugget;

    public double Gamma(double h)
    {
        if (h <= 0) return 0;
        double c = PartialSill, a = Range;
        return Nugget + Model switch
        {
            VariogramModel.Spherical => h >= a ? c : c * (1.5 * h / a - 0.5 * Math.Pow(h / a, 3)),
            VariogramModel.Exponential => c * (1 - Math.Exp(-3 * h / a)),
            VariogramModel.Gaussian => c * (1 - Math.Exp(-3 * h * h / (a * a))),
            _ => c * Math.Min(1, h / a)
        };
    }

    /// <summary>Empirical semivariogram in <paramref name="lags"/> bins, then a weighted least-squares grid-search fit.</summary>
    public static Variogram Fit(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, ReadOnlySpan<double> zs, VariogramModel model = VariogramModel.Spherical, int lags = 12, double? maxLag = null)
    {
        int n = xs.Length;
        var x = xs.ToArray(); var y = ys.ToArray(); var z = zs.ToArray();
        double maxD = 0;
        for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) maxD = Math.Max(maxD, Math.Sqrt((x[i] - x[j]) * (x[i] - x[j]) + (y[i] - y[j]) * (y[i] - y[j])));
        double cutoff = maxLag ?? maxD / 2;
        var sum = new double[lags]; var cnt = new int[lags];
        // subsample pairs for big inputs
        int stride = n > 1500 ? n / 1500 + 1 : 1;
        for (int i = 0; i < n; i += stride)
            for (int j = i + 1; j < n; j += stride)
            {
                double h = Math.Sqrt((x[i] - x[j]) * (x[i] - x[j]) + (y[i] - y[j]) * (y[i] - y[j]));
                if (h > cutoff) continue;
                int b = Math.Min(lags - 1, (int)(h / cutoff * lags));
                sum[b] += 0.5 * (z[i] - z[j]) * (z[i] - z[j]); cnt[b]++;
            }
        var bins = Enumerable.Range(0, lags).Where(b => cnt[b] > 0).Select(b => (H: (b + 0.5) * cutoff / lags, G: sum[b] / cnt[b], N: cnt[b])).ToArray();
        double variance = z.Select(v => (v - z.Average()) * (v - z.Average())).Average();
        if (bins.Length == 0) return new Variogram(model, 0, variance, Math.Max(cutoff, 1e-9));

        Variogram best = new(model, 0, variance, cutoff / 2);
        double bestErr = double.MaxValue;
        double gMax = bins.Max(b => b.G);
        for (int ri = 1; ri <= 30; ri++)
            for (int si = 1; si <= 20; si++)
                for (int ni = 0; ni <= 5; ni++)
                {
                    var v = new Variogram(model, gMax * ni * 0.08, gMax * (0.5 + si * 0.05), cutoff * ri / 15.0);
                    if (v.Nugget >= v.Sill) continue;
                    double err = bins.Sum(b => b.N * Math.Pow(b.G - v.Gamma(b.H), 2));
                    if (err < bestErr) { bestErr = err; best = v; }
                }
        return best;
    }
}

/// <summary>Spatial interpolation of point samples onto arbitrary locations or grids.</summary>
public static class Interpolation
{
    /// <summary>Inverse distance weighting using the k nearest samples.</summary>
    public static double[] Idw(ReadOnlySpan<double> sx, ReadOnlySpan<double> sy, ReadOnlySpan<double> sz,
        ReadOnlySpan<double> qx, ReadOnlySpan<double> qy, double power = 2, int neighbors = 12)
    {
        var tree = new KdTree(sx, sy);
        var z = sz.ToArray(); var qxa = qx.ToArray(); var qya = qy.ToArray();
        var res = new double[qx.Length];
        Parallel.For(0, res.Length, i =>
        {
            var nb = tree.Nearest(qxa[i], qya[i], neighbors);
            double num = 0, den = 0;
            foreach (var (idx, d) in nb)
            {
                if (d < 1e-12) { num = z[idx]; den = 1; break; }
                double w = 1 / Math.Pow(d, power);
                num += w * z[idx]; den += w;
            }
            res[i] = den == 0 ? double.NaN : num / den;
        });
        return res;
    }

    /// <summary>
    /// Ordinary kriging with a local neighbourhood (k nearest samples). Returns predictions and kriging variances.
    /// </summary>
    public static (double[] Values, double[] Variances) OrdinaryKriging(ReadOnlySpan<double> sx, ReadOnlySpan<double> sy, ReadOnlySpan<double> sz,
        ReadOnlySpan<double> qx, ReadOnlySpan<double> qy, Variogram? variogram = null, int neighbors = 16)
    {
        var vg = variogram ?? Variogram.Fit(sx, sy, sz);
        var tree = new KdTree(sx, sy);
        var x = sx.ToArray(); var y = sy.ToArray(); var z = sz.ToArray();
        var qxa = qx.ToArray(); var qya = qy.ToArray();
        var vals = new double[qx.Length]; var vars = new double[qx.Length];
        Parallel.For(0, vals.Length, i =>
        {
            var nb = tree.Nearest(qxa[i], qya[i], Math.Min(neighbors, x.Length));
            int k = nb.Length;
            var a = new double[k + 1, k + 2]; // augmented [Γ 1 | γ0]
            for (int r = 0; r < k; r++)
            {
                for (int c = 0; c < k; c++)
                {
                    int ir = nb[r].Index, ic = nb[c].Index;
                    a[r, c] = vg.Gamma(Math.Sqrt((x[ir] - x[ic]) * (x[ir] - x[ic]) + (y[ir] - y[ic]) * (y[ir] - y[ic])));
                }
                a[r, k] = 1;
                a[r, k + 1] = vg.Gamma(nb[r].Distance);
            }
            for (int c = 0; c < k; c++) a[k, c] = 1;
            a[k, k] = 0; a[k, k + 1] = 1;
            var gamma0 = new double[k];
            for (int r = 0; r < k; r++) gamma0[r] = a[r, k + 1];
            var w = Solve(a, k + 1);
            if (w is null) { vals[i] = double.NaN; vars[i] = double.NaN; return; }
            double v = 0, s2 = 0;
            for (int r = 0; r < k; r++) { v += w[r] * z[nb[r].Index]; s2 += w[r] * gamma0[r]; }
            vals[i] = v;
            vars[i] = s2 + w[k];
        });
        return (vals, vars);
    }

    /// <summary>Gaussian elimination with partial pivoting on an n×(n+1) augmented matrix.</summary>
    internal static double[]? Solve(double[,] m, int n)
    {
        int cols = n + 1;
        for (int p = 0; p < n; p++)
        {
            int max = p;
            for (int r = p + 1; r < n; r++) if (Math.Abs(m[r, p]) > Math.Abs(m[max, p])) max = r;
            if (Math.Abs(m[max, p]) < 1e-14) return null;
            if (max != p) for (int c = 0; c < cols; c++) (m[p, c], m[max, c]) = (m[max, c], m[p, c]);
            for (int r = p + 1; r < n; r++)
            {
                double f = m[r, p] / m[p, p];
                for (int c = p; c < cols; c++) m[r, c] -= f * m[p, c];
            }
        }
        var sol = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = m[r, n];
            for (int c = r + 1; c < n; c++) s -= m[r, c] * sol[c];
            sol[r] = s / m[r, r];
        }
        return sol;
    }
}
