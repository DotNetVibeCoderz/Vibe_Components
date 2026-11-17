namespace GeoPandas.Net.Analytics;

/// <summary>Global autocorrelation test result.</summary>
public sealed record GlobalAutocorrelation(
    string Statistic, double Value, double Expected, double VarianceNormal, double ZScore, double PValueNormal,
    double PValueSimulated, int Permutations)
{
    public override string ToString() =>
        $"{Statistic} = {Value:F4} (E = {Expected:F4}), z = {ZScore:F3}, p(norm) = {PValueNormal:F4}, p(sim) = {PValueSimulated:F4}";
}

/// <summary>Local Indicators of Spatial Association result per observation.</summary>
public sealed record LocalMoranResult(double[] Is, double[] ZScores, double[] PValues, string[] Quadrants);

/// <summary>Spatial autocorrelation statistics (PySAL <c>esda</c> equivalents).</summary>
public static class Autocorrelation
{
    /// <summary>Global Moran's I with normality-based inference and a permutation test.</summary>
    public static GlobalAutocorrelation MoransI(ReadOnlySpan<double> y, SpatialWeights w, int permutations = 999, int seed = 12345)
    {
        int n = y.Length;
        if (w.N != n) throw new ArgumentException("Weights size does not match data length.");
        var z = Center(y);
        double s0 = w.S0;
        double i0 = MoranStat(z, w, s0);
        double e = -1.0 / (n - 1);

        // Variance under normality (Cliff & Ord)
        double s1 = 0, s2 = 0;
        var colSum = new double[n];
        var rowSum = new double[n];
        var wmap = new Dictionary<(int, int), double>();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < w.Neighbors[i].Length; k++)
            {
                int j = w.Neighbors[i][k]; double v = w.Weights[i][k];
                wmap[(i, j)] = v;
                rowSum[i] += v; colSum[j] += v;
            }
        foreach (var ((i, j), v) in wmap)
        {
            double vji = wmap.TryGetValue((j, i), out var t) ? t : 0;
            s1 += (v + vji) * (v + vji);
        }
        s1 /= 2;
        for (int i = 0; i < n; i++) s2 += (rowSum[i] + colSum[i]) * (rowSum[i] + colSum[i]);
        double varNorm = (n * n * s1 - n * s2 + 3 * s0 * s0) / ((n * n - 1.0) * s0 * s0) - e * e;
        double zScore = (i0 - e) / Math.Sqrt(varNorm);
        double pNorm = 2 * (1 - NormalCdf(Math.Abs(zScore)));

