using System.Globalization;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using static GeoPandas.Net.Geometry.Index.H3Tables;

namespace GeoPandas.Net.Geometry.Index;

/// <summary>
/// Uber's H3 hierarchical hexagonal grid (version 4), a port of the reference C library (uber/h3, Apache License 2.0):
/// cell indexing of latitude/longitude, cell centres and boundaries, grid disks and rings, parents and children, compaction,
/// polygon filling and cell areas. Cells are 64-bit indexes; <see cref="ToString(ulong)"/> gives the usual hexadecimal form.
/// Angles in the public API are degrees.
/// </summary>
public static class H3
{
    public const int MaxResolution = 15;
    private const double EarthRadiusKm = 6371.007180918475;
    private const double Epsilon = 0.0000000000000001;
    private const double Sqrt3Over2 = 0.8660254037844386467637231707529361834714;
    private const double Sqrt7 = 2.6457513110645905905016157536392604257102;
    private const double Ap7RotRads = 0.333473172251832115336090755351601070065900389;
    private const double Res0UGnomonic = 0.38196601125010500003;
    private const double TwoPi = 2 * Math.PI;
    private const int InvalidBaseCell = 127;
    private const double FltEpsilon = 1.1920928955078125e-7;   // C FLT_EPSILON (not .NET float.Epsilon, the smallest denormal)
    private const ulong H3Init = 35184372088831UL;

    // ------------------------------------------------------------------ index bit fields

    public static int GetResolution(ulong h) => (int)((h >> 52) & 15);
    public static int GetBaseCell(ulong h) => (int)((h >> 45) & 127);
    private static int Mode(ulong h) => (int)((h >> 59) & 15);
    private static int Digit(ulong h, int r) => (int)((h >> ((MaxResolution - r) * 3)) & 7);
    private static ulong SetDigit(ulong h, int r, int d) => (h & ~(7UL << ((MaxResolution - r) * 3))) | ((ulong)d << ((MaxResolution - r) * 3));
    private static ulong SetResolution(ulong h, int r) => (h & ~(15UL << 52)) | ((ulong)r << 52);
    private static ulong SetBaseCell(ulong h, int bc) => (h & ~(127UL << 45)) | ((ulong)bc << 45);
    private static bool ClassIII(int res) => (res & 1) == 1;

    public static string ToString(ulong h) => h.ToString("x", CultureInfo.InvariantCulture);
    public static ulong Parse(string s) => ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Structural validity: cell mode, resolution, base cell, digits (no deleted pentagon sub-sequence).</summary>
    public static bool IsValid(ulong h)
    {
        if ((h >> 63) != 0 || Mode(h) != 1 || ((h >> 56) & 7) != 0) return false;
        int bc = GetBaseCell(h), res = GetResolution(h);
        if (bc >= 122) return false;
        bool foundFirst = false;
        for (int r = 1; r <= MaxResolution; r++)
        {
            int d = Digit(h, r);
            if (r <= res)
            {
                if (d == 7) return false;
                if (!foundFirst && d != 0)
                {
                    foundFirst = true;
                    if (IsBaseCellPentagon(bc) && d == 1) return false;
                }
            }
            else if (d != 7) return false;
        }
        return true;
    }

    public static bool IsPentagon(ulong h) => IsBaseCellPentagon(GetBaseCell(h)) && LeadingNonZeroDigit(h) == 0;

    // ------------------------------------------------------------------ IJK coordinates

    private struct Ijk
    {
        public int I, J, K;
        public Ijk(int i, int j, int k) { I = i; J = j; K = k; }
    }

    private static readonly Ijk[] UnitVecs = [new(0, 0, 0), new(0, 0, 1), new(0, 1, 0), new(0, 1, 1), new(1, 0, 0), new(1, 0, 1), new(1, 1, 0)];

    private static void Normalize(ref Ijk c)
    {
        if (c.I < 0) { c.J -= c.I; c.K -= c.I; c.I = 0; }
        if (c.J < 0) { c.I -= c.J; c.K -= c.J; c.J = 0; }
        if (c.K < 0) { c.I -= c.K; c.J -= c.K; c.K = 0; }
        int min = Math.Min(c.I, Math.Min(c.J, c.K));
        if (min > 0) { c.I -= min; c.J -= min; c.K -= min; }
    }

    private static Ijk Combine(Ijk c, Ijk iv, Ijk jv, Ijk kv)
    {
        var r = new Ijk(iv.I * c.I + jv.I * c.J + kv.I * c.K, iv.J * c.I + jv.J * c.J + kv.J * c.K, iv.K * c.I + jv.K * c.J + kv.K * c.K);
        Normalize(ref r);
        return r;
    }

