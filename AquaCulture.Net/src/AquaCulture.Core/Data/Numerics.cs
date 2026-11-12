namespace AquaCulture.Core.Data;

/// <summary>Result of a numerical minimisation.</summary>
public sealed record OptimizationResult(double[] Parameters, double Value, int Iterations, bool Converged);

/// <summary>Small numerical toolbox: Nelder–Mead, golden-section search, Brent root finding, RK4 and dense linear solves.</summary>
public static class Numerics
{
    /// <summary>Derivative-free Nelder–Mead simplex minimisation.</summary>
    public static OptimizationResult NelderMead(Func<double[], double> f, double[] start, double[]? step = null, int maxIter = 2000, double tol = 1e-9)
    {
        int n = start.Length;
        var simplex = new double[n + 1][];
        var values = new double[n + 1];
        simplex[0] = (double[])start.Clone();
        for (int i = 0; i < n; i++)
        {
            var p = (double[])start.Clone();
            double s = step?[i] ?? (start[i] != 0 ? 0.1 * Math.Abs(start[i]) : 0.1);
            p[i] += s;
            simplex[i + 1] = p;
        }
        for (int i = 0; i <= n; i++) values[i] = Safe(f(simplex[i]));

        int iter = 0;
        for (; iter < maxIter; iter++)
        {
            var order = Enumerable.Range(0, n + 1).OrderBy(i => values[i]).ToArray();
            simplex = order.Select(i => simplex[i]).ToArray();
            values = order.Select(i => values[i]).ToArray();
            if (Math.Abs(values[n] - values[0]) <= tol * (Math.Abs(values[0]) + tol)) return new(simplex[0], values[0], iter, true);

            var centroid = new double[n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) centroid[j] += simplex[i][j] / n;
            double[] Along(double t) { var r = new double[n]; for (int j = 0; j < n; j++) r[j] = centroid[j] + t * (simplex[n][j] - centroid[j]); return r; }

            var xr = Along(-1); double fr = Safe(f(xr));
            if (fr < values[0])
            {
                var xe = Along(-2); double fe = Safe(f(xe));
                (simplex[n], values[n]) = fe < fr ? (xe, fe) : (xr, fr);
            }
            else if (fr < values[n - 1]) (simplex[n], values[n]) = (xr, fr);
            else
            {
                var xc = fr < values[n] ? Along(-0.5) : Along(0.5); double fc = Safe(f(xc));
                if (fc < Math.Min(fr, values[n])) (simplex[n], values[n]) = (xc, fc);
                else
                {
                    for (int i = 1; i <= n; i++)
                    {
                        for (int j = 0; j < n; j++) simplex[i][j] = simplex[0][j] + 0.5 * (simplex[i][j] - simplex[0][j]);
                        values[i] = Safe(f(simplex[i]));
                    }
                }
            }
        }
        int best = Array.IndexOf(values, values.Min());
        return new(simplex[best], values[best], iter, false);
    }

    private static double Safe(double v) => double.IsFinite(v) ? v : double.MaxValue;

    /// <summary>Golden-section maximisation of a unimodal function on [a, b].</summary>
    public static (double X, double Value) GoldenMax(Func<double, double> f, double a, double b, double tol = 1e-7)
    {
        const double g = 0.6180339887498949;
        double c = b - g * (b - a), d = a + g * (b - a), fc = f(c), fd = f(d);
        while (Math.Abs(b - a) > tol * (1 + Math.Abs(c) + Math.Abs(d)))
        {
            if (fc > fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = f(c); }
            else { a = c; c = d; fc = fd; d = a + g * (b - a); fd = f(d); }
        }
        double x = (a + b) / 2;
        return (x, f(x));
    }

    /// <summary>Brent's method root of f on a bracketing interval [a, b].</summary>
    public static double Brent(Func<double, double> f, double a, double b, double tol = 1e-10, int maxIter = 200)
    {
        double fa = f(a), fb = f(b);
        if (fa * fb > 0) throw new ArgumentException("Root is not bracketed.");
        if (Math.Abs(fa) < Math.Abs(fb)) { (a, b) = (b, a); (fa, fb) = (fb, fa); }
        double c = a, fc = fa, d = 0; bool mflag = true;
        for (int i = 0; i < maxIter && fb != 0 && Math.Abs(b - a) > tol; i++)
        {
            double s;
            if (fa != fc && fb != fc)
                s = a * fb * fc / ((fa - fb) * (fa - fc)) + b * fa * fc / ((fb - fa) * (fb - fc)) + c * fa * fb / ((fc - fa) * (fc - fb));
            else s = b - fb * (b - a) / (fb - fa);
            bool cond = (s - (3 * a + b) / 4) * (s - b) >= 0 || (mflag && Math.Abs(s - b) >= Math.Abs(b - c) / 2)
                        || (!mflag && Math.Abs(s - b) >= Math.Abs(c - d) / 2);
            if (cond) { s = (a + b) / 2; mflag = true; } else mflag = false;
            double fs = f(s);
            d = c; c = b; fc = fb;
            if (fa * fs < 0) { b = s; fb = fs; } else { a = s; fa = fs; }
            if (Math.Abs(fa) < Math.Abs(fb)) { (a, b) = (b, a); (fa, fb) = (fb, fa); }
        }
        return b;
    }

    /// <summary>One classical Runge–Kutta 4 step for dy/dt = f(t, y).</summary>
    public static double[] Rk4(Func<double, double[], double[]> f, double t, double[] y, double h)
    {
        int n = y.Length;
        var k1 = f(t, y);
        var tmp = new double[n];
        for (int i = 0; i < n; i++) tmp[i] = y[i] + h / 2 * k1[i];
        var k2 = f(t + h / 2, tmp);
        for (int i = 0; i < n; i++) tmp[i] = y[i] + h / 2 * k2[i];
        var k3 = f(t + h / 2, tmp);
        for (int i = 0; i < n; i++) tmp[i] = y[i] + h * k3[i];
        var k4 = f(t + h, tmp);
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = y[i] + h / 6 * (k1[i] + 2 * k2[i] + 2 * k3[i] + k4[i]);
        return r;
    }

    /// <summary>Solves A·x = b with partial-pivot Gaussian elimination (A is copied).</summary>
    public static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-14) throw new InvalidOperationException("Matrix is singular.");
            if (piv != col)
            {
                for (int k = 0; k < n; k++) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
                (x[col], x[piv]) = (x[piv], x[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double factor = m[r, col] / m[col, col];
                if (factor == 0) continue;
                for (int k = col; k < n; k++) m[r, k] -= factor * m[col, k];
                x[r] -= factor * x[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
            x[r] = s / m[r, r];
        }
        return x;
    }

    /// <summary>Linear interpolation in a table of (x, y) sorted by x, clamped at the ends.</summary>
    public static double Interpolate(ReadOnlySpan<(double X, double Y)> table, double x)
    {
        if (table.Length == 0) return double.NaN;
        if (x <= table[0].X) return table[0].Y;
        if (x >= table[^1].X) return table[^1].Y;
        for (int i = 1; i < table.Length; i++)
            if (x <= table[i].X)
            {
                var (x0, y0) = table[i - 1]; var (x1, y1) = table[i];
                return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
            }
        return table[^1].Y;
    }
}
