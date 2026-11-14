using System.Diagnostics;
using Electrical.Core.Numerics;

namespace Electrical.Optimization;

/// <summary>
/// Smooth nonlinear program  min f(x)  s.t.  g(x) = 0,  h(x) ≤ 0,  xmin ≤ x ≤ xmax,  described by callbacks.
/// Jacobians are returned with one row per constraint (m × n); the Hessian callback returns the full symmetric
/// n × n matrix ∇²(σ·f + λᵀg + μᵀh). Callbacks should return matrices with a constant sparsity pattern (store explicit
/// zeros) so that the fill-reducing ordering of the KKT system can be reused across iterations.
/// </summary>
public sealed class NonlinearProgram
{
    /// <summary>Number of variables n.</summary>
    public required int VariableCount { get; init; }
    /// <summary>Number of nonlinear equality constraints g(x) = 0.</summary>
    public int EqualityCount { get; init; }
    /// <summary>Number of nonlinear inequality constraints h(x) ≤ 0.</summary>
    public int InequalityCount { get; init; }
    /// <summary>Lower variable bounds (null or −∞ entries = unbounded). Equal lower and upper bounds fix the variable.</summary>
    public double[]? Lower { get; init; }
    /// <summary>Upper variable bounds (null or +∞ entries = unbounded).</summary>
    public double[]? Upper { get; init; }
    /// <summary>f(x): returns the objective value and writes ∇f(x) into the second argument (length n).</summary>
    public required Func<double[], double[], double> Objective { get; init; }
    /// <summary>Writes g(x) (length <see cref="EqualityCount"/>) and h(x) (length <see cref="InequalityCount"/>).</summary>
    public Action<double[], double[], double[]>? Constraints { get; init; }
    /// <summary>Returns the Jacobians ∂g/∂x (EqualityCount × n) and ∂h/∂x (InequalityCount × n).</summary>
    public Func<double[], (SparseMatrix<double> Equality, SparseMatrix<double> Inequality)>? Jacobians { get; init; }
    /// <summary>Returns ∇²(σ·f + λᵀg + μᵀh) for (x, σ, λ, μ) as a full symmetric n × n matrix.</summary>
    public required Func<double[], double, double[], double[], SparseMatrix<double>> Hessian { get; init; }
}

/// <summary>Options of <see cref="InteriorPointSolver"/> (defaults follow MATPOWER's MIPS).</summary>
public sealed record InteriorPointOptions
{
    /// <summary>Termination tolerance on the scaled feasibility condition.</summary>
    public double FeasibilityTolerance { get; init; } = 1e-6;
    /// <summary>Termination tolerance on the scaled Lagrangian gradient.</summary>
    public double GradientTolerance { get; init; } = 1e-6;
    /// <summary>Termination tolerance on the scaled complementarity zᵀμ.</summary>
    public double ComplementarityTolerance { get; init; } = 1e-6;
    /// <summary>Termination tolerance on the relative cost change between iterations.</summary>
    public double CostTolerance { get; init; } = 1e-6;
    public int MaxIterations { get; init; } = 150;
    /// <summary>Enables the step-length control of Wang et al. (Lagrangian merit ratio ρ kept within [0.95, 1.05]).</summary>
    public bool StepControl { get; init; }
    /// <summary>Maximum number of step halvings per iteration when <see cref="StepControl"/> is on.</summary>
    public int MaxStepReductions { get; init; } = 20;
    /// <summary>Objective scaling σ used internally (returned objective and multipliers are unscaled).</summary>
    public double CostMultiplier { get; init; } = 1;
    /// <summary>Fraction-to-boundary factor ξ.</summary>
    public double StepFraction { get; init; } = 0.99995;
    /// <summary>Centering parameter σ_c of the perturbation update γ = σ_c·zᵀμ/n_iq.</summary>
    public double Centering { get; init; } = 0.1;
    /// <summary>Initial slack / multiplier value z₀.</summary>
    public double InitialSlack { get; init; } = 1;
    /// <summary>Multipliers of inactive inequalities below this value are reported as zero.</summary>
    public double MultiplierThreshold { get; init; } = 1e-5;

    public static InteriorPointOptions Default { get; } = new();
}

public enum InteriorPointStatus { Converged, MaxIterations, NumericalFailure }

/// <summary>Convergence measures of one interior-point iteration.</summary>
public readonly record struct InteriorPointIteration(int Iteration, double Objective, double Feasibility, double Gradient,
    double Complementarity, double CostChange, double StepPrimal, double StepDual, double Gamma);

