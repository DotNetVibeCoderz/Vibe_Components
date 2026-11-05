using Agritech.Core.Geo;

namespace Agritech.GIS;

/// <summary>Point quadtree over WGS-84 coordinates supporting bounding-box and radius queries.</summary>
public sealed class QuadTree<T>
{
    private const int Capacity = 16;
    private const int MaxDepth = 20;
    private readonly Node _root;

    public QuadTree(GeoBounds bounds) => _root = new Node(bounds, 0);
    public QuadTree() : this(new GeoBounds(-90, -180, 90, 180)) { }

    public int Count { get; private set; }

    public void Insert(GeoCoordinate p, T item)
    {
        if (!_root.Bounds.Contains(p)) throw new ArgumentOutOfRangeException(nameof(p), "Point outside index bounds.");
        _root.Insert(p, item);
        Count++;
    }

    public IEnumerable<(GeoCoordinate Point, T Item)> Query(GeoBounds area)
    {
        var res = new List<(GeoCoordinate, T)>();
        _root.Query(area, res);
        return res;
    }

    /// <summary>All items within radius (m) of a centre, nearest first.</summary>
    public IReadOnlyList<(GeoCoordinate Point, T Item, double DistanceM)> Within(GeoCoordinate center, double radiusM)
    {
        double dLat = radiusM / GeoMath.EarthRadiusM * GeoMath.RadToDeg;
        double dLon = dLat / Math.Max(0.01, Math.Cos(center.Latitude * GeoMath.DegToRad));
        var box = new GeoBounds(Math.Max(-90, center.Latitude - dLat), Math.Max(-180, center.Longitude - dLon),
            Math.Min(90, center.Latitude + dLat), Math.Min(180, center.Longitude + dLon));
        return Query(box)
            .Select(x => (x.Point, x.Item, DistanceM: GeoMath.Distance(center, x.Point)))
            .Where(x => x.DistanceM <= radiusM)
            .OrderBy(x => x.DistanceM)
            .ToList();
    }

    /// <summary>Nearest item by expanding radius search.</summary>
    public (GeoCoordinate Point, T Item, double DistanceM)? Nearest(GeoCoordinate center, double maxRadiusM = 50_000)
    {
        for (double r = 50; r <= maxRadiusM * 2; r *= 4)
        {
            var hits = Within(center, Math.Min(r, maxRadiusM));
            if (hits.Count > 0) return hits[0];
            if (r >= maxRadiusM) break;
        }
        return null;
    }

    private sealed class Node(GeoBounds bounds, int depth)
    {
        public readonly GeoBounds Bounds = bounds;
        private List<(GeoCoordinate, T)>? _items = [];
        private Node[]? _children;

        public void Insert(GeoCoordinate p, T item)
        {
            if (_children is not null) { Child(p).Insert(p, item); return; }
            _items!.Add((p, item));
            if (_items.Count > Capacity && depth < MaxDepth) Split();
        }

        private void Split()
        {
            var c = Bounds.Center;
            _children =
            [
                new(new GeoBounds(Bounds.MinLat, Bounds.MinLon, c.Latitude, c.Longitude), depth + 1),
                new(new GeoBounds(Bounds.MinLat, c.Longitude, c.Latitude, Bounds.MaxLon), depth + 1),
                new(new GeoBounds(c.Latitude, Bounds.MinLon, Bounds.MaxLat, c.Longitude), depth + 1),
                new(new GeoBounds(c.Latitude, c.Longitude, Bounds.MaxLat, Bounds.MaxLon), depth + 1),
            ];
            foreach (var (p, it) in _items!) Child(p).Insert(p, it);
            _items = null;
        }

        private Node Child(GeoCoordinate p)
        {
            var c = Bounds.Center;
            int idx = (p.Latitude >= c.Latitude ? 2 : 0) + (p.Longitude >= c.Longitude ? 1 : 0);
            return _children![idx];
        }

        public void Query(GeoBounds area, List<(GeoCoordinate, T)> res)
        {
            if (!Bounds.Intersects(area)) return;
            if (_children is null)
            {
                foreach (var e in _items!) if (area.Contains(e.Item1)) res.Add(e);
                return;
            }
            foreach (var ch in _children) ch.Query(area, res);
        }
    }
}
