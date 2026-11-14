using Electrical.Core.Network;

namespace Electrical.GIS;

/// <summary>Outage-area helpers: de-energized bus detection, convex hull polygons and polygon areas.</summary>
public static class OutageArea
{
    /// <summary>
    /// Buses that cannot be supplied: members of islands (over in-service branches) without the slack bus or an in-service
    /// generator, plus buses of type <see cref="BusType.Isolated"/>.
    /// </summary>
    public static IReadOnlyList<int> DeEnergizedBuses(PowerNetwork net)
    {
        var sources = new HashSet<int>(net.Generators.Where(g => g.InService).Select(g => g.Bus));
        if (net.SlackBus is { } s) sources.Add(s.Id);
        var dead = new List<int>();
        foreach (var island in Topology.Islands(net))
            if (!island.Any(sources.Contains)) dead.AddRange(island);
        dead.AddRange(net.Buses.Where(b => b.Type == BusType.Isolated).Select(b => b.Id));
        var order = net.Buses.Select((b, i) => (b.Id, i)).ToDictionary(t => t.Id, t => t.i);
        return dead.Distinct().OrderBy(id => order[id]).ToList();
    }

    /// <summary>
    /// Convex hull (counter-clockwise, not closed) of points, computed with Andrew's monotone chain in a local equirectangular plane.
    /// Returns the input (deduplicated) when fewer than three distinct points exist.
    /// </summary>
    public static IReadOnlyList<GeoPoint> ConvexHull(IEnumerable<GeoPoint> points)
    {
        var pts = points.Distinct().ToList();
        if (pts.Count < 3) return pts;
        double cosLat = Math.Cos(pts.Average(p => p.Latitude) * Math.PI / 180);
        var sorted = pts.OrderBy(p => p.Longitude).ThenBy(p => p.Latitude).ToList();
        double Cross(GeoPoint o, GeoPoint a, GeoPoint b) =>
            (a.Longitude - o.Longitude) * cosLat * (b.Latitude - o.Latitude) - (a.Latitude - o.Latitude) * (b.Longitude - o.Longitude) * cosLat;
        var hull = new GeoPoint[2 * sorted.Count];
        int k = 0;
        foreach (var p in sorted)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        for (int i = sorted.Count - 2, lower = k + 1; i >= 0; i--)
        {
            var p = sorted[i];
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        return hull[..(k - 1)];
    }

    /// <summary>
    /// Outage polygon around the given buses: convex hull of circles of <paramref name="bufferKm"/> (approximated by
    /// <paramref name="segments"/> points) around each located bus. Empty when no bus is located.
    /// </summary>
    public static IReadOnlyList<GeoPoint> Polygon(PowerNetwork net, IEnumerable<int> busIds, double bufferKm = 0.5, int segments = 16)
    {
        var pts = new List<GeoPoint>();
        foreach (int id in busIds)
        {
            if (!net.ContainsBus(id) || net.GetBus(id).Location is not GeoPoint c) continue;
            if (bufferKm <= 0) { pts.Add(c); continue; }
            for (int i = 0; i < segments; i++)
            {
                double a = 2 * Math.PI * i / segments;
                pts.Add(Geo.Offset(c, bufferKm * Math.Cos(a), bufferKm * Math.Sin(a)));
            }
        }
        return ConvexHull(pts);
    }

    /// <summary>Outage polygon around all de-energized buses of the network (see <see cref="DeEnergizedBuses"/>).</summary>
    public static IReadOnlyList<GeoPoint> Polygon(PowerNetwork net, double bufferKm = 0.5) => Polygon(net, DeEnergizedBuses(net), bufferKm);

    /// <summary>Area [km²] of a simple polygon ring (shoelace formula in a local tangent plane; adequate for regional extents).</summary>
    public static double AreaKm2(IReadOnlyList<GeoPoint> ring)
    {
        if (ring.Count < 3) return 0;
        var origin = new GeoPoint(ring.Average(p => p.Latitude), ring.Average(p => p.Longitude));
        double s = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var (x1, y1) = Geo.LocalKm(origin, ring[i]);
            var (x2, y2) = Geo.LocalKm(origin, ring[(i + 1) % ring.Count]);
            s += x1 * y2 - x2 * y1;
        }
        return Math.Abs(s) / 2;
    }

    /// <summary>Ray-casting point-in-polygon test (ring in degrees, not necessarily closed).</summary>
    public static bool Contains(IReadOnlyList<GeoPoint> ring, GeoPoint p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Latitude > p.Latitude) != (b.Latitude > p.Latitude) &&
                p.Longitude < (b.Longitude - a.Longitude) * (p.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + a.Longitude)
                inside = !inside;
        }
        return inside;
    }
}
