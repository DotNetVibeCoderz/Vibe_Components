using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Electrical.Optimization;

internal enum VarStatus : byte { Basic, AtLower, AtUpper, Free }

internal enum SimplexOutcome { Optimal, Infeasible, Unbounded, Cutoff, IterationLimit, TimeLimit, Error }

/// <summary>
/// Bounded dual simplex on the computational form A·x − s = 0, l ≤ (x, s) ≤ u (one logical s_i per row; s_i carries the row bounds),
/// minimising c·x. The basis inverse is kept explicitly (dense m×m, product-form row updates, refactorised every
/// <see cref="RefactorInterval"/> pivots) which suits the small/medium models of this library. Pricing uses dual steepest-edge weights;
/// the ratio test is Harris' two-pass test. Dual feasibility of the logical start basis is obtained by placing every nonbasic variable at the
/// bound matching the sign of its reduced cost, introducing temporary "artificial" bounds ±<see cref="ArtificialBound"/> for infinite ones;
/// a variable still at an artificial bound at the end with a non-zero reduced cost proves unboundedness. After a run of degenerate pivots the
/// engine switches to Bland's smallest-index rule (anti-cycling) until the objective moves again.
/// </summary>
internal sealed class DualSimplex
{
    public const double ArtificialBound = 1e7;
    private const int RefactorInterval = 150;
    private const double PrimalTol = 1e-9, DualTol = 1e-9, PivotTol = 1e-9;

    private readonly int _n, _m, _nTot;
    private readonly int[] _cs, _cr;
    private readonly double[] _cv;
    private readonly double[] _c;
    private readonly double[] _lo, _up, _baseLo, _baseUp;
    private readonly bool[] _art;
    private readonly int[] _head, _pos;
    private readonly VarStatus[] _st;
    private readonly double[] _x, _d, _binv, _beta;
    private readonly double[] _alpha, _w, _tau, _rhs;
    private readonly int[] _nzIdx;
    private int _sinceInvert;
    private bool _needInvert = true, _primalDirty = true;
    private int _degenerate;

    public long Iterations { get; private set; }
    public int Structurals => _n;
    public int Rows => _m;

    /// <param name="cost">Internal (minimisation) cost per structural.</param>
    public DualSimplex(LinearModel model, double[] cost)
    {
        _n = model.VariableCount;
        _m = model.ConstraintCount;
        _nTot = _n + _m;
        // transpose row-wise constraints into structural columns
        var count = new int[_n + 1];
        foreach (var r in model.Constraints) foreach (int j in r.Indices) count[j + 1]++;
        for (int j = 0; j < _n; j++) count[j + 1] += count[j];
        _cs = (int[])count.Clone();
        _cr = new int[count[_n]];
        _cv = new double[count[_n]];
        var fill = (int[])count.Clone();
        for (int i = 0; i < _m; i++)
        {
            var r = model.Constraints[i];
            for (int k = 0; k < r.Indices.Length; k++)
            {
                int p = fill[r.Indices[k]]++;
                _cr[p] = i;
                _cv[p] = r.Coefficients[k];
            }
        }
        _c = new double[_nTot];
        Array.Copy(cost, _c, _n);
        _lo = new double[_nTot]; _up = new double[_nTot]; _baseLo = new double[_nTot]; _baseUp = new double[_nTot];
        for (int j = 0; j < _n; j++) { _baseLo[j] = model.Variables[j].Lower; _baseUp[j] = model.Variables[j].Upper; }
        for (int i = 0; i < _m; i++) { _baseLo[_n + i] = model.Constraints[i].Lower; _baseUp[_n + i] = model.Constraints[i].Upper; }
        Array.Copy(_baseLo, _lo, _nTot);
        Array.Copy(_baseUp, _up, _nTot);
        _art = new bool[_nTot];
        _head = new int[_m];
        _pos = new int[_nTot];
        _st = new VarStatus[_nTot];
        _x = new double[_nTot];
        _d = new double[_nTot];
        _binv = new double[(long)_m * _m];
        _beta = new double[_m];
        _alpha = new double[_nTot];
        _w = new double[_m];
        _tau = new double[_m];
        _rhs = new double[_m];
        _nzIdx = new int[_m];
        SetLogicalBasis();
    }

