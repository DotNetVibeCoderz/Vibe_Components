using Agritech.Core.Geo;
using Agritech.Core.Hosting;

namespace Agritech.GIS;

/// <summary>Planar computational geometry utilities (operate in a local metric frame).</summary>
public static class Geometry
{
    /// <summary>Douglas–Peucker line simplification with tolerance in metres.</summary>
    public static IReadOnlyList<GeoCoordinate> Simplify(IReadOnlyList<GeoCoordinate> line, double toleranceM)
    {
        if (line.Count < 3) return line;
        var origin = line[0];
        var pts = line.Select(p => GeoMath.ToLocal(origin, p)).ToArray();
        var keep = new bool[pts.Length];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Length - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            double maxD = 0; int idx = -1;
            for (int i = s + 1; i < e; i++)
            {
                double d = PointSegmentDistance(pts[i], pts[s], pts[e]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && maxD > toleranceM) { keep[idx] = true; stack.Push((s, idx)); stack.Push((idx, e)); }
        }
        return line.Where((_, i) => keep[i]).ToList();
    }

    public static double PointSegmentDistance(Point2 p, Point2 a, Point2 b)
    {
        var ab = b - a;
        double len2 = ab.Dot(ab);
        if (len2 == 0) return p.DistanceTo(a);
        double t = Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>Convex hull (Andrew's monotone chain).</summary>
    public static IReadOnlyList<GeoCoordinate> ConvexHull(IEnumerable<GeoCoordinate> points)
    {
        var pts = points.Distinct().OrderBy(p => p.Longitude).ThenBy(p => p.Latitude).ToList();
        if (pts.Count < 3) return pts;
        static double Cross(GeoCoordinate o, GeoCoordinate a, GeoCoordinate b) =>
            (a.Longitude - o.Longitude) * (b.Latitude - o.Latitude) - (a.Latitude - o.Latitude) * (b.Longitude - o.Longitude);
        var hull = new List<GeoCoordinate>();
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            int start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    /// <summary>Clips a horizontal line y = const against a polygon in local coordinates; returns sorted x-intervals.</summary>
    public static List<(double X0, double X1)> ScanlineIntervals(ReadOnlySpan<Point2> ring, double y)
    {
        var xs = new List<double>();
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var a = ring[i]; var b = ring[j];
            if ((a.Y > y) != (b.Y > y)) xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
        }
        xs.Sort();
        var res = new List<(double, double)>();
        for (int k = 0; k + 1 < xs.Count; k += 2) res.Add((xs[k], xs[k + 1]));
        return res;
    }

    /// <summary>Rotates a point around the origin by angle (radians).</summary>
    public static Point2 Rotate(Point2 p, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new Point2(p.X * c - p.Y * s, p.X * s + p.Y * c);
    }

    /// <summary>Angle (radians) of the polygon's longest edge — the efficient direction for parallel tracks.</summary>
    public static double LongestEdgeAngle(ReadOnlySpan<Point2> ring)
    {
        double best = -1, angle = 0;
        for (int i = 0; i < ring.Length; i++)
        {
            var d = ring[(i + 1) % ring.Length] - ring[i];
            if (d.Length > best) { best = d.Length; angle = Math.Atan2(d.Y, d.X); }
        }
        return angle;
    }
}

public static class GisBuilderExtensions
{
    public static AgritechBuilder AddGis(this AgritechBuilder builder)
    {
        builder.TryAddModule("GIS");
        return builder;
    }
}
