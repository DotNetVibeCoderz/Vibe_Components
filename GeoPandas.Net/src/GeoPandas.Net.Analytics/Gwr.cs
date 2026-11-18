using System.Globalization;
using GeoPandas.Net.Geometry;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Analytics;

public enum GwrKernel { Bisquare, Gaussian, Exponential }

/// <summary>Result of a geographically weighted regression.</summary>
public sealed class GwrResult
{
    internal GwrResult(string[] names, double bandwidth, bool fixedBw, GwrKernel kernel, double[,] p, double[,] se, double[] pred, double[] resid,
        double[] localR2, double trS, double rss, double tss, int n)
    {
        Names = names; Bandwidth = bandwidth; Fixed = fixedBw; Kernel = kernel; Params = p; StdErrors = se; Predicted = pred; Residuals = resid;
        LocalR2 = localR2; EffectiveParameters = trS; Rss = rss; Tss = tss; N = n;
    }

    public IReadOnlyList<string> Names { get; }
    /// <summary>Number of nearest neighbours (adaptive) or distance (fixed).</summary>
    public double Bandwidth { get; }
    public bool Fixed { get; }
    public GwrKernel Kernel { get; }
    /// <summary>Local coefficients, n × k (column 0 is the intercept).</summary>
    public double[,] Params { get; }
    public double[,] StdErrors { get; }
    public double[] Predicted { get; }
    public double[] Residuals { get; }
    public double[] LocalR2 { get; }
    /// <summary>tr(S), the effective number of parameters.</summary>
    public double EffectiveParameters { get; }
    public double Rss { get; }
    public double Tss { get; }
    public int N { get; }
    public double R2 => 1 - Rss / Tss;
    public double AdjustedR2 => 1 - (1 - R2) * (N - 1) / (N - EffectiveParameters - 1);
    /// <summary>σ² = RSS / (n − tr S).</summary>
    public double Sigma2 => Rss / (N - EffectiveParameters);
    public double LogLikelihood => -N / 2.0 * (Math.Log(2 * Math.PI * Rss / N) + 1);
    public double Aic => -2 * LogLikelihood + 2 * (EffectiveParameters + 1);
    /// <summary>Corrected AIC (Fotheringham, Brunsdon &amp; Charlton 2002, eq. 2.33), the bandwidth selection criterion.</summary>
    public double Aicc => -2 * LogLikelihood + 2.0 * N * (EffectiveParameters + 1) / (N - EffectiveParameters - 2);

    public double[,] TValues
    {
        get
        {
            int n = Params.GetLength(0), k = Params.GetLength(1);
            var t = new double[n, k];
            for (int i = 0; i < n; i++) for (int j = 0; j < k; j++) t[i, j] = Params[i, j] / StdErrors[i, j];
            return t;
        }
    }

    /// <summary>Appends the local coefficients (<c>b_name</c>), standard errors (<c>se_name</c>), t values and local R² to the input frame.</summary>
    public GeoDataFrame AddTo(GeoDataFrame gdf)
    {
        var cols = new List<Column>();
        var t = TValues;
        for (int j = 0; j < Names.Count; j++)
        {
            int jj = j;
            cols.Add(Column.Create($"b_{Names[j]}", Enumerable.Range(0, N).Select(i => Params[i, jj]).ToArray()));
            cols.Add(Column.Create($"se_{Names[j]}", Enumerable.Range(0, N).Select(i => StdErrors[i, jj]).ToArray()));
            cols.Add(Column.Create($"t_{Names[j]}", Enumerable.Range(0, N).Select(i => t[i, jj]).ToArray()));
        }
        cols.Add(Column.Create("local_r2", LocalR2));
        cols.Add(Column.Create("predicted", Predicted));
        cols.Add(Column.Create("residual", Residuals));
        var r = gdf;
        foreach (var c in cols) r = r.WithColumn(c);
        return r;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"GWR ({Kernel}, {(Fixed ? $"fixed bandwidth {Bandwidth:G6}" : $"adaptive {Bandwidth:F0} neighbours")}): R² {R2:F4}, adj. R² {AdjustedR2:F4}, AICc {Aicc:F3}, ENP {EffectiveParameters:F3}, σ² {Sigma2:F4}");
}