/// <summary>Solution and multipliers of a <see cref="NonlinearProgram"/>. Multipliers are on the unscaled objective.</summary>
public sealed class InteriorPointResult
{
    public required double[] X { get; init; }
    public required double Objective { get; init; }
    public required InteriorPointStatus Status { get; init; }
    public bool Converged => Status == InteriorPointStatus.Converged;
    public required int Iterations { get; init; }
    /// <summary>Equality multipliers λ (∂f*/∂b for g(x) + b = 0).</summary>
    public required double[] Lambda { get; init; }
    /// <summary>Inequality multipliers μ ≥ 0.</summary>
    public required double[] Mu { get; init; }
    /// <summary>Lower-bound multipliers (≥ 0), length n.</summary>
    public required double[] MuLower { get; init; }
    /// <summary>Upper-bound multipliers (≥ 0), length n.</summary>
    public required double[] MuUpper { get; init; }
    public required IReadOnlyList<InteriorPointIteration> History { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>Number of KKT factorizations that needed the dense partial-pivoting fallback of <see cref="SparseLU{T}"/>.</summary>
    public int DenseFallbacks { get; init; }
}

/// <summary>
/// Primal-dual interior-point method for nonlinear programs (MIPS: H. Wang, C. E. Murillo-Sánchez, R. D. Zimmerman, R. J. Thomas,
/// "On computational issues of market-based optimal power flow", IEEE Trans. Power Systems 22(3), 2007). Each iteration solves the
/// reduced Newton/KKT system [M Jgᵀ; Jg 0]·[Δx; Δλ] = −[N; g] with <see cref="SparseLU{T}"/>; equality rows are paired with variables
/// by a bipartite matching (row interchange), which gives non-zero diagonals for the pivot-free LU, and the fill-reducing ordering is
/// reused across iterations while the pattern is unchanged.
/// </summary>
public static class InteriorPointSolver
{
    private const double Infinite = 1e10;
    private const double Regularization = 1e-8;

    public static InteriorPointResult Solve(NonlinearProgram p, double[] x0, InteriorPointOptions? options = null)
    {
        options ??= InteriorPointOptions.Default;
        var sw = Stopwatch.StartNew();
        int n = p.VariableCount, neqU = p.EqualityCount, niqU = p.InequalityCount;
        if (x0.Length != n) throw new ArgumentException("Initial point length mismatch.", nameof(x0));
        if ((neqU > 0 || niqU > 0) && (p.Constraints is null || p.Jacobians is null))
            throw new ArgumentException("Constraints and Jacobians callbacks are required when the program has constraints.", nameof(p));

        // classify bounds
        var lo = new double[n]; var up = new double[n];
        for (int i = 0; i < n; i++)
        {
            lo[i] = p.Lower?[i] ?? double.NegativeInfinity;
            up[i] = p.Upper?[i] ?? double.PositiveInfinity;
            if (lo[i] > up[i]) throw new ArgumentException($"Variable {i}: lower bound above upper bound.", nameof(p));
        }
        var eqB = new List<int>(); var ub = new List<int>(); var lb = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (lo[i] > -Infinite && up[i] < Infinite && Math.Abs(up[i] - lo[i]) <= 1e-12 * Math.Max(1, Math.Abs(up[i]))) { eqB.Add(i); continue; }
            if (up[i] < Infinite) ub.Add(i);
            if (lo[i] > -Infinite) lb.Add(i);
        }
        int neq = neqU + eqB.Count, niq = niqU + ub.Count + lb.Count;
        double cm = options.CostMultiplier;

        var x = (double[])x0.Clone();
        var df = new double[n];
        var gU = new double[neqU]; var hU = new double[niqU];
        var g = new double[neq]; var h = new double[niq];
        SparseMatrix<double> jg = Empty(neqU, n), jh = Empty(niqU, n);

        double Evaluate(double[] xx, double[] grad, double[] gg, double[] hh, bool jac)
        {
            double fv = p.Objective(xx, grad) * cm;
            for (int i = 0; i < n; i++) grad[i] *= cm;
            if (neqU > 0 || niqU > 0) p.Constraints!(xx, gU, hU);
            Array.Copy(gU, gg, neqU);
            for (int k = 0; k < eqB.Count; k++) gg[neqU + k] = xx[eqB[k]] - lo[eqB[k]];
            Array.Copy(hU, hh, niqU);
            for (int k = 0; k < ub.Count; k++) hh[niqU + k] = xx[ub[k]] - up[ub[k]];
            for (int k = 0; k < lb.Count; k++) hh[niqU + ub.Count + k] = lo[lb[k]] - xx[lb[k]];
            if (jac && (neqU > 0 || niqU > 0))
            {
                (jg, jh) = p.Jacobians!(xx);
                if (jg.Rows != neqU || jg.Cols != n || jh.Rows != niqU || jh.Cols != n) throw new InvalidOperationException("Jacobian dimension mismatch.");
            }
            return fv;
        }