    /// <summary>Basis of all logicals (B = −I), structurals nonbasic.</summary>
    public void SetLogicalBasis()
    {
        Array.Fill(_pos, -1);
        for (int j = 0; j < _n; j++) _st[j] = VarStatus.AtLower;
        for (int i = 0; i < _m; i++) { _head[i] = _n + i; _pos[_n + i] = i; _st[_n + i] = VarStatus.Basic; }
        _needInvert = true;
    }

    public double Objective
    {
        get
        {
            double s = 0;
            for (int j = 0; j < _n; j++) s += _c[j] * _x[j];
            return s;
        }
    }

    public double Value(int j) => _x[j];
    /// <summary>Row activity a_i·x.</summary>
    public double RowActivity(int i) => _x[_n + i];
    /// <summary>Dual of row i (∂objective/∂rhs) in minimisation form = reduced cost of its logical.</summary>
    public double RowDual(int i) => _st[_n + i] == VarStatus.Basic ? 0 : _d[_n + i];
    public double ReducedCost(int j) => _st[j] == VarStatus.Basic ? 0 : _d[j];

    public void SetBounds(int j, double lo, double up)
    {
        _baseLo[j] = lo; _baseUp[j] = up;
        _lo[j] = lo; _up[j] = up; _art[j] = false;
        if (_st[j] != VarStatus.Basic) { PlaceNonbasic(j); _primalDirty = true; }
    }


    public SimplexOutcome Run(double cutoff, long deadlineTicks, long maxIterations)
    {
        int artificialRounds = 0;
        bool verified = false, infeasibleChecked = false;
        if (_needInvert) { if (!Refactor()) return SimplexOutcome.Error; }
        else if (_primalDirty) ComputePrimal();
        bool bland = false;
        long start = Iterations;
        while (true)
        {
            if (Iterations - start > maxIterations) return SimplexOutcome.IterationLimit;
            if ((Iterations & 31) == 0 && deadlineTicks > 0 && Stopwatch.GetTimestamp() > deadlineTicks) return SimplexOutcome.TimeLimit;
            if (_sinceInvert >= RefactorInterval && !Refactor()) return SimplexOutcome.Error;
            if (cutoff < double.PositiveInfinity && Objective > cutoff + 1e-9 * Math.Max(1, Math.Abs(cutoff))) return SimplexOutcome.Cutoff;

            int r = SelectLeaving(bland);
            if (r < 0)
            {
                // candidate optimum: recompute x_B, y and d from the current inverse (refactorise when it has many updates),
                // restore dual feasibility, then check the artificial bounds
                if (!verified)
                {
                    verified = true;
                    if (_sinceInvert >= RefactorInterval / 4) { if (!Refactor()) return SimplexOutcome.Error; }
                    else Recompute();
                    continue;
                }
                bool changed = false;
                for (int j = 0; j < _nTot; j++)
                {
                    if (!_art[j] || _st[j] == VarStatus.Basic) continue;
                    bool atArtificial = (_st[j] == VarStatus.AtLower && double.IsNegativeInfinity(_baseLo[j]))
                                        || (_st[j] == VarStatus.AtUpper && double.IsPositiveInfinity(_baseUp[j]));
                    if (!atArtificial) continue;
                    if (Math.Abs(_d[j]) > DualTol * 10) return SimplexOutcome.Unbounded;
                    // degenerate: drop the artificial bound and move the variable off it
                    _lo[j] = _baseLo[j]; _up[j] = _baseUp[j]; _art[j] = false;
                    PlaceNonbasic(j, allowArtificial: false);
                    changed = true;
                }
                if (changed && artificialRounds++ < 5) { ComputePrimal(); verified = false; continue; }
                return SimplexOutcome.Optimal;
            }
            verified = false;
            int q = Pivot(r, bland, out double theta, out bool restart);
            if (restart) { if (!Refactor()) return SimplexOutcome.Error; continue; }
            if (q < 0)
            {
                if (!infeasibleChecked && _sinceInvert > 0) { infeasibleChecked = true; Recompute(); continue; }
                return SimplexOutcome.Infeasible;
            }
            infeasibleChecked = false;
            Iterations++;
            if (theta <= 1e-12) { if (++_degenerate > 60) bland = true; }
            else { _degenerate = 0; bland = false; }
        }
    }

    // ---------------------------------------------------------------- internals

    private bool Finite(double v) => !double.IsInfinity(v);

