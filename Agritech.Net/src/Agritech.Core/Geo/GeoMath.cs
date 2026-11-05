namespace Agritech.Core.Geo;

/// <summary>Core geodesic math on the WGS-84 mean sphere.</summary>
public static class GeoMath
{
    public const double EarthRadiusM = 6_371_008.8;
    public const double DegToRad = Math.PI / 180.0;
    public const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Great-circle distance (haversine) in metres.</summary>
    public static double Distance(GeoCoordinate a, GeoCoordinate b)
    {
        double dLat = (b.Latitude - a.Latitude) * DegToRad;
        double dLon = (b.Longitude - a.Longitude) * DegToRad;
        double s = Math.Sin(dLat / 2);
        double t = Math.Sin(dLon / 2);
        double h = s * s + Math.Cos(a.Latitude * DegToRad) * Math.Cos(b.Latitude * DegToRad) * t * t;
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Initial bearing from a to b in degrees [0,360).</summary>
    public static double Bearing(GeoCoordinate a, GeoCoordinate b)
    {
        double p1 = a.Latitude * DegToRad, p2 = b.Latitude * DegToRad, dl = (b.Longitude - a.Longitude) * DegToRad;
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * RadToDeg + 360) % 360;
    }

    /// <summary>Destination point given start, bearing (deg) and distance (m).</summary>
    public static GeoCoordinate Destination(GeoCoordinate start, double bearingDeg, double distanceM)
    {
        double d = distanceM / EarthRadiusM, th = bearingDeg * DegToRad;
        double p1 = start.Latitude * DegToRad, l1 = start.Longitude * DegToRad;
        double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(th));
        double l2 = l1 + Math.Atan2(Math.Sin(th) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
        double lon = (l2 * RadToDeg + 540) % 360 - 180;
        return new GeoCoordinate(p2 * RadToDeg, lon, start.Altitude);
    }

    /// <summary>Spherical polygon area in m² (ring may be open or closed).</summary>
    public static double PolygonArea(ReadOnlySpan<GeoCoordinate> ring)
    {
        int n = ring.Length;
        if (n < 3) return 0;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            sum += (b.Longitude - a.Longitude) * DegToRad *
                   (2 + Math.Sin(a.Latitude * DegToRad) + Math.Sin(b.Latitude * DegToRad));
        }
        return Math.Abs(sum * EarthRadiusM * EarthRadiusM / 2.0);
    }

    /// <summary>Ray-casting point-in-polygon test (planar in lon/lat).</summary>
    public static bool Contains(ReadOnlySpan<GeoCoordinate> ring, GeoCoordinate p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var a = ring[i]; var b = ring[j];
            if ((a.Latitude > p.Latitude) != (b.Latitude > p.Latitude) &&
                p.Longitude < (b.Longitude - a.Longitude) * (p.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + a.Longitude)
                inside = !inside;
        }
        return inside;
    }

    public static GeoBounds Bounds(ReadOnlySpan<GeoCoordinate> pts)
    {
        double minLat = double.MaxValue, minLon = double.MaxValue, maxLat = double.MinValue, maxLon = double.MinValue;
        foreach (var p in pts)
        {
            minLat = Math.Min(minLat, p.Latitude); maxLat = Math.Max(maxLat, p.Latitude);
            minLon = Math.Min(minLon, p.Longitude); maxLon = Math.Max(maxLon, p.Longitude);
        }
        return new GeoBounds(minLat, minLon, maxLat, maxLon);
    }

    /// <summary>Projects to a local tangent plane (metres east/north of origin). Accurate at field scale.</summary>
    public static Point2 ToLocal(GeoCoordinate origin, GeoCoordinate p)
    {
        double x = (p.Longitude - origin.Longitude) * DegToRad * EarthRadiusM * Math.Cos(origin.Latitude * DegToRad);
        double y = (p.Latitude - origin.Latitude) * DegToRad * EarthRadiusM;
        return new Point2(x, y);
    }

    public static GeoCoordinate FromLocal(GeoCoordinate origin, Point2 p)
    {
        double lat = origin.Latitude + p.Y / EarthRadiusM * RadToDeg;
        double lon = origin.Longitude + p.X / (EarthRadiusM * Math.Cos(origin.Latitude * DegToRad)) * RadToDeg;
        return new GeoCoordinate(lat, lon);
    }
}
