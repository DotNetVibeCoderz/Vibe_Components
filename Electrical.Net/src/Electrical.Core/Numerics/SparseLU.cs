using System.Runtime.CompilerServices;
using System.Numerics;

namespace Electrical.Core.Numerics;

/// <summary>
/// Sparse LU factorization P·A·Pᵀ = L·U with a minimum-degree (Tinney scheme 2) fill-reducing ordering and
/// row-wise (IKJ) elimination. Suited to power-network matrices (Y-bus, Jacobians, gain matrices), which are
/// structurally symmetric and well-conditioned on the diagonal. The ordering can be reused across refactorizations
/// of matrices with the same pattern (e.g. Newton–Raphson iterations).
/// </summary>
public sealed class SparseLU<T> where T : INumberBase<T>
{
    private readonly int _n;
    private readonly int[] _perm;   // position k -> original index
    private readonly int[][] _lIdx; private readonly T[][] _lVal;
    private readonly int[][] _uIdx; private readonly T[][] _uVal;
    private readonly T[] _diag;
    private readonly DenseLU<T>? _dense;

    private SparseLU(int n, int[] perm, int[][] lIdx, T[][] lVal, int[][] uIdx, T[][] uVal, T[] diag)
    {
        _n = n; _perm = perm; _lIdx = lIdx; _lVal = lVal; _uIdx = uIdx; _uVal = uVal; _diag = diag;
    }

    private SparseLU(DenseLU<T> dense, int[] perm)
    {
        _n = dense.Size; _dense = dense; _perm = perm;
        _lIdx = _uIdx = []; _lVal = _uVal = []; _diag = [];
    }

    /// <summary>The fill-reducing ordering used (position → original index). Pass it back to <see cref="Factorize"/> to skip reordering.</summary>
    public int[] Ordering => _perm;

    public int Size => _n;

