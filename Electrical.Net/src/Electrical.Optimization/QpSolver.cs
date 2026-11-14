using Electrical.Core.Numerics;

namespace Electrical.Optimization;

/// <summary>
/// Convex quadratic program  min ½xᵀQx + cᵀx  subject to  A·x = b,  G·x ≤ h.
/// Dense storage (intended for up to a few hundred variables/constraints).
/// </summary>
public sealed class QuadraticProgram
{
    public QuadraticProgram(int variables)
    {
        N = variables;
        Q = new DenseMatrix<double>(variables, variables);
        C = new double[variables];
    }

    public int N { get; }
    /// <summary>Positive semidefinite Hessian (n × n).</summary>
    public DenseMatrix<double> Q { get; }
    /// <summary>Linear cost (n).</summary>
    public double[] C { get; }
    /// <summary>Equality rows (coefficients, right-hand side).</summary>
    public List<(double[] Row, double Rhs)> Equalities { get; } = [];
    /// <summary>Inequality rows aᵀx ≤ rhs.</summary>
    public List<(double[] Row, double Rhs)> Inequalities { get; } = [];

    public int AddEquality(double[] row, double rhs) { Equalities.Add((row, rhs)); return Equalities.Count - 1; }
    public int AddInequality(double[] row, double rhs) { Inequalities.Add((row, rhs)); return Inequalities.Count - 1; }

    /// <summary>Adds lo ≤ x_j ≤ hi as two inequalities; returns (upper index, lower index).</summary>
    public (int Upper, int Lower) AddBounds(int j, double lo, double hi)
    {
        var u = new double[N]; u[j] = 1;
        var l = new double[N]; l[j] = -1;
        return (AddInequality(u, hi), AddInequality(l, -lo));
    }

    public double Objective(ReadOnlySpan<double> x)
    {
        var qx = Q.Multiply(x);
        double s = 0;
        for (int i = 0; i < N; i++) s += 0.5 * x[i] * qx[i] + C[i] * x[i];
        return s;
    }
}

/// <summary>QP solution with Lagrange multipliers.</summary>
/// <param name="X">Primal solution.</param>
/// <param name="EqualityDuals">y for A·x = b with the Lagrangian f − yᵀ(Ax − b) (y = ∂f*/∂b, e.g. the energy price for a balance row).</param>
/// <param name="InequalityDuals">z ≥ 0 for G·x ≤ h with the Lagrangian f + zᵀ(Gx − h) (z = −∂f*/∂h, shadow price of the limit).</param>
/// <param name="Slacks">h − G·x (≥ 0).</param>
public sealed record QpResult(double[] X, double[] EqualityDuals, double[] InequalityDuals, double[] Slacks, double Objective, int Iterations,
    bool Converged, double PrimalResidual, double DualResidual, double Gap);