        double f = Evaluate(x, df, g, h, true);
        double gamma = 1, z0 = options.InitialSlack;
        var lam = new double[neq];
        var z = new double[niq]; var mu = new double[niq];
        for (int k = 0; k < niq; k++)
        {
            z[k] = h[k] < -z0 ? -h[k] : z0;
            mu[k] = gamma / z[k] > z0 ? gamma / z[k] : z0;
        }
        var lx = new double[n];
        void Gradient() // Lx = ∇f + Jgᵀλ + Jhᵀμ
        {
            Array.Copy(df, lx, n);
            for (int r = 0; r < neqU; r++) { var ci = jg.RowIndices(r); var cv = jg.RowValues(r); for (int q = 0; q < ci.Length; q++) lx[ci[q]] += cv[q] * lam[r]; }
            for (int k = 0; k < eqB.Count; k++) lx[eqB[k]] += lam[neqU + k];
            for (int r = 0; r < niqU; r++) { var ci = jh.RowIndices(r); var cv = jh.RowValues(r); for (int q = 0; q < ci.Length; q++) lx[ci[q]] += cv[q] * mu[r]; }
            for (int k = 0; k < ub.Count; k++) lx[ub[k]] += mu[niqU + k];
            for (int k = 0; k < lb.Count; k++) lx[lb[k]] -= mu[niqU + ub.Count + k];
        }
        Gradient();
        double f0 = f;
        (double feas, double grad, double comp, double cost) Conditions()
        {
            double gn = NormInf(g), maxh = niq > 0 ? h.Max() : 0;
            double xn = NormInf(x), zn = NormInf(z), ln = NormInf(lam), mn = NormInf(mu);
            double zmu = 0;
            for (int k = 0; k < niq; k++) zmu += z[k] * mu[k];
            return (Math.Max(gn, maxh) / (1 + Math.Max(xn, zn)), NormInf(lx) / (1 + Math.Max(ln, mn)), zmu / (1 + xn), Math.Abs(f - f0) / (1 + Math.Abs(f0)));
        }
        var history = new List<InteriorPointIteration>();
        var c0 = Conditions();
        history.Add(new InteriorPointIteration(0, f / cm, c0.feas, c0.grad, c0.comp, 0, 0, 0, gamma));
        var status = InteriorPointStatus.MaxIterations;
        if (c0.feas < options.FeasibilityTolerance && c0.grad < options.GradientTolerance && c0.comp < options.ComplementarityTolerance)
            status = InteriorPointStatus.Converged;

