namespace CesiumNet;

/// <summary>Result of an inverse geodesic computation between two points.</summary>
/// <param name="Distance">Ellipsoidal surface distance in meters.</param>
/// <param name="InitialBearing">Forward azimuth at the start point, degrees clockwise from north [0, 360).</param>
/// <param name="FinalBearing">Forward azimuth at the end point, degrees [0, 360).</param>
public readonly record struct GeodesicInverse(double Distance, double InitialBearing, double FinalBearing);

/// <summary>
/// Geodesic calculations on the WGS84 ellipsoid: distance, bearing, destination,
/// interpolation, path length, polygon area and centroid. Used by measurement tools
/// and terrain profiling. All results are in meters / square meters / degrees.
/// </summary>
public static class Geodesy
{
    private static readonly double A = Ellipsoid.Wgs84.SemiMajorAxis;
    private static readonly double F = Ellipsoid.Wgs84.Flattening;
    private static readonly double B = Ellipsoid.Wgs84.SemiMinorAxis;
    private static readonly double AuthalicRadius = Ellipsoid.Wgs84.AuthalicRadius;

    /// <summary>Mean earth radius in meters (IUGG).</summary>
    public const double MeanEarthRadius = 6371008.8;

    /// <summary>Great-circle distance on a sphere (fast, ~0.5% error). Meters.</summary>
    public static double HaversineDistance(GeoPoint a, GeoPoint b)
    {
        var dLat = GeoMath.ToRadians(b.Latitude - a.Latitude);
        var dLon = GeoMath.ToRadians(b.Longitude - a.Longitude);
        var lat1 = GeoMath.ToRadians(a.Latitude);
        var lat2 = GeoMath.ToRadians(b.Latitude);
        var h = System.Math.Sin(dLat / 2) * System.Math.Sin(dLat / 2) +
                System.Math.Cos(lat1) * System.Math.Cos(lat2) * System.Math.Sin(dLon / 2) * System.Math.Sin(dLon / 2);
        return 2 * MeanEarthRadius * System.Math.Asin(System.Math.Min(1, System.Math.Sqrt(h)));
    }

    /// <summary>Ellipsoidal surface distance (Vincenty, millimeter accuracy). Meters.</summary>
    public static double Distance(GeoPoint a, GeoPoint b) => Inverse(a, b).Distance;

    /// <summary>Straight-line (chord) 3D distance including heights. Meters.</summary>
    public static double SlantDistance(GeoPoint a, GeoPoint b) => Cartesian3.Distance(a.ToCartesian(), b.ToCartesian());

    /// <summary>Initial bearing from <paramref name="a"/> to <paramref name="b"/>, degrees [0, 360).</summary>
    public static double Bearing(GeoPoint a, GeoPoint b) => Inverse(a, b).InitialBearing;

    /// <summary>
    /// Solves the inverse geodesic problem with Vincenty's formulae. Falls back to a spherical
    /// solution for nearly antipodal points where Vincenty does not converge.
    /// </summary>
    public static GeodesicInverse Inverse(GeoPoint p1, GeoPoint p2)
    {
        if (p1.Latitude == p2.Latitude && p1.Longitude == p2.Longitude)
            return new GeodesicInverse(0, 0, 0);

        var L = GeoMath.ToRadians(p2.Longitude - p1.Longitude);
        var U1 = System.Math.Atan((1 - F) * System.Math.Tan(GeoMath.ToRadians(p1.Latitude)));
        var U2 = System.Math.Atan((1 - F) * System.Math.Tan(GeoMath.ToRadians(p2.Latitude)));
        double sinU1 = System.Math.Sin(U1), cosU1 = System.Math.Cos(U1);
        double sinU2 = System.Math.Sin(U2), cosU2 = System.Math.Cos(U2);

        double lambda = L, lambdaP;
        double sinSigma, cosSigma, sigma, sinAlpha, cos2Alpha, cos2SigmaM, sinLambda, cosLambda;
        var iter = 0;
        do
        {
            sinLambda = System.Math.Sin(lambda);
            cosLambda = System.Math.Cos(lambda);
            var t1 = cosU2 * sinLambda;
            var t2 = cosU1 * sinU2 - sinU1 * cosU2 * cosLambda;
            sinSigma = System.Math.Sqrt(t1 * t1 + t2 * t2);
            if (sinSigma == 0) return new GeodesicInverse(0, 0, 0);
            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
            sigma = System.Math.Atan2(sinSigma, cosSigma);
            sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cos2Alpha = 1 - sinAlpha * sinAlpha;
            cos2SigmaM = cos2Alpha != 0 ? cosSigma - 2 * sinU1 * sinU2 / cos2Alpha : 0;
            var C = F / 16 * cos2Alpha * (4 + F * (4 - 3 * cos2Alpha));
            lambdaP = lambda;
            lambda = L + (1 - C) * F * sinAlpha *
                     (sigma + C * sinSigma * (cos2SigmaM + C * cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)));
        } while (System.Math.Abs(lambda - lambdaP) > 1e-12 && ++iter < 200);

