using System.Globalization;
using System.Text;

namespace GeoPandas.Net.Analytics;

/// <summary>A Lagrange multiplier test for spatial dependence in OLS residuals.</summary>
public sealed record LagrangeMultiplier(string Name, double Statistic, int Df, double PValue);

/// <summary>Coefficients and fit statistics of an OLS, spatial lag or spatial error model (PySAL <c>spreg</c> equivalent).</summary>
public sealed class RegressionResult
{
    internal RegressionResult(string model, string dependent, string[] names, double[] betas, double[] se, double[] stat, double[] p, bool tStats,
        double logLik, int n, int k, double sigma2, double r2, double[] predicted, double[] residuals)
    {
        Model = model; Dependent = dependent; Names = names; Betas = betas; StdErrors = se; Statistics = stat; PValues = p; UsesTStatistics = tStats;
        LogLikelihood = logLik; N = n; K = k; Sigma2 = sigma2; R2 = r2; Predicted = predicted; Residuals = residuals;
    }

    /// <summary>"OLS", "ML spatial lag" or "ML spatial error".</summary>
    public string Model { get; }
    public string Dependent { get; }
    /// <summary>Coefficient names: CONSTANT, the regressors, then W_y (lag) or lambda (error).</summary>
    public IReadOnlyList<string> Names { get; }
    public double[] Betas { get; }
    public double[] StdErrors { get; }
    /// <summary>t statistics (OLS) or z statistics (ML models).</summary>
    public double[] Statistics { get; }
    public double[] PValues { get; }
    public bool UsesTStatistics { get; }
    public double LogLikelihood { get; }
    public int N { get; }
    /// <summary>Number of coefficients used in AIC/SC: β plus ρ for the lag model; β only for the error model (GeoDa / spreg convention).</summary>
    public int K { get; }
    public double Sigma2 { get; }
    /// <summary>R² for OLS; squared correlation of observed and predicted values (pseudo R²) for the ML models.</summary>
    public double R2 { get; }
    public double AdjustedR2 => 1 - (1 - R2) * (N - 1) / (N - K);
    public double Aic => -2 * LogLikelihood + 2 * K;
    public double Schwarz => -2 * LogLikelihood + K * Math.Log(N);
    public double[] Predicted { get; }
    public double[] Residuals { get; }
    /// <summary>Spatial autoregressive coefficient ρ (lag model) or λ (error model).</summary>
    public double? Rho => Model.Contains("lag", StringComparison.Ordinal) || Model.Contains("error", StringComparison.Ordinal) ? Betas[^1] : null;
    /// <summary>Lagrange multiplier diagnostics (OLS with weights only).</summary>
    public IReadOnlyList<LagrangeMultiplier> Diagnostics { get; internal set; } = [];

    /// <summary>Coefficient table: name, coefficient, std_error, t or z, p_value.</summary>
    public DataFrame Coefficients() => new(
        Column.Create("variable", Names.ToArray()), Column.Create("coefficient", Betas), Column.Create("std_error", StdErrors),
        Column.Create(UsesTStatistics ? "t" : "z", Statistics), Column.Create("p_value", PValues));

    public override string ToString()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine(ci, $"{Model}: {Dependent} ~ {string.Join(" + ", Names.Where(n => n != "CONSTANT"))}  (n = {N})");
        sb.AppendLine(ci, $"  {(Model == "OLS" ? "R²" : "pseudo R²")} {R2:F4}   log L {LogLikelihood:F3}   AIC {Aic:F3}   SC {Schwarz:F3}   σ² {Sigma2:F4}");
        for (int i = 0; i < Betas.Length; i++)
            sb.AppendLine(ci, $"  {Names[i],-14} {Betas[i],12:F6} {StdErrors[i],12:F6} {(UsesTStatistics ? "t" : "z")} {Statistics[i],9:F3}  p {PValues[i]:F4}");
        foreach (var d in Diagnostics) sb.AppendLine(ci, $"  {d.Name,-24} {d.Statistic,10:F4}  df {d.Df}  p {d.PValue:F4}");
        return sb.ToString();
    }
}

/// <summary>
/// Regression with spatial effects: OLS with Lagrange multiplier diagnostics, and maximum-likelihood spatial lag and spatial error
/// models (Anselin 1988; the PySAL <c>spreg</c> <c>OLS</c>, <c>ML_Lag</c> and <c>ML_Error</c> estimators with the "full" method).
/// The ML models build dense n×n matrices, so they are meant for up to a few thousand observations.
/// </summary>
public static class SpatialRegression
{
    private const int MaxDenseN = 6000;