/// <summary>
/// Geographically weighted regression (Fotheringham, Brunsdon &amp; Charlton 2002), Gaussian family, matching PySAL <c>mgwr</c>:
/// adaptive (k nearest) or fixed bandwidths, bisquare / Gaussian / exponential kernels, golden-section bandwidth selection on AICc.
/// </summary>
public static class Gwr
{
    /// <summary>GWR on a GeoDataFrame (point geometries or polygon centroids as locations).</summary>
    public static GwrResult Fit(GeoDataFrame gdf, string dependent, IReadOnlyList<string> regressors, double? bandwidth = null,
        GwrKernel kernel = GwrKernel.Bisquare, bool fixedBandwidth = false)
    {
        var (xs, ys) = Coordinates(gdf);
        var y = Enumerable.Range(0, gdf.RowCount).Select(i => gdf[dependent].GetDouble(i)).ToArray();
        var x = new double[gdf.RowCount, regressors.Count];
        for (int j = 0; j < regressors.Count; j++) for (int i = 0; i < gdf.RowCount; i++) x[i, j] = gdf[regressors[j]].GetDouble(i);
        double bw = bandwidth ?? SelectBandwidth(xs, ys, y, x, kernel, fixedBandwidth);
        return Fit(xs, ys, y, x, bw, kernel, fixedBandwidth, ["Intercept", .. regressors]);
    }

    internal static (double[] X, double[] Y) Coordinates(GeoDataFrame gdf)
    {
        var xs = new double[gdf.RowCount]; var ys = new double[gdf.RowCount];
        for (int i = 0; i < xs.Length; i++)
        {
            var g = gdf.Geometry[i] ?? throw new ArgumentException($"Row {i} has no geometry.");
            var c = g is Point p ? p.Coordinate : g.Centroid.Coordinate;
            xs[i] = c.X; ys[i] = c.Y;
        }
        return (xs, ys);
    }

    private static double[,] WithConstant(double[,] x)
    {
        int n = x.GetLength(0), k = x.GetLength(1);
        var r = new double[n, k + 1];
        for (int i = 0; i < n; i++) { r[i, 0] = 1; for (int j = 0; j < k; j++) r[i, j + 1] = x[i, j]; }
        return r;
    }

    private static double[] KernelWeights(double[] xs, double[] ys, int i, double bw, GwrKernel kernel, bool fixedBw, double[] buffer)
    {
        int n = xs.Length;
        var d = new double[n];
        for (int j = 0; j < n; j++) d[j] = Math.Sqrt((xs[i] - xs[j]) * (xs[i] - xs[j]) + (ys[i] - ys[j]) * (ys[i] - ys[j]));
        double h;
        if (fixedBw) h = bw;
        else
        {
            // distance to the bw-th nearest observation (the point itself included), as mgwr
            d.CopyTo(buffer, 0);
            int kth = Math.Clamp((int)bw - 1, 0, n - 1);
            h = QuickSelect(buffer, kth) * 1.0000001;
        }
        var w = new double[n];
        for (int j = 0; j < n; j++)
        {
            double z = d[j] / h;
            w[j] = kernel switch
            {
                GwrKernel.Bisquare => d[j] >= h ? 0 : (1 - z * z) * (1 - z * z),
                GwrKernel.Gaussian => Math.Exp(-0.5 * z * z),
                _ => Math.Exp(-z),
            };
        }
        return w;
    }