        if (iter >= 200)
        {
            // Antipodal: fall back to spherical approximation.
            return new GeodesicInverse(HaversineDistance(p1, p2), SphericalBearing(p1, p2),
                GeoMath.NormalizeBearing(SphericalBearing(p2, p1) + 180));
        }

        var uSq = cos2Alpha * (A * A - B * B) / (B * B);
        var bigA = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
        var bigB = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
        var deltaSigma = bigB * sinSigma * (cos2SigmaM + bigB / 4 *
            (cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM) -
             bigB / 6 * cos2SigmaM * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2SigmaM * cos2SigmaM)));
        var s = B * bigA * (sigma - deltaSigma);

        var a1 = System.Math.Atan2(cosU2 * sinLambda, cosU1 * sinU2 - sinU1 * cosU2 * cosLambda);
        var a2 = System.Math.Atan2(cosU1 * sinLambda, -sinU1 * cosU2 + cosU1 * sinU2 * cosLambda);
        return new GeodesicInverse(s, GeoMath.NormalizeBearing(GeoMath.ToDegrees(a1)), GeoMath.NormalizeBearing(GeoMath.ToDegrees(a2)));
    }

    /// <summary>
    /// Solves the direct geodesic problem (Vincenty): the point reached by travelling
    /// <paramref name="distance"/> meters from <paramref name="start"/> along <paramref name="bearing"/> degrees.
    /// The start height is preserved.
    /// </summary>
    public static GeoPoint Destination(GeoPoint start, double bearing, double distance)
    {
        var alpha1 = GeoMath.ToRadians(bearing);
        double sinAlpha1 = System.Math.Sin(alpha1), cosAlpha1 = System.Math.Cos(alpha1);
        var tanU1 = (1 - F) * System.Math.Tan(GeoMath.ToRadians(start.Latitude));
        var cosU1 = 1 / System.Math.Sqrt(1 + tanU1 * tanU1);
        var sinU1 = tanU1 * cosU1;
        var sigma1 = System.Math.Atan2(tanU1, cosAlpha1);
        var sinAlpha = cosU1 * sinAlpha1;
        var cos2Alpha = 1 - sinAlpha * sinAlpha;
        var uSq = cos2Alpha * (A * A - B * B) / (B * B);
        var bigA = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
        var bigB = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));

        var sigma = distance / (B * bigA);
        double sigmaP, cos2SigmaM, sinSigma, cosSigma;
        var iter = 0;
        do
        {
            cos2SigmaM = System.Math.Cos(2 * sigma1 + sigma);
            sinSigma = System.Math.Sin(sigma);
            cosSigma = System.Math.Cos(sigma);
            var deltaSigma = bigB * sinSigma * (cos2SigmaM + bigB / 4 *
                (cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM) -
                 bigB / 6 * cos2SigmaM * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2SigmaM * cos2SigmaM)));
            sigmaP = sigma;
            sigma = distance / (B * bigA) + deltaSigma;
        } while (System.Math.Abs(sigma - sigmaP) > 1e-12 && ++iter < 200);

        var tmp = sinU1 * sinSigma - cosU1 * cosSigma * cosAlpha1;
        var lat2 = System.Math.Atan2(sinU1 * cosSigma + cosU1 * sinSigma * cosAlpha1,
            (1 - F) * System.Math.Sqrt(sinAlpha * sinAlpha + tmp * tmp));
        var lambda = System.Math.Atan2(sinSigma * sinAlpha1, cosU1 * cosSigma - sinU1 * sinSigma * cosAlpha1);
        var C = F / 16 * cos2Alpha * (4 + F * (4 - 3 * cos2Alpha));
        var L = lambda - (1 - C) * F * sinAlpha *
                (sigma + C * sinSigma * (cos2SigmaM + C * cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)));
        var lon2 = GeoMath.NormalizeLongitude(start.Longitude + GeoMath.ToDegrees(L));
        return new GeoPoint(lon2, GeoMath.ToDegrees(lat2), start.Height);
    }

    /// <summary>Returns <paramref name="count"/> evenly spaced points along the geodesic from a to b (inclusive). Heights are linearly interpolated.</summary>
    public static GeoPoint[] Interpolate(GeoPoint a, GeoPoint b, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);
        var inv = Inverse(a, b);
        var result = new GeoPoint[count];
        result[0] = a;
        result[^1] = b;
        for (var i = 1; i < count - 1; i++)
        {
            var t = (double)i / (count - 1);
            var p = Destination(a, inv.InitialBearing, inv.Distance * t);
            result[i] = p with { Height = a.Height + (b.Height - a.Height) * t };
        }
        return result;
    }

    /// <summary>Geodesic midpoint between two points.</summary>
    public static GeoPoint Midpoint(GeoPoint a, GeoPoint b)
    {
        var inv = Inverse(a, b);
        return Destination(a, inv.InitialBearing, inv.Distance / 2) with { Height = (a.Height + b.Height) / 2 };
    }

    /// <summary>Total ellipsoidal length of a polyline in meters.</summary>
    public static double PathLength(IReadOnlyList<GeoPoint> path)
    {
        double sum = 0;
        for (var i = 1; i < path.Count; i++) sum += Distance(path[i - 1], path[i]);
        return sum;
    }

    /// <summary>Perimeter of a polygon ring (closed automatically) in meters.</summary>
    public static double Perimeter(IReadOnlyList<GeoPoint> ring)
    {
        if (ring.Count < 2) return 0;
        var len = PathLength(ring);
        if (ring[0] != ring[^1]) len += Distance(ring[^1], ring[0]);
        return len;
    }

    /// <summary>
    /// Area of a polygon ring on the ellipsoid surface in square meters (spherical excess on the
    /// WGS84 authalic sphere, typically within 0.1% of the exact ellipsoidal area). Ring orientation does not matter.
    /// </summary>
    public static double Area(IReadOnlyList<GeoPoint> ring)
    {
        var n = ring.Count;
        if (n < 3) return 0;
        double total = 0;
        for (var i = 0; i < n; i++)
        {
            var p1 = ring[i];
            var p2 = ring[(i + 1) % n];
            total += GeoMath.ToRadians(p2.Longitude - p1.Longitude) *
                     (2 + System.Math.Sin(GeoMath.ToRadians(p1.Latitude)) + System.Math.Sin(GeoMath.ToRadians(p2.Latitude)));
        }
        return System.Math.Abs(total * AuthalicRadius * AuthalicRadius / 2);
    }

    /// <summary>Centroid of a set of points (average on the ellipsoid via ECEF), height 0.</summary>
    public static GeoPoint Centroid(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count == 0) throw new ArgumentException("At least one point is required.", nameof(points));
        var sum = Cartesian3.Zero;
        foreach (var p in points) sum += p.WithHeight(0).ToCartesian();
        return (sum / points.Count).ToGeoPoint().WithHeight(0);
    }

    /// <summary>Planar point-in-polygon test in longitude/latitude space (ray casting). Suitable for regional polygons.</summary>
    public static bool Contains(IReadOnlyList<GeoPoint> ring, GeoPoint p)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i]; var b = ring[j];
            if ((a.Latitude > p.Latitude) != (b.Latitude > p.Latitude) &&
                p.Longitude < (b.Longitude - a.Longitude) * (p.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + a.Longitude)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Generates a geodesic circle (polygon ring) around a center.</summary>
    public static GeoPoint[] Circle(GeoPoint center, double radius, int segments = 64)
    {
        var ring = new GeoPoint[segments];
        for (var i = 0; i < segments; i++)
            ring[i] = Destination(center, 360.0 * i / segments, radius);
        return ring;
    }

    /// <summary>Formats a distance as "850 m" / "12.40 km".</summary>
    public static string FormatDistance(double meters)
        => meters >= 1000
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{meters / 1000:F2} km")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{meters:F1} m");

    /// <summary>Formats an area as "m²" / "ha" / "km²".</summary>
    public static string FormatArea(double squareMeters)
        => squareMeters switch
        {
            >= 1_000_000 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{squareMeters / 1_000_000:F2} km²"),
            >= 10_000 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{squareMeters / 10_000:F2} ha"),
            _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{squareMeters:F1} m²"),
        };

    private static double SphericalBearing(GeoPoint a, GeoPoint b)
    {
        var lat1 = GeoMath.ToRadians(a.Latitude); var lat2 = GeoMath.ToRadians(b.Latitude);
        var dLon = GeoMath.ToRadians(b.Longitude - a.Longitude);
        var y = System.Math.Sin(dLon) * System.Math.Cos(lat2);
        var x = System.Math.Cos(lat1) * System.Math.Sin(lat2) - System.Math.Sin(lat1) * System.Math.Cos(lat2) * System.Math.Cos(dLon);
        return GeoMath.NormalizeBearing(GeoMath.ToDegrees(System.Math.Atan2(y, x)));
    }
}
