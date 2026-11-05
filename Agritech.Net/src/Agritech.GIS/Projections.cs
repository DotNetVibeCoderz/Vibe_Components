using Agritech.Core.Geo;

namespace Agritech.GIS;

/// <summary>UTM coordinate (metres). Northern hemisphere when <see cref="IsNorth"/>.</summary>
public readonly record struct UtmCoordinate(int Zone, bool IsNorth, double Easting, double Northing)
{
    public override string ToString() => $"{Zone}{(IsNorth ? 'N' : 'S')} {Easting:F2}E {Northing:F2}N";
    /// <summary>EPSG code (326xx north, 327xx south) on WGS-84.</summary>
    public int Epsg => (IsNorth ? 32600 : 32700) + Zone;
}

/// <summary>WGS-84 coordinate conversions: UTM (Krüger series, sub-mm accuracy within zone) and Web Mercator.</summary>
public static class Projections
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257223563;
    private const double K0 = 0.9996;

    public static int UtmZone(double lon, double lat = 0)
    {
        if (lat >= 56 && lat < 64 && lon >= 3 && lon < 12) return 32; // Norway exception
        if (lat >= 72 && lat < 84)
        {
            if (lon >= 0 && lon < 9) return 31;
            if (lon >= 9 && lon < 21) return 33;
            if (lon >= 21 && lon < 33) return 35;
            if (lon >= 33 && lon < 42) return 37;
        }
        return Math.Clamp((int)Math.Floor((lon + 180) / 6) + 1, 1, 60);
    }

    public static UtmCoordinate ToUtm(GeoCoordinate c, int? forceZone = null)
    {
        int zone = forceZone ?? UtmZone(c.Longitude, c.Latitude);
        double lon0 = ((zone - 1) * 6 - 180 + 3) * GeoMath.DegToRad;
        double phi = c.Latitude * GeoMath.DegToRad, lam = c.Longitude * GeoMath.DegToRad - lon0;

        double n = F / (2 - F);
        double aHat = A / (1 + n) * (1 + n * n / 4 + Math.Pow(n, 4) / 64);
        double[] alpha =
        [
            n / 2 - 2.0 / 3 * n * n + 5.0 / 16 * Math.Pow(n, 3),
            13.0 / 48 * n * n - 3.0 / 5 * Math.Pow(n, 3),
            61.0 / 240 * Math.Pow(n, 3),
        ];
        double e = Math.Sqrt(F * (2 - F));
        double t = Math.Sinh(Atanh(Math.Sin(phi)) - e * Atanh(e * Math.Sin(phi)));
        double xiP = Math.Atan2(t, Math.Cos(lam));
        double etaP = Atanh(Math.Sin(lam) / Math.Sqrt(1 + t * t));
        double xi = xiP, eta = etaP;
        for (int j = 1; j <= 3; j++)
        {
            xi += alpha[j - 1] * Math.Sin(2 * j * xiP) * Math.Cosh(2 * j * etaP);
            eta += alpha[j - 1] * Math.Cos(2 * j * xiP) * Math.Sinh(2 * j * etaP);
        }
        double easting = 500000 + K0 * aHat * eta;
        double northing = K0 * aHat * xi + (c.Latitude < 0 ? 10_000_000 : 0);
        return new UtmCoordinate(zone, c.Latitude >= 0, easting, northing);
    }

    public static GeoCoordinate FromUtm(UtmCoordinate u)
    {
        double n = F / (2 - F);
        double aHat = A / (1 + n) * (1 + n * n / 4 + Math.Pow(n, 4) / 64);
        double[] beta =
        [
            n / 2 - 2.0 / 3 * n * n + 37.0 / 96 * Math.Pow(n, 3),
            1.0 / 48 * n * n + 1.0 / 15 * Math.Pow(n, 3),
            17.0 / 480 * Math.Pow(n, 3),
        ];
        double[] delta =
        [
            2 * n - 2.0 / 3 * n * n - 2 * Math.Pow(n, 3),
            7.0 / 3 * n * n - 8.0 / 5 * Math.Pow(n, 3),
            56.0 / 15 * Math.Pow(n, 3),
        ];
        double xi = (u.Northing - (u.IsNorth ? 0 : 10_000_000)) / (K0 * aHat);
        double eta = (u.Easting - 500000) / (K0 * aHat);
        double xiP = xi, etaP = eta;
        for (int j = 1; j <= 3; j++)
        {
            xiP -= beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etaP -= beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        double chi = Math.Asin(Math.Sin(xiP) / Math.Cosh(etaP));
        double phi = chi;
        for (int j = 1; j <= 3; j++) phi += delta[j - 1] * Math.Sin(2 * j * chi);
        double lon0 = ((u.Zone - 1) * 6 - 180 + 3) * GeoMath.DegToRad;
        double lam = lon0 + Math.Atan2(Math.Sinh(etaP), Math.Cos(xiP));
        return new GeoCoordinate(phi * GeoMath.RadToDeg, lam * GeoMath.RadToDeg);
    }

    /// <summary>EPSG:3857 Web Mercator (metres).</summary>
    public static Point2 ToWebMercator(GeoCoordinate c)
    {
        double x = A * c.Longitude * GeoMath.DegToRad;
        double lat = Math.Clamp(c.Latitude, -85.05112878, 85.05112878);
        double y = A * Math.Log(Math.Tan(Math.PI / 4 + lat * GeoMath.DegToRad / 2));
        return new Point2(x, y);
    }

    public static GeoCoordinate FromWebMercator(Point2 p) =>
        new((2 * Math.Atan(Math.Exp(p.Y / A)) - Math.PI / 2) * GeoMath.RadToDeg, p.X / A * GeoMath.RadToDeg);

    /// <summary>Slippy-map tile index (z/x/y) containing a coordinate.</summary>
    public static (int X, int Y) TileXY(GeoCoordinate c, int zoom)
    {
        int n = 1 << zoom;
        int x = (int)Math.Floor((c.Longitude + 180) / 360 * n);
        double lat = c.Latitude * GeoMath.DegToRad;
        int y = (int)Math.Floor((1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * n);
        return (Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
    }

    private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));
}
