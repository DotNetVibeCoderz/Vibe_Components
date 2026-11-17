namespace GeoPandas.Net.Geometry.Index;

/// <summary>
/// Static 2-D KD-tree over points (implicit, array-based, median split). Ideal for k-NN, radius search,
/// DBSCAN and network snapping. Immutable and thread-safe after construction.
/// </summary>
public sealed class KdTree
{
    private readonly double[] _x, _y;
    private readonly int[] _perm; // tree order -> original index

    public KdTree(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        if (xs.Length != ys.Length) throw new ArgumentException("xs and ys length mismatch.");
        int n = xs.Length;
        _x = xs.ToArray(); _y = ys.ToArray();
        _perm = Enumerable.Range(0, n).ToArray();
        Build(0, n, 0);
    }

    public KdTree(IReadOnlyList<(double X, double Y)> points)
        : this(points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray()) { }

    public int Count => _x.Length;

    private void Build(int lo, int hi, int depth)
    {
        if (hi - lo <= 1) return;
        int mid = (lo + hi) >> 1;
        Select(lo, hi - 1, mid, depth & 1);
        Build(lo, mid, depth + 1);
        Build(mid + 1, hi, depth + 1);
    }

    private double Key(int i, int axis) => axis == 0 ? _x[_perm[i]] : _y[_perm[i]];

    // Hoare quickselect on _perm.
    private void Select(int lo, int hi, int k, int axis)
    {
        while (hi > lo)
        {
            double pivot = Key((lo + hi) >> 1, axis);
            int i = lo, j = hi;
            while (i <= j)
            {
                while (Key(i, axis) < pivot) i++;
                while (Key(j, axis) > pivot) j--;
                if (i <= j) { (_perm[i], _perm[j]) = (_perm[j], _perm[i]); i++; j--; }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return;
        }
    }

    /// <summary>Indices of the k nearest points with Euclidean distances, closest first.</summary>
    public (int Index, double Distance)[] Nearest(double x, double y, int k = 1)
    {
        k = Math.Min(k, Count);
        if (k <= 0) return [];
        var heap = new PriorityQueue<int, double>(Comparer<double>.Create((a, b) => b.CompareTo(a))); // max-heap of squared dist
        SearchKnn(0, Count, 0, x, y, k, heap);
        var res = new (int, double)[heap.Count];
        for (int i = res.Length - 1; i >= 0; i--)
        {
            heap.TryDequeue(out int idx, out double d2);
            res[i] = (idx, Math.Sqrt(d2));
        }
        return res;
    }

    private void SearchKnn(int lo, int hi, int depth, double x, double y, int k, PriorityQueue<int, double> heap)
    {
        if (lo >= hi) return;
        int mid = (lo + hi) >> 1;
        int p = _perm[mid];
        double dx = _x[p] - x, dy = _y[p] - y, d2 = dx * dx + dy * dy;
        if (heap.Count < k) heap.Enqueue(p, d2);
        else if (heap.TryPeek(out _, out double worst) && d2 < worst) { heap.Dequeue(); heap.Enqueue(p, d2); }

        int axis = depth & 1;
        double diff = axis == 0 ? x - _x[p] : y - _y[p];
        int nearLo = diff < 0 ? lo : mid + 1, nearHi = diff < 0 ? mid : hi;
        int farLo = diff < 0 ? mid + 1 : lo, farHi = diff < 0 ? hi : mid;
        SearchKnn(nearLo, nearHi, depth + 1, x, y, k, heap);
        if (heap.Count < k || (heap.TryPeek(out _, out double w2) && diff * diff < w2))
            SearchKnn(farLo, farHi, depth + 1, x, y, k, heap);
    }

    /// <summary>Indices of all points within <paramref name="radius"/> of (x, y) (unordered).</summary>
    public List<int> Radius(double x, double y, double radius)
    {
        var res = new List<int>();
        RadiusInto(x, y, radius, res);
        return res;
    }

    /// <summary>Allocation-free variant: appends to <paramref name="output"/>.</summary>
    public void RadiusInto(double x, double y, double radius, List<int> output)
    {
        double r2 = radius * radius;
        Span<(int lo, int hi, int depth)> stack = stackalloc (int, int, int)[128];
        int sp = 0;
        stack[sp++] = (0, Count, 0);
        while (sp > 0)
        {
            var (lo, hi, depth) = stack[--sp];
            if (lo >= hi) continue;
            int mid = (lo + hi) >> 1;
            int p = _perm[mid];
            double dx = _x[p] - x, dy = _y[p] - y;
            if (dx * dx + dy * dy <= r2) output.Add(p);
            double diff = (depth & 1) == 0 ? x - _x[p] : y - _y[p];
            if (diff - radius <= 0 && sp < stack.Length) stack[sp++] = (lo, mid, depth + 1);
            if (diff + radius >= 0 && sp < stack.Length) stack[sp++] = (mid + 1, hi, depth + 1);
        }
    }
}