        int dim = n + neq;
        int[]? rowPos = null;          // KKT row interchange from the equality/variable matching
        int[]? ordering = null; long signature = 0;
        var dx = new double[n]; var dz = new double[niq]; var dmu = new double[niq]; var dlam = new double[neq];
        var x1 = new double[n]; var df1 = new double[n]; var g1 = new double[neq]; var h1 = new double[niq];
        int it = 0, denseFallbacks = 0;
        while (status != InteriorPointStatus.Converged && it < options.MaxIterations)
        {
            it++;
            var lamU = lam.AsSpan(0, neqU).ToArray();
            var muU = mu.AsSpan(0, niqU).ToArray();
            var lxx = p.Hessian(x, cm, lamU, muU);
            rowPos ??= MatchEqualities(n, neqU, jg, eqB);

            // assemble M = Lxx + Jhᵀ diag(μ/z) Jh, N = Lx + Jhᵀ diag(1/z)(μ∘h + γ) and K = [M Jgᵀ; Jg 0] (original, symmetric order)
            var nvec = (double[])lx.Clone();
            var kb = new SparseBuilder<double>(dim, dim);
            for (int r = 0; r < n; r++)
            {
                var ci = lxx.RowIndices(r); var cv = lxx.RowValues(r);
                for (int q = 0; q < ci.Length; q++) kb.Add(r, ci[q], cv[q]);
            }
            for (int k = 0; k < niqU; k++)
            {
                var ci = jh.RowIndices(k); var cv = jh.RowValues(k);
                double w = mu[k] / z[k], cw = (mu[k] * h[k] + gamma) / z[k];
                for (int a = 0; a < ci.Length; a++)
                {
                    nvec[ci[a]] += cv[a] * cw;
                    for (int b = 0; b < ci.Length; b++) kb.Add(ci[a], ci[b], w * cv[a] * cv[b]);
                }
            }
            for (int k = 0; k < ub.Count; k++)
            {
                int i = ub[k], kk = niqU + k;
                kb.Add(i, i, mu[kk] / z[kk]);
                nvec[i] += (mu[kk] * h[kk] + gamma) / z[kk];
            }
            for (int k = 0; k < lb.Count; k++)
            {
                int i = lb[k], kk = niqU + ub.Count + k;
                kb.Add(i, i, mu[kk] / z[kk]);
                nvec[i] -= (mu[kk] * h[kk] + gamma) / z[kk];
            }
            for (int r = 0; r < neqU; r++)
            {
                var ci = jg.RowIndices(r); var cv = jg.RowValues(r);
                for (int q = 0; q < ci.Length; q++) { kb.Add(n + r, ci[q], cv[q]); kb.Add(ci[q], n + r, cv[q]); }
            }
            for (int k = 0; k < eqB.Count; k++)
            {
                int i = eqB[k], r = n + neqU + k;
                kb.Add(r, i, 1); kb.Add(i, r, 1);
            }
            for (int i = 0; i < dim; i++) kb.Add(i, i, 0); // keep every diagonal in the pattern
            var kkt = kb.ToCsr();
            var rhs = new double[dim];
            for (int i = 0; i < n; i++) rhs[i] = -nvec[i];
            for (int r = 0; r < neq; r++) rhs[n + r] = -g[r];

            // Symmetric scaling S·K·S with s_j = 1/√max(1, |M_jj|) tames the huge μ/z terms of active bounds, a tiny primal
            // regularization on near-zero diagonals (curvature-free variables at inactive bounds) keeps pivots away from zero, and the
            // matched rows are interchanged. Iterative refinement against the unregularized K recovers the exact Newton step.
            var sc = new double[dim];
            for (int i = 0; i < dim; i++) sc[i] = i < n ? 1 / Math.Sqrt(Math.Max(1, Math.Abs(kkt[i, i]))) : 1;
            var bb = new SparseBuilder<double>(dim, dim);
            for (int r = 0; r < dim; r++)
            {
                var ci = kkt.RowIndices(r); var cv = kkt.RowValues(r);
                for (int q = 0; q < ci.Length; q++) bb.Add(rowPos[r], ci[q], sc[r] * cv[q] * sc[ci[q]]);
                if (r < n && Math.Abs(kkt[r, r] * sc[r] * sc[r]) < Regularization) bb.Add(rowPos[r], r, Regularization);
            }
            var bmat = bb.ToCsr();

            double[] sol;
            try
            {
                long sig = PatternSignature(bmat);
                var lu = SparseLU<double>.Factorize(bmat, ordering is not null && sig == signature ? ordering : null);
                if (lu.FactorNonZeros < (long)dim * dim || dim <= 2) { ordering = lu.Ordering; signature = sig; }
                else { ordering = null; denseFallbacks++; } // dense fallback was used: do not reuse its identity ordering
                double[] SolveScaled(double[] b)
                {
                    var pb = new double[dim];
                    for (int r = 0; r < dim; r++) pb[rowPos[r]] = sc[r] * b[r];
                    var w = lu.Solve(pb);
                    for (int i = 0; i < dim; i++) w[i] *= sc[i];
                    return w;
                }
                sol = SolveScaled(rhs);
                double rn0 = double.PositiveInfinity;
                for (int refine = 0; refine < 3; refine++)
                {
                    var kx = kkt.Multiply(sol);
                    var res = new double[dim];
                    double rn = 0;
                    for (int i = 0; i < dim; i++) { res[i] = rhs[i] - kx[i]; rn = Math.Max(rn, Math.Abs(res[i])); }
                    if (rn <= 1e-14 * (1 + NormInf(rhs)) || rn >= rn0) break;
                    rn0 = rn;
                    var corr = SolveScaled(res);
                    for (int i = 0; i < dim; i++) sol[i] += corr[i];
                }
                if (!sol.All(double.IsFinite)) sol = new DenseLU<double>(kkt.ToDense()).Solve(rhs);
            }
            catch (Exception e) when (e is SingularMatrixException or InvalidOperationException)
            {
                status = InteriorPointStatus.NumericalFailure;
                break;
            }
            Array.Copy(sol, dx, n);
            Array.Copy(sol, n, dlam, 0, neq);
            ComputeSlackSteps(dx);

            void ComputeSlackSteps(double[] step)
            {
                for (int k = 0; k < niqU; k++)
                {
                    var ci = jh.RowIndices(k); var cv = jh.RowValues(k);
                    double s = 0;
                    for (int q = 0; q < ci.Length; q++) s += cv[q] * step[ci[q]];
                    dz[k] = -h[k] - z[k] - s;
                }
                for (int k = 0; k < ub.Count; k++) { int kk = niqU + k; dz[kk] = -h[kk] - z[kk] - step[ub[k]]; }
                for (int k = 0; k < lb.Count; k++) { int kk = niqU + ub.Count + k; dz[kk] = -h[kk] - z[kk] + step[lb[k]]; }
                for (int k = 0; k < niq; k++) dmu[k] = -mu[k] + (gamma - mu[k] * dz[k]) / z[k];
            }

            if (options.StepControl)
            {
                double logz = 0;
                for (int k = 0; k < niq; k++) logz += Math.Log(z[k]);
                double Merit(double fv, double[] gg, double[] hh)
                {
                    double s = fv - gamma * logz;
                    for (int r = 0; r < neq; r++) s += lam[r] * gg[r];
                    for (int k = 0; k < niq; k++) s += mu[k] * (hh[k] + z[k]);
                    return s;
                }
                double l0 = Merit(f, g, h);
                double alpha = 1, lin = 0, quad = 0;
                var hdx = lxx.Multiply(dx);
                for (int i = 0; i < n; i++) { lin += lx[i] * dx[i]; quad += dx[i] * hdx[i]; }
                for (int red = 0; red <= options.MaxStepReductions; red++)
                {
                    for (int i = 0; i < n; i++) x1[i] = x[i] + alpha * dx[i];
                    double fl = Evaluate(x1, df1, g1, h1, false);
                    double rho = (Merit(fl, g1, h1) - l0) / (alpha * lin + 0.5 * alpha * alpha * quad);
                    if (rho > 0.95 && rho < 1.05) break;
                    if (red == options.MaxStepReductions) break;
                    alpha *= 0.5;
                }
                if (alpha < 1)
                {
                    for (int i = 0; i < n; i++) dx[i] *= alpha;
                    for (int r = 0; r < neq; r++) dlam[r] *= alpha;
                    for (int k = 0; k < niq; k++) { dz[k] *= alpha; dmu[k] *= alpha; }
                }
            }

            double alphap = 1, alphad = 1;
            for (int k = 0; k < niq; k++)
            {
                if (dz[k] < 0) alphap = Math.Min(alphap, options.StepFraction * z[k] / -dz[k]);
                if (dmu[k] < 0) alphad = Math.Min(alphad, options.StepFraction * mu[k] / -dmu[k]);
            }
            for (int i = 0; i < n; i++) x[i] += alphap * dx[i];
            for (int k = 0; k < niq; k++) { z[k] += alphap * dz[k]; mu[k] += alphad * dmu[k]; }
            for (int r = 0; r < neq; r++) lam[r] += alphad * dlam[r];
            if (niq > 0)
            {
                double zmu = 0;
                for (int k = 0; k < niq; k++) zmu += z[k] * mu[k];
                gamma = options.Centering * zmu / niq;
            }

            f = Evaluate(x, df, g, h, true);
            Gradient();
            var c = Conditions();
            history.Add(new InteriorPointIteration(it, f / cm, c.feas, c.grad, c.comp, c.cost, alphap, alphad, gamma));
            if (c.feas < options.FeasibilityTolerance && c.grad < options.GradientTolerance &&
                c.comp < options.ComplementarityTolerance && c.cost < options.CostTolerance)
            {
                status = InteriorPointStatus.Converged;
                break;
            }
            if (!x.All(double.IsFinite) || alphap < 1e-8 || alphad < 1e-8 || gamma < double.Epsilon || gamma > 1 / double.Epsilon)
            {
                status = InteriorPointStatus.NumericalFailure;
                break;
            }
            f0 = f;
        }

