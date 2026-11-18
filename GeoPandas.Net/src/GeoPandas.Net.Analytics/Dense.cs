namespace GeoPandas.Net.Analytics;

/// <summary>Small dense linear-algebra kernels for the regression models (row-major <c>double[,]</c>).</summary>
internal static class Dense
{
    public static double[,] Identity(int n)
    {
        var m = new double[n, n];
        for (int i = 0; i < n; i++) m[i, i] = 1;
        return m;
    }

    /// <summary>XᵀX for an n×k design matrix, optionally with row weights.</summary>
    public static double[,] XtX(double[,] x, double[]? w = null)
    {
        int n = x.GetLength(0), k = x.GetLength(1);
        var r = new double[k, k];
        for (int i = 0; i < n; i++)
        {
            double wi = w is null ? 1 : w[i];
            if (wi == 0) continue;
            for (int a = 0; a < k; a++)
            {
                double xa = x[i, a] * wi;
                for (int b = a; b < k; b++) r[a, b] += xa * x[i, b];
            }
        }
        for (int a = 0; a < k; a++) for (int b = 0; b < a; b++) r[a, b] = r[b, a];
        return r;
    }

    /// <summary>Xᵀv (optionally weighted).</summary>
    public static double[] Xtv(double[,] x, ReadOnlySpan<double> v, double[]? w = null)
    {
        int n = x.GetLength(0), k = x.GetLength(1);
        var r = new double[k];
        for (int i = 0; i < n; i++)
        {
            double vi = v[i] * (w is null ? 1 : w[i]);
            if (vi == 0) continue;
            for (int a = 0; a < k; a++) r[a] += x[i, a] * vi;
        }
        return r;
    }

