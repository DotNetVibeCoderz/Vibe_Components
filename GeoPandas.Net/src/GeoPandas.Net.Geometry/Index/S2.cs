using System.Globalization;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace GeoPandas.Net.Geometry.Index;

/// <summary>
/// Google's S2 cell hierarchy: the sphere projected onto a cube (quadratic projection) and each face ordered by a Hilbert curve.
/// Cell ids are 64-bit (face, position, level 0–30); tokens are the usual hex strings. Implements the cell-id algorithms of the S2 Geometry
/// library (Apache 2.0): point indexing, centres, vertices, parents/children, edge neighbours, exact area and polygon fills.
/// Angles in the public API are degrees.
/// </summary>
public static class S2
{
    public const int MaxLevel = 30;
    private const int PosBits = 2 * MaxLevel + 1;
    private const int MaxSize = 1 << MaxLevel;
    private const int LookupBits = 4;
    private const int SwapMask = 1, InvertMask = 2;

    private static readonly int[][] PosToIj = [[0, 1, 3, 2], [0, 2, 3, 1], [3, 2, 0, 1], [3, 1, 0, 2]];
    private static readonly int[] PosToOrientation = [SwapMask, 0, 0, InvertMask | SwapMask];
    private static readonly int[] LookupPos = new int[1 << (2 * LookupBits + 2)];
    private static readonly int[] LookupIj = new int[1 << (2 * LookupBits + 2)];

    static S2()
    {
        for (int o = 0; o < 4; o++) InitLookup(0, 0, 0, o, 0, o);
    }

    private static void InitLookup(int level, int i, int j, int origOrientation, int pos, int orientation)
    {
        if (level == LookupBits)
        {
            int ij = (i << LookupBits) + j;
            LookupPos[(ij << 2) + origOrientation] = (pos << 2) + orientation;
            LookupIj[(pos << 2) + origOrientation] = (ij << 2) + orientation;
            return;
        }
        level++; i <<= 1; j <<= 1; pos <<= 2;
        var r = PosToIj[orientation];
        for (int k = 0; k < 4; k++)
            InitLookup(level, i + (r[k] >> 1), j + (r[k] & 1), origOrientation, pos + k, orientation ^ PosToOrientation[k]);
    }

    // ------------------------------------------------------------------ projections

    private static double StToUv(double s) => s >= 0.5 ? (1.0 / 3.0) * (4 * s * s - 1) : (1.0 / 3.0) * (1 - 4 * (1 - s) * (1 - s));
    private static double UvToSt(double u) => u >= 0 ? 0.5 * Math.Sqrt(1 + 3 * u) : 1 - 0.5 * Math.Sqrt(1 - 3 * u);
    private static int StToIj(double s) => Math.Max(0, Math.Min(MaxSize - 1, (int)Math.Floor(MaxSize * s)));

    private static (double X, double Y, double Z) FaceUvToXyz(int face, double u, double v) => face switch
    {
        0 => (1, u, v), 1 => (-u, 1, v), 2 => (-u, -v, 1), 3 => (-1, -v, -u), 4 => (v, -1, -u), _ => (v, u, -1)
    };

