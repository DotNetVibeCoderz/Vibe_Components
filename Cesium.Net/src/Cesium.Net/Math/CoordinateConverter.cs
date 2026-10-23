namespace CesiumNet;

/// <summary>Reference ellipsoid parameters.</summary>
public sealed record Ellipsoid(double SemiMajorAxis, double InverseFlattening)
{
    /// <summary>World Geodetic System 1984 — the datum used by CesiumJS and GPS.</summary>
    public static readonly Ellipsoid Wgs84 = new(6378137.0, 298.257223563);

    public double Flattening => 1.0 / InverseFlattening;
    public double SemiMinorAxis => SemiMajorAxis * (1 - Flattening);
    /// <summary>First eccentricity squared.</summary>
    public double EccentricitySquared => Flattening * (2 - Flattening);
    /// <summary>Second eccentricity squared.</summary>
    public double SecondEccentricitySquared => EccentricitySquared / (1 - EccentricitySquared);
    /// <summary>Radius of the sphere with the same surface area (used for area calculations).</summary>
    public double AuthalicRadius
    {
        get
        {
            // R² = a²/2 · (1 + (1 − e²)/e · atanh(e))
            var a = SemiMajorAxis; var e2 = EccentricitySquared; var e = System.Math.Sqrt(e2);
            return System.Math.Sqrt(a * a / 2 * (1 + (1 - e2) / e * System.Math.Atanh(e)));
        }
    }
}

/// <summary>
/// Conversions between coordinate systems: WGS84 geodetic (lon/lat/height), ECEF,
/// local East-North-Up (ENU) and Web Mercator (EPSG:3857).
/// All methods are allocation-free.
/// </summary>
public static class CoordinateConverter
{
    /// <summary>Web Mercator maximum latitude (degrees).</summary>
    public const double WebMercatorMaxLatitude = 85.05112877980659;

    /// <summary>Geodetic (degrees, meters) → Earth-Centered, Earth-Fixed (meters).</summary>
    public static Cartesian3 GeodeticToEcef(GeoPoint p, Ellipsoid? ellipsoid = null)
    {
        var el = ellipsoid ?? Ellipsoid.Wgs84;
        var lat = GeoMath.ToRadians(p.Latitude);
        var lon = GeoMath.ToRadians(p.Longitude);
        var sinLat = System.Math.Sin(lat);
        var cosLat = System.Math.Cos(lat);
        var e2 = el.EccentricitySquared;
        var n = el.SemiMajorAxis / System.Math.Sqrt(1 - e2 * sinLat * sinLat);
        return new Cartesian3(
            (n + p.Height) * cosLat * System.Math.Cos(lon),
            (n + p.Height) * cosLat * System.Math.Sin(lon),
            (n * (1 - e2) + p.Height) * sinLat);
    }

    /// <summary>ECEF (meters) → geodetic (degrees, meters), using Bowring's iterative method (sub-millimeter).</summary>
    public static GeoPoint EcefToGeodetic(Cartesian3 c, Ellipsoid? ellipsoid = null)
    {
        var el = ellipsoid ?? Ellipsoid.Wgs84;
        var a = el.SemiMajorAxis;
        var e2 = el.EccentricitySquared;
        var p = System.Math.Sqrt(c.X * c.X + c.Y * c.Y);
        var lon = System.Math.Atan2(c.Y, c.X);

        if (p < 1e-9)
        {
            // On the polar axis.
            var latPole = c.Z >= 0 ? System.Math.PI / 2 : -System.Math.PI / 2;
            return GeoPoint.FromRadians(0, latPole, System.Math.Abs(c.Z) - el.SemiMinorAxis);
        }

        var lat = System.Math.Atan2(c.Z, p * (1 - e2));
        double h = 0;
        for (var i = 0; i < 6; i++)
        {
            var sinLat = System.Math.Sin(lat);
            var n = a / System.Math.Sqrt(1 - e2 * sinLat * sinLat);
            h = p / System.Math.Cos(lat) - n;
            var next = System.Math.Atan2(c.Z, p * (1 - e2 * n / (n + h)));
            if (System.Math.Abs(next - lat) < 1e-13) { lat = next; break; }
            lat = next;
        }

        // Recompute height robustly near the poles.
        var sl = System.Math.Sin(lat);
        var nn = a / System.Math.Sqrt(1 - e2 * sl * sl);
        h = System.Math.Abs(lat) < System.Math.PI / 4
            ? p / System.Math.Cos(lat) - nn
            : c.Z / sl - nn * (1 - e2);

        return GeoPoint.FromRadians(lon, lat, h);
    }