    public static double[] Mul(double[,] a, ReadOnlySpan<double> v)
    {
        int n = a.GetLength(0), k = a.GetLength(1);
        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < k; j++) s += a[i, j] * v[j];
            r[i] = s;
        }
        return r;
    }

    public static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Inverse of a symmetric positive-definite matrix (Cholesky); null when not positive definite.</summary>
    public static double[,]? InverseSpd(double[,] a)
    {
        int n = a.GetLength(0);
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (s <= 0) return null;
                    l[i, i] = Math.Sqrt(s);
                }
                else l[i, j] = s / l[j, j];
            }
        // inverse of L, then (L⁻¹)ᵀ L⁻¹
        var li = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            li[i, i] = 1 / l[i, i];
            for (int j = 0; j < i; j++)
            {
                double s = 0;
                for (int k = j; k < i; k++) s -= l[i, k] * li[k, j];
                li[i, j] = s / l[i, i];
            }
        }
        var inv = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double s = 0;
                for (int k = i; k < n; k++) s += li[k, i] * li[k, j];
                inv[i, j] = inv[j, i] = s;
            }
        return inv;
    }

    /// <summary>General inverse by Gauss-Jordan with partial pivoting; null when singular.</summary>
    public static double[,]? Inverse(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        var inv = Identity(n);
        for (int p = 0; p < n; p++)
        {
            int max = p;
            for (int r = p + 1; r < n; r++) if (Math.Abs(m[r, p]) > Math.Abs(m[max, p])) max = r;
            if (Math.Abs(m[max, p]) < 1e-300) return null;
            if (max != p)
                for (int c = 0; c < n; c++) { (m[p, c], m[max, c]) = (m[max, c], m[p, c]); (inv[p, c], inv[max, c]) = (inv[max, c], inv[p, c]); }
            double d = 1 / m[p, p];
            for (int c = 0; c < n; c++) { m[p, c] *= d; inv[p, c] *= d; }
            for (int r = 0; r < n; r++)
            {
                if (r == p) continue;
                double f = m[r, p];
                if (f == 0) continue;
                for (int c = 0; c < n; c++) { m[r, c] -= f * m[p, c]; inv[r, c] -= f * inv[p, c]; }
            }
        }
        return inv;
    }

    /// <summary>ln|det A| by LU decomposition (A is destroyed); NaN for a non-positive determinant.</summary>
    public static double LogDet(double[,] a)
    {
        int n = a.GetLength(0);
        double logdet = 0; int sign = 1;
        for (int p = 0; p < n; p++)
        {
            int max = p;
            for (int r = p + 1; r < n; r++) if (Math.Abs(a[r, p]) > Math.Abs(a[max, p])) max = r;
            if (a[max, p] == 0) return double.NegativeInfinity;
            if (max != p) { for (int c = 0; c < n; c++) (a[p, c], a[max, c]) = (a[max, c], a[p, c]); sign = -sign; }
            double piv = a[p, p];
            if (piv < 0) sign = -sign;
            logdet += Math.Log(Math.Abs(piv));
            for (int r = p + 1; r < n; r++)
            {
                double f = a[r, p] / piv;
                if (f == 0) continue;
                for (int c = p + 1; c < n; c++) a[r, c] -= f * a[p, c];
            }
        }
        return sign > 0 ? logdet : double.NaN;
    }

    /// <summary>Eigenvalues of a real symmetric matrix (Householder tridiagonalisation + implicit QL). A is destroyed.</summary>
    public static double[] SymmetricEigenvalues(double[,] a)
    {
        int n = a.GetLength(0);
        var d = new double[n]; var e = new double[n];
        // tred2 without eigenvectors
        for (int i = n - 1; i > 0; i--)
        {
            int l = i - 1;
            double h = 0, scale = 0;
            if (l > 0)
            {
                for (int k = 0; k <= l; k++) scale += Math.Abs(a[i, k]);
                if (scale == 0) e[i] = a[i, l];
                else
                {
                    for (int k = 0; k <= l; k++) { a[i, k] /= scale; h += a[i, k] * a[i, k]; }
                    double f = a[i, l];
                    double g = f >= 0 ? -Math.Sqrt(h) : Math.Sqrt(h);
                    e[i] = scale * g;
                    h -= f * g;
                    a[i, l] = f - g;
                    f = 0;
                    for (int j = 0; j <= l; j++)
                    {
                        g = 0;
                        for (int k = 0; k <= j; k++) g += a[j, k] * a[i, k];
                        for (int k = j + 1; k <= l; k++) g += a[k, j] * a[i, k];
                        e[j] = g / h;
                        f += e[j] * a[i, j];
                    }
                    double hh = f / (h + h);
                    for (int j = 0; j <= l; j++)
                    {
                        f = a[i, j];
                        e[j] = g = e[j] - hh * f;
                        for (int k = 0; k <= j; k++) a[j, k] -= f * e[k] + g * a[i, k];
                    }
                }
            }
            else e[i] = a[i, l];
            d[i] = h;
        }
        for (int i = 0; i < n; i++) d[i] = a[i, i];
        // tqli
        for (int i = 1; i < n; i++) e[i - 1] = e[i];
        if (n > 0) e[n - 1] = 0;
        for (int l = 0; l < n; l++)
        {
            int iter = 0, m;
            do
            {
                for (m = l; m < n - 1; m++)
                {
                    double dd = Math.Abs(d[m]) + Math.Abs(d[m + 1]);
                    if (Math.Abs(e[m]) <= 1e-15 * dd) break;
                }
                if (m != l)
                {
                    if (iter++ == 60) throw new InvalidOperationException("Eigenvalue iteration did not converge.");
                    double g = (d[l + 1] - d[l]) / (2 * e[l]);
                    double r = Hypot(g, 1);
                    g = d[m] - d[l] + e[l] / (g + (g >= 0 ? Math.Abs(r) : -Math.Abs(r)));
                    double s = 1, c = 1, p = 0;
                    int i;
                    for (i = m - 1; i >= l; i--)
                    {
                        double f = s * e[i], b = c * e[i];
                        e[i + 1] = r = Hypot(f, g);
                        if (r == 0) { d[i + 1] -= p; e[m] = 0; break; }
                        s = f / r; c = g / r;
                        g = d[i + 1] - p;
                        r = (d[i] - g) * s + 2 * c * b;
                        d[i + 1] = g + (p = s * r);
                        g = c * r - b;
                    }
                    if (r == 0 && i >= l) continue;
                    d[l] -= p; e[l] = g; e[m] = 0;
                }
            } while (m != l);
        }
        Array.Sort(d);
        return d;
    }

    private static double Hypot(double a, double b)
    {
        double x = Math.Abs(a), y = Math.Abs(b);
        if (x > y) { double r = y / x; return x * Math.Sqrt(1 + r * r); }
        if (y == 0) return 0;
        { double r = x / y; return y * Math.Sqrt(1 + r * r); }
    }

    /// <summary>Bounded scalar minimisation (Brent's method, as scipy's <c>fminbound</c>).</summary>
    public static double MinimizeBounded(Func<double, double> f, double a, double b, double xtol = 1e-7, int maxIter = 500)
    {
        const double golden = 0.3819660112501051;
        double v = a + golden * (b - a), w = v, x = v, e = 0, d = 0;
        double fx = f(x), fv = fx, fw = fx;
        for (int it = 0; it < maxIter; it++)
        {
            double xm = 0.5 * (a + b), tol1 = 1.4901161193847656e-8 * Math.Abs(x) + xtol / 3, tol2 = 2 * tol1;
            if (Math.Abs(x - xm) <= tol2 - 0.5 * (b - a)) break;
            bool golden_step = true;
            if (Math.Abs(e) > tol1)
            {
                double r = (x - w) * (fx - fv), q = (x - v) * (fx - fw), p = (x - v) * q - (x - w) * r;
                q = 2 * (q - r);
                if (q > 0) p = -p;
                q = Math.Abs(q);
                double etemp = e;
                e = d;
                if (!(Math.Abs(p) >= Math.Abs(0.5 * q * etemp) || p <= q * (a - x) || p >= q * (b - x)))
                {
                    d = p / q;
                    double u0 = x + d;
                    if (u0 - a < tol2 || b - u0 < tol2) d = xm >= x ? tol1 : -tol1;
                    golden_step = false;
                }
            }
            if (golden_step) { e = x >= xm ? a - x : b - x; d = golden * e; }
            double u = Math.Abs(d) >= tol1 ? x + d : x + (d >= 0 ? tol1 : -tol1);
            double fu = f(u);
            if (fu <= fx)
            {
                if (u >= x) a = x; else b = x;
                v = w; fv = fw; w = x; fw = fx; x = u; fx = fu;
            }
            else
            {
                if (u < x) a = u; else b = u;
                if (fu <= fw || w == x) { v = w; fv = fw; w = u; fw = fu; }
                else if (fu <= fv || v == x || v == w) { v = u; fv = fu; }
            }
        }
        return x;
    }
}

