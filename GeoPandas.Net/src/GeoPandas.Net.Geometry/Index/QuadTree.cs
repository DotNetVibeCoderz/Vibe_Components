using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Geometry.Index;

/// <summary>
/// Dynamic region quadtree supporting insert/remove of items with envelopes. Use it when data changes
/// frequently (IoT/digital-twin streams); prefer <see cref="StrTree{T}"/> for static data.
/// Not thread-safe for concurrent writes.
/// </summary>
public sealed class QuadTree<T>
{
    private readonly int _maxItems;
    private readonly int _maxDepth;
    private Node _root;

    public QuadTree(Envelope bounds, int maxItemsPerNode = 16, int maxDepth = 16)
    {
        _maxItems = maxItemsPerNode;
        _maxDepth = maxDepth;
        _root = new Node(new Envelope(bounds), 0);
    }

    public int Count { get; private set; }

    public void Insert(Envelope env, T item)
    {
        while (!_root.Bounds.Contains(env)) Grow(env);
        _root.Insert(env, item, _maxItems, _maxDepth);
        Count++;
    }

    public bool Remove(Envelope env, T item)
    {
        bool removed = _root.Remove(env, item);
        if (removed) Count--;
        return removed;
    }

    public List<T> Query(Envelope search)
    {
        var res = new List<T>();
        _root.Query(search, res);
        return res;
    }

    private void Grow(Envelope target)
    {
        var b = _root.Bounds;
        double w = Math.Max(b.Width, 1e-9), h = Math.Max(b.Height, 1e-9);
        bool growLeft = target.MinX < b.MinX, growDown = target.MinY < b.MinY;
        var nb = new Envelope(
            growLeft ? b.MinX - w : b.MinX, growLeft ? b.MaxX : b.MaxX + w,
            growDown ? b.MinY - h : b.MinY, growDown ? b.MaxY : b.MaxY + h);
        var newRoot = new Node(nb, 0);
        foreach (var (e, it) in _root.All()) newRoot.Insert(e, it, _maxItems, _maxDepth);
        _root = newRoot;
    }

    private sealed class Node(Envelope bounds, int depth)
    {
        public Envelope Bounds { get; } = bounds;
        private readonly List<(Envelope Env, T Item)> _items = [];
        private Node[]? _children;

        public void Insert(Envelope env, T item, int maxItems, int maxDepth)
        {
            if (_children is not null)
            {
                foreach (var c in _children)
                    if (c.Bounds.Contains(env)) { c.Insert(env, item, maxItems, maxDepth); return; }
                _items.Add((env, item));
                return;
            }
            _items.Add((env, item));
            if (_items.Count > maxItems && depth < maxDepth) Split(maxItems, maxDepth);
        }

        private void Split(int maxItems, int maxDepth)
        {
            double cx = Bounds.Centre.X, cy = Bounds.Centre.Y;
            _children =
            [
                new Node(new Envelope(Bounds.MinX, cx, Bounds.MinY, cy), depth + 1),
                new Node(new Envelope(cx, Bounds.MaxX, Bounds.MinY, cy), depth + 1),
                new Node(new Envelope(Bounds.MinX, cx, cy, Bounds.MaxY), depth + 1),
                new Node(new Envelope(cx, Bounds.MaxX, cy, Bounds.MaxY), depth + 1),
            ];
            var old = _items.ToArray();
            _items.Clear();
            foreach (var (e, it) in old) Insert(e, it, maxItems, maxDepth);
        }

        public bool Remove(Envelope env, T item)
        {
            if (!Bounds.Intersects(env)) return false;
            for (int i = 0; i < _items.Count; i++)
                if (EqualityComparer<T>.Default.Equals(_items[i].Item, item) && _items[i].Env.Equals(env)) { _items.RemoveAt(i); return true; }
            if (_children is null) return false;
            foreach (var c in _children) if (c.Remove(env, item)) return true;
            return false;
        }

        public void Query(Envelope search, List<T> output)
        {
            if (!Bounds.Intersects(search)) return;
            foreach (var (e, it) in _items) if (e.Intersects(search)) output.Add(it);
            if (_children is null) return;
            foreach (var c in _children) c.Query(search, output);
        }

        public IEnumerable<(Envelope, T)> All()
        {
            foreach (var x in _items) yield return x;
            if (_children is null) yield break;
            foreach (var c in _children) foreach (var x in c.All()) yield return x;
        }
    }
}
