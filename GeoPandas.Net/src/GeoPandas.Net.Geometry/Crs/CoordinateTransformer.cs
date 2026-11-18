using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Geometry.Crs;

/// <summary>
/// Transforms coordinates between two CRSs: inverse projection → datum shift (geocentric 7-parameter Helmert via WGS 84)
/// → forward projection. Instances are immutable and thread-safe; obtain them with <see cref="Create"/> (cached).
/// </summary>
public sealed class CoordinateTransformer
{
    private const double Deg = Math.PI / 180.0;
    private const double Sec = Math.PI / (180.0 * 3600.0);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(CoordinateReferenceSystem, CoordinateReferenceSystem), CoordinateTransformer> s_cache = new();

    private readonly bool _identity;
    private readonly bool _datumShift;

    private CoordinateTransformer(CoordinateReferenceSystem source, CoordinateReferenceSystem target)
    {
        Source = source;
        Target = target;
        _identity = source.Equals(target) && ReferenceEquals(source.Geoid, target.Geoid);
        _datumShift = !_identity && !SameDatum(source.Datum, target.Datum);
    }

    public CoordinateReferenceSystem Source { get; }
    public CoordinateReferenceSystem Target { get; }
    public bool IsIdentity => _identity;

    public static CoordinateTransformer Create(CoordinateReferenceSystem source, CoordinateReferenceSystem target) =>
        s_cache.GetOrAdd((source, target), static k => new CoordinateTransformer(k.Item1, k.Item2));

    private static bool SameDatum(Datum a, Datum b)
    {
        if (a.IsWgs84Compatible && b.IsWgs84Compatible) return true;
        return a.Equals(b);
    }

    /// <summary>Transforms a single coordinate in place. Z (ellipsoidal height) is carried through datum shifts.</summary>
    public void Transform(ref double x, ref double y, ref double z)
    {
        if (_identity) return;
        double lon, lat;

        // 1. source → geodetic radians
        if (Source.Projection is { } sp) sp.Inverse(x * Source.ToMeter, y * Source.ToMeter, out lon, out lat);
        else { lon = x * Deg; lat = y * Deg; }

        // 1b. orthometric → ellipsoidal height (source geoid)
        if (Source.Geoid is { } sgd && !double.IsNaN(z)) z += sgd.Undulation(lon / Deg, lat / Deg);

        // 2. datum shift
        if (_datumShift)
        {
            double h = double.IsNaN(z) ? 0 : z;
            var sEll = Source.Ellipsoid; var sTw = Source.Datum.ToWgs84;
            var tEll = Target.Ellipsoid; var tTw = Target.Datum.ToWgs84;
            // grid shifts work on geodetic coordinates and replace the Helmert of their side
            if (Source.Datum.GridShift is { } sg) { sg.Forward(ref lon, ref lat); sEll = Ellipsoid.Wgs84; sTw = null; }
            var tg = Target.Datum.GridShift;
            if (tg is not null) { tEll = Ellipsoid.Wgs84; tTw = null; }
            bool sOk = sTw is null || sTw.All(v => v == 0), tOk = tTw is null || tTw.All(v => v == 0);
            if (!(sOk && tOk && Math.Abs(sEll.A - tEll.A) < 1e-6 && Math.Abs(sEll.F - tEll.F) < 1e-12))
            {
                GeodeticToGeocentric(sEll, lon, lat, h, out double gx, out double gy, out double gz);
                if (sTw is not null) Helmert(sTw, false, ref gx, ref gy, ref gz);
                if (tTw is not null) Helmert(tTw, true, ref gx, ref gy, ref gz);
                GeocentricToGeodetic(tEll, gx, gy, gz, out lon, out lat, out h);
            }
            tg?.Inverse(ref lon, ref lat);
            if (!double.IsNaN(z)) z = h;
        }

        // 2b. ellipsoidal → orthometric height (target geoid)
        if (Target.Geoid is { } tgd && !double.IsNaN(z)) z -= tgd.Undulation(lon / Deg, lat / Deg);

        // 3. geodetic → target
        if (Target.Projection is { } tp)
        {
            tp.Forward(lon, lat, out x, out y);
            x /= Target.ToMeter;
            y /= Target.ToMeter;
        }
        else
        {
            x = lon / Deg;
            y = lat / Deg;
            if (x > 180) x -= 360; else if (x < -180) x += 360;
        }
    }