/// <summary>
/// Mehrotra predictor–corrector primal–dual interior-point method for <see cref="QuadraticProgram"/>.
/// Each iteration solves the reduced KKT system [[Q + GᵀZS⁻¹G, −Aᵀ], [A, 0]] with dense LU.
/// </summary>
public static class QpSolver
{
    /// <param name="initialX">Optional starting point (e.g. a dispatch that meets the equalities); improves robustness on large problems.</param>
    public static QpResult Solve(QuadraticProgram qp, double tolerance = 1e-8, int maxIterations = 100, double[]? initialX = null)
    {
        int n = qp.N, p = qp.Equalities.Count, m = qp.Inequalities.Count;
        var x = initialX is { } x0 && x0.Length == n ? (double[])x0.Clone() : new double[n];
        var y = new double[p]; var z = new double[m]; var s = new double[m];
        // scale for relative stopping
        double scale = 1 + qp.C.Select(Math.Abs).DefaultIfEmpty(0).Max();
        // Interior start: slacks from the starting point (at least a fraction of the row scale) and duals on the central path
        // s·z = μ0, with μ0 tied to the cost scale so that neither primal nor dual residuals dominate the first steps.
        double mu0 = Math.Max(1, scale);
        for (int i = 0; i < m; i++)
        {
            var (row, rhs) = qp.Inequalities[i];
            double gx = Dot(row, x);
            double rowScale = 1 + Math.Abs(rhs);
            s[i] = Math.Max(rhs - gx, 1e-2 * rowScale);
            z[i] = mu0 / s[i];
        }
        var rd = new double[n]; var rp = new double[p]; var rg = new double[m];
        int it;
        bool converged = false;
        double rpNorm = 0, rdNorm = 0, mu = 0;
        for (it = 0; it < maxIterations; it++)
        {
            Residuals(qp, x, y, z, s, rd, rp, rg);
            rpNorm = Math.Max(NormInf(rp), NormInf(rg));
            rdNorm = NormInf(rd);
            mu = m > 0 ? Dot(s, z) / m : 0;
            if (rpNorm < tolerance * scale && rdNorm < tolerance * scale && mu < tolerance) { converged = true; break; }

            // Build reduced KKT matrix
            int dim = n + p;
            var k = new DenseMatrix<double>(dim, dim);
            for (int a = 0; a < n; a++) for (int b = 0; b < n; b++) k[a, b] = qp.Q[a, b];
            var d = new double[m];
            for (int i = 0; i < m; i++)
            {
                d[i] = z[i] / s[i];
                var g = qp.Inequalities[i].Row;
                for (int a = 0; a < n; a++)
                {
                    if (g[a] == 0) continue;
                    double ga = d[i] * g[a];
                    for (int b = 0; b < n; b++) if (g[b] != 0) k[a, b] += ga * g[b];
                }
            }
            for (int e = 0; e < p; e++)
            {
                var row = qp.Equalities[e].Row;
                for (int a = 0; a < n; a++) { k[a, n + e] = -row[a]; k[n + e, a] = row[a]; }
            }
            for (int a = 0; a < n; a++) k[a, a] += 1e-12; // tiny regularization for purely linear objectives
            DenseLU<double> lu;
            try { lu = new DenseLU<double>(k); }
            catch (SingularMatrixException) { break; }

            // Predictor (affine scaling): rc = −s∘z
            var rc = new double[m];
            for (int i = 0; i < m; i++) rc[i] = -s[i] * z[i];
            var (dxA, dyA, dzA, dsA) = Direction(qp, lu, rd, rp, rg, rc, s, z);
            double aP = MaxStep(s, dsA), aD = MaxStep(z, dzA);
            double muAff = 0;
            for (int i = 0; i < m; i++) muAff += (s[i] + aP * dsA[i]) * (z[i] + aD * dzA[i]);
            muAff = m > 0 ? muAff / m : 0;
            double sigma = mu > 0 ? Math.Pow(muAff / mu, 3) : 0;
            // Corrector
            for (int i = 0; i < m; i++) rc[i] = -s[i] * z[i] - dsA[i] * dzA[i] + sigma * mu;
            var (dx, dy, dz, ds) = Direction(qp, lu, rd, rp, rg, rc, s, z);
            aP = Math.Min(1, 0.995 * MaxStep(s, ds));
            aD = Math.Min(1, 0.995 * MaxStep(z, dz));
            for (int a = 0; a < n; a++) x[a] += aP * dx[a];
            for (int i = 0; i < m; i++) { s[i] += aP * ds[i]; z[i] += aD * dz[i]; }
            for (int e = 0; e < p; e++) y[e] += aD * dy[e];
        }
        var slack = new double[m];
        for (int i = 0; i < m; i++) slack[i] = qp.Inequalities[i].Rhs - Dot(qp.Inequalities[i].Row, x);
        return new QpResult(x, y, z, slack, qp.Objective(x), it, converged, rpNorm, rdNorm, mu);
    }

    // Solves the Newton system for given right-hand sides.
    private static (double[] Dx, double[] Dy, double[] Dz, double[] Ds) Direction(QuadraticProgram qp, DenseLU<double> lu,
        double[] rd, double[] rp, double[] rg, double[] rc, double[] s, double[] z)
    {
        int n = qp.N, p = qp.Equalities.Count, m = qp.Inequalities.Count;
        var rhs = new double[n + p];
        for (int a = 0; a < n; a++) rhs[a] = -rd[a];
        var t = new double[m];
        for (int i = 0; i < m; i++)
        {
            t[i] = (rc[i] + z[i] * rg[i]) / s[i];
            var g = qp.Inequalities[i].Row;
            for (int a = 0; a < n; a++) if (g[a] != 0) rhs[a] -= g[a] * t[i];
        }
        for (int e = 0; e < p; e++) rhs[n + e] = -rp[e];
        var sol = lu.Solve(rhs);
        var dx = sol[..n];
        var dy = sol[n..];
        var dz = new double[m]; var ds = new double[m];
        for (int i = 0; i < m; i++)
        {
            double gdx = Dot(qp.Inequalities[i].Row, dx);
            ds[i] = -rg[i] - gdx;
            dz[i] = (rc[i] - z[i] * ds[i]) / s[i];
        }
        return (dx, dy, dz, ds);
    }

    private static void Residuals(QuadraticProgram qp, double[] x, double[] y, double[] z, double[] s, double[] rd, double[] rp, double[] rg)
    {
        int n = qp.N;
        var qx = qp.Q.Multiply(x);
        for (int a = 0; a < n; a++) rd[a] = qx[a] + qp.C[a];
        for (int e = 0; e < qp.Equalities.Count; e++)
        {
            var (row, rhs) = qp.Equalities[e];
            for (int a = 0; a < n; a++) rd[a] -= row[a] * y[e];
            rp[e] = Dot(row, x) - rhs;
        }
        for (int i = 0; i < qp.Inequalities.Count; i++)
        {
            var (row, rhs) = qp.Inequalities[i];
            for (int a = 0; a < n; a++) rd[a] += row[a] * z[i];
            rg[i] = Dot(row, x) + s[i] - rhs;
        }
    }

    private static double MaxStep(double[] v, double[] dv)
    {
        double a = 1;
        for (int i = 0; i < v.Length; i++) if (dv[i] < 0) a = Math.Min(a, -v[i] / dv[i]);
        return a;
    }

    internal static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static double NormInf(double[] v) => v.Length == 0 ? 0 : v.Max(Math.Abs);
}
