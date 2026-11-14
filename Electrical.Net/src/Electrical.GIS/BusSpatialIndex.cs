using Electrical.Core.Network;

namespace Electrical.GIS;

/// <summary>A bus found by a spatial query with its great-circle distance [km].</summary>
public readonly record struct BusHit(int BusId, GeoPoint Location, double DistanceKm);

/// <summary>
/// Static k-d tree over bus locations for nearest-asset search. Points are stored as 3-D unit vectors so that Euclidean
/// chord distance is monotonic in great-circle distance (correct across the antimeridian and near the poles).
/// Build O(n log² n), queries O(log n) on average.
/// </summary>
public sealed class BusSpatialIndex
{
    private readonly int[] _ids;
    private readonly GeoPoint[] _pts;
    private readonly double[][] _xyz;
    private readonly int[] _tree; // implicit balanced tree: node = index range midpoint
    private readonly int[] _axis;

    /// <summary>Indexes all buses that have a <see cref="Bus.Location"/>.</summary>
    public BusSpatialIndex(PowerNetwork net)
        : this(net.Buses.Where(b => b.Location is not null).Select(b => (b.Id, b.Location!.Value))) { }

    /// <summary>Indexes arbitrary (id, location) pairs.</summary>
    public BusSpatialIndex(IEnumerable<(int Id, GeoPoint Location)> items)
    {
        var list = items.ToList();
        _ids = list.Select(i => i.Id).ToArray();
        _pts = list.Select(i => i.Location).ToArray();
        _xyz = _pts.Select(ToXyz).ToArray();
        _tree = Enumerable.Range(0, _ids.Length).ToArray();
        _axis = new int[_ids.Length];
        Build(0, _tree.Length, 0);
    }

    /// <summary>Number of indexed points.</summary>
    public int Count => _ids.Length;

    private static double[] ToXyz(GeoPoint p)
    {
        double la = p.Latitude * Math.PI / 180, lo = p.Longitude * Math.PI / 180;
        return [Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la)];
    }

    private void Build(int lo, int hi, int depth)
    {
        if (hi - lo <= 0) return;
        int axis = depth % 3;
        Array.Sort(_tree, lo, hi - lo, Comparer<int>.Create((a, b) =>
        {
            int c = _xyz[a][axis].CompareTo(_xyz[b][axis]);
            return c != 0 ? c : a.CompareTo(b);
        }));
        int mid = (lo + hi) / 2;
        _axis[mid] = axis;
        Build(lo, mid, depth + 1);
        Build(mid + 1, hi, depth + 1);
    }

    private static double Chord2(double[] a, double[] b)
    {
        double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return dx * dx + dy * dy + dz * dz;
    }

    private static double ChordToKm(double chord2) => 2 * Geo.EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(chord2) / 2));
    private static double KmToChord2(double km) { double c = 2 * Math.Sin(Math.Min(Math.PI, km / Geo.EarthRadiusKm) / 2); return c * c; }

    /// <summary>Nearest bus to a point, or null when the index is empty.</summary>
    public BusHit? Nearest(GeoPoint p)
    {
        var r = KNearest(p, 1);
        return r.Count == 0 ? null : r[0];
    }

    /// <summary>The <paramref name="k"/> nearest buses, closest first (ties broken by insertion order).</summary>
    public IReadOnlyList<BusHit> KNearest(GeoPoint p, int k)
    {
        if (k <= 0 || Count == 0) return [];
        var q = ToXyz(p);
        var best = new List<(double D, int I)>(k + 1);
        void Visit(int lo, int hi)
        {
            if (hi - lo <= 0) return;
            int mid = (lo + hi) / 2, node = _tree[mid], axis = _axis[mid];
            double d = Chord2(q, _xyz[node]);
            if (best.Count < k || d < best[^1].D || (d == best[^1].D && node < best[^1].I))
            {
                int pos = best.FindIndex(b => d < b.D || (d == b.D && node < b.I));
                best.Insert(pos < 0 ? best.Count : pos, (d, node));
                if (best.Count > k) best.RemoveAt(best.Count - 1);
            }
            double diff = q[axis] - _xyz[node][axis];
            var (first, second) = diff < 0 ? ((lo, mid), (mid + 1, hi)) : ((mid + 1, hi), (lo, mid));
            Visit(first.Item1, first.Item2);
            if (best.Count < k || diff * diff <= best[^1].D) Visit(second.Item1, second.Item2);
        }
        Visit(0, _tree.Length);
        return best.Select(b => new BusHit(_ids[b.I], _pts[b.I], ChordToKm(b.D))).ToList();
    }

    /// <summary>All buses within <paramref name="radiusKm"/> of a point, closest first.</summary>
    public IReadOnlyList<BusHit> WithinRadius(GeoPoint p, double radiusKm)
    {
        if (Count == 0 || radiusKm < 0) return [];
        var q = ToXyz(p);
        double r2 = KmToChord2(radiusKm);
        var hits = new List<(double D, int I)>();
        void Visit(int lo, int hi)
        {
            if (hi - lo <= 0) return;
            int mid = (lo + hi) / 2, node = _tree[mid], axis = _axis[mid];
            double d = Chord2(q, _xyz[node]);
            if (d <= r2) hits.Add((d, node));
            double diff = q[axis] - _xyz[node][axis];
            if (diff - Math.Sqrt(r2) <= 0) Visit(lo, mid);
            if (diff + Math.Sqrt(r2) >= 0) Visit(mid + 1, hi);
        }
        Visit(0, _tree.Length);
        return hits.OrderBy(h => h.D).ThenBy(h => h.I).Select(h => new BusHit(_ids[h.I], _pts[h.I], ChordToKm(h.D))).ToList();
    }

    /// <summary>Buses whose location lies inside a bounding box.</summary>
    public IReadOnlyList<int> InBounds(GeoBounds box) => Enumerable.Range(0, Count).Where(i => box.Contains(_pts[i])).Select(i => _ids[i]).ToList();
}