    /// <summary>Puts nonbasic j at the bound consistent with the sign of d_j (dual feasibility), adding artificial bounds if needed.</summary>
    private void PlaceNonbasic(int j, bool allowArtificial = true)
    {
        double lo = _lo[j], up = _up[j];
        if (Finite(lo) && lo == up) { _st[j] = VarStatus.AtLower; _x[j] = lo; return; }
        double dj = _d[j];
        VarStatus want;
        if (dj > DualTol) want = VarStatus.AtLower;
        else if (dj < -DualTol) want = VarStatus.AtUpper;
        else if (_st[j] == VarStatus.AtLower && Finite(lo)) want = VarStatus.AtLower;
        else if (_st[j] == VarStatus.AtUpper && Finite(up)) want = VarStatus.AtUpper;
        else want = Finite(lo) ? VarStatus.AtLower : Finite(up) ? VarStatus.AtUpper : VarStatus.Free;
        if (want == VarStatus.AtLower && !Finite(lo))
        {
            if (!allowArtificial) want = Finite(up) ? VarStatus.AtUpper : VarStatus.Free;
            else { _lo[j] = -ArtificialBound; _art[j] = true; }
        }
        if (want == VarStatus.AtUpper && !Finite(up))
        {
            if (!allowArtificial) want = Finite(_lo[j]) ? VarStatus.AtLower : VarStatus.Free;
            else { _up[j] = ArtificialBound; _art[j] = true; }
        }
        _st[j] = want;
        _x[j] = want switch { VarStatus.AtLower => _lo[j], VarStatus.AtUpper => _up[j], _ => 0 };
    }

    /// <summary>Recomputes duals, nonbasic placement and basic values from the current (updated) inverse.</summary>
    private void Recompute()
    {
        ComputeDual();
        for (int j = 0; j < _nTot; j++) if (_st[j] != VarStatus.Basic) PlaceNonbasic(j);
        ComputePrimal();
    }

    private bool Refactor()
    {
        if (!Invert())
        {
            SetLogicalBasis();
            if (!Invert()) return false;
        }
        ComputeDual();
        for (int j = 0; j < _nTot; j++) if (_st[j] != VarStatus.Basic) PlaceNonbasic(j);
        ComputePrimal();
        ComputeWeights();
        return true;
    }

    /// <summary>Dense Gauss–Jordan inversion of B with partial pivoting; logical columns are placed first (trivial pivots).</summary>
    private bool Invert()
    {
        int m = _m;
        _sinceInvert = 0;
        _needInvert = false;
        if (m == 0) return true;
        var bm = new double[(long)m * m];  // B, row-major
        for (int i = 0; i < m; i++)
        {
            int j = _head[i];
            if (j >= _n) bm[(long)(j - _n) * m + i] = -1;
            else for (int p = _cs[j]; p < _cs[j + 1]; p++) bm[(long)_cr[p] * m + i] = _cv[p];
        }
        Array.Clear(_binv);
        for (int i = 0; i < m; i++) _binv[(long)i * m + i] = 1;
        var rowOfCol = new int[m];       // pivot row chosen for basis column i
        var used = new bool[m];
        var order = Enumerable.Range(0, m).OrderBy(i => _head[i] >= _n ? 0 : 1).ToArray();
        foreach (int col in order)
        {
            int piv = -1;
            double best = 1e-11;
            for (int i = 0; i < m; i++)
            {
                if (used[i]) continue;
                double a = Math.Abs(bm[(long)i * m + col]);
                if (a > best) { best = a; piv = i; }
            }
            if (piv < 0) return false;
            used[piv] = true;
            rowOfCol[col] = piv;
            double inv = 1 / bm[(long)piv * m + col];
            ScaleRow(bm, piv, inv);
            ScaleRow(_binv, piv, inv);
            for (int i = 0; i < m; i++)
            {
                if (i == piv) continue;
                double f = bm[(long)i * m + col];
                if (f == 0) continue;
                AxpyRow(bm, i, piv, -f);
                AxpyRow(_binv, i, piv, -f);
            }
        }
        // rows of the result are permuted: basis column col ↔ row rowOfCol[col] of [I | B⁻¹]
        var perm = new double[(long)m * m];
        for (int col = 0; col < m; col++)
            Array.Copy(_binv, (long)rowOfCol[col] * m, perm, (long)col * m, m);
        Array.Copy(perm, _binv, perm.LongLength);
        return true;
    }

