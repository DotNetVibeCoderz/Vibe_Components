using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using GeoPandas.Net.Data;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.CAD;

/// <summary>DGN read options.</summary>
public sealed record DgnReadOptions
{
    public CoordinateReferenceSystem? Crs { get; init; }
    /// <summary>Segments of a full ellipse / circle.</summary>
    public int CurveSegments { get; init; } = 64;
    /// <summary>Closed shapes as polygons (otherwise rings).</summary>
    public bool ShapesAsPolygons { get; init; } = true;
}

/// <summary>
/// MicroStation DGN version 7 (ISFF) reader and writer, following the element layout documented by GDAL's dgnlib: the terminal control block
/// (units and global origin), lines, line strings, shapes, complex chains and shapes, arcs, ellipses, text and cells, with level, colour,
/// weight and style. VAX D-float values and middle-endian integers are converted to and from IEEE. DGN v8 (an OLE container) is not supported.
/// </summary>
public static class Dgn
{
    public static void Register() =>
        GeoIO.Register(new GeoDriver("DGN", [".dgn"], (p, _) => Read(p), (g, p, l) => Write(g, p)));

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void AutoRegister() => Register();
#pragma warning restore CA2255

    private const int TypeCell = 2, TypeLine = 3, TypeLineString = 4, TypeShape = 6, TypeTcb = 9, TypeCurve = 11, TypeComplexChain = 12,
        TypeComplexShape = 14, TypeEllipse = 15, TypeArc = 16, TypeText = 17;

    private static readonly Dictionary<int, string> TypeNames = new()
    {
        [TypeCell] = "CELL", [TypeLine] = "LINE", [TypeLineString] = "LINE_STRING", [TypeShape] = "SHAPE", [TypeCurve] = "CURVE",
        [TypeComplexChain] = "COMPLEX_CHAIN", [TypeComplexShape] = "COMPLEX_SHAPE", [TypeEllipse] = "ELLIPSE", [TypeArc] = "ARC", [TypeText] = "TEXT"
    };

    // ------------------------------------------------------------------ numeric formats

    /// <summary>DGN int32: the two 16-bit words in big-word order, each little-endian.</summary>
    public static int Int32(ReadOnlySpan<byte> p) => p[2] | p[3] << 8 | p[0] << 16 | p[1] << 24;

    /// <summary>Writes a DGN (middle-endian) int32.</summary>
    public static void PutInt32(Span<byte> p, int v)
    {
        p[0] = (byte)(v >> 16); p[1] = (byte)(v >> 24); p[2] = (byte)v; p[3] = (byte)(v >> 8);
    }

    /// <summary>VAX D-float (8 bytes) → IEEE double (GDAL DGN2IEEEDouble).</summary>
    public static double VaxToIeee(ReadOnlySpan<byte> src)
    {
        Span<byte> d = stackalloc byte[8];
        d[2] = src[0]; d[3] = src[1]; d[0] = src[2]; d[1] = src[3];
        d[6] = src[4]; d[7] = src[5]; d[4] = src[6]; d[5] = src[7];
        uint hi = BinaryPrimitives.ReadUInt32LittleEndian(d), lo = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        uint sign = hi & 0x80000000, exponent = (hi >> 23) & 0xff;
        if (exponent != 0) exponent = exponent - 129 + 1023;
        uint rnd = lo & 7;
        lo = (lo >> 3) & 0x1fffffff | (hi << 29);
        if (rnd != 0) lo |= 1;
        hi = (hi >> 3) & 0x000fffff | exponent << 20 | sign;
        return BitConverter.UInt64BitsToDouble((ulong)hi << 32 | lo);
    }