    /// <summary>Converts an ECEF position into local East-North-Up coordinates (meters) relative to <paramref name="origin"/>.</summary>
    public static Cartesian3 EcefToEnu(Cartesian3 ecef, GeoPoint origin, Ellipsoid? ellipsoid = null)
    {
        var o = GeodeticToEcef(origin, ellipsoid);
        var d = ecef - o;
        var lat = GeoMath.ToRadians(origin.Latitude);
        var lon = GeoMath.ToRadians(origin.Longitude);
        var sLat = System.Math.Sin(lat); var cLat = System.Math.Cos(lat);
        var sLon = System.Math.Sin(lon); var cLon = System.Math.Cos(lon);
        return new Cartesian3(
            -sLon * d.X + cLon * d.Y,
            -sLat * cLon * d.X - sLat * sLon * d.Y + cLat * d.Z,
            cLat * cLon * d.X + cLat * sLon * d.Y + sLat * d.Z);
    }

    /// <summary>Converts local East-North-Up coordinates (meters) relative to <paramref name="origin"/> into ECEF.</summary>
    public static Cartesian3 EnuToEcef(Cartesian3 enu, GeoPoint origin, Ellipsoid? ellipsoid = null)
    {
        var o = GeodeticToEcef(origin, ellipsoid);
        var lat = GeoMath.ToRadians(origin.Latitude);
        var lon = GeoMath.ToRadians(origin.Longitude);
        var sLat = System.Math.Sin(lat); var cLat = System.Math.Cos(lat);
        var sLon = System.Math.Sin(lon); var cLon = System.Math.Cos(lon);
        return new Cartesian3(
            o.X - sLon * enu.X - sLat * cLon * enu.Y + cLat * cLon * enu.Z,
            o.Y + cLon * enu.X - sLat * sLon * enu.Y + cLat * sLon * enu.Z,
            o.Z + cLat * enu.Y + sLat * enu.Z);
    }

    /// <summary>Geodetic → local ENU (meters) relative to <paramref name="origin"/>.</summary>
    public static Cartesian3 GeodeticToEnu(GeoPoint point, GeoPoint origin) => EcefToEnu(GeodeticToEcef(point), origin);

    /// <summary>Local ENU (meters) relative to <paramref name="origin"/> → geodetic.</summary>
    public static GeoPoint EnuToGeodetic(Cartesian3 enu, GeoPoint origin) => EcefToGeodetic(EnuToEcef(enu, origin));

    /// <summary>Geodetic → Web Mercator (EPSG:3857) meters. Latitude is clamped to ±85.0511°.</summary>
    public static Cartesian2 GeodeticToWebMercator(GeoPoint p)
    {
        var r = Ellipsoid.Wgs84.SemiMajorAxis;
        var lat = System.Math.Clamp(p.Latitude, -WebMercatorMaxLatitude, WebMercatorMaxLatitude);
        var x = r * GeoMath.ToRadians(p.Longitude);
        var y = r * System.Math.Log(System.Math.Tan(System.Math.PI / 4 + GeoMath.ToRadians(lat) / 2));
        return new Cartesian2(x, y);
    }

    /// <summary>Web Mercator (EPSG:3857) meters → geodetic (height 0).</summary>
    public static GeoPoint WebMercatorToGeodetic(Cartesian2 m)
    {
        var r = Ellipsoid.Wgs84.SemiMajorAxis;
        var lon = GeoMath.ToDegrees(m.X / r);
        var lat = GeoMath.ToDegrees(2 * System.Math.Atan(System.Math.Exp(m.Y / r)) - System.Math.PI / 2);
        return new GeoPoint(lon, lat);
    }

    /// <summary>Converts a geodetic point to slippy-map tile indices (XYZ / Web Mercator) at a zoom level.</summary>
    public static (int X, int Y) GeodeticToTile(GeoPoint p, int zoom)
    {
        var n = 1 << zoom;
        var lat = GeoMath.ToRadians(System.Math.Clamp(p.Latitude, -WebMercatorMaxLatitude, WebMercatorMaxLatitude));
        var x = (int)System.Math.Floor((p.Longitude + 180.0) / 360.0 * n);
        var y = (int)System.Math.Floor((1 - System.Math.Log(System.Math.Tan(lat) + 1 / System.Math.Cos(lat)) / System.Math.PI) / 2 * n);
        return (System.Math.Clamp(x, 0, n - 1), System.Math.Clamp(y, 0, n - 1));
    }

    /// <summary>Formats a coordinate as degrees-minutes-seconds, e.g. <c>6°10'30.0"S</c>.</summary>
    public static string ToDms(double degrees, bool isLatitude)
    {
        var hemi = isLatitude ? (degrees >= 0 ? 'N' : 'S') : (degrees >= 0 ? 'E' : 'W');
        var abs = System.Math.Abs(degrees);
        var d = (int)abs;
        var mFull = (abs - d) * 60;
        var m = (int)mFull;
        var s = System.Math.Round((mFull - m) * 60, 1);
        if (s >= 60) { s = 0; m++; }
        if (m >= 60) { m = 0; d++; }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{d}°{m:00}'{s:00.0}\"{hemi}");
    }
}