        // zero multipliers of clearly inactive constraints, unscale
        for (int k = 0; k < niq; k++)
            if (h[k] < -options.FeasibilityTolerance && mu[k] < options.MultiplierThreshold) mu[k] = 0;
        var muLower = new double[n]; var muUpper = new double[n];
        for (int k = 0; k < ub.Count; k++) muUpper[ub[k]] = mu[niqU + k] / cm;
        for (int k = 0; k < lb.Count; k++) muLower[lb[k]] = mu[niqU + ub.Count + k] / cm;
        for (int k = 0; k < eqB.Count; k++)
        {
            double l = lam[neqU + k] / cm;
            if (l > 0) muUpper[eqB[k]] = l; else muLower[eqB[k]] = -l;
        }
        return new InteriorPointResult
        {
            X = x, Objective = f / cm, Status = status, Iterations = it,
            Lambda = lam.Take(neqU).Select(v => v / cm).ToArray(),
            Mu = mu.Take(niqU).Select(v => v / cm).ToArray(),
            MuLower = muLower, MuUpper = muUpper, History = history, Elapsed = sw.Elapsed, DenseFallbacks = denseFallbacks,
        };
    }

    private static SparseMatrix<double> Empty(int rows, int cols) => new SparseBuilder<double>(rows, cols).ToCsr();