    public (double X, double Y) Transform(double x, double y)
    {
        double z = double.NaN;
        Transform(ref x, ref y, ref z);
        return (x, y);
    }

    /// <summary>Transforms coordinate arrays in place (vectorised, parallel for large inputs).</summary>
    public void Transform(Span<double> xs, Span<double> ys)
    {
        if (xs.Length != ys.Length) throw new ArgumentException("xs and ys must have equal length.");
        if (_identity) return;
        for (int i = 0; i < xs.Length; i++)
        {
            double z = double.NaN;
            Transform(ref xs[i], ref ys[i], ref z);
        }
    }

    /// <summary>Returns a transformed copy of the geometry.</summary>
    public NtsGeometry Transform(NtsGeometry geometry)
    {
        if (_identity) return geometry;
        var copy = geometry.Copy();
        copy.Apply(new TransformFilter(this));
        copy.GeometryChanged();
        return copy;
    }

    /// <summary>Transforms an envelope by densifying its edges (handles curved projected boundaries).</summary>
    public Envelope Transform(Envelope env, int densify = 21)
    {
        if (_identity || env.IsNull) return env;
        var result = new Envelope();
        for (int i = 0; i < densify; i++)
        {
            double t = i / (double)(densify - 1);
            double x = env.MinX + t * env.Width, y = env.MinY + t * env.Height;
            foreach (var (px, py) in new[] { (x, env.MinY), (x, env.MaxY), (env.MinX, y), (env.MaxX, y) })
            {
                var (tx, ty) = Transform(px, py);
                if (double.IsFinite(tx) && double.IsFinite(ty)) result.ExpandToInclude(tx, ty);
            }
        }
        return result;
    }

    private sealed class TransformFilter(CoordinateTransformer t) : IEntireCoordinateSequenceFilter
    {
        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq)
        {
            bool hasZ = seq.HasZ;
            for (int i = 0; i < seq.Count; i++)
            {
                double x = seq.GetX(i), y = seq.GetY(i), z = hasZ ? seq.GetZ(i) : double.NaN;
                t.Transform(ref x, ref y, ref z);
                seq.SetX(i, x);
                seq.SetY(i, y);
                if (hasZ && !double.IsNaN(z)) seq.SetZ(i, z);
            }
        }
    }

    // ------------------------------------------------------------------ geodesy helpers

    public static void GeodeticToGeocentric(Ellipsoid e, double lon, double lat, double h, out double x, out double y, out double z)
    {
        double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
        double n = e.A / Math.Sqrt(1 - e.E2 * sinLat * sinLat);
        x = (n + h) * cosLat * Math.Cos(lon);
        y = (n + h) * cosLat * Math.Sin(lon);
        z = (n * (1 - e.E2) + h) * sinLat;
    }

    public static void GeocentricToGeodetic(Ellipsoid e, double x, double y, double z, out double lon, out double lat, out double h)
    {
        double a = e.A, e2 = e.E2;
        double p = Math.Sqrt(x * x + y * y);
        lon = Math.Atan2(y, x);
        lat = Math.Atan2(z, p * (1 - e2));
        h = 0;
        for (int i = 0; i < 10; i++)
        {
            double sinLat = Math.Sin(lat);
            double n = a / Math.Sqrt(1 - e2 * sinLat * sinLat);
            h = p / Math.Cos(lat) - n;
            double next = Math.Atan2(z, p * (1 - e2 * n / (n + h)));
            if (Math.Abs(next - lat) < 1e-13) { lat = next; break; }
            lat = next;
        }
    }

    /// <summary>Position-vector 7-parameter Helmert. <paramref name="inverse"/> applies WGS84 → datum.</summary>
    private static void Helmert(double[] p, bool inverse, ref double x, ref double y, ref double z)
    {
        double s = inverse ? -1 : 1;
        double tx = s * p[0], ty = s * p[1], tz = s * p[2];
        double rx = s * p[3] * Sec, ry = s * p[4] * Sec, rz = s * p[5] * Sec;
        double m = 1 + s * p[6] * 1e-6;
        double nx = tx + m * (x - rz * y + ry * z);
        double ny = ty + m * (rz * x + y - rx * z);
        double nz = tz + m * (-ry * x + rx * y + z);
        x = nx; y = ny; z = nz;
    }
}