/// <summary>Distribution functions used for p-values.</summary>
public static class Distributions
{
    /// <summary>Standard normal CDF.</summary>
    public static double NormalCdf(double x) => Autocorrelation.NormalCdf(x);

    /// <summary>Two-sided p-value of a z statistic (computed from the upper tail, so tiny p-values keep their precision).</summary>
    public static double NormalTwoSided(double z) => ChiSquareSf(z * z, 1);

    /// <summary>Upper-tail probability of χ²(df).</summary>
    public static double ChiSquareSf(double x, double df) => x <= 0 ? 1 : RegularizedGammaQ(df / 2, x / 2);

    /// <summary>Upper regularised gamma Q(a, x) = 1 − P(a, x), by continued fraction for x ≥ a + 1.</summary>
    public static double RegularizedGammaQ(double a, double x)
    {
        if (x <= 0) return 1;
        if (x < a + 1) return 1 - RegularizedGammaP(a, x);
        double gln = LogGamma(a);
        double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, h = d;
        for (int i = 1; i < 1000; i++)
        {
            double an = -i * (i - a); b += 2;
            d = an * d + b; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d; double del = d * c; h *= del;
            if (Math.Abs(del - 1) < 1e-15) break;
        }
        return Math.Exp(-x + a * Math.Log(x) - gln) * h;
    }

    /// <summary>Two-sided p-value of a Student t statistic.</summary>
    public static double StudentTTwoSided(double t, double df) => RegularizedBeta(df / (df + t * t), df / 2, 0.5);

    public static double RegularizedGammaP(double a, double x)
    {
        if (x <= 0) return 0;
        double gln = LogGamma(a);
        if (x < a + 1)
        {
            double ap = a, sum = 1 / a, del = sum;
            for (int n = 0; n < 1000; n++) { ap++; del *= x / ap; sum += del; if (Math.Abs(del) < Math.Abs(sum) * 1e-15) break; }
            return sum * Math.Exp(-x + a * Math.Log(x) - gln);
        }
        double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, h = d;
        for (int i = 1; i < 1000; i++)
        {
            double an = -i * (i - a); b += 2;
            d = an * d + b; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d; double del = d * c; h *= del;
            if (Math.Abs(del - 1) < 1e-15) break;
        }
        return 1 - Math.Exp(-x + a * Math.Log(x) - gln) * h;
    }

    /// <summary>Regularised incomplete beta I_x(a, b).</summary>
    public static double RegularizedBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double bt = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        return x < (a + 1) / (a + b + 2) ? bt * BetaCf(x, a, b) / a : 1 - bt * BetaCf(1 - x, b, a) / b;
    }

    private static double BetaCf(double x, double a, double b)
    {
        double qab = a + b, qap = a + 1, qam = a - 1, c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < 1e-300) d = 1e-300;
        d = 1 / d; double h = d;
        for (int m = 1; m <= 1000; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = 1 + aa / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d; h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = 1 + aa / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d; double del = d * c; h *= del;
            if (Math.Abs(del - 1) < 1e-15) break;
        }
        return h;
    }

    public static double LogGamma(double x)
    {
        double[] c = [76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5];
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        foreach (var v in c) ser += v / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }
}
