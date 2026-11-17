using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Geometry.Index;

/// <summary>
/// Static, bulk-loaded R-tree using the Sort-Tile-Recursive algorithm (Leutenegger et al.).
/// Nodes are stored in flat arrays (cache friendly, no per-node allocations). Read-only after construction,
/// therefore safe for concurrent queries. This backs <c>GeoDataFrame.SIndex</c>.
/// </summary>
/// <typeparam name="T">Payload type, typically the row index (int).</typeparam>
public sealed class StrTree<T>
{
    private readonly int _nodeCapacity;
    // Flattened node storage: bounds and child ranges per node. Leaves reference items.
    private double[] _minX = [], _minY = [], _maxX = [], _maxY = [];
    private int[] _childStart = [], _childCount = [];
    private bool[] _isLeafLevel = [];
    private readonly T[] _items;
    private readonly Envelope[] _itemEnv;
    private int _root = -1;

    public StrTree(IReadOnlyList<(Envelope Envelope, T Item)> entries, int nodeCapacity = 10)
    {
        if (nodeCapacity < 2) throw new ArgumentOutOfRangeException(nameof(nodeCapacity));
        _nodeCapacity = nodeCapacity;
        var valid = entries.Where(e => e.Envelope is { IsNull: false }).ToArray();
        _items = new T[valid.Length];
        _itemEnv = new Envelope[valid.Length];
        for (int i = 0; i < valid.Length; i++) { _items[i] = valid[i].Item; _itemEnv[i] = valid[i].Envelope; }
        Count = valid.Length;
        Build();
    }

    /// <summary>Builds a tree over row indices from geometries (null/empty geometries are skipped).</summary>
    public static StrTree<int> FromGeometries(IReadOnlyList<NetTopologySuite.Geometries.Geometry?> geometries, int nodeCapacity = 10)
    {
        var list = new List<(Envelope, int)>(geometries.Count);
        for (int i = 0; i < geometries.Count; i++)
        {
            var g = geometries[i];
            if (g is null || g.IsEmpty) continue;
            list.Add((g.EnvelopeInternal, i));
        }
        return new StrTree<int>(list, nodeCapacity);
    }

    public int Count { get; }

    /// <summary>Envelope of all items.</summary>
    public Envelope Bounds => _root < 0 ? new Envelope() : new Envelope(_minX[_root], _maxX[_root], _minY[_root], _maxY[_root]);

    private void Build()
    {
        int n = _items.Length;
        if (n == 0) return;

        var minX = new List<double>(); var minY = new List<double>(); var maxX = new List<double>(); var maxY = new List<double>();
        var cStart = new List<int>(); var cCount = new List<int>(); var leafLevel = new List<bool>();

        // Level 0: sort item indices with STR and pack into leaf nodes.
        int[] order = Enumerable.Range(0, n).ToArray();
        var centersX = new double[n]; var centersY = new double[n];
        for (int i = 0; i < n; i++) { centersX[i] = (_itemEnv[i].MinX + _itemEnv[i].MaxX) / 2; centersY[i] = (_itemEnv[i].MinY + _itemEnv[i].MaxY) / 2; }
        StrSort(order, centersX, centersY);

        // Reorder items so each leaf references a contiguous range.
        var items2 = new T[n]; var env2 = new Envelope[n];
        for (int i = 0; i < n; i++) { items2[i] = _items[order[i]]; env2[i] = _itemEnv[order[i]]; }
        Array.Copy(items2, _items, n); Array.Copy(env2, _itemEnv, n);

        var level = new List<int>();
        for (int s = 0; s < n; s += _nodeCapacity)
        {
            int c = Math.Min(_nodeCapacity, n - s);
            double a = double.MaxValue, b = double.MaxValue, d = double.MinValue, e = double.MinValue;
            for (int i = s; i < s + c; i++)
            {
                var en = _itemEnv[i];
                a = Math.Min(a, en.MinX); b = Math.Min(b, en.MinY); d = Math.Max(d, en.MaxX); e = Math.Max(e, en.MaxY);
            }
            level.Add(minX.Count);
            minX.Add(a); minY.Add(b); maxX.Add(d); maxY.Add(e); cStart.Add(s); cCount.Add(c); leafLevel.Add(true);
        }

        // Upper levels: nodes are appended level by level, so children of a parent are contiguous after sorting.
        while (level.Count > 1)
        {
            int m = level.Count;
            var ord = Enumerable.Range(0, m).ToArray();
            var cx = new double[m]; var cy = new double[m];
            for (int i = 0; i < m; i++) { int nd = level[i]; cx[i] = (minX[nd] + maxX[nd]) / 2; cy[i] = (minY[nd] + maxY[nd]) / 2; }
            StrSort(ord, cx, cy);

            // Re-append sorted level nodes contiguously so parents can reference [start, count).
            var sortedIds = new List<int>(m);
            foreach (var oi in ord)
            {
                int nd = level[oi];
                sortedIds.Add(minX.Count);
                minX.Add(minX[nd]); minY.Add(minY[nd]); maxX.Add(maxX[nd]); maxY.Add(maxY[nd]);
                cStart.Add(cStart[nd]); cCount.Add(cCount[nd]); leafLevel.Add(leafLevel[nd]);
            }

            var next = new List<int>();
            for (int s = 0; s < m; s += _nodeCapacity)
            {
                int c = Math.Min(_nodeCapacity, m - s);
                double a = double.MaxValue, b = double.MaxValue, d = double.MinValue, e = double.MinValue;
                for (int i = s; i < s + c; i++)
                {
                    int nd = sortedIds[i];
                    a = Math.Min(a, minX[nd]); b = Math.Min(b, minY[nd]); d = Math.Max(d, maxX[nd]); e = Math.Max(e, maxY[nd]);
                }
                next.Add(minX.Count);
                minX.Add(a); minY.Add(b); maxX.Add(d); maxY.Add(e); cStart.Add(sortedIds[s]); cCount.Add(c); leafLevel.Add(false);
            }
            level = next;
        }

        _root = level[0];
        _minX = [.. minX]; _minY = [.. minY]; _maxX = [.. maxX]; _maxY = [.. maxY];
        _childStart = [.. cStart]; _childCount = [.. cCount]; _isLeafLevel = [.. leafLevel];
    }

