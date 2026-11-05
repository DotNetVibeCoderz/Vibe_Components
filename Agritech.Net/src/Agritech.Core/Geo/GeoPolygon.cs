namespace Agritech.Core.Geo;

/// <summary>Simple polygon (outer ring + optional holes) in WGS-84.</summary>
public sealed class GeoPolygon
{
    private readonly GeoCoordinate[] _outer;
    private readonly GeoCoordinate[][] _holes;

    public GeoPolygon(IEnumerable<GeoCoordinate> outer, IEnumerable<IEnumerable<GeoCoordinate>>? holes = null)
    {
        _outer = Normalize(outer);
        if (_outer.Length < 3) throw new ArgumentException("A polygon needs at least 3 vertices.", nameof(outer));
        _holes = holes?.Select(Normalize).ToArray() ?? [];
    }

    private static GeoCoordinate[] Normalize(IEnumerable<GeoCoordinate> ring)
    {
        var arr = ring.ToArray();
        if (arr.Length > 1 && arr[0] == arr[^1]) arr = arr[..^1];
        return arr;
    }

    public IReadOnlyList<GeoCoordinate> Outer => _outer;
    public IReadOnlyList<GeoCoordinate[]> Holes => _holes;

    /// <summary>Area in square metres (holes subtracted).</summary>
    public double AreaM2 => GeoMath.PolygonArea(_outer) - _holes.Sum(h => GeoMath.PolygonArea(h));
    public double AreaHectares => AreaM2 / 10_000.0;
    public GeoBounds Bounds => GeoMath.Bounds(_outer);

    public double PerimeterM
    {
        get
        {
            double p = 0;
            for (int i = 0; i < _outer.Length; i++) p += GeoMath.Distance(_outer[i], _outer[(i + 1) % _outer.Length]);
            return p;
        }
    }

    public GeoCoordinate Centroid
    {
        get
        {
            var origin = _outer[0];
            double a = 0, cx = 0, cy = 0;
            for (int i = 0; i < _outer.Length; i++)
            {
                var p = GeoMath.ToLocal(origin, _outer[i]);
                var q = GeoMath.ToLocal(origin, _outer[(i + 1) % _outer.Length]);
                double cross = p.Cross(q);
                a += cross; cx += (p.X + q.X) * cross; cy += (p.Y + q.Y) * cross;
            }
            if (Math.Abs(a) < 1e-12) return origin;
            return GeoMath.FromLocal(origin, new Point2(cx / (3 * a), cy / (3 * a)));
        }
    }

    public bool Contains(GeoCoordinate p)
    {
        if (!GeoMath.Contains(_outer, p)) return false;
        foreach (var h in _holes) if (GeoMath.Contains(h, p)) return false;
        return true;
    }

    /// <summary>Outer ring projected to a local metric plane anchored at the first vertex.</summary>
    public Point2[] ToLocal(out GeoCoordinate origin)
    {
        origin = _outer[0];
        var o = origin;
        return _outer.Select(p => GeoMath.ToLocal(o, p)).ToArray();
    }

    /// <summary>Creates a rectangle polygon of given width/height in metres centred on a point.</summary>
    public static GeoPolygon Rectangle(GeoCoordinate center, double widthM, double heightM)
    {
        double hw = widthM / 2, hh = heightM / 2;
        return new GeoPolygon(
        [
            GeoMath.FromLocal(center, new Point2(-hw, -hh)), GeoMath.FromLocal(center, new Point2(hw, -hh)),
            GeoMath.FromLocal(center, new Point2(hw, hh)), GeoMath.FromLocal(center, new Point2(-hw, hh)),
        ]);
    }
}