    private static int LRound(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    private static void UpAp7(ref Ijk c)
    {
        int i = c.I - c.K, j = c.J - c.K;
        c = new Ijk(LRound((3 * i - j) / 7.0), LRound((i + 2 * j) / 7.0), 0);
        Normalize(ref c);
    }

    private static void UpAp7r(ref Ijk c)
    {
        int i = c.I - c.K, j = c.J - c.K;
        c = new Ijk(LRound((2 * i + j) / 7.0), LRound((3 * j - i) / 7.0), 0);
        Normalize(ref c);
    }

    private static void DownAp7(ref Ijk c) => c = Combine(c, new(3, 0, 1), new(1, 3, 0), new(0, 1, 3));
    private static void DownAp7r(ref Ijk c) => c = Combine(c, new(3, 1, 0), new(0, 3, 1), new(1, 0, 3));
    private static void DownAp3(ref Ijk c) => c = Combine(c, new(2, 0, 1), new(1, 2, 0), new(0, 1, 2));
    private static void DownAp3r(ref Ijk c) => c = Combine(c, new(2, 1, 0), new(0, 2, 1), new(1, 0, 2));
    private static void Rotate60ccw(ref Ijk c) => c = Combine(c, new(1, 1, 0), new(0, 1, 1), new(1, 0, 1));
    private static void Rotate60cw(ref Ijk c) => c = Combine(c, new(1, 0, 1), new(1, 1, 0), new(0, 1, 1));

    private static void Neighbor(ref Ijk c, int digit)
    {
        if (digit > 0 && digit < 7)
        {
            var u = UnitVecs[digit];
            c = new Ijk(c.I + u.I, c.J + u.J, c.K + u.K);
            Normalize(ref c);
        }
    }

    private static int UnitIjkToDigit(Ijk c)
    {
        Normalize(ref c);
        for (int d = 0; d < 7; d++) if (c.I == UnitVecs[d].I && c.J == UnitVecs[d].J && c.K == UnitVecs[d].K) return d;
        return 7;
    }

    private static int RotateDigit60ccw(int d) => d switch { 1 => 5, 5 => 4, 4 => 6, 6 => 2, 2 => 3, 3 => 1, _ => d };
    private static int RotateDigit60cw(int d) => d switch { 1 => 3, 3 => 2, 2 => 6, 6 => 4, 4 => 5, 5 => 1, _ => d };

    private static (double X, double Y) IjkToHex2d(Ijk h)
    {
        int i = h.I - h.K, j = h.J - h.K;
        return (i - 0.5 * j, j * Sqrt3Over2);
    }

    private static Ijk Hex2dToIjk(double x, double y)
    {
        var h = new Ijk(0, 0, 0);
        double a1 = Math.Abs(x), a2 = Math.Abs(y);
        double x2 = a2 / Sqrt3Over2, x1 = a1 + x2 / 2.0;
        int m1 = (int)x1, m2 = (int)x2;
        double r1 = x1 - m1, r2 = x2 - m2;
        if (r1 < 0.5)
        {
            if (r1 < 1.0 / 3.0)
            {
                h.I = m1;
                h.J = r2 < (1.0 + r1) / 2.0 ? m2 : m2 + 1;
            }
            else
            {
                h.J = r2 < 1.0 - r1 ? m2 : m2 + 1;
                h.I = (1.0 - r1) <= r2 && r2 < 2.0 * r1 ? m1 + 1 : m1;
            }
        }
        else
        {
            if (r1 < 2.0 / 3.0)
            {
                h.J = r2 < 1.0 - r1 ? m2 : m2 + 1;
                h.I = (2.0 * r1 - 1.0) < r2 && r2 < (1.0 - r1) ? m1 : m1 + 1;
            }
            else
            {
                h.I = m1 + 1;
                h.J = r2 < r1 / 2.0 ? m2 : m2 + 1;
            }
        }
        if (x < 0.0)
        {
            if (h.J % 2 == 0) { long axisi = h.J / 2, diff = h.I - axisi; h.I = (int)(h.I - 2.0 * diff); }
            else { long axisi = (h.J + 1) / 2, diff = h.I - axisi; h.I = (int)(h.I - (2.0 * diff + 1)); }
        }
        if (y < 0.0) { h.I -= (2 * h.J + 1) / 2; h.J = -h.J; }
        Normalize(ref h);
        return h;
    }

    // ------------------------------------------------------------------ geodesy on the sphere

    private static double PosAngle(double rads)
    {
        double tmp = rads < 0.0 ? rads + TwoPi : rads;
        if (rads >= TwoPi) tmp -= TwoPi;
        return tmp;
    }



    // ------------------------------------------------------------------ face / IJK

    private struct FaceIjk
    {
        public int Face;
        public Ijk Coord;
    }

    // h3 4.5 projects through unit vectors (vec3d.h / faceijk.c), which this port follows bit for bit
    private const double InvRes0UGnomonic = 2.61803398874989588842;
    private const double RSqrt7 = 0.37796447300922722721451653623418006081576;
    private const double OneThird = 0.333333333333333333333333333333333333333;

    private static (double X, double Y, double Z) LinComb(double a, (double X, double Y, double Z) v1, double b, (double X, double Y, double Z) v2) =>
        (a * v1.X + b * v2.X, a * v1.Y + b * v2.Y, a * v1.Z + b * v2.Z);
    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static (double X, double Y, double Z) Normalized((double X, double Y, double Z) v)
    {
        double n = Math.Sqrt(Dot(v, v)), s = n > 0 ? 1.0 / n : 0.0;
        return (v.X * s, v.Y * s, v.Z * s);
    }
    private static (double X, double Y, double Z) FacePoint(int f) => (FaceCenterPoint[f * 3], FaceCenterPoint[f * 3 + 1], FaceCenterPoint[f * 3 + 2]);

    private static void TangentBasis((double X, double Y, double Z) p, out (double X, double Y, double Z) north, out (double X, double Y, double Z) east)
    {
        (double, double, double) pole = (0.0, 0.0, 1.0);
        north = Normalized(LinComb(1.0, pole, -Dot(pole, p), p));
        east = (north.Y * p.Z - north.Z * p.Y, north.Z * p.X - north.X * p.Z, north.X * p.Y - north.Y * p.X);
    }

    private static (int Face, double X, double Y) GeoToHex2d(double lat, double lng, int res)
    {
        double rc = Math.Cos(lat);
        (double X, double Y, double Z) p = (Math.Cos(lng) * rc, Math.Sin(lng) * rc, Math.Sin(lat));
        int face = 0; double sqd = 5.0;
        for (int f = 0; f < 20; f++)
        {
            var d = LinComb(1.0, FacePoint(f), -1.0, p);
            double t = Dot(d, d);
            if (t < sqd) { face = f; sqd = t; }
        }
        double r = Math.Acos(1 - sqd * 0.5);
        if (r < Epsilon) return (face, 0, 0);
        var fc = FacePoint(face);
        TangentBasis(fc, out var north, out var east);
        var proj = Normalized(LinComb(1.0, p, -Dot(p, fc), fc));
        double az = Math.Atan2(Dot(proj, east), Dot(proj, north));
        double theta = PosAngle(FaceAxesAzRadsCII[face * 3] - PosAngle(az));
        if (ClassIII(res)) theta = PosAngle(theta - Ap7RotRads);
        r = Math.Tan(r) * InvRes0UGnomonic;
        for (int i = 0; i < res; i++) r *= Sqrt7;
        return (face, r * Math.Cos(theta), r * Math.Sin(theta));
    }

    private static (double Lat, double Lng) Hex2dToGeo(double x, double y, int face, int res, bool substrate)
    {
        double r = Math.Sqrt(x * x + y * y);
        (double X, double Y, double Z) v3;
        if (r < Epsilon) v3 = FacePoint(face);
        else
        {
            double theta = Math.Atan2(y, x);
            for (int i = 0; i < res; i++) r *= RSqrt7;
            if (substrate)
            {
                r *= OneThird;
                if (ClassIII(res)) r *= RSqrt7;
            }
            r = Math.Atan(r * Res0UGnomonic);
            if (!substrate && ClassIII(res)) theta = PosAngle(theta + Ap7RotRads);
            theta = PosAngle(FaceAxesAzRadsCII[face * 3] - theta);
            var fc = FacePoint(face);
            TangentBasis(fc, out var north, out var east);
            var dir = LinComb(Math.Cos(theta), north, Math.Sin(theta), east);
            v3 = Normalized(LinComb(Math.Cos(r), fc, Math.Sin(r), dir));
        }
        return (Math.Asin(v3.Z), Math.Atan2(v3.Y, v3.X));
    }

    // ------------------------------------------------------------------ base cells

    private static bool IsBaseCellPentagon(int bc) => bc is >= 0 and < 122 && BaseCellData[bc * 7 + 4] != 0;
    private static bool IsBaseCellPolarPentagon(int bc) => bc is 4 or 117;
    private static int FaceIjkToBaseCell(FaceIjk h) => FaceIjkBaseCells[(((h.Face * 3 + h.Coord.I) * 3 + h.Coord.J) * 3 + h.Coord.K) * 2];
    private static int FaceIjkToBaseCellRot(FaceIjk h) => FaceIjkBaseCells[(((h.Face * 3 + h.Coord.I) * 3 + h.Coord.J) * 3 + h.Coord.K) * 2 + 1];
    private static bool BaseCellIsCwOffset(int bc, int face) => BaseCellData[bc * 7 + 5] == face || BaseCellData[bc * 7 + 6] == face;
    private static FaceIjk BaseCellHome(int bc) => new() { Face = BaseCellData[bc * 7], Coord = new Ijk(BaseCellData[bc * 7 + 1], BaseCellData[bc * 7 + 2], BaseCellData[bc * 7 + 3]) };

    private static int LeadingNonZeroDigit(ulong h)
    {
        int res = GetResolution(h);
        for (int r = 1; r <= res; r++) { int d = Digit(h, r); if (d != 0) return d; }
        return 0;
    }

    private static ulong Rotate60ccw(ulong h)
    {
        int res = GetResolution(h);
        for (int r = 1; r <= res; r++) h = SetDigit(h, r, RotateDigit60ccw(Digit(h, r)));
        return h;
    }

    private static ulong Rotate60cw(ulong h)
    {
        int res = GetResolution(h);
        for (int r = 1; r <= res; r++) h = SetDigit(h, r, RotateDigit60cw(Digit(h, r)));
        return h;
    }

    private static ulong RotatePent60ccw(ulong h)
    {
        bool found = false;
        int res = GetResolution(h);
        for (int r = 1; r <= res; r++)
        {
            h = SetDigit(h, r, RotateDigit60ccw(Digit(h, r)));
            if (!found && Digit(h, r) != 0)
            {
                found = true;
                if (LeadingNonZeroDigit(h) == 1) h = Rotate60ccw(h);
            }
        }
        return h;
    }

    // ------------------------------------------------------------------ index ⇄ face IJK

    private static ulong FaceIjkToH3(FaceIjk fijk, int res)
    {
        ulong h = H3Init;
        h = (h & ~(15UL << 59)) | (1UL << 59);
        h = SetResolution(h, res);
        if (res == 0)
        {
            if (fijk.Coord.I > 2 || fijk.Coord.J > 2 || fijk.Coord.K > 2) return 0;
            return SetBaseCell(h, FaceIjkToBaseCell(fijk));
        }
        var bc = fijk;
        for (int r = res - 1; r >= 0; r--)
        {
            var last = bc.Coord;
            Ijk center;
            if (ClassIII(r + 1)) { UpAp7(ref bc.Coord); center = bc.Coord; DownAp7(ref center); }
            else { UpAp7r(ref bc.Coord); center = bc.Coord; DownAp7r(ref center); }
            var diff = new Ijk(last.I - center.I, last.J - center.J, last.K - center.K);
            Normalize(ref diff);
            h = SetDigit(h, r + 1, UnitIjkToDigit(diff));
        }
        if (bc.Coord.I > 2 || bc.Coord.J > 2 || bc.Coord.K > 2) return 0;
        int baseCell = FaceIjkToBaseCell(bc);
        h = SetBaseCell(h, baseCell);
        int numRots = FaceIjkToBaseCellRot(bc);
        if (IsBaseCellPentagon(baseCell))
        {
            if (LeadingNonZeroDigit(h) == 1) h = BaseCellIsCwOffset(baseCell, bc.Face) ? Rotate60cw(h) : Rotate60ccw(h);
            for (int i = 0; i < numRots; i++) h = RotatePent60ccw(h);
        }
        else for (int i = 0; i < numRots; i++) h = Rotate60ccw(h);
        return h;
    }

    private static bool ToFaceIjkWithInitialized(ulong h, ref FaceIjk fijk)
    {
        int res = GetResolution(h);
        bool possibleOverage = !(!IsBaseCellPentagon(GetBaseCell(h)) && (res == 0 || (fijk.Coord.I == 0 && fijk.Coord.J == 0 && fijk.Coord.K == 0)));
        for (int r = 1; r <= res; r++)
        {
            if (ClassIII(r)) DownAp7(ref fijk.Coord); else DownAp7r(ref fijk.Coord);
            Neighbor(ref fijk.Coord, Digit(h, r));
        }
        return possibleOverage;
    }

    private enum Overage { None, FaceEdge, NewFace }

    private static Overage AdjustOverageClassII(ref FaceIjk fijk, int res, bool pentLeading4, bool substrate)
    {
        var overage = Overage.None;
        int maxDim = MaxDimByCIIres[res];
        if (substrate) maxDim *= 3;
        ref var ijk = ref fijk.Coord;
        int sum = ijk.I + ijk.J + ijk.K;
        if (substrate && sum == maxDim) overage = Overage.FaceEdge;
        else if (sum > maxDim)
        {
            overage = Overage.NewFace;
            int dir;
            if (ijk.K > 0)
            {
                if (ijk.J > 0) dir = 3;     // JK
                else
                {
                    dir = 2;                // KI
                    if (pentLeading4)
                    {
                        var tmp = new Ijk(ijk.I - maxDim, ijk.J, ijk.K);
                        Rotate60cw(ref tmp);
                        ijk = new Ijk(tmp.I + maxDim, tmp.J, tmp.K);
                    }
                }
            }
            else dir = 1;                   // IJ
            int o = (fijk.Face * 4 + dir) * 5;
            fijk.Face = FaceNeighbors[o];
            for (int i = 0; i < FaceNeighbors[o + 4]; i++) Rotate60ccw(ref ijk);
            int unitScale = UnitScaleByCIIres[res];
            if (substrate) unitScale *= 3;
            ijk = new Ijk(ijk.I + FaceNeighbors[o + 1] * unitScale, ijk.J + FaceNeighbors[o + 2] * unitScale, ijk.K + FaceNeighbors[o + 3] * unitScale);
            Normalize(ref ijk);
            if (substrate && ijk.I + ijk.J + ijk.K == maxDim) overage = Overage.FaceEdge;
        }
        return overage;
    }

    private static FaceIjk ToFaceIjk(ulong h)
    {
        int baseCell = GetBaseCell(h);
        if (baseCell >= 122) throw new ArgumentException($"Invalid H3 cell {ToString(h)}.");
        if (IsBaseCellPentagon(baseCell) && LeadingNonZeroDigit(h) == 5) h = Rotate60cw(h);
        var fijk = BaseCellHome(baseCell);
        if (!ToFaceIjkWithInitialized(h, ref fijk)) return fijk;
        var orig = fijk.Coord;
        int res = GetResolution(h);
        if (ClassIII(res)) { DownAp7r(ref fijk.Coord); res++; }
        bool pentLeading4 = IsBaseCellPentagon(baseCell) && LeadingNonZeroDigit(h) == 4;
        if (AdjustOverageClassII(ref fijk, res, pentLeading4, false) != Overage.None)
        {
            if (IsBaseCellPentagon(baseCell)) while (AdjustOverageClassII(ref fijk, res, false, false) != Overage.None) { }
            if (res != GetResolution(h)) UpAp7r(ref fijk.Coord);
        }
        else if (res != GetResolution(h)) fijk.Coord = orig;
        return fijk;
    }

    // ------------------------------------------------------------------ public conversions

    /// <summary>The cell containing a point (degrees) at a resolution 0–15.</summary>
    public static ulong LatLngToCell(double latDeg, double lngDeg, int res)
    {
        if (res is < 0 or > MaxResolution) throw new ArgumentOutOfRangeException(nameof(res));
        if (!double.IsFinite(latDeg) || !double.IsFinite(lngDeg)) throw new ArgumentException("Non-finite coordinate.");
        var (face, x, y) = GeoToHex2d(latDeg * Math.PI / 180, lngDeg * Math.PI / 180, res);
        return FaceIjkToH3(new FaceIjk { Face = face, Coord = Hex2dToIjk(x, y) }, res);
    }

    /// <summary>The cell centre (degrees).</summary>
    public static (double Lat, double Lng) CellToLatLng(ulong h)
    {
        var fijk = ToFaceIjk(h);
        var (x, y) = IjkToHex2d(fijk.Coord);
        var (lat, lng) = Hex2dToGeo(x, y, fijk.Face, GetResolution(h), false);
        return (lat * 180 / Math.PI, lng * 180 / Math.PI);
    }

    private static readonly Ijk[] VertsCII = [new(2, 1, 0), new(1, 2, 0), new(0, 2, 1), new(0, 1, 2), new(1, 0, 2), new(2, 0, 1)];
    private static readonly Ijk[] VertsCIII = [new(5, 4, 0), new(1, 5, 0), new(0, 5, 4), new(0, 1, 5), new(4, 0, 5), new(5, 0, 1)];

    private static FaceIjk[] Verts(ref FaceIjk fijk, ref int res, int n)
    {
        var verts = ClassIII(res) ? VertsCIII : VertsCII;
        DownAp3(ref fijk.Coord);
        DownAp3r(ref fijk.Coord);
        if (ClassIII(res)) { DownAp7r(ref fijk.Coord); res++; }
        var outv = new FaceIjk[n];
        for (int v = 0; v < n; v++)
        {
            var c = new Ijk(fijk.Coord.I + verts[v].I, fijk.Coord.J + verts[v].J, fijk.Coord.K + verts[v].K);
            Normalize(ref c);
            outv[v] = new FaceIjk { Face = fijk.Face, Coord = c };
        }
        return outv;
    }

    private static (double X, double Y) Intersect((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
    {
        double s1x = p1.X - p0.X, s1y = p1.Y - p0.Y, s2x = p3.X - p2.X, s2y = p3.Y - p2.Y;
        double t = (s2x * (p0.Y - p2.Y) - s2y * (p0.X - p2.X)) / (-s2x * s1y + s1x * s2y);
        return (p0.X + t * s1x, p0.Y + t * s1y);
    }

    private static ((double X, double Y) E0, (double X, double Y) E1) FaceEdge(int dir, int maxDim)
    {
        (double, double) v0 = (3.0 * maxDim, 0.0), v1 = (-1.5 * maxDim, 3.0 * Sqrt3Over2 * maxDim), v2 = (-1.5 * maxDim, -3.0 * Sqrt3Over2 * maxDim);
        return dir switch { 1 => (v0, v1), 3 => (v1, v2), _ => (v2, v0) };
    }

    /// <summary>Boundary vertices (degrees, counter-clockwise), including the extra vertices of class III cells that cross icosahedron edges.</summary>
    public static IReadOnlyList<(double Lat, double Lng)> CellToBoundary(ulong h) =>
        BoundaryRads(h).Select(p => (p.Lat * 180 / Math.PI, p.Lng * 180 / Math.PI)).ToList();

    /// <summary>Boundary vertices in radians (as the C library computes them).</summary>
    private static List<(double Lat, double Lng)> BoundaryRads(ulong h)
    {
        var fijk = ToFaceIjk(h);
        int res = GetResolution(h);
        var outv = new List<(double Lat, double Lng)>(10);
        if (IsPentagon(h))
        {
            int adjRes = res;
            var center = fijk;
            var verts = Verts(ref center, ref adjRes, 5);
            FaceIjk last = default;
            for (int vert = 0; vert < 6; vert++)
            {
                var f = verts[vert % 5];
                while (AdjustOverageClassII(ref f, adjRes, false, true) == Overage.NewFace) { }
                if (ClassIII(res) && vert > 0)
                {
                    var tmp = f;
                    var orig0 = IjkToHex2d(last.Coord);
                    int currentToLastDir = AdjacentFaceDir[tmp.Face * 20 + last.Face];
                    int o = (tmp.Face * 4 + currentToLastDir) * 5;
                    tmp.Face = FaceNeighbors[o];
                    for (int i = 0; i < FaceNeighbors[o + 4]; i++) Rotate60ccw(ref tmp.Coord);
                    int scale = UnitScaleByCIIres[adjRes] * 3;
                    tmp.Coord = new Ijk(tmp.Coord.I + FaceNeighbors[o + 1] * scale, tmp.Coord.J + FaceNeighbors[o + 2] * scale, tmp.Coord.K + FaceNeighbors[o + 3] * scale);
                    Normalize(ref tmp.Coord);
                    var orig1 = IjkToHex2d(tmp.Coord);
                    var (e0, e1) = FaceEdge(AdjacentFaceDir[tmp.Face * 20 + f.Face], MaxDimByCIIres[adjRes]);
                    var inter = Intersect(orig0, orig1, e0, e1);
                    outv.Add((Hex2dToGeo(inter.X, inter.Y, tmp.Face, adjRes, true)));
                }
                if (vert < 5)
                {
                    var p = IjkToHex2d(f.Coord);
                    outv.Add((Hex2dToGeo(p.X, p.Y, f.Face, adjRes, true)));
                }
                last = f;
            }
            return outv;
        }
        {
            int adjRes = res;
            var center = fijk;
            var verts = Verts(ref center, ref adjRes, 6);
            int lastFace = -1;
            var lastOverage = Overage.None;
            for (int vert = 0; vert < 7; vert++)
            {
                int v = vert % 6;
                var f = verts[v];
                var overage = AdjustOverageClassII(ref f, adjRes, false, true);
                if (ClassIII(res) && vert > 0 && f.Face != lastFace && lastOverage != Overage.FaceEdge)
                {
                    int lastV = (v + 5) % 6;
                    var orig0 = IjkToHex2d(verts[lastV].Coord);
                    var orig1 = IjkToHex2d(verts[v].Coord);
                    int face2 = lastFace == center.Face ? f.Face : lastFace;
                    var (e0, e1) = FaceEdge(AdjacentFaceDir[center.Face * 20 + face2], MaxDimByCIIres[adjRes]);
                    var inter = Intersect(orig0, orig1, e0, e1);
                    bool atVertex = (Math.Abs(orig0.X - inter.X) < FltEpsilon && Math.Abs(orig0.Y - inter.Y) < FltEpsilon)
                                    || (Math.Abs(orig1.X - inter.X) < FltEpsilon && Math.Abs(orig1.Y - inter.Y) < FltEpsilon);
                    if (!atVertex) outv.Add((Hex2dToGeo(inter.X, inter.Y, center.Face, adjRes, true)));
                }
                if (vert < 6)
                {
                    var p = IjkToHex2d(f.Coord);
                    outv.Add((Hex2dToGeo(p.X, p.Y, f.Face, adjRes, true)));
                }
                lastFace = f.Face;
                lastOverage = overage;
            }
            return outv;
        }
    }

    /// <summary>The cell as a lon/lat polygon (WGS 84). Cells crossing the antimeridian keep continuous longitudes (beyond ±180).</summary>
    public static Polygon CellToPolygon(ulong h)
    {
        var b = CellToBoundary(h);
        var pts = b.Select(p => new Coordinate(p.Lng, p.Lat)).ToList();
        for (int i = 1; i < pts.Count; i++)
            if (pts[i].X - pts[i - 1].X > 180) pts[i].X -= 360; else if (pts[i].X - pts[i - 1].X < -180) pts[i].X += 360;
        pts.Add(pts[0].Copy());
        return Geo.Factory.CreatePolygon(pts.ToArray());
    }

    // ------------------------------------------------------------------ hierarchy

    public static ulong CellToParent(ulong h, int parentRes)
    {
        int res = GetResolution(h);
        if (parentRes < 0 || parentRes > res) throw new ArgumentOutOfRangeException(nameof(parentRes));
        if (parentRes == res) return h;
        ulong p = SetResolution(h, parentRes);
        for (int r = parentRes + 1; r <= res; r++) p = SetDigit(p, r, 7);
        return p;
    }

    public static ulong CellToCenterChild(ulong h, int childRes)
    {
        int res = GetResolution(h);
        if (childRes < res || childRes > MaxResolution) throw new ArgumentOutOfRangeException(nameof(childRes));
        ulong c = SetResolution(h, childRes);
        for (int r = res + 1; r <= childRes; r++) c = SetDigit(c, r, 0);
        return c;
    }

    /// <summary>All children at a finer resolution (7 per level; 6 under a pentagon).</summary>
    public static IEnumerable<ulong> CellToChildren(ulong h, int childRes)
    {
        int res = GetResolution(h);
        if (childRes < res || childRes > MaxResolution) throw new ArgumentOutOfRangeException(nameof(childRes));
        if (childRes == res) { yield return h; yield break; }
        bool pent = IsPentagon(h);
        ulong baseChild = SetResolution(h, res + 1);
        for (int d = 0; d < 7; d++)
        {
            if (pent && d == 1) continue;
            foreach (var c in CellToChildren(SetDigit(baseChild, res + 1, d), childRes)) yield return c;
        }
    }

    /// <summary>Replaces complete sets of siblings by their parent, recursively.</summary>
    public static IReadOnlyList<ulong> Compact(IEnumerable<ulong> cells)
    {
        var current = new HashSet<ulong>(cells);
        var result = new List<ulong>();
        while (current.Count > 0)
        {
            int res = current.Max(GetResolution);
            var atRes = current.Where(c => GetResolution(c) == res).ToList();
            if (res == 0) { result.AddRange(current); break; }
            var parents = atRes.GroupBy(c => CellToParent(c, res - 1)).ToList();
            foreach (var g in parents)
            {
                int expected = IsPentagon(g.Key) ? 6 : 7;
                if (g.Count() == expected) { foreach (var c in g) current.Remove(c); current.Add(g.Key); }
                else { foreach (var c in g) { current.Remove(c); result.Add(c); } }
            }
        }
        return result;
    }

    public static IReadOnlyList<ulong> Uncompact(IEnumerable<ulong> cells, int res) =>
        cells.SelectMany(c => CellToChildren(c, res)).ToList();

    // ------------------------------------------------------------------ neighbours

    private static readonly int[] Directions = [2, 3, 1, 5, 4, 6];

    /// <summary>The neighbour of a cell in an IJK direction, tracking rotations; false across a pentagon's deleted direction.</summary>
    private static bool NeighborRotations(ulong origin, int dir, ref int rotations, out ulong result)
    {
        ulong current = origin;
        result = 0;
        rotations %= 6;
        for (int i = 0; i < rotations; i++) dir = RotateDigit60ccw(dir);
        int newRotations = 0;
        int oldBaseCell = GetBaseCell(current);
        int oldLeadingDigit = LeadingNonZeroDigit(current);
        int r = GetResolution(current) - 1;
        while (true)
        {
            if (r == -1)
            {
                current = SetBaseCell(current, BaseCellNeighbors[oldBaseCell * 7 + dir]);
                newRotations = BaseCellNeighbor60CCWRots[oldBaseCell * 7 + dir];
                if (GetBaseCell(current) == InvalidBaseCell)
                {
                    current = SetBaseCell(current, BaseCellNeighbors[oldBaseCell * 7 + 5]);
                    newRotations = BaseCellNeighbor60CCWRots[oldBaseCell * 7 + 5];
                    current = Rotate60ccw(current);
                    rotations++;
                }
                break;
            }
            int oldDigit = Digit(current, r + 1), nextDir;
            if (oldDigit == 7) return false;
            if (ClassIII(r + 1)) { current = SetDigit(current, r + 1, NewDigitII[oldDigit * 7 + dir]); nextDir = NewAdjustmentII[oldDigit * 7 + dir]; }
            else { current = SetDigit(current, r + 1, NewDigitIII[oldDigit * 7 + dir]); nextDir = NewAdjustmentIII[oldDigit * 7 + dir]; }
            if (nextDir != 0) { dir = nextDir; r--; }
            else break;
        }
        int newBaseCell = GetBaseCell(current);
        if (IsBaseCellPentagon(newBaseCell))
        {
            bool alreadyAdjusted = false;
            if (LeadingNonZeroDigit(current) == 1)
            {
                if (oldBaseCell != newBaseCell)
                {
                    current = BaseCellIsCwOffset(newBaseCell, BaseCellData[oldBaseCell * 7]) ? Rotate60cw(current) : Rotate60ccw(current);
                    alreadyAdjusted = true;
                }
                else
                {
                    if (oldLeadingDigit == 0) return false;          // the deleted K sub-sequence of a pentagon
                    if (oldLeadingDigit == 3) { current = Rotate60ccw(current); rotations++; }
                    else if (oldLeadingDigit == 5) { current = Rotate60cw(current); rotations += 5; }
                    else return false;
                }
            }
            for (int i = 0; i < newRotations; i++) current = RotatePent60ccw(current);
            if (oldBaseCell != newBaseCell)
            {
                if (IsBaseCellPolarPentagon(newBaseCell))
                {
                    if (oldBaseCell != 118 && oldBaseCell != 8 && LeadingNonZeroDigit(current) != 3) rotations++;
                }
                else if (LeadingNonZeroDigit(current) == 5 && !alreadyAdjusted) rotations++;
            }
        }
        else for (int i = 0; i < newRotations; i++) current = Rotate60ccw(current);
        rotations = (rotations + newRotations) % 6;
        result = current;
        return true;
    }

    /// <summary>The (up to 6) neighbours of a cell.</summary>
    public static IReadOnlyList<ulong> Neighbors(ulong h)
    {
        var res = new List<ulong>(6);
        foreach (var d in Directions)
        {
            int rot = 0;
            if (NeighborRotations(h, d, ref rot, out var n) && n != h && !res.Contains(n)) res.Add(n);
        }
        return res;
    }

    /// <summary>All cells within grid distance k (the origin first), as h3 <c>gridDisk</c>.</summary>
    public static IReadOnlyList<ulong> GridDisk(ulong origin, int k) => GridDiskDistances(origin, k).Select(t => t.Cell).ToList();

    /// <summary>Cells within grid distance k with their distances (breadth-first, pentagon-safe).</summary>
    public static IReadOnlyList<(ulong Cell, int Distance)> GridDiskDistances(ulong origin, int k)
    {
        if (k < 0) throw new ArgumentOutOfRangeException(nameof(k));
        var dist = new Dictionary<ulong, int> { [origin] = 0 };
        var order = new List<(ulong, int)> { (origin, 0) };
        var frontier = new List<ulong> { origin };
        for (int ring = 1; ring <= k; ring++)
        {
            var next = new List<ulong>();
            foreach (var c in frontier)
                foreach (var n in Neighbors(c))
                    if (dist.TryAdd(n, ring)) { next.Add(n); order.Add((n, ring)); }
            frontier = next;
        }
        return order;
    }

    /// <summary>The hollow ring of cells at exactly grid distance k.</summary>
    public static IReadOnlyList<ulong> GridRing(ulong origin, int k) => GridDiskDistances(origin, k).Where(t => t.Distance == k).Select(t => t.Cell).ToList();

    // ------------------------------------------------------------------ metrics



    /// <summary>Exact cell area on the authalic sphere, in steradians: Cagnoli's formula with compensated summation (h3 4.5 <c>cellAreaRads2</c>).</summary>
    public static double CellAreaRads2(ulong h) => LoopAreaRadsCore(BoundaryRads(h));

    /// <summary>Area (steradians) of a counter-clockwise lon/lat loop given as (lat, lng) degrees.</summary>
    public static double LoopAreaRads2(IReadOnlyList<(double Lat, double Lng)> loop) =>
        LoopAreaRadsCore(loop.Select(p => (p.Lat * Math.PI / 180, p.Lng * Math.PI / 180)).ToList());

    private static double LoopAreaRadsCore(IReadOnlyList<(double Lat, double Lng)> loop)
    {
        // classic Kahan summation, exactly as h3's adder.h
        double sum = 0, comp = 0;
        void Add(double x)
        {
            double y = x - comp, t = sum + y;
            comp = (t - sum) - y;
            sum = t;
        }
        for (int i = 0; i < loop.Count; i++)
        {
            var x = loop[i]; var y = loop[(i + 1) % loop.Count];
            double xl = x.Lat / 2.0 + Math.PI / 4.0, yl = y.Lat / 2.0 + Math.PI / 4.0;
            double sa = Math.Sin(xl) * Math.Sin(yl), ca = Math.Cos(xl) * Math.Cos(yl), d = y.Lng - x.Lng;
            Add(-2.0 * Math.Atan2(sa * Math.Sin(d), sa * Math.Cos(d) + ca));
        }
        if (sum < 0.0) Add(4.0 * Math.PI);
        return sum;
    }

    public static double CellAreaKm2(ulong h) => CellAreaRads2(h) * EarthRadiusKm * EarthRadiusKm;
    public static double CellAreaM2(ulong h) => CellAreaKm2(h) * 1e6;

    /// <summary>Average hexagon edge length (km) per resolution, from the H3 documentation.</summary>
    public static double AverageEdgeLengthKm(int res) => EdgeKm[res];
    private static readonly double[] EdgeKm = [1281.256011, 483.0568391, 182.5129565, 68.97922179, 26.07175968, 9.854090990, 3.724532667, 1.406475763, 0.531414010, 0.200786148, 0.075863783, 0.028663897, 0.010830188, 0.004092010, 0.001546100, 0.000584169];

    // ------------------------------------------------------------------ regions

    /// <summary>
    /// Cells whose centre lies inside a lon/lat polygon (H3's default centre-containment mode). Candidates are found by sampling the polygon
    /// and growing one ring, so every centre-contained cell is found; the antimeridian is not handled.
    /// </summary>
    public static IReadOnlyList<ulong> PolygonToCells(NetTopologySuite.Geometries.Geometry polygon, int res)
    {
        var prepared = PreparedGeometryFactory.Prepare(polygon);
        var env = polygon.EnvelopeInternal;
        double stepLat = AverageEdgeLengthKm(res) / 111.2 * 0.75;
        var candidates = new HashSet<ulong>();
        for (double lat = env.MinY; lat <= env.MaxY + stepLat; lat += stepLat)
        {
            double la = Math.Min(lat, env.MaxY);
            double stepLng = stepLat / Math.Max(Math.Cos(la * Math.PI / 180), 0.05);
            for (double lng = env.MinX; lng <= env.MaxX + stepLng; lng += stepLng)
                candidates.Add(LatLngToCell(la, Math.Min(lng, env.MaxX), res));
        }
        foreach (var c in polygon.Coordinates) candidates.Add(LatLngToCell(c.Y, c.X, res));
        var grown = new HashSet<ulong>(candidates);
        foreach (var c in candidates) foreach (var n in Neighbors(c)) grown.Add(n);
        var result = new List<ulong>();
        foreach (var c in grown)
        {
            var (lat, lng) = CellToLatLng(c);
            if (prepared.Contains(Geo.Factory.CreatePoint(new Coordinate(lng, lat)))) result.Add(c);
        }
        result.Sort();
        return result;
    }
}