    private static double QuickSelect(double[] a, int k)
    {
        int lo = 0, hi = a.Length - 1;
        while (lo < hi)
        {
            double pivot = a[(lo + hi) >> 1];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j) { (a[i], a[j]) = (a[j], a[i]); i++; j--; }
            }
            if (k <= j) hi = j; else if (k >= i) lo = i; else return a[k];
        }
        return a[k];
    }

    /// <summary>Fits GWR at a given bandwidth. <paramref name="x"/> excludes the intercept.</summary>
    public static GwrResult Fit(double[] xs, double[] ys, double[] y, double[,] x, double bandwidth, GwrKernel kernel = GwrKernel.Bisquare,
        bool fixedBandwidth = false, IReadOnlyList<string>? names = null)
    {
        var X = WithConstant(x);
        int n = y.Length, k = X.GetLength(1);
        var p = new double[n, k]; var se2 = new double[n, k]; var pred = new double[n]; var influ = new double[n];
        var localR2 = new double[n];
        bool failed = false;
        Parallel.For(0, n, () => new double[n], (i, _, buf) =>
        {
            var w = KernelWeights(xs, ys, i, bandwidth, kernel, fixedBandwidth, buf);
            var xtwxInv = Dense.InverseSpd(Dense.XtX(X, w));
            if (xtwxInv is null) { failed = true; return buf; }
            var beta = Dense.Mul(xtwxInv, Dense.Xtv(X, y, w));
            double yhat = 0;
            for (int a = 0; a < k; a++) { p[i, a] = beta[a]; yhat += X[i, a] * beta[a]; }
            pred[i] = yhat;
            // C = (XᵀWX)⁻¹XᵀW: influence S_ii = x_i C_·i, CCᵀ diagonal for the standard errors
            var cct = new double[k];
            double sii = 0;
            for (int j = 0; j < n; j++)
            {
                if (w[j] == 0) continue;
                for (int a = 0; a < k; a++)
                {
                    double c = 0;
                    for (int b = 0; b < k; b++) c += xtwxInv[a, b] * X[j, b];
                    c *= w[j];
                    cct[a] += c * c;
                    if (j == i) sii += X[i, a] * c;
                }
            }
            influ[i] = sii;
            for (int a = 0; a < k; a++) se2[i, a] = cct[a];
            return buf;
        }, _ => { });
        if (failed) throw new InvalidOperationException("A local XᵀWX is singular; increase the bandwidth.");
        var resid = y.Select((v, i) => v - pred[i]).ToArray();
        double rss = resid.Sum(e => e * e), ybarAll = y.Average(), tss = y.Sum(v => (v - ybarAll) * (v - ybarAll)), trS = influ.Sum();
        double sigma2 = rss / (n - trS);
        // local R² (FBC 2002 eq. 9.8-9.10, as mgwr): kernel-weighted TSS around the weighted mean and weighted GWR residuals
        Parallel.For(0, n, () => new double[n], (i, _, buf) =>
        {
            var w = KernelWeights(xs, ys, i, bandwidth, kernel, fixedBandwidth, buf);
            double sw = 0, swy = 0;
            for (int j = 0; j < n; j++) { sw += w[j]; swy += w[j] * y[j]; }
            double ybar = swy / sw, tssw = 0, rssw = 0;
            for (int j = 0; j < n; j++) { tssw += w[j] * (y[j] - ybar) * (y[j] - ybar); rssw += w[j] * resid[j] * resid[j]; }
            localR2[i] = (tssw - rssw) / tssw;
            return buf;
        }, _ => { });
        var se = new double[n, k];
        for (int i = 0; i < n; i++) for (int a = 0; a < k; a++) se[i, a] = Math.Sqrt(se2[i, a] * sigma2);
        var nm = names?.ToArray() ?? ["Intercept", .. Enumerable.Range(1, k - 1).Select(j => $"x{j}")];
        return new GwrResult(nm, bandwidth, fixedBandwidth, kernel, p, se, pred, resid, localR2, trS, rss, tss, n);
    }

    /// <summary>AICc of the GWR fit at a bandwidth (only tr(S) and RSS, cheaper than <see cref="Fit(double[], double[], double[], double[,], double, GwrKernel, bool, IReadOnlyList{string}?)"/>).</summary>
    public static double Aicc(double[] xs, double[] ys, double[] y, double[,] x, double bandwidth, GwrKernel kernel = GwrKernel.Bisquare, bool fixedBandwidth = false)
    {
        var X = WithConstant(x);
        int n = y.Length, k = X.GetLength(1);
        var pred = new double[n]; var influ = new double[n];
        bool failed = false;
        Parallel.For(0, n, () => new double[n], (i, _, buf) =>
        {
            var w = KernelWeights(xs, ys, i, bandwidth, kernel, fixedBandwidth, buf);
            var inv = Dense.InverseSpd(Dense.XtX(X, w));
            if (inv is null) { failed = true; return buf; }
            var beta = Dense.Mul(inv, Dense.Xtv(X, y, w));
            double yhat = 0, sii = 0;
            for (int a = 0; a < k; a++)
            {
                yhat += X[i, a] * beta[a];
                double c = 0;
                for (int b = 0; b < k; b++) c += inv[a, b] * X[i, b];
                sii += X[i, a] * c * w[i];
            }
            pred[i] = yhat; influ[i] = sii;
            return buf;
        }, _ => { });
        if (failed) return double.PositiveInfinity;
        double rss = 0;
        for (int i = 0; i < n; i++) rss += (y[i] - pred[i]) * (y[i] - pred[i]);
        double trS = influ.Sum();
        if (n - trS - 2 <= 0) return double.PositiveInfinity;
        double ll = -n / 2.0 * (Math.Log(2 * Math.PI * rss / n) + 1);
        return -2 * ll + 2.0 * n * (trS + 1) / (n - trS - 2);
    }

    /// <summary>
    /// Golden-section bandwidth search minimising AICc, as <c>mgwr.sel_bw.Sel_BW(...).search()</c>: adaptive bandwidths are integer
    /// neighbour counts in [40 + 2k, n]; fixed bandwidths span [min NN distance / 2, 2 × max distance].
    /// </summary>
    public static double SelectBandwidth(double[] xs, double[] ys, double[] y, double[,] x, GwrKernel kernel = GwrKernel.Bisquare, bool fixedBandwidth = false,
        double? min = null, double? max = null)
    {
        int n = y.Length, kx = x.GetLength(1) + 1;
        double a, c;
        if (!fixedBandwidth) { a = min ?? 40 + 2 * kx; c = max ?? n; }
        else
        {
            double minD = double.MaxValue, maxD = 0;
            for (int i = 0; i < n; i++)
            {
                double nn = double.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    double d = Math.Sqrt((xs[i] - xs[j]) * (xs[i] - xs[j]) + (ys[i] - ys[j]) * (ys[i] - ys[j]));
                    nn = Math.Min(nn, d); maxD = Math.Max(maxD, d);
                }
                minD = Math.Min(minD, nn);
            }
            a = min ?? minD / 2; c = max ?? maxD * 2;
        }
        const double delta = 0.38197;
        double b = a + delta * Math.Abs(c - a), d2 = c - delta * Math.Abs(c - a);
        var cache = new Dictionary<double, double>();
        double Score(double bw) => cache.TryGetValue(bw, out var s) ? s : cache[bw] = Aicc(xs, ys, y, x, bw, kernel, fixedBandwidth);
        double opt = b, diff = 1e9;
        for (int it = 0; it < 200 && Math.Abs(diff) > 1e-6; it++)
        {
            if (!fixedBandwidth) { b = Math.Round(b, MidpointRounding.ToEven); d2 = Math.Round(d2, MidpointRounding.ToEven); }
            double sb = Score(b), sd = Score(d2);
            if (sb <= sd) { opt = b; c = d2; d2 = b; b = a + delta * Math.Abs(c - a); }
            else { opt = d2; a = b; b = d2; d2 = c - delta * Math.Abs(c - a); }
            diff = sb - sd;
        }
        return opt;
    }
}