    /// <summary>Number of stored factor entries (L + U including diagonal); a measure of fill-in.</summary>
    public long FactorNonZeros
    {
        get
        {
            if (_dense is not null) return (long)_n * _n;
            long s = _n;
            for (int i = 0; i < _n; i++) s += _lIdx[i].Length + _uIdx[i].Length;
            return s;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SparseLU<T> Factorize(SparseMatrix<T> a, int[]? ordering = null)
    {
        if (a.Rows != a.Cols) throw new ArgumentException("Matrix must be square.", nameof(a));
        int n = a.Rows;
        var perm = ordering ?? MinimumDegreeOrdering(a);
        if (perm.Length != n) throw new ArgumentException("Ordering length mismatch.", nameof(ordering));
        var inv = new int[n];
        for (int k = 0; k < n; k++) inv[perm[k]] = k;

        // Scale for relative pivot test
        double maxAbs = 0;
        for (int p = 0; p < a.NonZeros; p++) maxAbs = Math.Max(maxAbs, Scalar.Magnitude(a.Values[p]));
        double tiny = Math.Max(maxAbs, 1e-300) * 1e-13;

        var lIdx = new int[n][]; var lVal = new T[n][];
        var uIdx = new int[n][]; var uVal = new T[n][];
        var diag = new T[n];
        var w = new T[n];
        var mark = new int[n];
        Array.Fill(mark, -1);
        var heap = new int[Math.Max(16, n)];
        int heapCount = 0;
        var upper = new List<int>(32);
        var lowerIdx = new List<int>(32); var lowerVal = new List<T>(32);

        for (int i = 0; i < n; i++)
        {
            int orig = perm[i];
            upper.Clear(); lowerIdx.Clear(); lowerVal.Clear();
            mark[i] = i; w[i] = T.Zero;
            for (int p = a.RowPtr[orig]; p < a.RowPtr[orig + 1]; p++)
            {
                int j = inv[a.ColIdx[p]];
                if (mark[j] != i) { mark[j] = i; w[j] = T.Zero; if (j < i) HeapPush(heap, ref heapCount, j); else if (j > i) upper.Add(j); }
                w[j] += a.Values[p];
            }
            while (heapCount > 0)
            {
                int k = HeapPop(heap, ref heapCount);
                T lik = w[k] / diag[k];
                if (lik == T.Zero) continue;
                lowerIdx.Add(k); lowerVal.Add(lik);
                var ui = uIdx[k]; var uv = uVal[k];
                for (int q = 0; q < ui.Length; q++)
                {
                    int j = ui[q];
                    if (mark[j] != i)
                    {
                        mark[j] = i; w[j] = T.Zero;
                        if (j < i) HeapPush(heap, ref heapCount, j); else if (j > i) upper.Add(j);
                    }
                    w[j] -= lik * uv[q];
                }
            }
            if (Scalar.Magnitude(w[i]) <= tiny)
            {
                // Zero pivot under this ordering: fall back to dense partial pivoting for moderate sizes.
                if (n <= 4000) return new SparseLU<T>(new DenseLU<T>(a.ToDense()), Identity(n));
                throw new SingularMatrixException($"Zero pivot at position {i} (bus/variable {orig}).");
            }
            diag[i] = w[i];
            lIdx[i] = lowerIdx.ToArray(); lVal[i] = lowerVal.ToArray();
            upper.Sort();
            var ux = new int[upper.Count]; var uvv = new T[upper.Count];
            for (int q = 0; q < upper.Count; q++) { ux[q] = upper[q]; uvv[q] = w[upper[q]]; }
            uIdx[i] = ux; uVal[i] = uvv;
        }
        return new SparseLU<T>(n, perm, lIdx, lVal, uIdx, uVal, diag);
    }

    // Minimal binary min-heap on int keys (each column index is pushed at most once per row).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HeapPush(int[] h, ref int count, int key)
    {
        int i = count++;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (h[parent] <= key) break;
            h[i] = h[parent];
            i = parent;
        }
        h[i] = key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HeapPop(int[] h, ref int count)
    {
        int top = h[0];
        int last = h[--count];
        int i = 0;
        while (true)
        {
            int c = 2 * i + 1;
            if (c >= count) break;
            if (c + 1 < count && h[c + 1] < h[c]) c++;
            if (last <= h[c]) break;
            h[i] = h[c];
            i = c;
        }
        if (count > 0) h[i] = last;
        return top;
    }

    private static int[] Identity(int n)
    {
        var p = new int[n];
        for (int i = 0; i < n; i++) p[i] = i;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public T[] Solve(ReadOnlySpan<T> b)
    {
        if (b.Length != _n) throw new ArgumentException("Dimension mismatch.", nameof(b));
        if (_dense is not null) return _dense.Solve(b);
        var y = new T[_n];
        for (int k = 0; k < _n; k++) y[k] = b[_perm[k]];
        for (int i = 0; i < _n; i++)
        {
            T s = y[i];
            var li = _lIdx[i]; var lv = _lVal[i];
            for (int q = 0; q < li.Length; q++) s -= lv[q] * y[li[q]];
            y[i] = s;
        }
        for (int i = _n - 1; i >= 0; i--)
        {
            T s = y[i];
            var ui = _uIdx[i]; var uv = _uVal[i];
            for (int q = 0; q < ui.Length; q++) s -= uv[q] * y[ui[q]];
            y[i] = s / _diag[i];
        }
        var x = new T[_n];
        for (int k = 0; k < _n; k++) x[_perm[k]] = y[k];
        return x;
    }

    /// <summary>Minimum-degree ordering on the symmetrized pattern of A (Tinney scheme 2).</summary>
    public static int[] MinimumDegreeOrdering(SparseMatrix<T> a)
    {
        int n = a.Rows;
        var adj = new HashSet<int>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        for (int i = 0; i < n; i++)
            foreach (int j in a.RowIndices(i))
                if (j != i) { adj[i].Add(j); adj[j].Add(i); }
        return MinimumDegreeOrdering(adj);
    }

    /// <summary>
    /// Minimum-degree ordering of an undirected graph given as adjacency sets (consumed). Uses a bucket queue with lazy
    /// deletion, so each elimination costs O(deg²) for the clique update plus O(deg) queue work.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int[] MinimumDegreeOrdering(HashSet<int>[] adj)
    {
        int n = adj.Length;
        var degree = new int[n];
        var eliminated = new bool[n];
        var buckets = new List<Stack<int>>();
        void Push(int v, int d)
        {
            while (buckets.Count <= d) buckets.Add(new Stack<int>());
            buckets[d].Push(v);
        }
        for (int i = 0; i < n; i++) { degree[i] = adj[i].Count; Push(i, degree[i]); }
        var order = new int[n];
        var nb = new List<int>();
        int minD = 0;
        for (int k = 0; k < n; k++)
        {
            int v = -1;
            while (v < 0)
            {
                while (minD < buckets.Count && buckets[minD].Count == 0) minD++;
                int c = buckets[minD].Pop();
                if (!eliminated[c] && degree[c] == minD) v = c; // skip stale entries
            }
            order[k] = v;
            eliminated[v] = true;
            nb.Clear();
            nb.AddRange(adj[v]);
            foreach (int u in nb) adj[u].Remove(v);
            for (int x = 0; x < nb.Count; x++)
            {
                var ax = adj[nb[x]];
                for (int y = x + 1; y < nb.Count; y++)
                    if (ax.Add(nb[y])) adj[nb[y]].Add(nb[x]);
            }
            foreach (int u in nb)
            {
                degree[u] = adj[u].Count;
                Push(u, degree[u]);
                if (degree[u] < minD) minD = degree[u];
            }
            adj[v].Clear();
        }
        return order;
    }
}
