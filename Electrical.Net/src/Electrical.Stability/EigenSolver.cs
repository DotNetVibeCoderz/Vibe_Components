using System.Numerics;
using Electrical.Core.Numerics;

namespace Electrical.Stability;

/// <summary>
/// Eigenvalues of real non-symmetric matrices: balancing (Parlett–Reinsch), reduction to upper Hessenberg form by stabilised
/// elementary similarity transformations, then the Francis double-shift QR algorithm (EISPACK <c>balanc/elmhes/hqr</c>).
/// Eigenvectors are obtained by complex inverse iteration.
/// </summary>
public static class EigenSolver
{
    /// <summary>All eigenvalues of a square real matrix (the input is not modified).</summary>
    public static Complex[] Eigenvalues(DenseMatrix<double> matrix, bool balance = true)
    {
        if (matrix.Rows != matrix.Cols) throw new ArgumentException("Matrix must be square.", nameof(matrix));
        int n = matrix.Rows;
        if (n == 0) return [];
        // 1-based working copy (mirrors the classical algorithm formulation)
        var a = new double[n + 1, n + 1];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i + 1, j + 1] = matrix[i, j];
        if (balance) Balance(a, n);
        Hessenberg(a, n);
        for (int i = 3; i <= n; i++)
            for (int j = 1; j <= i - 2; j++) a[i, j] = 0;
        var wr = new double[n + 1];
        var wi = new double[n + 1];
        Hqr(a, n, wr, wi);
        var r = new Complex[n];
        for (int i = 0; i < n; i++) r[i] = new Complex(wr[i + 1], wi[i + 1]);
        return r.OrderByDescending(z => z.Real).ThenByDescending(z => z.Imaginary).ToArray();
    }

    public static Complex[] Eigenvalues(double[,] matrix, bool balance = true) => Eigenvalues(new DenseMatrix<double>(matrix), balance);

    /// <summary>Right eigenvector A·v = λ·v (normalised to unit 2-norm, largest component real) by inverse iteration.</summary>
    public static Complex[] RightEigenvector(DenseMatrix<double> a, Complex lambda) => InverseIteration(a, lambda, transpose: false);

    /// <summary>Left eigenvector wᵀ·A = λ·wᵀ by inverse iteration on Aᵀ.</summary>
    public static Complex[] LeftEigenvector(DenseMatrix<double> a, Complex lambda) => InverseIteration(a, lambda, transpose: true);

    /// <summary>Normalised participation factors p_k = |v_k·w_k| / Σ|v·w| of the states in the mode λ.</summary>
    public static double[] ParticipationFactors(DenseMatrix<double> a, Complex lambda)
    {
        var v = RightEigenvector(a, lambda);
        var w = LeftEigenvector(a, lambda);
        var p = new double[v.Length];
        for (int k = 0; k < v.Length; k++) p[k] = (v[k] * w[k]).Magnitude;
        double s = p.Sum();
        if (s > 0) for (int k = 0; k < p.Length; k++) p[k] /= s;
        return p;
    }

    private static Complex[] InverseIteration(DenseMatrix<double> a, Complex lambda, bool transpose)
    {
        int n = a.Rows;
        double scale = 0;
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) scale = Math.Max(scale, Math.Abs(a[i, j]));
        var shift = lambda + new Complex(1e-10 * Math.Max(scale, 1), 1e-10 * Math.Max(scale, 1));
        var m = new DenseMatrix<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = transpose ? a[j, i] : a[i, j];
        for (int i = 0; i < n; i++) m[i, i] -= shift;
        var lu = new DenseLU<Complex>(m);
        var x = new Complex[n];
        for (int i = 0; i < n; i++) x[i] = new Complex(1.0 / Math.Sqrt(n), 0.1 * (i % 3));
        for (int it = 0; it < 4; it++)
        {
            x = lu.Solve(x);
            double norm = Math.Sqrt(x.Sum(z => z.Magnitude * z.Magnitude));
            for (int i = 0; i < n; i++) x[i] /= norm;
        }
        int big = 0;
        for (int i = 1; i < n; i++) if (x[i].Magnitude > x[big].Magnitude) big = i;
        var rot = Complex.Conjugate(x[big]) / x[big].Magnitude;
        for (int i = 0; i < n; i++) x[i] *= rot;
        return x;
    }

    private static void Balance(double[,] a, int n)
    {
        const double radix = 2.0, sqrdx = radix * radix;
        bool done = false;
        while (!done)
        {
            done = true;
            for (int i = 1; i <= n; i++)
            {
                double r = 0, c = 0;
                for (int j = 1; j <= n; j++)
                    if (j != i) { c += Math.Abs(a[j, i]); r += Math.Abs(a[i, j]); }
                if (c == 0 || r == 0) continue;
                double g = r / radix, f = 1, s = c + r;
                while (c < g) { f *= radix; c *= sqrdx; }
                g = r * radix;
                while (c > g) { f /= radix; c /= sqrdx; }
                if ((c + r) / f < 0.95 * s)
                {
                    done = false;
                    g = 1 / f;
                    for (int j = 1; j <= n; j++) a[i, j] *= g;
                    for (int j = 1; j <= n; j++) a[j, i] *= f;
                }
            }
        }
    }

    private static void Hessenberg(double[,] a, int n)
    {
        for (int m = 2; m < n; m++)
        {
            double x = 0;
            int i = m;
            for (int j = m; j <= n; j++)
                if (Math.Abs(a[j, m - 1]) > Math.Abs(x)) { x = a[j, m - 1]; i = j; }
            if (i != m)
            {
                for (int j = m - 1; j <= n; j++) (a[i, j], a[m, j]) = (a[m, j], a[i, j]);
                for (int j = 1; j <= n; j++) (a[j, i], a[j, m]) = (a[j, m], a[j, i]);
            }
            if (x != 0)
            {
                for (i = m + 1; i <= n; i++)
                {
                    double y = a[i, m - 1];
                    if (y == 0) continue;
                    y /= x;
                    a[i, m - 1] = y;
                    for (int j = m; j <= n; j++) a[i, j] -= y * a[m, j];
                    for (int j = 1; j <= n; j++) a[j, m] += y * a[j, i];
                }
            }
        }
    }

    private static void Hqr(double[,] a, int n, double[] wr, double[] wi)
    {
        double anorm = 0;
        for (int i = 1; i <= n; i++)
            for (int j = Math.Max(i - 1, 1); j <= n; j++) anorm += Math.Abs(a[i, j]);
        int nn = n;
        double t = 0;
        double p = 0, q = 0, r = 0, s, w, x, y, z = 0;
        while (nn >= 1)
        {
            int its = 0, l;
            do
            {
                for (l = nn; l >= 2; l--)
                {
                    s = Math.Abs(a[l - 1, l - 1]) + Math.Abs(a[l, l]);
                    if (s == 0) s = anorm;
                    if (Math.Abs(a[l, l - 1]) + s == s) { a[l, l - 1] = 0; break; }
                }
                if (l < 1) l = 1;
                x = a[nn, nn];
                if (l == nn)
                {
                    wr[nn] = x + t; wi[nn--] = 0;
                }
                else
                {
                    y = a[nn - 1, nn - 1];
                    w = a[nn, nn - 1] * a[nn - 1, nn];
                    if (l == nn - 1)
                    {
                        p = 0.5 * (y - x);
                        q = p * p + w;
                        z = Math.Sqrt(Math.Abs(q));
                        x += t;
                        if (q >= 0)
                        {
                            z = p + (p >= 0 ? Math.Abs(z) : -Math.Abs(z));
                            wr[nn - 1] = wr[nn] = x + z;
                            if (z != 0) wr[nn] = x - w / z;
                            wi[nn - 1] = wi[nn] = 0;
                        }
                        else
                        {
                            wr[nn - 1] = wr[nn] = x + p;
                            wi[nn - 1] = -(wi[nn] = z);
                        }
                        nn -= 2;
                    }
                    else
                    {
                        if (its == 60) throw new InvalidOperationException("QR eigenvalue iteration did not converge.");
                        if (its == 10 || its == 20 || its == 40)
                        {
                            t += x;
                            for (int i = 1; i <= nn; i++) a[i, i] -= x;
                            s = Math.Abs(a[nn, nn - 1]) + Math.Abs(a[nn - 1, nn - 2]);
                            y = x = 0.75 * s;
                            w = -0.4375 * s * s;
                        }
                        ++its;
                        int m;
                        for (m = nn - 2; m >= l; m--)
                        {
                            z = a[m, m];
                            r = x - z;
                            s = y - z;
                            p = (r * s - w) / a[m + 1, m] + a[m, m + 1];
                            q = a[m + 1, m + 1] - z - r - s;
                            r = a[m + 2, m + 1];
                            s = Math.Abs(p) + Math.Abs(q) + Math.Abs(r);
                            p /= s; q /= s; r /= s;
                            if (m == l) break;
                            double u = Math.Abs(a[m, m - 1]) * (Math.Abs(q) + Math.Abs(r));
                            double v = Math.Abs(p) * (Math.Abs(a[m - 1, m - 1]) + Math.Abs(z) + Math.Abs(a[m + 1, m + 1]));
                            if (u + v == v) break;
                        }
                        for (int i = m + 2; i <= nn; i++)
                        {
                            a[i, i - 2] = 0;
                            if (i != m + 2) a[i, i - 3] = 0;
                        }
                        for (int k = m; k <= nn - 1; k++)
                        {
                            if (k != m)
                            {
                                p = a[k, k - 1];
                                q = a[k + 1, k - 1];
                                r = 0;
                                if (k != nn - 1) r = a[k + 2, k - 1];
                                if ((x = Math.Abs(p) + Math.Abs(q) + Math.Abs(r)) != 0) { p /= x; q /= x; r /= x; }
                            }
                            double sq = Math.Sqrt(p * p + q * q + r * r);
                            s = p >= 0 ? sq : -sq;
                            if (s != 0)
                            {
                                if (k == m) { if (l != m) a[k, k - 1] = -a[k, k - 1]; }
                                else a[k, k - 1] = -s * x;
                                p += s;
                                x = p / s; y = q / s; z = r / s;
                                q /= p; r /= p;
                                for (int j = k; j <= nn; j++)
                                {
                                    p = a[k, j] + q * a[k + 1, j];
                                    if (k != nn - 1) { p += r * a[k + 2, j]; a[k + 2, j] -= p * z; }
                                    a[k + 1, j] -= p * y;
                                    a[k, j] -= p * x;
                                }
                                int mmin = nn < k + 3 ? nn : k + 3;
                                for (int i = l; i <= mmin; i++)
                                {
                                    p = x * a[i, k] + y * a[i, k + 1];
                                    if (k != nn - 1) { p += z * a[i, k + 2]; a[i, k + 2] -= p * r; }
                                    a[i, k + 1] -= p * q;
                                    a[i, k] -= p;
                                }
                            }
                        }
                    }
                }
            } while (l < nn - 1);
        }
    }
}