    /// <summary>Ordinary least squares with an intercept; with <paramref name="w"/> the LM-lag/LM-error diagnostics are added.</summary>
    public static RegressionResult Ols(GeoDataFrame gdf, string dependent, IReadOnlyList<string> regressors, SpatialWeights? w = null) =>
        Ols(Values(gdf, dependent), Design(gdf, regressors), w, dependent, regressors);

    public static RegressionResult SpatialLag(GeoDataFrame gdf, string dependent, IReadOnlyList<string> regressors, SpatialWeights w) =>
        SpatialLag(Values(gdf, dependent), Design(gdf, regressors), w, dependent, regressors);

    public static RegressionResult SpatialError(GeoDataFrame gdf, string dependent, IReadOnlyList<string> regressors, SpatialWeights w) =>
        SpatialError(Values(gdf, dependent), Design(gdf, regressors), w, dependent, regressors);

    private static double[] Values(DataFrame df, string c)
    {
        var col = df[c];
        var v = new double[df.RowCount];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = col.GetDouble(i);
            if (double.IsNaN(v[i])) throw new ArgumentException($"Column '{c}' has missing values (row {i}).");
        }
        return v;
    }

    private static double[,] Design(DataFrame df, IReadOnlyList<string> regressors)
    {
        var x = new double[df.RowCount, regressors.Count];
        for (int j = 0; j < regressors.Count; j++)
        {
            var v = Values(df, regressors[j]);
            for (int i = 0; i < v.Length; i++) x[i, j] = v[i];
        }
        return x;
    }

    /// <summary>Adds the constant column.</summary>
    private static double[,] WithConstant(double[,] x)
    {
        int n = x.GetLength(0), k = x.GetLength(1);
        var r = new double[n, k + 1];
        for (int i = 0; i < n; i++)
        {
            r[i, 0] = 1;
            for (int j = 0; j < k; j++) r[i, j + 1] = x[i, j];
        }
        return r;
    }

    private static string[] Names(IReadOnlyList<string>? regressors, int k) =>
        ["CONSTANT", .. regressors ?? Enumerable.Range(1, k).Select(i => $"x{i}").ToArray()];

    public static RegressionResult Ols(double[] y, double[,] xNoConstant, SpatialWeights? w = null, string dependent = "y", IReadOnlyList<string>? regressors = null)
    {
        var x = WithConstant(xNoConstant);
        int n = y.Length, k = x.GetLength(1);
        var xtxi = Dense.InverseSpd(Dense.XtX(x)) ?? throw new InvalidOperationException("XᵀX is singular (collinear regressors).");
        var b = Dense.Mul(xtxi, Dense.Xtv(x, y));
        var pred = Dense.Mul(x, b);
        var e = y.Select((v, i) => v - pred[i]).ToArray();
        double ee = Dense.Dot(e, e), ybar = y.Average(), tss = y.Sum(v => (v - ybar) * (v - ybar));
        double s2 = ee / (n - k);
        var se = Enumerable.Range(0, k).Select(i => Math.Sqrt(s2 * xtxi[i, i])).ToArray();
        var t = b.Select((v, i) => v / se[i]).ToArray();
        var p = t.Select(v => Distributions.StudentTTwoSided(v, n - k)).ToArray();
        double ll = -n / 2.0 * Math.Log(2 * Math.PI) - n / 2.0 * Math.Log(ee / n) - n / 2.0;
        var res = new RegressionResult("OLS", dependent, Names(regressors, k - 1), b, se, t, p, true, ll, n, k, s2, 1 - ee / tss, pred, e);
        if (w is not null) res.Diagnostics = LmTests(y, x, b, e, w);
        return res;
    }

    /// <summary>LM-lag, LM-error, their robust forms and LM-SARMA (Anselin et al. 1996).</summary>
    private static List<LagrangeMultiplier> LmTests(double[] y, double[,] x, double[] b, double[] e, SpatialWeights w)
    {
        Check(w, y.Length);
        int n = y.Length;
        double ee = Dense.Dot(e, e), s2 = ee / n;
        var we = w.Lag(e); var wy = w.Lag(y);
        double utwuDs = Dense.Dot(e, we) / s2, utwyDs = Dense.Dot(e, wy) / s2;
        // T = tr(WᵀW + WW)
        double t = 0;
        var lookup = new Dictionary<(int, int), double>();
        for (int i = 0; i < n; i++) for (int k = 0; k < w.Neighbors[i].Length; k++) lookup[(i, w.Neighbors[i][k])] = w.Weights[i][k];
        foreach (var ((i, j), v) in lookup) t += v * v + v * lookup.GetValueOrDefault((j, i));
        // (WXb)ᵀ M (WXb)
        var wxb = w.Lag(Dense.Mul(x, b));
        var xtxi = Dense.InverseSpd(Dense.XtX(x))!;
        var proj = Dense.Mul(x, Dense.Mul(xtxi, Dense.Xtv(x, wxb)));
        double mq = 0;
        for (int i = 0; i < n; i++) mq += wxb[i] * (wxb[i] - proj[i]);
        double nj = mq / s2 + t;
        double lmErr = utwuDs * utwuDs / t, lmLag = utwyDs * utwyDs / nj;
        double rlmLag = Math.Pow(utwyDs - utwuDs, 2) / (nj - t);
        double rlmErr = Math.Pow(utwuDs - t * utwyDs / nj, 2) / (t - t * t / nj);
        double sarma = rlmLag + lmErr;
        return
        [
            new("Lagrange Multiplier (lag)", lmLag, 1, Distributions.ChiSquareSf(lmLag, 1)),
            new("Robust LM (lag)", rlmLag, 1, Distributions.ChiSquareSf(rlmLag, 1)),
            new("Lagrange Multiplier (error)", lmErr, 1, Distributions.ChiSquareSf(lmErr, 1)),
            new("Robust LM (error)", rlmErr, 1, Distributions.ChiSquareSf(rlmErr, 1)),
            new("Lagrange Multiplier (SARMA)", sarma, 2, Distributions.ChiSquareSf(sarma, 2)),
        ];
    }

    private static void Check(SpatialWeights w, int n)
    {
        if (w.N != n) throw new ArgumentException($"Weights have {w.N} observations, data has {n}.");
    }

    private static double[,] DenseW(SpatialWeights w)
    {
        if (w.N > MaxDenseN) throw new NotSupportedException($"ML spatial models use dense n×n matrices; n = {w.N} exceeds {MaxDenseN}.");
        var m = new double[w.N, w.N];
        for (int i = 0; i < w.N; i++) for (int k = 0; k < w.Neighbors[i].Length; k++) m[i, w.Neighbors[i][k]] = w.Weights[i][k];
        return m;
    }

    /// <summary>ln|I − ρW| for the optimiser: eigenvalues when W is (similar to) symmetric, else an LU decomposition per call.</summary>
    private static Func<double, double> LogDetFunction(SpatialWeights w, double[,] wd)
    {
        int n = w.N;
        bool symmetric = true;
        for (int i = 0; i < n && symmetric; i++) for (int j = 0; j < i; j++) if (Math.Abs(wd[i, j] - wd[j, i]) > 1e-12) { symmetric = false; break; }
        double[]? eig = null;
        if (symmetric) eig = Dense.SymmetricEigenvalues((double[,])wd.Clone());
        else
        {
            // row-standardised symmetric binary structure: W = D⁻¹A is similar to D^-½ A D^-½
            bool binarySym = true;
            for (int i = 0; i < n && binarySym; i++)
            {
                var wt = w.Weights[i];
                if (wt.Length > 0 && wt.Any(v => Math.Abs(v - wt[0]) > 1e-12)) binarySym = false;
                foreach (var j in w.Neighbors[i]) if (wd[j, i] == 0) { binarySym = false; break; }
            }
            if (binarySym)
            {
                var deg = Enumerable.Range(0, n).Select(i => (double)w.Neighbors[i].Length).ToArray();
                var s = new double[n, n];
                for (int i = 0; i < n; i++) foreach (var j in w.Neighbors[i]) s[i, j] = 1 / Math.Sqrt(deg[i] * deg[j]);
                eig = Dense.SymmetricEigenvalues(s);
            }
        }
        if (eig is not null)
        {
            var ev = eig;
            return rho =>
            {
                double s = 0;
                foreach (var l in ev) { double v = 1 - rho * l; if (v <= 0) return double.NegativeInfinity; s += Math.Log(v); }
                return s;
            };
        }
        return rho =>
        {
            var a = new double[n, n];
            for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) a[i, j] = -rho * wd[i, j]; a[i, i] += 1; }
            var ld = Dense.LogDet(a);
            return double.IsNaN(ld) ? double.NegativeInfinity : ld;
        };
    }

    /// <summary>W (I − ρW)⁻¹ and its traces tr(WA), tr(WA²), tr(WAᵀWA).</summary>
    private static (double[,] Wa, double Tr1, double Tr2, double Tr3) WaTraces(double[,] wd, double rho)
    {
        int n = wd.GetLength(0);
        var a = new double[n, n];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) a[i, j] = -rho * wd[i, j]; a[i, i] += 1; }
        var ai = Dense.Inverse(a) ?? throw new InvalidOperationException("I − ρW is singular.");
        var wa = new double[n, n];
        Parallel.For(0, n, i =>
        {
            for (int k = 0; k < n; k++)
            {
                double wik = wd[i, k];
                if (wik == 0) continue;
                for (int j = 0; j < n; j++) wa[i, j] += wik * ai[k, j];
            }
        });
        double tr1 = 0, tr2 = 0, tr3 = 0;
        for (int i = 0; i < n; i++)
        {
            tr1 += wa[i, i];
            for (int j = 0; j < n; j++) { tr2 += wa[i, j] * wa[j, i]; tr3 += wa[i, j] * wa[i, j]; }
        }
        return (wa, tr1, tr2, tr3);
    }

    private static double SquaredCorrelation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
        return sab * sab / (saa * sbb);
    }

    /// <summary>Maximum-likelihood spatial lag model y = ρWy + Xβ + ε.</summary>
    public static RegressionResult SpatialLag(double[] y, double[,] xNoConstant, SpatialWeights w, string dependent = "y", IReadOnlyList<string>? regressors = null)
    {
        Check(w, y.Length);
        var x = WithConstant(xNoConstant);
        int n = y.Length, k = x.GetLength(1);
        var wd = DenseW(w);
        var wy = w.Lag(y);
        var xtxi = Dense.InverseSpd(Dense.XtX(x)) ?? throw new InvalidOperationException("XᵀX is singular (collinear regressors).");
        var b0 = Dense.Mul(xtxi, Dense.Xtv(x, y)); var b1 = Dense.Mul(xtxi, Dense.Xtv(x, wy));
        var p0 = Dense.Mul(x, b0); var p1 = Dense.Mul(x, b1);
        var e0 = y.Select((v, i) => v - p0[i]).ToArray(); var e1 = wy.Select((v, i) => v - p1[i]).ToArray();
        double e0e0 = Dense.Dot(e0, e0), e0e1 = Dense.Dot(e0, e1), e1e1 = Dense.Dot(e1, e1);
        var logDet = LogDetFunction(w, wd);
        double Neg(double rho)
        {
            double ee = e0e0 - 2 * rho * e0e1 + rho * rho * e1e1;
            var jac = logDet(rho);
            return double.IsNegativeInfinity(jac) ? double.MaxValue : n / 2.0 * Math.Log(ee / n) - jac;
        }
        double rho = Dense.MinimizeBounded(Neg, -1, 1);
        var b = b0.Select((v, i) => v - rho * b1[i]).ToArray();
        var xb = Dense.Mul(x, b);
        var u = y.Select((v, i) => v - xb[i] - rho * wy[i]).ToArray();
        double sig2 = Dense.Dot(u, u) / n;
        double ll = -Neg(rho) - n / 2.0 * Math.Log(2 * Math.PI) - n / 2.0;

        // asymptotic variance from the information matrix of (β, ρ, σ²)
        var (wa, tr1, tr2, tr3) = WaTraces(wd, rho);
        var wpredy = Dense.Mul(wa, xb);
        double wpyTwpy = Dense.Dot(wpredy, wpredy);
        var xTwpy = Dense.Xtv(x, wpredy);
        var xtx = Dense.XtX(x);
        var info = new double[k + 2, k + 2];
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++) info[i, j] = xtx[i, j] / sig2;
            info[i, k] = info[k, i] = xTwpy[i] / sig2;
        }
        info[k, k] = tr2 + tr3 + wpyTwpy / sig2;
        info[k, k + 1] = info[k + 1, k] = tr1 / sig2;
        info[k + 1, k + 1] = n / (2 * sig2 * sig2);
        var vm = Dense.Inverse(info) ?? throw new InvalidOperationException("Information matrix is singular.");
        var betas = b.Append(rho).ToArray();
        var se = Enumerable.Range(0, k + 1).Select(i => Math.Sqrt(vm[i, i])).ToArray();
        var z = betas.Select((v, i) => v / se[i]).ToArray();
        var pv = z.Select(Distributions.NormalTwoSided).ToArray();
        var pred = y.Select((v, i) => v - u[i]).ToArray();
        return new RegressionResult("ML spatial lag", dependent, [.. Names(regressors, k - 1), "W_" + dependent], betas, se, z, pv, false,
            ll, n, k + 1, sig2, SquaredCorrelation(y, pred), pred, u);
    }

    /// <summary>Maximum-likelihood spatial error model y = Xβ + u, u = λWu + ε.</summary>
    public static RegressionResult SpatialError(double[] y, double[,] xNoConstant, SpatialWeights w, string dependent = "y", IReadOnlyList<string>? regressors = null)
    {
        Check(w, y.Length);
        var x = WithConstant(xNoConstant);
        int n = y.Length, k = x.GetLength(1);
        var wd = DenseW(w);
        var wy = w.Lag(y);
        var wx = new double[n, k];
        for (int j = 0; j < k; j++)
        {
            var col = new double[n];
            for (int i = 0; i < n; i++) col[i] = x[i, j];
            var lag = w.Lag(col);
            for (int i = 0; i < n; i++) wx[i, j] = lag[i];
        }
        var logDet = LogDetFunction(w, wd);
        (double[] B, double Sig2, double[,] Xs, double[] Ys) Filtered(double lam)
        {
            var ys = new double[n]; var xs = new double[n, k];
            for (int i = 0; i < n; i++)
            {
                ys[i] = y[i] - lam * wy[i];
                for (int j = 0; j < k; j++) xs[i, j] = x[i, j] - lam * wx[i, j];
            }
            var xsxsi = Dense.InverseSpd(Dense.XtX(xs));
            if (xsxsi is null) return (new double[k], double.NaN, xs, ys);
            var bb = Dense.Mul(xsxsi, Dense.Xtv(xs, ys));
            var p = Dense.Mul(xs, bb);
            double ee = 0;
            for (int i = 0; i < n; i++) ee += (ys[i] - p[i]) * (ys[i] - p[i]);
            return (bb, ee / n, xs, ys);
        }
        double Neg(double lam)
        {
            var (_, s2, _, _) = Filtered(lam);
            var jac = logDet(lam);
            return double.IsNaN(s2) || double.IsNegativeInfinity(jac) ? double.MaxValue : n / 2.0 * Math.Log(s2) - jac;
        }
        double lambda = Dense.MinimizeBounded(Neg, -1, 1);
        var (b, sig2, xsF, _) = Filtered(lambda);
        double ll = -Neg(lambda) - n / 2.0 * Math.Log(2 * Math.PI) - n / 2.0;
        var xsxsInv = Dense.InverseSpd(Dense.XtX(xsF))!;
        var (_, tr1, tr2, tr3) = WaTraces(wd, lambda);
        var v2 = new double[2, 2] { { tr2 + tr3, tr1 / sig2 }, { tr1 / sig2, n / (2 * sig2 * sig2) } };
        var v2i = Dense.Inverse(v2)!;
        var se = Enumerable.Range(0, k).Select(i => Math.Sqrt(sig2 * xsxsInv[i, i])).Append(Math.Sqrt(v2i[0, 0])).ToArray();
        var betas = b.Append(lambda).ToArray();
        var z = betas.Select((v, i) => v / se[i]).ToArray();
        var pv = z.Select(Distributions.NormalTwoSided).ToArray();
        var pred = Dense.Mul(x, b);
        var u = y.Select((v, i) => v - pred[i]).ToArray();
        // K counts the β only, as GeoDa and spreg do for the error model (λ is reported separately), so AIC/SC match them
        return new RegressionResult("ML spatial error", dependent, [.. Names(regressors, k - 1), "lambda"], betas, se, z, pv, false,
            ll, n, k, sig2, SquaredCorrelation(y, pred), pred, u);
    }
}
