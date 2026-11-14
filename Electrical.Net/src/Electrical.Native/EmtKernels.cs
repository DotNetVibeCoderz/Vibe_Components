namespace Electrical.Native;

/// <summary>
/// Hot-loop kernels of the electromagnetic-transients (EMT) engine, backed by the Rust library (<c>eln_emt_*</c>, v1.3+)
/// when available, with managed fallbacks that perform the same floating-point operations in the same order
/// (bit-for-bit identical results). Set <see cref="PreferNative"/> to false to force managed code.
/// </summary>
public static class EmtKernels
{
    /// <summary>Coefficients per branch in a coefficient set: <c>[g, kcNew, kcOld, cv, ci, cc]</c>.</summary>
    public const int CoefficientStride = 6;

    /// <summary>State values per branch: <c>[i, vc, ih]</c> (A, V, A).</summary>
    public const int StateStride = 3;

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            if (NativeMethods.Version() < 10300) return false;
            unsafe { return NativeMethods.EmtBranchUpdate(0, null, null, 0, null, null, null, null, null) == 0; }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    });

    /// <summary>Use native code when available (default true).</summary>
    public static bool PreferNative { get; set; } = true;

    /// <summary>True when a Rust library with the EMT kernels was found and loaded.</summary>
    public static bool IsNativeAvailable => Available.Value;

    private static bool UseNative => PreferNative && Available.Value;

    /// <summary>"native (Rust)" or "managed".</summary>
    public static string Backend => UseNative ? "native (Rust)" : "managed";

    /// <summary>
    /// History-current recurrence of series R-L-C branches. For branch k with <c>vb = v[from] − v[to]</c> (index &lt; 0 = ground):
    /// <c>i = g·vb + ih; vc += kcNew·i + kcOld·i_prev; ih' = g'·(cv'·vb + ci'·i + cc'·vc)</c> (primed = <paramref name="next"/> set),
    /// then <c>rhs[from] −= ih'</c>, <c>rhs[to] += ih'</c>. Voltages in V, currents in A, conductances in S.
    /// <paramref name="allowNative"/> = false forces the managed path for this call.
    /// </summary>
    public static unsafe void BranchUpdate(ReadOnlySpan<int> from, ReadOnlySpan<int> to, ReadOnlySpan<double> v,
        ReadOnlySpan<double> current, ReadOnlySpan<double> next, Span<double> state, Span<double> rhs, bool allowNative = true)
    {
        int n = from.Length;
        if (to.Length != n || current.Length < CoefficientStride * n || next.Length < CoefficientStride * n || state.Length < StateStride * n || rhs.Length < v.Length)
            throw new ArgumentException("Dimension mismatch.");
        if (n == 0) return;
        if (allowNative && UseNative)
        {
            fixed (int* f = from) fixed (int* t = to) fixed (double* vp = v) fixed (double* c = current) fixed (double* x = next)
            fixed (double* s = state) fixed (double* r = rhs)
            {
                int code = NativeMethods.EmtBranchUpdate((nuint)n, f, t, (nuint)v.Length, vp, c, x, s, r);
                if (code == 0) return;
                throw new ArgumentException($"Native EMT branch update rejected the input (code {code}).");
            }
        }
        ManagedBranchUpdate(from, to, v, current, next, state, rhs);
    }

    /// <summary>Managed reference implementation of <see cref="BranchUpdate"/>.</summary>
    public static void ManagedBranchUpdate(ReadOnlySpan<int> from, ReadOnlySpan<int> to, ReadOnlySpan<double> v,
        ReadOnlySpan<double> current, ReadOnlySpan<double> next, Span<double> state, Span<double> rhs)
    {
        int nv = v.Length;
        for (int k = 0; k < from.Length; k++)
        {
            int f = from[k], t = to[k];
            if (f >= nv || t >= nv) throw new ArgumentException("Node index out of range.");
            double vf = f >= 0 ? v[f] : 0.0;
            double vt = t >= 0 ? v[t] : 0.0;
            double vb = vf - vt;
            int c = CoefficientStride * k, s = StateStride * k;
            double iPrev = state[s];
            double i = current[c] * vb + state[s + 2];
            double vc = state[s + 1] + current[c + 1] * i + current[c + 2] * iPrev;
            double ih = next[c] * (next[c + 3] * vb + next[c + 4] * i + next[c + 5] * vc);
            state[s] = i;
            state[s + 1] = vc;
            state[s + 2] = ih;
            if (f >= 0) rhs[f] -= ih;
            if (t >= 0) rhs[t] += ih;
        }
    }

    /// <summary>
    /// Forward/backward substitution x = A⁻¹·b with a pre-factored sparse LU (P·A·Pᵀ = L·U): <paramref name="perm"/> maps
    /// position → original index, L rows hold the strictly-lower part (unit diagonal), U rows the strictly-upper part,
    /// <paramref name="diag"/> the pivots. <paramref name="work"/> is scratch of length n.
    /// </summary>
    public static unsafe void LuSolve(ReadOnlySpan<int> perm, ReadOnlySpan<int> lPtr, ReadOnlySpan<int> lIdx, ReadOnlySpan<double> lVal,
        ReadOnlySpan<int> uPtr, ReadOnlySpan<int> uIdx, ReadOnlySpan<double> uVal, ReadOnlySpan<double> diag,
        ReadOnlySpan<double> b, Span<double> x, Span<double> work, bool allowNative = true)
    {
        int n = diag.Length;
        if (perm.Length != n || lPtr.Length != n + 1 || uPtr.Length != n + 1 || b.Length < n || x.Length < n || work.Length < n)
            throw new ArgumentException("Dimension mismatch.");
        if (n == 0) return;
        if (allowNative && UseNative)
        {
            fixed (int* p = perm) fixed (int* lp = lPtr) fixed (int* li = lIdx) fixed (double* lv = lVal)
            fixed (int* up = uPtr) fixed (int* ui = uIdx) fixed (double* uv = uVal) fixed (double* d = diag)
            fixed (double* bp = b) fixed (double* xp = x) fixed (double* w = work)
            {
                int code = NativeMethods.EmtLuSolve((nuint)n, p, lp, li, lv, (nuint)lIdx.Length, up, ui, uv, (nuint)uIdx.Length, d, bp, xp, w);
                if (code == 0) return;
                throw new ArgumentException($"Native EMT LU solve rejected the input (code {code}).");
            }
        }
        ManagedLuSolve(perm, lPtr, lIdx, lVal, uPtr, uIdx, uVal, diag, b, x, work);
    }

    /// <summary>Managed reference implementation of <see cref="LuSolve"/>.</summary>
    public static void ManagedLuSolve(ReadOnlySpan<int> perm, ReadOnlySpan<int> lPtr, ReadOnlySpan<int> lIdx, ReadOnlySpan<double> lVal,
        ReadOnlySpan<int> uPtr, ReadOnlySpan<int> uIdx, ReadOnlySpan<double> uVal, ReadOnlySpan<double> diag,
        ReadOnlySpan<double> b, Span<double> x, Span<double> work)
    {
        int n = diag.Length;
        var y = work;
        for (int k = 0; k < n; k++) y[k] = b[perm[k]];
        for (int i = 0; i < n; i++)
        {
            double s = y[i];
            for (int q = lPtr[i]; q < lPtr[i + 1]; q++) s -= lVal[q] * y[lIdx[q]];
            y[i] = s;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int q = uPtr[i]; q < uPtr[i + 1]; q++) s -= uVal[q] * y[uIdx[q]];
            y[i] = s / diag[i];
        }
        for (int k = 0; k < n; k++) x[perm[k]] = y[k];
    }
}