    private static double NormInf(double[] v)
    {
        double m = 0;
        foreach (double a in v) m = Math.Max(m, Math.Abs(a));
        return m;
    }

    private static long PatternSignature(SparseMatrix<double> a)
    {
        unchecked
        {
            long h = 1469598103934665603L;
            for (int r = 0; r < a.Rows; r++)
            {
                foreach (int c in a.RowIndices(r)) h = (h ^ c) * 1099511628211L;
                h = (h ^ -1) * 1099511628211L;
            }
            return h ^ a.NonZeros;
        }
    }

    /// <summary>
    /// Pairs each equality row with a distinct variable it depends on (maximum bipartite matching, largest |∂g/∂x| first) and returns the
    /// KKT row position of every original row: matched variable and equality rows are interchanged, so both get the Jacobian entry as pivot.
    /// </summary>
    private static int[] MatchEqualities(int n, int neqU, SparseMatrix<double> jg, List<int> eqB)
    {
        int neq = neqU + eqB.Count;
        var cand = new int[neq][];
        for (int r = 0; r < neqU; r++)
        {
            var ci = jg.RowIndices(r).ToArray(); var cv = jg.RowValues(r).ToArray();
            var mag = cv.Select(Math.Abs).ToArray();
            var idx = Enumerable.Range(0, ci.Length).Where(q => mag[q] > 0).OrderByDescending(q => mag[q]).ThenBy(q => ci[q]).ToArray();
            cand[r] = idx.Select(q => ci[q]).ToArray();
        }
        for (int k = 0; k < eqB.Count; k++) cand[neqU + k] = [eqB[k]];
        var varRow = new int[n]; Array.Fill(varRow, -1);
        var rowVar = new int[neq]; Array.Fill(rowVar, -1);
        var visit = new int[n]; Array.Fill(visit, -1);
        var order = Enumerable.Range(0, neq).OrderBy(r => cand[r].Length).ThenBy(r => r).ToArray();
        // greedy pass (each row takes its largest entry if still free), then augmenting paths (Kuhn) for the rows left over,
        // trying free variables before displacing another row so the pairs stay close to the diagonal
        foreach (int r in order)
        {
            if (cand[r].Length == 0) continue;
            int j = cand[r][0];
            if (varRow[j] < 0) { varRow[j] = r; rowVar[r] = j; }
        }
        bool Augment(int r, int stamp, int depth)
        {
            if (depth > 2000) return false;
            foreach (int j in cand[r])
                if (varRow[j] < 0) { varRow[j] = r; rowVar[r] = j; return true; }
            foreach (int j in cand[r])
            {
                if (visit[j] == stamp) continue;
                visit[j] = stamp;
                if (varRow[j] < 0 || Augment(varRow[j], stamp, depth + 1)) { varRow[j] = r; rowVar[r] = j; return true; }
            }
            return false;
        }
        int st = 0;
        foreach (int r in order) if (rowVar[r] < 0) Augment(r, st++, 0);
        var pos = new int[n + neq];
        for (int i = 0; i < pos.Length; i++) pos[i] = i;
        for (int r = 0; r < neq; r++)
            if (rowVar[r] >= 0) { pos[rowVar[r]] = n + r; pos[n + r] = rowVar[r]; }
        return pos;
    }
}