    private void ScaleRow(double[] a, int row, double s)
    {
        var span = a.AsSpan(row * _m, _m);
        for (int k = 0; k < span.Length; k++) span[k] *= s;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void AxpyRow(double[] a, int dst, int src, double f)
    {
        var s = a.AsSpan(src * _m, _m);
        var t = a.AsSpan(dst * _m, _m);
        for (int k = 0; k < s.Length; k++) if (s[k] != 0) t[k] += f * s[k];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ComputePrimal()
    {
        _primalDirty = false;
        Array.Clear(_rhs);
        for (int j = 0; j < _nTot; j++)
        {
            if (_st[j] == VarStatus.Basic) continue;
            double xj = _x[j];
            if (xj == 0) continue;
            if (j >= _n) _rhs[j - _n] += xj;   // column −e_i → −(−1)·x
            else for (int p = _cs[j]; p < _cs[j + 1]; p++) _rhs[_cr[p]] -= _cv[p] * xj;
        }
        int m = _m;
        for (int i = 0; i < m; i++)
        {
            var row = _binv.AsSpan(i * m, m);
            double s = 0;
            for (int k = 0; k < m; k++) s += row[k] * _rhs[k];
            _x[_head[i]] = s;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ComputeDual()
    {
        int m = _m;
        var y = new double[m];
        for (int i = 0; i < m; i++)
        {
            double cb = _c[_head[i]];
            if (cb == 0) continue;
            var row = _binv.AsSpan(i * m, m);
            for (int k = 0; k < m; k++) y[k] += cb * row[k];
        }
        for (int j = 0; j < _n; j++)
        {
            if (_st[j] == VarStatus.Basic) { _d[j] = 0; continue; }
            double s = _c[j];
            for (int p = _cs[j]; p < _cs[j + 1]; p++) s -= y[_cr[p]] * _cv[p];
            _d[j] = s;
        }
        for (int i = 0; i < m; i++) _d[_n + i] = _st[_n + i] == VarStatus.Basic ? 0 : y[i];
    }

    private void ComputeWeights()
    {
        int m = _m;
        for (int i = 0; i < m; i++)
        {
            var row = _binv.AsSpan(i * m, m);
            double s = 0;
            for (int k = 0; k < m; k++) s += row[k] * row[k];
            _beta[i] = Math.Max(s, 1e-12);
        }
    }

    private double Infeasibility(int i, out int sigma)
    {
        int j = _head[i];
        double v = _x[j];
        double lo = _lo[j], up = _up[j];
        if (v < lo - PrimalTol * Math.Max(1, Math.Abs(lo))) { sigma = 1; return lo - v; }
        if (v > up + PrimalTol * Math.Max(1, Math.Abs(up))) { sigma = -1; return v - up; }
        sigma = 0;
        return 0;
    }

    private int SelectLeaving(bool bland)
    {
        int best = -1;
        double bestScore = 0;
        int bestIdx = int.MaxValue;
        for (int i = 0; i < _m; i++)
        {
            double inf = Infeasibility(i, out _);
            if (inf <= 0) continue;
            if (bland)
            {
                if (_head[i] < bestIdx) { bestIdx = _head[i]; best = i; }
            }
            else
            {
                double score = inf * inf / _beta[i];
                if (score > bestScore) { bestScore = score; best = i; }
            }
        }
        return best;
    }

    /// <summary>One dual simplex pivot with leaving row r. Returns the entering variable (−1 = primal infeasible).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Pivot(int r, bool bland, out double theta, out bool restart)
    {
        int m = _m;
        theta = 0;
        restart = false;
        Infeasibility(r, out int sigma);
        int leaving = _head[r];
        var rho = _binv.AsSpan(r * m, m);
        // alpha_j = rho·a_j for nonbasic j
        for (int j = 0; j < _n; j++)
        {
            if (_st[j] == VarStatus.Basic) continue;
            double s = 0;
            for (int p = _cs[j]; p < _cs[j + 1]; p++) s += rho[_cr[p]] * _cv[p];
            _alpha[j] = s;
        }
        for (int i = 0; i < m; i++) if (_st[_n + i] != VarStatus.Basic) _alpha[_n + i] = -rho[i];

        // Harris two-pass ratio test
        double thetaMax = double.PositiveInfinity;
        for (int j = 0; j < _nTot; j++)
        {
            if (!IsCandidate(j, sigma)) continue;
            double a = Math.Abs(_alpha[j]);
            double t = (Math.Abs(_d[j]) + DualTol) / a;
            if (t < thetaMax) thetaMax = t;
        }
        if (double.IsPositiveInfinity(thetaMax)) return -1;
        int q = -1;
        double bestAlpha = 0;
        double minRatio = double.PositiveInfinity;
        if (bland)
            for (int j = 0; j < _nTot; j++)
            {
                if (!IsCandidate(j, sigma)) continue;
                double ratio = Math.Abs(_d[j]) / Math.Abs(_alpha[j]);
                if (ratio < minRatio - 1e-12) { minRatio = ratio; q = j; }
            }
        else
            for (int j = 0; j < _nTot; j++)
            {
                if (!IsCandidate(j, sigma)) continue;
                double a = Math.Abs(_alpha[j]);
                if (Math.Abs(_d[j]) / a <= thetaMax && a > bestAlpha) { bestAlpha = a; q = j; }
            }
        if (q < 0) return -1;
        theta = Math.Abs(_d[q]) / Math.Abs(_alpha[q]);

        // FTRAN: w = B⁻¹ a_q
        Array.Clear(_w);
        if (q >= _n) { int k = q - _n; for (int i = 0; i < m; i++) _w[i] = -_binv[(long)i * m + k]; }
        else
            for (int p = _cs[q]; p < _cs[q + 1]; p++)
            {
                int k = _cr[p];
                double v = _cv[p];
                for (int i = 0; i < m; i++) _w[i] += v * _binv[(long)i * m + k];
            }
        double wr = _w[r];
        if (Math.Abs(wr) < PivotTol || Math.Abs(wr - _alpha[q]) > 1e-7 * (1 + Math.Abs(wr)))
        {
            if (_sinceInvert > 0) { restart = true; return q; }
            if (Math.Abs(wr) < 1e-12) return -1;
        }

        // dual steepest-edge: tau = B⁻¹ rho
        // (sparsity pattern of the pivot row of B⁻¹ is re-used by the steepest-edge and inverse updates)
        int nz = 0;
        for (int k = 0; k < m; k++) if (rho[k] != 0) _nzIdx[nz++] = k;
        var nzIdx = _nzIdx.AsSpan(0, nz);
        for (int i = 0; i < m; i++)
        {
            if (_w[i] == 0) { _tau[i] = 0; continue; }
            var row = _binv.AsSpan(i * m, m);
            double s = 0;
            foreach (int k in nzIdx) s += row[k] * rho[k];
            _tau[i] = s;
        }

        // primal update
        double target = sigma > 0 ? _lo[leaving] : _up[leaving];
        double dq = (_x[leaving] - target) / wr;
        for (int i = 0; i < m; i++) if (_w[i] != 0) _x[_head[i]] -= _w[i] * dq;
        _x[q] += dq;
        _x[leaving] = target;

        // dual update: d_j += sigma·theta·alpha_j
        double step = sigma * theta;
        if (step != 0)
            for (int j = 0; j < _nTot; j++) if (_st[j] != VarStatus.Basic) _d[j] += step * _alpha[j];
        _d[q] = 0;
        _d[leaving] = step;

        // basis update
        _st[leaving] = sigma > 0 ? VarStatus.AtLower : VarStatus.AtUpper;
        _st[q] = VarStatus.Basic;
        _pos[leaving] = -1;
        _pos[q] = r;
        _head[r] = q;
        double betaR = _beta[r];
        var pivotRow = _binv.AsSpan(r * m, m);
        double inv = 1 / wr;
        foreach (int k in nzIdx) pivotRow[k] *= inv;
        for (int i = 0; i < m; i++)
        {
            double wi = _w[i];
            if (i == r || wi == 0) continue;
            var row = _binv.AsSpan(i * m, m);
            foreach (int k in nzIdx) row[k] -= wi * pivotRow[k];
            double ratio = wi / wr;
            _beta[i] = Math.Max(_beta[i] - 2 * ratio * _tau[i] + ratio * ratio * betaR, 1e-12);
        }
        _beta[r] = Math.Max(betaR / (wr * wr), 1e-12);
        _sinceInvert++;
        return q;
    }

    private bool IsCandidate(int j, int sigma)
    {
        var s = _st[j];
        if (s == VarStatus.Basic) return false;
        double a = _alpha[j];
        if (Math.Abs(a) < PivotTol) return false;
        if (_lo[j] == _up[j]) return false;
        return s switch
        {
            VarStatus.AtLower => sigma * a < 0,
            VarStatus.AtUpper => sigma * a > 0,
            _ => true,
        };
    }
}