    private static (int Face, double U, double V) XyzToFaceUv(double x, double y, double z)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
        int face = ax > ay ? (ax > az ? 0 : 2) : (ay > az ? 1 : 2);
        double c = face switch { 0 => x, 1 => y, _ => z };
        if (c < 0) face += 3;
        return face switch
        {
            0 => (0, y / x, z / x), 1 => (1, -x / y, z / y), 2 => (2, -x / z, -y / z),
            3 => (3, z / x, y / x), 4 => (4, z / y, -x / y), _ => (5, -y / z, -x / z)
        };
    }

    private static (double X, double Y, double Z) LatLngToXyz(double latDeg, double lngDeg)
    {
        double lat = latDeg * Math.PI / 180, lng = lngDeg * Math.PI / 180;
        return (Math.Cos(lat) * Math.Cos(lng), Math.Cos(lat) * Math.Sin(lng), Math.Sin(lat));
    }

    private static (double Lat, double Lng) XyzToLatLng(double x, double y, double z) =>
        (Math.Atan2(z, Math.Sqrt(x * x + y * y)) * 180 / Math.PI, Math.Atan2(y, x) * 180 / Math.PI);

    // ------------------------------------------------------------------ id encoding

    private static ulong FromFaceIj(int face, int i, int j)
    {
        ulong n = (ulong)face << (PosBits - 1);
        int bits = face & SwapMask;
        const int mask = (1 << LookupBits) - 1;
        for (int k = 7; k >= 0; k--)
        {
            bits += ((i >> (k * LookupBits)) & mask) << (LookupBits + 2);
            bits += ((j >> (k * LookupBits)) & mask) << 2;
            bits = LookupPos[bits];
            n |= (ulong)(bits >> 2) << (k * 2 * LookupBits);
            bits &= SwapMask | InvertMask;
        }
        return n * 2 + 1;
    }

    private static (int Face, int I, int J) ToFaceIj(ulong id)
    {
        int i = 0, j = 0, face = (int)(id >> PosBits);
        int bits = face & SwapMask;
        for (int k = 7; k >= 0; k--)
        {
            int nbits = k == 7 ? MaxLevel - 7 * LookupBits : LookupBits;
            bits += (int)((id >> (k * 2 * LookupBits + 1)) & ((1UL << (2 * nbits)) - 1)) << 2;
            bits = LookupIj[bits];
            i += (bits >> (LookupBits + 2)) << (k * LookupBits);
            j += ((bits >> 2) & ((1 << LookupBits) - 1)) << (k * LookupBits);
            bits &= SwapMask | InvertMask;
        }
        return (face, i, j);
    }

    private static ulong Lsb(ulong id) => id & (ulong)-(long)id;
    private static ulong LsbForLevel(int level) => 1UL << (2 * (MaxLevel - level));

    public static int Level(ulong id) => MaxLevel - System.Numerics.BitOperations.TrailingZeroCount(id) / 2;
    public static int Face(ulong id) => (int)(id >> PosBits);
    public static bool IsValid(ulong id) => (id >> PosBits) < 6 && (Lsb(id) & 0x1555555555555555UL) != 0;
    public static bool IsLeaf(ulong id) => (id & 1) != 0;

    public static ulong Parent(ulong id, int level)
    {
        if (level < 0 || level > Level(id)) throw new ArgumentOutOfRangeException(nameof(level));
        ulong lsb = LsbForLevel(level);
        return (id & (ulong)-(long)lsb) | lsb;
    }

    public static IEnumerable<ulong> Children(ulong id)
    {
        if (IsLeaf(id)) yield break;
        ulong lsb = Lsb(id) >> 2;
        ulong c = id - Lsb(id) + lsb;
        for (int k = 0; k < 4; k++, c += lsb << 1) yield return c;
    }

    public static IEnumerable<ulong> Children(ulong id, int level)
    {
        if (level < Level(id)) throw new ArgumentOutOfRangeException(nameof(level));
        if (level == Level(id)) { yield return id; yield break; }
        foreach (var c in Children(id)) foreach (var g in Children(c, level)) yield return g;
    }

    /// <summary>The level-<paramref name="level"/> cell containing a point (degrees).</summary>
    public static ulong FromLatLng(double latDeg, double lngDeg, int level = MaxLevel)
    {
        var (x, y, z) = LatLngToXyz(latDeg, lngDeg);
        var (face, u, v) = XyzToFaceUv(x, y, z);
        ulong leaf = FromFaceIj(face, StToIj(UvToSt(u)), StToIj(UvToSt(v)));
        return level == MaxLevel ? leaf : Parent(leaf, level);
    }

    public static string ToToken(ulong id)
    {
        if (id == 0) return "X";
        return id.ToString("x16", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    public static ulong FromToken(string token)
    {
        if (token is "X" or "x") return 0;
        return ulong.Parse(token.PadRight(16, '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ geometry

    private static (int Face, int Si, int Ti) CenterSiTi(ulong id)
    {
        var (face, i, j) = ToFaceIj(id);
        int delta = IsLeaf(id) ? 1 : (((ulong)i ^ (id >> 2)) & 1) != 0 ? 2 : 0;
        return (face, 2 * i + delta, 2 * j + delta);
    }

    /// <summary>Cell centre (degrees).</summary>
    public static (double Lat, double Lng) ToLatLng(ulong id)
    {
        var (face, si, ti) = CenterSiTi(id);
        var (x, y, z) = FaceUvToXyz(face, StToUv(0.5 / MaxSize * si), StToUv(0.5 / MaxSize * ti));
        return XyzToLatLng(x, y, z);
    }

    private static (int Face, double U0, double V0, double U1, double V1) UvBounds(ulong id)
    {
        var (face, i, j) = ToFaceIj(id);
        int size = 1 << (MaxLevel - Level(id));
        int i0 = i & -size, j0 = j & -size;
        return (face, StToUv((double)i0 / MaxSize), StToUv((double)j0 / MaxSize), StToUv((double)(i0 + size) / MaxSize), StToUv((double)(j0 + size) / MaxSize));
    }

    private static (double X, double Y, double Z)[] VerticesXyz(ulong id)
    {
        var (f, u0, v0, u1, v1) = UvBounds(id);
        return [Norm(FaceUvToXyz(f, u0, v0)), Norm(FaceUvToXyz(f, u1, v0)), Norm(FaceUvToXyz(f, u1, v1)), Norm(FaceUvToXyz(f, u0, v1))];
        static (double, double, double) Norm((double X, double Y, double Z) p) { double n = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z); return (p.X / n, p.Y / n, p.Z / n); }
    }

    /// <summary>The four cell vertices (degrees), counter-clockwise.</summary>
    public static IReadOnlyList<(double Lat, double Lng)> Vertices(ulong id) => VerticesXyz(id).Select(p => XyzToLatLng(p.X, p.Y, p.Z)).ToList();

    /// <summary>The cell as a lon/lat polygon with edges densified along the great circles (each edge into <paramref name="segments"/> parts).</summary>
    public static Polygon ToPolygon(ulong id, int segments = 4)
    {
        var v = VerticesXyz(id);
        var pts = new List<Coordinate>();
        for (int e = 0; e < 4; e++)
        {
            var a = v[e]; var b = v[(e + 1) % 4];
            for (int s = 0; s < segments; s++)
            {
                double t = (double)s / segments;
                double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t, z = a.Z + (b.Z - a.Z) * t;
                var (lat, lng) = XyzToLatLng(x, y, z);
                pts.Add(new Coordinate(lng, lat));
            }
        }
        for (int i = 1; i < pts.Count; i++)
            if (pts[i].X - pts[i - 1].X > 180) pts[i].X -= 360; else if (pts[i].X - pts[i - 1].X < -180) pts[i].X += 360;
        pts.Add(pts[0].Copy());
        return Geo.Factory.CreatePolygon(pts.ToArray());
    }

    /// <summary>Exact cell area in steradians (two spherical triangles, Girard/L'Huilier).</summary>
    public static double ExactAreaSr(ulong id)
    {
        var v = VerticesXyz(id);
        return TriangleArea(v[0], v[1], v[2]) + TriangleArea(v[0], v[2], v[3]);
    }

    /// <summary>Cell area in km² on a sphere of radius 6 371.0088 km (mean Earth radius, as S2's <c>S2Earth</c>).</summary>
    public static double AreaKm2(ulong id) => ExactAreaSr(id) * 6371.0088 * 6371.0088;

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) p, (double X, double Y, double Z) q) =>
        (p.Y * q.Z - p.Z * q.Y, p.Z * q.X - p.X * q.Z, p.X * q.Y - p.Y * q.X);

    private static double Angle((double X, double Y, double Z) p, (double X, double Y, double Z) q)
    {
        var c = Cross(p, q);
        return Math.Atan2(Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z), p.X * q.X + p.Y * q.Y + p.Z * q.Z);
    }

    /// <summary>(b + a) × (b − a): twice a × b, numerically robust when a ≈ ±b (S2 RobustCrossProd).</summary>
    private static (double X, double Y, double Z) RobustCross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Cross((b.X + a.X, b.Y + a.Y, b.Z + a.Z), (b.X - a.X, b.Y - a.Y, b.Z - a.Z));

    /// <summary>Spherical triangle area as S2::Area: L'Huilier, or Girard's formula for very thin triangles.</summary>
    private static double TriangleArea((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
    {
        double sa = Angle(b, c), sb = Angle(c, a), sc = Angle(a, b), s = 0.5 * (sa + sb + sc);
        if (s >= 3e-4)
        {
            double s2 = s * s, dmin = s - Math.Max(sa, Math.Max(sb, sc));
            if (dmin < 1e-2 * s * s2 * s2)
            {
                var ab = RobustCross(a, b); var bc = RobustCross(b, c); var ac = RobustCross(a, c);
                double girard = Math.Max(0.0, Angle(ab, ac) - Angle(ab, bc) + Angle(bc, ac));
                if (dmin < 2 * (0.1 * girard)) return girard;
            }
        }
        return 4 * Math.Atan(Math.Sqrt(Math.Max(0.0, Math.Tan(0.5 * s) * Math.Tan(0.5 * (s - sa)) * Math.Tan(0.5 * (s - sb)) * Math.Tan(0.5 * (s - sc)))));
    }

    // ------------------------------------------------------------------ neighbours

    private static ulong FromFaceIjWrap(int face, int i, int j)
    {
        i = Math.Max(-1, Math.Min(MaxSize, i));
        j = Math.Max(-1, Math.Min(MaxSize, j));
        const double scale = 1.0 / MaxSize;
        double limit = Math.BitIncrement(1.0);
        double u = Math.Max(-limit, Math.Min(limit, scale * ((i << 1) + 1 - MaxSize)));
        double v = Math.Max(-limit, Math.Min(limit, scale * ((j << 1) + 1 - MaxSize)));
        var (x, y, z) = FaceUvToXyz(face, u, v);
        var (f2, u2, v2) = XyzToFaceUv(x, y, z);
        return FromFaceIj(f2, StToIj(0.5 * (u2 + 1)), StToIj(0.5 * (v2 + 1)));
    }

    private static ulong FromFaceIjSame(int face, int i, int j, bool sameFace) => sameFace ? FromFaceIj(face, i, j) : FromFaceIjWrap(face, i, j);

    /// <summary>The four edge neighbours (down, right, up, left in IJ space), across cube faces where needed.</summary>
    public static ulong[] EdgeNeighbors(ulong id)
    {
        int level = Level(id), size = 1 << (MaxLevel - level);
        var (face, i, j) = ToFaceIj(id);
        return
        [
            Parent(FromFaceIjSame(face, i, j - size, j - size >= 0), level),
            Parent(FromFaceIjSame(face, i + size, j, i + size < MaxSize), level),
            Parent(FromFaceIjSame(face, i, j + size, j + size < MaxSize), level),
            Parent(FromFaceIjSame(face, i - size, j, i - size >= 0), level),
        ];
    }

    // ------------------------------------------------------------------ regions

    /// <summary>Level-<paramref name="level"/> cells whose centre lies in a lon/lat polygon.</summary>
    public static IReadOnlyList<ulong> PolygonToCells(NetTopologySuite.Geometries.Geometry polygon, int level) => Fill(polygon, level, centre: true);

    /// <summary>Level-<paramref name="level"/> cells intersecting a lon/lat geometry (a fixed-level covering).</summary>
    public static IReadOnlyList<ulong> Cover(NetTopologySuite.Geometries.Geometry geometry, int level) => Fill(geometry, level, centre: false);

    private static IReadOnlyList<ulong> Fill(NetTopologySuite.Geometries.Geometry g, int level, bool centre)
    {
        var prepared = PreparedGeometryFactory.Prepare(g);
        var env = g.EnvelopeInternal;
        // seed cells from samples spaced at a fraction of the cell size, then grow while cells still touch the geometry
        double cellDeg = 90.0 / (1 << level) * 0.5;
        var seeds = new HashSet<ulong>();
        for (double lat = env.MinY; lat <= env.MaxY + cellDeg; lat += cellDeg)
            for (double lng = env.MinX; lng <= env.MaxX + cellDeg; lng += cellDeg)
                seeds.Add(FromLatLng(Math.Min(lat, env.MaxY), Math.Min(lng, env.MaxX), level));
        foreach (var c in g.Coordinates) seeds.Add(FromLatLng(c.Y, c.X, level));
        var seen = new HashSet<ulong>(seeds);
        var queue = new Queue<ulong>(seeds);
        var result = new List<ulong>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            bool keep;
            if (centre) { var (lat, lng) = ToLatLng(id); keep = prepared.Contains(Geo.Factory.CreatePoint(new Coordinate(lng, lat))); }
            else keep = prepared.Intersects(ToPolygon(id));
            if (!keep && !prepared.Intersects(ToPolygon(id))) continue;
            if (keep) result.Add(id);
            foreach (var n in EdgeNeighbors(id)) if (seen.Add(n)) queue.Enqueue(n);
        }
        result.Sort();
        return result;
    }
}