        double pSim = Permute(z, permutations, seed, zz => MoranStat(zz, w, s0), i0);
        return new GlobalAutocorrelation("Moran's I", i0, e, varNorm, zScore, pNorm, pSim, permutations);
    }

    private static double MoranStat(double[] z, SpatialWeights w, double s0)
    {
        double num = 0, den = 0;
        for (int i = 0; i < z.Length; i++)
        {
            den += z[i] * z[i];
            var nb = w.Neighbors[i]; var wt = w.Weights[i];
            for (int k = 0; k < nb.Length; k++) num += wt[k] * z[i] * z[nb[k]];
        }
        return z.Length / s0 * num / den;
    }

    /// <summary>Geary's C (values &lt; 1 indicate positive autocorrelation).</summary>
    public static GlobalAutocorrelation GearysC(ReadOnlySpan<double> y, SpatialWeights w, int permutations = 999, int seed = 12345)
    {
        int n = y.Length;
        var z = Center(y);
        double s0 = w.S0;
        double c0 = GearyStat(y.ToArray(), z, w, s0);
        var yy = y.ToArray();
        double pSim = PermuteLower(yy, permutations, seed, arr => GearyStat(arr, Center(arr), w, s0), c0);
        // normal approximation (randomisation-free): Var(C) ≈ [(2S1 + S2)(n-1) - 4 S0²] / [2 (n+1) S0²]
        double s1 = 0, s2 = 0;
        var rs = new double[n]; var cs = new double[n];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < w.Neighbors[i].Length; k++) { s1 += 2 * w.Weights[i][k] * w.Weights[i][k]; rs[i] += w.Weights[i][k]; cs[w.Neighbors[i][k]] += w.Weights[i][k]; }
        s1 /= 2;
        for (int i = 0; i < n; i++) s2 += (rs[i] + cs[i]) * (rs[i] + cs[i]);
        double var = ((2 * s1 + s2) * (n - 1) - 4 * s0 * s0) / (2 * (n + 1) * s0 * s0);
        double zScore = (c0 - 1) / Math.Sqrt(Math.Abs(var));
        return new GlobalAutocorrelation("Geary's C", c0, 1, var, zScore, 2 * (1 - NormalCdf(Math.Abs(zScore))), pSim, permutations);
    }

    private static double GearyStat(double[] y, double[] z, SpatialWeights w, double s0)
    {
        int n = y.Length;
        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            den += z[i] * z[i];
            var nb = w.Neighbors[i]; var wt = w.Weights[i];
            for (int k = 0; k < nb.Length; k++) { double d = y[i] - y[nb[k]]; num += wt[k] * d * d; }
        }
        return (n - 1) * num / (2 * s0 * den);
    }

    /// <summary>Local Moran's I (LISA) with conditional permutation p-values and HH/LL/HL/LH quadrants.</summary>
    public static LocalMoranResult LocalMoran(ReadOnlySpan<double> y, SpatialWeights w, int permutations = 999, int seed = 12345, double significance = 0.05)
    {
        var wr = w.IsRowStandardized ? w : w.RowStandardize();
        int n = y.Length;
        var z = Center(y);
        double m2 = z.Sum(v => v * v) / n;
        var lag = wr.Lag(z);
        var Is = new double[n];
        for (int i = 0; i < n; i++) Is[i] = z[i] / m2 * lag[i];

        var p = new double[n];
        var zs = new double[n];
        Parallel.For(0, n, i =>
        {
            var rng = new Random(seed + i);
            int k = wr.Neighbors[i].Length;
            if (k == 0) { p[i] = 1; return; }
            var pool = Enumerable.Range(0, n).Where(j => j != i).ToArray();
            int larger = 0;
            double sum = 0, sumSq = 0;
            for (int r = 0; r < permutations; r++)
            {
                double l = 0;
                for (int t = 0; t < k; t++)
                {
                    int swap = rng.Next(t, pool.Length);
                    (pool[t], pool[swap]) = (pool[swap], pool[t]);
                    l += wr.Weights[i][t] * z[pool[t]];
                }
                double ii = z[i] / m2 * l;
                sum += ii; sumSq += ii * ii;
                if (Is[i] >= 0 ? ii >= Is[i] : ii <= Is[i]) larger++;
            }
            p[i] = (larger + 1.0) / (permutations + 1.0);
            double mean = sum / permutations, sd = Math.Sqrt(Math.Max(1e-300, sumSq / permutations - mean * mean));
            zs[i] = (Is[i] - mean) / sd;
        });
        var quad = new string[n];
        for (int i = 0; i < n; i++)
        {
            string q = z[i] >= 0 ? (lag[i] >= 0 ? "HH" : "HL") : (lag[i] >= 0 ? "LH" : "LL");
            quad[i] = p[i] <= significance ? q : "ns";
        }
        return new LocalMoranResult(Is, zs, p, quad);
    }

    /// <summary>Getis-Ord Gi* hot-spot z-scores (includes the focal observation). Positive = hot spot, negative = cold spot.</summary>
    public static (double[] ZScores, double[] PValues) GetisOrdGiStar(ReadOnlySpan<double> x, SpatialWeights w)
    {
        int n = x.Length;
        var xa = x.ToArray();
        double mean = xa.Average();
        double s = Math.Sqrt(xa.Sum(v => v * v) / n - mean * mean);
        var zs = new double[n]; var ps = new double[n];
        for (int i = 0; i < n; i++)
        {
            // include self with weight 1 (Gi*)
            double wsum = 1, wsq = 1, wx = xa[i];
            var nb = w.Neighbors[i]; var wt = w.Weights[i];
            for (int k = 0; k < nb.Length; k++)
            {
                if (nb[k] == i) continue;
                wsum += wt[k]; wsq += wt[k] * wt[k]; wx += wt[k] * xa[nb[k]];
            }
            double den = s * Math.Sqrt((n * wsq - wsum * wsum) / (n - 1));
            zs[i] = den == 0 ? 0 : (wx - mean * wsum) / den;
            ps[i] = 2 * (1 - NormalCdf(Math.Abs(zs[i])));
        }
        return (zs, ps);
    }

    /// <summary>Adds Gi* columns and a hot/cold classification to the frame.</summary>
    public static GeoDataFrame HotSpots(GeoDataFrame gdf, string column, SpatialWeights w, double significance = 0.05)
    {
        var (z, p) = GetisOrdGiStar(gdf[column].ToDoubleArray(), w);
        var label = z.Select((v, i) => p[i] > significance ? "Not significant" : v > 0 ? (p[i] <= 0.01 ? "Hot spot (99%)" : "Hot spot (95%)") : (p[i] <= 0.01 ? "Cold spot (99%)" : "Cold spot (95%)")).ToArray();
        return gdf.WithColumn(Column.Create("gi_z", z)).WithColumn(Column.Create("gi_p", p)).WithColumn(Column.Create("hotspot", label));
    }

    // ------------------------------------------------------------------ helpers

    private static double[] Center(ReadOnlySpan<double> y)
    {
        double m = 0;
        foreach (var v in y) m += v;
        m /= y.Length;
        var z = new double[y.Length];
        for (int i = 0; i < y.Length; i++) z[i] = y[i] - m;
        return z;
    }

    private static double Permute(double[] z, int permutations, int seed, Func<double[], double> stat, double observed)
    {
        if (permutations <= 0) return double.NaN;
        var rng = new Random(seed);
        var arr = z.ToArray();
        int extreme = 0;
        double sum = 0;
        var sims = new double[permutations];
        for (int r = 0; r < permutations; r++) { rng.Shuffle(arr); sims[r] = stat(arr); sum += sims[r]; }
        double mean = sum / permutations;
        // one-sided in the direction of the observed deviation (PySAL convention)
        extreme = observed >= mean ? sims.Count(s => s >= observed) : sims.Count(s => s <= observed);
        return (extreme + 1.0) / (permutations + 1.0);
    }

    private static double PermuteLower(double[] y, int permutations, int seed, Func<double[], double> stat, double observed) =>
        Permute(y, permutations, seed, stat, observed);

    /// <summary>Standard normal CDF (Zelen &amp; Severo / erf approximation, |err| &lt; 1.5e-7).</summary>
    public static double NormalCdf(double x)
    {
        double t = 1 / (1 + 0.3275911 * Math.Abs(x) / Math.Sqrt(2));
        double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x / 2);
        return 0.5 * (1 + Math.Sign(x) * erf);
    }
}