    /// <summary>IEEE double → VAX D-float (GDAL IEEE2DGNDouble).</summary>
    public static void IeeeToVax(double v, Span<byte> dest)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(v);
        uint hi = (uint)(bits >> 32), lo = (uint)bits;
        uint sign = hi & 0x80000000;
        int exponent = (int)((hi >> 20) & 0x7ff);
        if (exponent != 0) exponent = exponent - 1023 + 129;
        if (exponent > 255) { hi = sign | 0x7fffffff; lo = 0xffffffff; }
        else if (exponent <= 0) { hi = 0; lo = 0; }
        else
        {
            hi = ((hi << 3) | (lo >> 29)) & 0x007fffff | (uint)exponent << 23 | sign;
            lo <<= 3;
        }
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(s, hi);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], lo);
        dest[2] = s[0]; dest[3] = s[1]; dest[0] = s[2]; dest[1] = s[3];
        dest[6] = s[4]; dest[7] = s[5]; dest[4] = s[6]; dest[5] = s[7];
    }

    private static string Rad50(ushort w)
    {
        const string chars = " ABCDEFGHIJKLMNOPQRSTUVWXYZ$.?0123456789";
        var c = new char[3];
        for (int i = 2; i >= 0; i--) { c[i] = chars[w % 40 < chars.Length ? w % 40 : 0]; w /= 40; }
        return new string(c);
    }

    // ------------------------------------------------------------------ reader

    private sealed class Tcb
    {
        public int Dimension = 2;
        public double Scale = 1, OriginX, OriginY, OriginZ;
        public string MasterUnits = "", SubUnits = "";
        public (double X, double Y, double Z) Transform(int x, int y, int z) => (x * Scale - OriginX, y * Scale - OriginY, z * Scale - OriginZ);
    }

    public static GeoDataFrame Read(string path, DgnReadOptions? options = null) => Parse(File.ReadAllBytes(path), options);

    public static GeoDataFrame Parse(ReadOnlySpan<byte> data, DgnReadOptions? options = null)
    {
        options ??= new DgnReadOptions();
        var f = Geo.Factory;
        var tcb = new Tcb();
        var geoms = new List<NtsGeometry?>();
        var level = new List<long>(); var type = new List<string>(); var color = new List<long>(); var weight = new List<long>(); var style = new List<long>();
        var text = new List<string?>(); var height = new List<double?>(); var cell = new List<string?>();
        string? currentCell = null; int cellRemainingWords = 0;
        // complex chain / shape assembly
        List<Coordinate>? complexPts = null; int complexRemaining = 0; int complexType = 0;
        (int Level, int Color, int Weight, int Style) complexSym = default;

        void Emit(NtsGeometry g, int t, int lv, int col, int wt, int st, string? txt = null, double? h = null)
        {
            geoms.Add(g); type.Add(TypeNames.GetValueOrDefault(t, t.ToString(CultureInfo.InvariantCulture))); level.Add(lv); color.Add(col); weight.Add(wt); style.Add(st);
            text.Add(txt); height.Add(h); cell.Add(currentCell);
        }
        Coordinate Pt(ReadOnlySpan<byte> p, int at)
        {
            var (x, y, z) = tcb.Dimension == 3
                ? tcb.Transform(Int32(p[at..]), Int32(p[(at + 4)..]), Int32(p[(at + 8)..]))
                : tcb.Transform(Int32(p[at..]), Int32(p[(at + 4)..]), 0);
            return tcb.Dimension == 3 ? new CoordinateZ(x, y, z) : new Coordinate(x, y);
        }
        List<Coordinate> Ellipse(double cx, double cy, double a, double b, double rotDeg, double startDeg, double sweepDeg, int segs)
        {
            var pts = new List<Coordinate>();
            int n = Math.Max(4, (int)Math.Ceiling(segs * Math.Abs(sweepDeg) / 360));
            double r = rotDeg * Math.PI / 180;
            for (int i = 0; i <= n; i++)
            {
                double t = (startDeg + sweepDeg * i / n) * Math.PI / 180, x = a * Math.Cos(t), y = b * Math.Sin(t);
                pts.Add(new Coordinate(cx + x * Math.Cos(r) - y * Math.Sin(r), cy + x * Math.Sin(r) + y * Math.Cos(r)));
            }
            return pts;
        }

        Span<byte> sweepBuf = stackalloc byte[4];
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] == 0xFF && data[pos + 1] == 0xFF) break;
            int words = data[pos + 2] | data[pos + 3] << 8;
            int size = words * 2 + 4;
            if (pos + size > data.Length) break;
            var e = data.Slice(pos, size);
            pos += size;
            int lv = e[0] & 0x3f, t = e[1] & 0x7f;
            bool complex = (e[0] & 0x80) != 0, deleted = (e[1] & 0x80) != 0;
            if (t == TypeTcb)
            {
                if (size >= 1264)
                {
                    tcb.Dimension = (e[0] & 0x40) != 0 ? 3 : 2;
                    int subPerMaster = Int32(e[1112..]), uorPerSub = Int32(e[1116..]);
                    tcb.MasterUnits = Encoding.ASCII.GetString(e.Slice(1120, 2)).TrimEnd('\0', ' ');
                    tcb.SubUnits = Encoding.ASCII.GetString(e.Slice(1122, 2)).TrimEnd('\0', ' ');
                    double div = (double)subPerMaster * uorPerSub;
                    if (div == 0) div = 1;
                    tcb.Scale = 1 / div;
                    tcb.OriginX = VaxToIeee(e[1240..]) / div; tcb.OriginY = VaxToIeee(e[1248..]) / div; tcb.OriginZ = VaxToIeee(e[1256..]) / div;
                }
                continue;
            }
            if (deleted) continue;
            if (cellRemainingWords > 0) { cellRemainingWords -= size / 2; if (cellRemainingWords <= 0) currentCell = null; }
            if (size < 36) continue;
            int col = e[35], wt = (e[34] & 0xf8) >> 3, st = e[34] & 0x7;
            int ptSize = tcb.Dimension == 3 ? 12 : 8;
            switch (t)
            {
                case TypeCell:
                    currentCell = Rad50((ushort)(e[38] | e[39] << 8)) + Rad50((ushort)(e[40] | e[41] << 8));
                    currentCell = currentCell.Trim();
                    cellRemainingWords = e[36] | e[37] << 8;
                    continue;
                case TypeComplexChain:
                case TypeComplexShape:
                    complexPts = []; complexType = t; complexRemaining = e[38] | e[39] << 8; complexSym = (lv, col, wt, st);
                    if (complexRemaining == 0) complexPts = null;
                    continue;
                case TypeLine:
                case TypeLineString:
                case TypeShape:
                case TypeCurve:
                {
                    List<Coordinate> pts;
                    if (t == TypeLine) pts = [Pt(e, 36), Pt(e, 36 + ptSize)];
                    else
                    {
                        int n = e[36] | e[37] << 8;
                        pts = new List<Coordinate>(n);
                        for (int i = 0; i < n && 38 + (i + 1) * ptSize <= size; i++) pts.Add(Pt(e, 38 + i * ptSize));
                    }
                    if (complex && complexPts is not null)
                    {
                        foreach (var p in pts) if (complexPts.Count == 0 || !complexPts[^1].Equals2D(p)) complexPts.Add(p);
                        if (--complexRemaining == 0) FlushComplex();
                        continue;
                    }
                    if (pts.Count == 0) continue;
                    if (t == TypeShape && options.ShapesAsPolygons && pts.Count >= 3)
                    {
                        if (!pts[0].Equals2D(pts[^1])) pts.Add(pts[0].Copy());
                        if (pts.Count >= 4) { Emit(f.CreatePolygon(pts.ToArray()), t, lv, col, wt, st); continue; }
                    }
                    if (pts.Count == 1) pts.Add(pts[0].Copy());
                    Emit(f.CreateLineString(pts.ToArray()), t, lv, col, wt, st);
                    continue;
                }
                case TypeEllipse:
                case TypeArc:
                {
                    double start = 0, sweep = 360, a, b, rot, ox, oy;
                    if (t == TypeArc)
                    {
                        start = Int32(e[36..]) / 360000.0;
                        var sw = sweepBuf;
                        e.Slice(40, 4).CopyTo(sw);
                        bool neg = (sw[1] & 0x80) != 0;
                        sw[1] &= 0x7f;
                        sweep = Int32(sw) / 360000.0 * (neg ? -1 : 1);
                        if (sweep == 0) sweep = 360;
                        a = VaxToIeee(e[44..]) * tcb.Scale; b = VaxToIeee(e[52..]) * tcb.Scale;
                        if (tcb.Dimension == 2) { rot = Int32(e[60..]) / 360000.0; ox = VaxToIeee(e[64..]); oy = VaxToIeee(e[72..]); }
                        else { rot = 0; ox = VaxToIeee(e[76..]); oy = VaxToIeee(e[84..]); }   // 3-D: quaternion rotation not applied
                    }
                    else
                    {
                        a = VaxToIeee(e[36..]) * tcb.Scale; b = VaxToIeee(e[44..]) * tcb.Scale;
                        if (tcb.Dimension == 2) { rot = Int32(e[52..]) / 360000.0; ox = VaxToIeee(e[56..]); oy = VaxToIeee(e[64..]); }
                        else { rot = 0; ox = VaxToIeee(e[68..]); oy = VaxToIeee(e[76..]); }
                    }
                    double cx = ox * tcb.Scale - tcb.OriginX, cy = oy * tcb.Scale - tcb.OriginY;
                    var pts = Ellipse(cx, cy, a, b, rot, start, sweep, options.CurveSegments);
                    if (complex && complexPts is not null)
                    {
                        foreach (var p in pts) if (complexPts.Count == 0 || !complexPts[^1].Equals2D(p)) complexPts.Add(p);
                        if (--complexRemaining == 0) FlushComplex();
                        continue;
                    }
                    if (t == TypeEllipse && options.ShapesAsPolygons) { pts[^1] = pts[0].Copy(); Emit(f.CreatePolygon(pts.ToArray()), t, lv, col, wt, st); }
                    else Emit(f.CreateLineString(pts.ToArray()), t, lv, col, wt, st);
                    continue;
                }
                case TypeText:
                {
                    double heightMult, rot;
                    int n, at;
                    Coordinate origin;
                    if (tcb.Dimension == 2)
                    {
                        heightMult = Int32(e[42..]) * tcb.Scale * 6 / 1000; rot = Int32(e[46..]) / 360000.0;
                        origin = Pt(e, 50); n = e[58]; at = 60;
                    }
                    else
                    {
                        heightMult = Int32(e[42..]) * tcb.Scale * 6 / 1000; rot = 0;
                        origin = Pt(e, 62); n = e[74]; at = 76;
                    }
                    string s;
                    if (n >= 2 && e[at] == 0xFF && e[at + 1] == 0xFD)
                    {
                        // multi-byte text: 16-bit characters after the marker
                        var chars = new StringBuilder();
                        for (int i = at + 2; i + 1 < at + n && i + 1 < size; i += 2) chars.Append((char)(e[i] | e[i + 1] << 8));
                        s = chars.ToString();
                    }
                    else s = Encoding.Latin1.GetString(e.Slice(at, Math.Min(n, size - at)));
                    _ = rot;
                    Emit(f.CreatePoint(origin), t, lv, col, wt, st, s, heightMult);
                    continue;
                }
            }
            if (complex && complexPts is not null && --complexRemaining == 0) FlushComplex();
        }
        return new GeoDataFrame(
        [
            Column.Create("type", type.ToArray()), Column.Create("level", level.ToArray()), Column.Create("color", color.ToArray()),
            Column.Create("weight", weight.ToArray()), Column.Create("style", style.ToArray()), new Column<string?>("text", text.ToArray()),
            Column<double>.FromNullable("height", height.ToArray()), new Column<string?>("cell", cell.ToArray()),
            new GeoSeries("geometry", geoms.ToArray(), options.Crs)
        ]);

        void FlushComplex()
        {
            var pts = complexPts!;
            complexPts = null;
            if (pts.Count < 2) return;
            if (complexType == TypeComplexShape && options.ShapesAsPolygons && pts.Count >= 3)
            {
                if (!pts[0].Equals2D(pts[^1])) pts.Add(pts[0].Copy());
                Emit(f.CreatePolygon(pts.ToArray()), complexType, complexSym.Level, complexSym.Color, complexSym.Weight, complexSym.Style);
            }
            else Emit(f.CreateLineString(pts.ToArray()), complexType, complexSym.Level, complexSym.Color, complexSym.Weight, complexSym.Style);
        }
    }

    // ------------------------------------------------------------------ writer

    /// <summary>
    /// Writes a 2-D DGN v7 file. Lines → LINE / LINE_STRING (COMPLEX_CHAIN beyond 101 vertices), polygon rings → SHAPE (COMPLEX_SHAPE beyond
    /// 101 vertices), points → TEXT when <paramref name="textColumn"/> has a value, else a zero-length LINE (as GDAL). Coordinates are stored
    /// as integers with <paramref name="resolution"/> (master units per unit of resolution, default 1 mm) around the data's centre.
    /// Level, colour, weight and style come from the optional columns (DGN ranges: level 1–63, colour 0–255, weight 0–31, style 0–7).
    /// </summary>
    public static void Write(GeoDataFrame gdf, string path, string? levelColumn = "level", string? colorColumn = "color", string? textColumn = "text",
        double resolution = 0.001, double textHeight = 2.5, string masterUnits = "m", string subUnits = "mm")
    {
        var env = gdf.TotalBounds;
        double cx = env.IsNull ? 0 : Math.Round((env.MinX + env.MaxX) / 2), cy = env.IsNull ? 0 : Math.Round((env.MinY + env.MaxY) / 2);
        // integer coordinates are (x + origin) / resolution: choose the origin so the centre maps to 0
        double originX = -cx, originY = -cy;
        using var fs = File.Create(path);
        int subPerMaster = (int)Math.Round(1 / resolution), uorPerSub = 1;
        fs.Write(BuildTcb(subPerMaster, uorPerSub, originX / resolution, originY / resolution, masterUnits, subUnits));
        int Int(double v, double origin) => checked((int)Math.Round((v + origin) / resolution));
        long Col(string? name, int i, long def) => name is not null && gdf.Columns.Any(c => c.Name == name) && !gdf[name].IsNull(i) ? (long)gdf[name].GetDouble(i) : def;
        for (int i = 0; i < gdf.RowCount; i++)
        {
            var g = gdf.Geometry[i];
            if (g is null || g.IsEmpty) continue;
            int lv = (int)Math.Clamp(Col(levelColumn, i, 1), 1, 63), color = (int)Math.Clamp(Col(colorColumn, i, 0), 0, 255);
            int wt = (int)Math.Clamp(Col("weight", i, 0), 0, 31), st = (int)Math.Clamp(Col("style", i, 0), 0, 7);
            string? txt = textColumn is not null && gdf.Columns.Any(c => c.Name == textColumn) && gdf[textColumn].GetValue(i) is { } tv ? Convert.ToString(tv, CultureInfo.InvariantCulture) : null;
            foreach (var part in Parts(g))
            {
                switch (part)
                {
                    case NetTopologySuite.Geometries.Point p when !string.IsNullOrEmpty(txt):
                        fs.Write(TextElement(lv, color, wt, st, Int(p.X, originX), Int(p.Y, originY), txt!, textHeight / resolution));
                        break;
                    case NetTopologySuite.Geometries.Point p:
                    {
                        var xy = (Int(p.X, originX), Int(p.Y, originY));
                        fs.Write(Linear(TypeLine, lv, color, wt, st, [xy, xy], false));
                        break;
                    }
                    case LineString ls:
                        WriteChain(fs, ls.Coordinates.Select(c => (Int(c.X, originX), Int(c.Y, originY))).ToList(), false, lv, color, wt, st);
                        break;
                    case Polygon poly:
                        foreach (var ring in new[] { poly.ExteriorRing }.Concat(poly.InteriorRings))
                            WriteChain(fs, ring.Coordinates.Select(c => (Int(c.X, originX), Int(c.Y, originY))).ToList(), true, lv, color, wt, st);
                        break;
                }
            }
        }
        fs.Write([0xFF, 0xFF]);
    }

    private static IEnumerable<NtsGeometry> Parts(NtsGeometry g) =>
        g is GeometryCollection gc ? gc.Geometries.SelectMany(Parts) : [g];

    private static void WriteChain(Stream fs, List<(int X, int Y)> pts, bool shape, int lv, int color, int wt, int st)
    {
        const int max = 101;
        if (pts.Count <= max)
        {
            fs.Write(Linear(pts.Count == 2 && !shape ? TypeLine : shape ? TypeShape : TypeLineString, lv, color, wt, st, pts, false));
            return;
        }
        // complex header followed by line-string components sharing their end points
        var comps = new List<byte[]>();
        for (int s = 0; s < pts.Count - 1; s += max - 1)
            comps.Add(Linear(TypeLineString, lv, color, wt, st, pts.Skip(s).Take(max).ToList(), true));
        int total = comps.Sum(c => c.Length / 2);
        var header = Core(shape ? TypeComplexShape : TypeComplexChain, lv, color, wt, st, 44, false, pts);
        header[36] = (byte)total; header[37] = (byte)(total >> 8);
        header[38] = (byte)comps.Count; header[39] = (byte)(comps.Count >> 8);
        fs.Write(header);
        foreach (var c in comps) fs.Write(c);
    }

    private static byte[] Linear(int type, int lv, int color, int wt, int st, List<(int X, int Y)> pts, bool complex)
    {
        byte[] e;
        if (type == TypeLine)
        {
            e = Core(type, lv, color, wt, st, 52, complex, pts);
            PutInt32(e.AsSpan(36), pts[0].X); PutInt32(e.AsSpan(40), pts[0].Y); PutInt32(e.AsSpan(44), pts[^1].X); PutInt32(e.AsSpan(48), pts[^1].Y);
            return e;
        }
        e = Core(type, lv, color, wt, st, 38 + 8 * pts.Count, complex, pts);
        e[36] = (byte)pts.Count; e[37] = (byte)(pts.Count >> 8);
        for (int i = 0; i < pts.Count; i++) { PutInt32(e.AsSpan(38 + 8 * i), pts[i].X); PutInt32(e.AsSpan(42 + 8 * i), pts[i].Y); }
        return e;
    }

    private static byte[] TextElement(int lv, int color, int wt, int st, int x, int y, string text, double heightUor)
    {
        var chars = Encoding.Latin1.GetBytes(text.Length > 255 ? text[..255] : text);
        int size = 60 + chars.Length + (chars.Length % 2);
        var e = Core(TypeText, lv, color, wt, st, size, false, [(x, y)]);
        e[36] = 0;          // font
        e[37] = 1;          // justification: left centre
        int mult = (int)Math.Round(heightUor * 1000 / 6);
        PutInt32(e.AsSpan(38), mult); PutInt32(e.AsSpan(42), mult); PutInt32(e.AsSpan(46), 0);
        PutInt32(e.AsSpan(50), x); PutInt32(e.AsSpan(54), y);
        e[58] = (byte)chars.Length; e[59] = 0;
        chars.CopyTo(e.AsSpan(60));
        return e;
    }

    /// <summary>Element header: level/type, words to follow, range (offset-binary), graphic group, attribute index, properties, symbology.</summary>
    private static byte[] Core(int type, int lv, int color, int wt, int st, int size, bool complex, List<(int X, int Y)> pts)
    {
        var e = new byte[size];
        e[0] = (byte)(lv & 0x3f | (complex ? 0x80 : 0));
        e[1] = (byte)type;
        int words = size / 2 - 2;
        e[2] = (byte)words; e[3] = (byte)(words >> 8);
        int xmin = pts.Min(p => p.X), ymin = pts.Min(p => p.Y), xmax = pts.Max(p => p.X), ymax = pts.Max(p => p.Y);
        PutInt32(e.AsSpan(4), xmin); PutInt32(e.AsSpan(8), ymin); PutInt32(e.AsSpan(12), 0);
        PutInt32(e.AsSpan(16), xmax); PutInt32(e.AsSpan(20), ymax); PutInt32(e.AsSpan(24), 0);
        foreach (int k in new[] { 5, 9, 13, 17, 21, 25 }) e[k] ^= 0x80;     // two's complement → offset binary
        int attIndex = size / 2 - 16;
        e[30] = (byte)attIndex; e[31] = (byte)(attIndex >> 8);
        e[32] = 0; e[33] = 0;
        e[34] = (byte)(st & 7 | (wt & 0x1f) << 3);
        e[35] = (byte)color;
        return e;
    }

    /// <summary>A 2-D terminal control block (1 536 bytes) with units and global origin (in UORs).</summary>
    private static byte[] BuildTcb(int subPerMaster, int uorPerSub, double originXUor, double originYUor, string master, string sub)
    {
        var e = new byte[1536];
        e[0] = 0x08; e[1] = TypeTcb;
        int words = e.Length / 2 - 2;
        e[2] = (byte)words; e[3] = (byte)(words >> 8);
        PutInt32(e.AsSpan(1112), subPerMaster);
        PutInt32(e.AsSpan(1116), uorPerSub);
        Encoding.ASCII.GetBytes(master.PadRight(2)[..2]).CopyTo(e.AsSpan(1120));
        Encoding.ASCII.GetBytes(sub.PadRight(2)[..2]).CopyTo(e.AsSpan(1122));
        IeeeToVax(originXUor, e.AsSpan(1240));
        IeeeToVax(originYUor, e.AsSpan(1248));
        IeeeToVax(0, e.AsSpan(1256));
        return e;
    }
}
