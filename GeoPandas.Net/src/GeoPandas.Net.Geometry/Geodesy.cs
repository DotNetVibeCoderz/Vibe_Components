using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Geometry;

/// <summary>Geodesic computations on the ellipsoid / sphere for lon/lat data (degrees).</summary>
public static class Geodesy
{
    private const double Deg = Math.PI / 180.0;
    /// <summary>Mean Earth radius (IUGG), metres.</summary>
    public const double EarthRadius = 6371008.8;

    /// <summary>Great-circle distance in metres (haversine).</summary>
    public static double Haversine(double lon1, double lat1, double lon2, double lat2, double radius = EarthRadius)
    {
        double p1 = lat1 * Deg, p2 = lat2 * Deg;
        double dp = p2 - p1, dl = (lon2 - lon1) * Deg;
        double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * radius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Vincenty inverse: ellipsoidal distance (m) and initial/final azimuths (degrees). Falls back to haversine for antipodal non-convergence.</summary>
    public static (double Distance, double InitialBearing, double FinalBearing) Vincenty(double lon1, double lat1, double lon2, double lat2, Ellipsoid? ellipsoid = null)
    {
        var e = ellipsoid ?? Ellipsoid.Wgs84;
        double a = e.A, f = e.F, b = e.B;
        double L = (lon2 - lon1) * Deg;
        double u1 = Math.Atan((1 - f) * Math.Tan(lat1 * Deg)), u2 = Math.Atan((1 - f) * Math.Tan(lat2 * Deg));
        double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1), sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);
        double lambda = L, lambdaP;
        double sinSigma, cosSigma, sigma, sinAlpha, cos2Alpha, cos2SigmaM;
        int iter = 0;
        do
        {
            double sinL = Math.Sin(lambda), cosL = Math.Cos(lambda);
            sinSigma = Math.Sqrt(Math.Pow(cosU2 * sinL, 2) + Math.Pow(cosU1 * sinU2 - sinU1 * cosU2 * cosL, 2));
            if (sinSigma == 0) return (0, 0, 0);
            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosL;
            sigma = Math.Atan2(sinSigma, cosSigma);
            sinAlpha = cosU1 * cosU2 * sinL / sinSigma;
            cos2Alpha = 1 - sinAlpha * sinAlpha;
            cos2SigmaM = cos2Alpha != 0 ? cosSigma - 2 * sinU1 * sinU2 / cos2Alpha : 0;
            double c = f / 16 * cos2Alpha * (4 + f * (4 - 3 * cos2Alpha));
            lambdaP = lambda;
            lambda = L + (1 - c) * f * sinAlpha * (sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)));
        } while (Math.Abs(lambda - lambdaP) > 1e-12 && ++iter < 200);

        if (iter >= 200)
            return (Haversine(lon1, lat1, lon2, lat2), double.NaN, double.NaN);

        double uSq = cos2Alpha * (a * a - b * b) / (b * b);
        double A = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
        double B = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
        double dSigma = B * sinSigma * (cos2SigmaM + B / 4 * (cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM) -
                         B / 6 * cos2SigmaM * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2SigmaM * cos2SigmaM)));
        double s = b * A * (sigma - dSigma);
        double sinLam = Math.Sin(lambda), cosLam = Math.Cos(lambda);
        double a1 = Math.Atan2(cosU2 * sinLam, cosU1 * sinU2 - sinU1 * cosU2 * cosLam) / Deg;
        double a2 = Math.Atan2(cosU1 * sinLam, -sinU1 * cosU2 + cosU1 * sinU2 * cosLam) / Deg;
        return (s, (a1 + 360) % 360, (a2 + 360) % 360);
    }

    /// <summary>Destination point from a start, bearing (deg) and distance (m) on a sphere.</summary>
    public static (double Lon, double Lat) Destination(double lon, double lat, double bearing, double distance, double radius = EarthRadius)
    {
        double d = distance / radius, br = bearing * Deg, p1 = lat * Deg, l1 = lon * Deg;
        double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(br));
        double l2 = l1 + Math.Atan2(Math.Sin(br) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
        return (((l2 / Deg) + 540) % 360 - 180, p2 / Deg);
    }

    /// <summary>Geodesic length (m) of a lon/lat geometry (sum of segment haversine distances).</summary>
    public static double Length(NtsGeometry g)
    {
        double total = 0;
        for (int i = 0; i < g.NumGeometries; i++)
        {
            var part = g.GetGeometryN(i);
            switch (part)
            {
                case LineString ls: total += SeqLength(ls.CoordinateSequence); break;
                case Polygon p:
                    total += SeqLength(p.ExteriorRing.CoordinateSequence);
                    foreach (var h in p.InteriorRings) total += SeqLength(h.CoordinateSequence);
                    break;
                case GeometryCollection gc when gc != g: total += Length(gc); break;
            }
        }
        return total;
    }

    private static double SeqLength(CoordinateSequence s)
    {
        double t = 0;
        for (int i = 1; i < s.Count; i++) t += Haversine(s.GetX(i - 1), s.GetY(i - 1), s.GetX(i), s.GetY(i));
        return t;
    }

    /// <summary>Geodesic area (m²) of a lon/lat (multi)polygon on the sphere (Chamberlain &amp; Duquette).</summary>
    public static double Area(NtsGeometry g)
    {
        double total = 0;
        for (int i = 0; i < g.NumGeometries; i++)
        {
            if (g.GetGeometryN(i) is not Polygon p) continue;
            total += Math.Abs(RingArea(p.ExteriorRing.CoordinateSequence));
            foreach (var h in p.InteriorRings) total -= Math.Abs(RingArea(h.CoordinateSequence));
        }
        return total;
    }

    private static double RingArea(CoordinateSequence s)
    {
        int n = s.Count;
        if (n < 4) return 0;
        double area = 0;
        for (int i = 0; i < n - 1; i++)
        {
            double l1 = s.GetX(i) * Deg, l2 = s.GetX(i + 1) * Deg;
            double p1 = s.GetY(i) * Deg, p2 = s.GetY(i + 1) * Deg;
            area += (l2 - l1) * (2 + Math.Sin(p1) + Math.Sin(p2));
        }
        return area * EarthRadius * EarthRadius / 2;
    }
}
