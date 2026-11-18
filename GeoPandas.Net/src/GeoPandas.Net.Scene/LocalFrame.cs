using GeoPandas.Net.Geometry.Crs;

namespace GeoPandas.Net.Scene;

/// <summary>
/// A local east-north-up (ENU) frame tangent to the WGS 84 ellipsoid at an origin, with exact conversions from any CRS
/// (via geographic coordinates and ECEF) and the ENU → ECEF matrix used as a 3D Tiles root transform.
/// </summary>
public sealed class LocalFrame
{
    private const double A = 6378137.0, F = 1 / 298.257223563, E2 = F * (2 - F);
    private readonly double _x0, _y0, _z0;
    private readonly double[] _e, _n, _u;

    public LocalFrame(double lonDeg, double latDeg, double height = 0)
    {
        Lon = lonDeg; Lat = latDeg; Height = height;
        (_x0, _y0, _z0) = ToEcef(lonDeg, latDeg, height);
        double l = lonDeg * Math.PI / 180, p = latDeg * Math.PI / 180;
        _e = [-Math.Sin(l), Math.Cos(l), 0];
        _n = [-Math.Sin(p) * Math.Cos(l), -Math.Sin(p) * Math.Sin(l), Math.Cos(p)];
        _u = [Math.Cos(p) * Math.Cos(l), Math.Cos(p) * Math.Sin(l), Math.Sin(p)];
    }

    public double Lon { get; }
    public double Lat { get; }
    public double Height { get; }

    /// <summary>The frame centred on the centre of an envelope in <paramref name="crs"/>.</summary>
    public static LocalFrame AtCentre(NetTopologySuite.Geometries.Envelope env, CoordinateReferenceSystem? crs, double height = 0)
    {
        double cx = (env.MinX + env.MaxX) / 2, cy = (env.MinY + env.MaxY) / 2;
        if (crs is null || crs.IsGeographic) return new LocalFrame(cx, cy, height);
        var (lon, lat) = CoordinateTransformer.Create(crs, CoordinateReferenceSystem.Wgs84).Transform(cx, cy);
        return new LocalFrame(lon, lat, height);
    }

    public static (double X, double Y, double Z) ToEcef(double lonDeg, double latDeg, double h)
    {
        double l = lonDeg * Math.PI / 180, p = latDeg * Math.PI / 180;
        double n = A / Math.Sqrt(1 - E2 * Math.Sin(p) * Math.Sin(p));
        return ((n + h) * Math.Cos(p) * Math.Cos(l), (n + h) * Math.Cos(p) * Math.Sin(l), (n * (1 - E2) + h) * Math.Sin(p));
    }

    /// <summary>Geographic (degrees, ellipsoidal height) → local ENU metres.</summary>
    public (double E, double N, double U) FromGeographic(double lonDeg, double latDeg, double h)
    {
        var (x, y, z) = ToEcef(lonDeg, latDeg, h);
        double dx = x - _x0, dy = y - _y0, dz = z - _z0;
        return (_e[0] * dx + _e[1] * dy + _e[2] * dz, _n[0] * dx + _n[1] * dy + _n[2] * dz, _u[0] * dx + _u[1] * dy + _u[2] * dz);
    }

    /// <summary>A converter from a CRS (x, y, z = height) to ENU.</summary>
    public Func<double, double, double, (double E, double N, double U)> From(CoordinateReferenceSystem? crs)
    {
        if (crs is null || crs.IsGeographic) return FromGeographic;
        var t = CoordinateTransformer.Create(crs, CoordinateReferenceSystem.Wgs84);
        return (x, y, z) => { var (lon, lat) = t.Transform(x, y); return FromGeographic(lon, lat, z); };
    }

    /// <summary>Column-major 4×4 ENU → ECEF matrix (the 3D Tiles <c>transform</c>).</summary>
    public double[] EnuToEcef() =>
    [
        _e[0], _e[1], _e[2], 0,
        _n[0], _n[1], _n[2], 0,
        _u[0], _u[1], _u[2], 0,
        _x0, _y0, _z0, 1
    ];
}