    private void StrSort(int[] order, double[] cx, double[] cy)
    {
        int n = order.Length;
        int leaves = (int)Math.Ceiling(n / (double)_nodeCapacity);
        int slices = (int)Math.Ceiling(Math.Sqrt(leaves));
        int sliceSize = slices * _nodeCapacity;
        Array.Sort(order, (a, b) => cx[a].CompareTo(cx[b]));
        for (int s = 0; s < n; s += sliceSize)
        {
            int len = Math.Min(sliceSize, n - s);
            Array.Sort(order, s, len, Comparer<int>.Create((a, b) => cy[a].CompareTo(cy[b])));
        }
    }

    /// <summary>Visits every item whose envelope intersects <paramref name="search"/>.</summary>
    public void Query(Envelope search, Action<T> visitor)
    {
        if (_root < 0 || search.IsNull) return;
        Span<int> stackInit = stackalloc int[64];
        var stack = new ValueStack(stackInit);
        stack.Push(_root);
        while (stack.Count > 0)
        {
            int nd = stack.Pop();
            if (_minX[nd] > search.MaxX || _maxX[nd] < search.MinX || _minY[nd] > search.MaxY || _maxY[nd] < search.MinY) continue;
            int start = _childStart[nd], count = _childCount[nd];
            if (_isLeafLevel[nd])
            {
                for (int i = start; i < start + count; i++)
                    if (_itemEnv[i].Intersects(search)) visitor(_items[i]);
            }
            else
            {
                for (int i = start; i < start + count; i++) stack.Push(i);
            }
        }
        stack.Dispose();
    }

    /// <summary>Returns items whose envelope intersects <paramref name="search"/>.</summary>
    public List<T> Query(Envelope search)
    {
        var result = new List<T>();
        Query(search, result.Add);
        return result;
    }

    /// <summary>
    /// k-nearest items to a point using best-first branch-and-bound search.
    /// <paramref name="distance"/> computes the exact distance to an item (defaults to envelope distance).
    /// </summary>
    public List<(T Item, double Distance)> Nearest(double x, double y, int k = 1, Func<T, double>? distance = null, double maxDistance = double.PositiveInfinity) =>
        Nearest(new Envelope(x, x, y, y), k, distance, maxDistance);

    /// <summary>
    /// k-nearest items to a query envelope. Envelope-to-envelope distance is a valid lower bound for the exact
    /// geometry distance, so results are exact when <paramref name="distance"/> computes true geometry distance.
    /// </summary>
    public List<(T Item, double Distance)> Nearest(Envelope query, int k = 1, Func<T, double>? distance = null, double maxDistance = double.PositiveInfinity)
    {
        var result = new List<(T, double)>(k);
        if (_root < 0 || k <= 0) return result;
        // queue entries: node id (>=0) or item id encoded as ~index (<0)
        var pq = new PriorityQueue<int, double>();
        pq.Enqueue(_root, BoxDist(_minX[_root], _minY[_root], _maxX[_root], _maxY[_root], query));
        while (pq.TryDequeue(out int id, out double d))
        {
            if (d > maxDistance) break;
            if (id < 0)
            {
                result.Add((_items[~id], d));
                if (result.Count == k) break;
                continue;
            }
            int start = _childStart[id], count = _childCount[id];
            if (_isLeafLevel[id])
            {
                for (int i = start; i < start + count; i++)
                {
                    var e = _itemEnv[i];
                    double di = distance?.Invoke(_items[i]) ?? BoxDist(e.MinX, e.MinY, e.MaxX, e.MaxY, query);
                    if (di <= maxDistance) pq.Enqueue(~i, di);
                }
            }
            else
            {
                for (int i = start; i < start + count; i++) pq.Enqueue(i, BoxDist(_minX[i], _minY[i], _maxX[i], _maxY[i], query));
            }
        }
        return result;
    }

    private static double BoxDist(double minX, double minY, double maxX, double maxY, Envelope q)
    {
        double dx = q.MaxX < minX ? minX - q.MaxX : q.MinX > maxX ? q.MinX - maxX : 0;
        double dy = q.MaxY < minY ? minY - q.MaxY : q.MinY > maxY ? q.MinY - maxY : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Tiny growable stack backed by stackalloc / ArrayPool.</summary>
    private ref struct ValueStack
    {
        private Span<int> _span;
        private int[]? _rented;
        public int Count;

        public ValueStack(Span<int> initial) { _span = initial; _rented = null; Count = 0; }

        public void Push(int v)
        {
            if (Count == _span.Length)
            {
                var bigger = System.Buffers.ArrayPool<int>.Shared.Rent(_span.Length * 2);
                _span.CopyTo(bigger);
                if (_rented is not null) System.Buffers.ArrayPool<int>.Shared.Return(_rented);
                _rented = bigger;
                _span = bigger;
            }
            _span[Count++] = v;
        }

        public int Pop() => _span[--Count];

        public void Dispose()
        {
            if (_rented is not null) System.Buffers.ArrayPool<int>.Shared.Return(_rented);
        }
    }
}
