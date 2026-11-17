using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Data;

/// <summary>
/// ESRI Shapefile reader / writer (pure managed): .shp geometry, .shx index, .dbf attributes (dBase III),
/// .prj CRS (WKT) and .cpg encoding. Supports Point, MultiPoint, PolyLine, Polygon and their Z/M variants.
/// </summary>
public static class Shapefile
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private enum ShapeType
    {
        Null = 0, Point = 1, PolyLine = 3, Polygon = 5, MultiPoint = 8,
        PointZ = 11, PolyLineZ = 13, PolygonZ = 15, MultiPointZ = 18,
        PointM = 21, PolyLineM = 23, PolygonM = 25, MultiPointM = 28
    }

    // ------------------------------------------------------------------ read

    public static GeoDataFrame Read(string shpPath)
    {
        var basePath = Path.ChangeExtension(shpPath, null);
        var crs = File.Exists(basePath + ".prj") && CoordinateReferenceSystem.TryParse(File.ReadAllText(basePath + ".prj"), out var c) ? c : null;
        var encoding = File.Exists(basePath + ".cpg") ? ResolveEncoding(File.ReadAllText(basePath + ".cpg").Trim()) : Encoding.UTF8;

        var geoms = ReadShp(File.ReadAllBytes(shpPath));
        var dbfPath = basePath + ".dbf";
        var cols = File.Exists(dbfPath) ? ReadDbf(File.ReadAllBytes(dbfPath), encoding) : [];
        if (cols.Count > 0 && cols[0].Length != geoms.Count)
            throw new InvalidDataException($"Record count mismatch: .shp has {geoms.Count}, .dbf has {cols[0].Length}.");
        cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, geoms.ToArray(), crs));
        return new GeoDataFrame(cols);
    }

    private static Encoding ResolveEncoding(string cpg)
    {
        var n = cpg.ToUpperInvariant().Replace("-", "");
        if (n.Contains("UTF8") || n == "65001") return Encoding.UTF8;
        if (n is "1252" or "ANSI1252" or "WINDOWS1252" or "88591" or "ISO88591" or "LATIN1") return Encoding.Latin1;
        try { return Encoding.GetEncoding(cpg); } catch { return Encoding.UTF8; }
    }

    private static List<NtsGeometry?> ReadShp(byte[] data)
    {
        if (BinaryPrimitives.ReadInt32BigEndian(data) != 9994) throw new InvalidDataException("Not a shapefile (bad file code).");
        var result = new List<NtsGeometry?>();
        int pos = 100;
        var f = Geo.Factory;
        while (pos + 8 <= data.Length)
        {
            int contentLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos + 4)) * 2;
            var rec = data.AsSpan(pos + 8, Math.Min(contentLen, data.Length - pos - 8));
            pos += 8 + contentLen;
            var type = (ShapeType)BinaryPrimitives.ReadInt32LittleEndian(rec);
            switch (type)
            {
                case ShapeType.Null: result.Add(null); break;
                case ShapeType.Point or ShapeType.PointM:
                    result.Add(f.CreatePoint(new Coordinate(D(rec, 4), D(rec, 12))));
                    break;
                case ShapeType.PointZ:
                    result.Add(f.CreatePoint(new CoordinateZ(D(rec, 4), D(rec, 12), D(rec, 20))));
                    break;
                case ShapeType.MultiPoint or ShapeType.MultiPointZ or ShapeType.MultiPointM:
                {
                    int n = I(rec, 36);
                    var z = type == ShapeType.MultiPointZ ? 40 + 16 * n + 16 : -1;
                    var pts = new Point[n];
                    for (int i = 0; i < n; i++)
                        pts[i] = f.CreatePoint(z > 0 ? new CoordinateZ(D(rec, 40 + 16 * i), D(rec, 48 + 16 * i), D(rec, z + 8 * i)) : new Coordinate(D(rec, 40 + 16 * i), D(rec, 48 + 16 * i)));
                    result.Add(f.CreateMultiPoint(pts));
                    break;
                }
                case ShapeType.PolyLine or ShapeType.PolyLineZ or ShapeType.PolyLineM or ShapeType.Polygon or ShapeType.PolygonZ or ShapeType.PolygonM:
                {
                    int numParts = I(rec, 36), numPoints = I(rec, 40);
                    int ptsOff = 44 + 4 * numParts;
                    bool hasZ = type is ShapeType.PolyLineZ or ShapeType.PolygonZ;
                    int zOff = ptsOff + 16 * numPoints + 16;
                    var parts = new List<Coordinate[]>(numParts);
                    for (int p = 0; p < numParts; p++)
                    {
                        int start = I(rec, 44 + 4 * p);
                        int end = p + 1 < numParts ? I(rec, 44 + 4 * (p + 1)) : numPoints;
                        var cs = new Coordinate[end - start];
                        for (int k = start; k < end; k++)
                        {
                            double x = D(rec, ptsOff + 16 * k), y = D(rec, ptsOff + 16 * k + 8);
                            cs[k - start] = hasZ ? new CoordinateZ(x, y, D(rec, zOff + 8 * k)) : new Coordinate(x, y);
                        }
                        parts.Add(cs);
                    }
                    bool isPoly = type is ShapeType.Polygon or ShapeType.PolygonZ or ShapeType.PolygonM;
                    result.Add(isPoly ? BuildPolygon(parts) : BuildLine(parts));
                    break;
                }
                default: throw new NotSupportedException($"Shape type {type} is not supported.");
            }
        }
        return result;
    }

    private static double D(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadDoubleLittleEndian(s[o..]);
    private static int I(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s[o..]);

    private static NtsGeometry BuildLine(List<Coordinate[]> parts)
    {
        var f = Geo.Factory;
        var lines = parts.Where(p => p.Length >= 2).Select(p => f.CreateLineString(p)).ToArray();
        return lines.Length == 1 ? lines[0] : f.CreateMultiLineString(lines);
    }

    /// <summary>Shells are clockwise, holes counter-clockwise; holes are assigned to the containing shell.</summary>
    private static NtsGeometry BuildPolygon(List<Coordinate[]> parts)
    {
        var f = Geo.Factory;
        var shells = new List<LinearRing>();
        var holes = new List<LinearRing>();
        foreach (var p in parts)
        {
            if (p.Length < 4) continue;
            var ring = f.CreateLinearRing(p[0].Equals2D(p[^1]) ? p : [.. p, p[0].Copy()]);
            if (Orientation.IsCCW(ring.CoordinateSequence)) holes.Add(ring); else shells.Add(ring);
        }
        if (shells.Count == 0) { shells = holes; holes = []; }
        var shellHoles = shells.Select(_ => new List<LinearRing>()).ToList();
        var shellPolys = shells.Select(s => f.CreatePolygon(s)).ToList();
        foreach (var h in holes)
        {
            var pt = f.CreatePoint(h.GetCoordinateN(0));
            int idx = shellPolys.FindIndex(sp => sp.Contains(pt) || sp.Covers(h));
            if (idx < 0) { shells.Add((LinearRing)h.Reverse()); shellHoles.Add([]); shellPolys.Add(f.CreatePolygon(shells[^1])); }
            else shellHoles[idx].Add(h);
        }
        var polys = shells.Select((s, i) => f.CreatePolygon(s, shellHoles[i].ToArray())).ToArray();
        return polys.Length == 1 ? polys[0] : f.CreateMultiPolygon(polys);
    }

    private static List<Column> ReadDbf(byte[] data, Encoding enc)
    {
        int nRec = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int headerLen = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(8));
        int recLen = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(10));
        var fields = new List<(string Name, char Type, int Len, int Dec, int Offset)>();
        int off = 1;
        for (int p = 32; p + 32 <= headerLen && data[p] != 0x0D; p += 32)
        {
            var name = Encoding.ASCII.GetString(data, p, 11).TrimEnd('\0', ' ');
            char type = (char)data[p + 11];
            int len = data[p + 16], dec = data[p + 17];
            fields.Add((name, type, len, dec, off));
            off += len;
        }
        var raw = fields.Select(_ => new string?[nRec]).ToArray();
        for (int r = 0; r < nRec; r++)
        {
            int rs = headerLen + r * recLen;
            if (rs + recLen > data.Length) break;
            for (int k = 0; k < fields.Count; k++)
            {
                var fd = fields[k];
                var s = enc.GetString(data, rs + fd.Offset, fd.Len).Trim('\0', ' ');
                raw[k][r] = s.Length == 0 ? null : s;
            }
        }
        var cols = new List<Column>();
        var used = new HashSet<string>();
        for (int k = 0; k < fields.Count; k++)
        {
            var (name, type, _, dec, _) = fields[k];
            var colName = used.Add(name) ? name : name + "_" + k;
            var vals = raw[k];
            switch (char.ToUpperInvariant(type))
            {
                case 'N' or 'F':
                    if (dec == 0 && vals.All(v => v is null || long.TryParse(v, NumberStyles.Integer, Inv, out _)))
                        cols.Add(Column<long>.FromNullable(colName, vals.Select(v => v is null ? (long?)null : long.Parse(v, Inv)).ToArray()));
                    else
                        cols.Add(Column<double>.FromNullable(colName, vals.Select(v => v is not null && double.TryParse(v, NumberStyles.Float, Inv, out var d) ? d : (double?)null).ToArray()));
                    break;
                case 'L':
                    cols.Add(Column<bool>.FromNullable(colName, vals.Select(v => v is null ? (bool?)null : "TtYy".Contains(v[0]) ? true : "FfNn".Contains(v[0]) ? false : null).ToArray()));
                    break;
                case 'D':
                    cols.Add(Column<DateTime>.FromNullable(colName, vals.Select(v => v is not null && DateTime.TryParseExact(v, "yyyyMMdd", Inv, DateTimeStyles.None, out var dt) ? dt : (DateTime?)null).ToArray()));
                    break;
                default:
                    cols.Add(new Column<string?>(colName, vals));
                    break;
            }
        }
        return cols;
    }

    // ------------------------------------------------------------------ write

    /// <summary>Writes .shp/.shx/.dbf/.prj/.cpg. All geometries must share one family (point, line or polygon).</summary>
    public static void Write(GeoDataFrame gdf, string shpPath)
    {
        var basePath = Path.ChangeExtension(shpPath, null);
        var geoms = gdf.Geometry;
        var first = geoms.FirstOrDefault(g => g is not null);
        bool hasZ = geoms.Any(g => g is not null && Geo.HasZ(g));
        var type = first switch
        {
            null => ShapeType.Null,
            Point => hasZ ? ShapeType.PointZ : ShapeType.Point,
            MultiPoint => hasZ ? ShapeType.MultiPointZ : ShapeType.MultiPoint,
            ILineal => hasZ ? ShapeType.PolyLineZ : ShapeType.PolyLine,
            IPolygonal => hasZ ? ShapeType.PolygonZ : ShapeType.Polygon,
            _ => throw new NotSupportedException($"Cannot write {first.GeometryType} to a shapefile.")
        };

        var records = new List<byte[]>();
        var env = new Envelope();
        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var g in geoms)
        {
            if (g is null || g.IsEmpty) { records.Add(BitConverter.GetBytes(0)); continue; }
            env.ExpandToInclude(g.EnvelopeInternal);
            foreach (var c in g.Coordinates) if (!double.IsNaN(c.Z)) { zMin = Math.Min(zMin, c.Z); zMax = Math.Max(zMax, c.Z); }
            records.Add(EncodeShape(g, type));
        }
        if (zMin > zMax) { zMin = 0; zMax = 0; }

        using (var shp = new FileStream(basePath + ".shp", FileMode.Create))
        using (var shx = new FileStream(basePath + ".shx", FileMode.Create))
        {
            int shpLen = 100 + records.Sum(r => 8 + r.Length);
            WriteHeader(shp, shpLen, type, env, zMin, zMax);
            WriteHeader(shx, 100 + 8 * records.Count, type, env, zMin, zMax);
            int offset = 100;
            Span<byte> b4 = stackalloc byte[4];
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                BinaryPrimitives.WriteInt32BigEndian(b4, i + 1); shp.Write(b4);
                BinaryPrimitives.WriteInt32BigEndian(b4, r.Length / 2); shp.Write(b4);
                shp.Write(r);
                BinaryPrimitives.WriteInt32BigEndian(b4, offset / 2); shx.Write(b4);
                BinaryPrimitives.WriteInt32BigEndian(b4, r.Length / 2); shx.Write(b4);
                offset += 8 + r.Length;
            }
        }
        WriteDbf(gdf, basePath + ".dbf");
        if (gdf.Crs is { } crs) File.WriteAllText(basePath + ".prj", crs.ToWkt());
        File.WriteAllText(basePath + ".cpg", "UTF-8");
    }

    private static void WriteHeader(Stream s, int fileLenBytes, ShapeType type, Envelope env, double zMin, double zMax)
    {
        var h = new byte[100];
        BinaryPrimitives.WriteInt32BigEndian(h, 9994);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(24), fileLenBytes / 2);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(32), (int)type);
        if (!env.IsNull)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(h.AsSpan(36), env.MinX);
            BinaryPrimitives.WriteDoubleLittleEndian(h.AsSpan(44), env.MinY);
            BinaryPrimitives.WriteDoubleLittleEndian(h.AsSpan(52), env.MaxX);
            BinaryPrimitives.WriteDoubleLittleEndian(h.AsSpan(60), env.MaxY);
        }
        BinaryPrimitives.WriteDoubleLittleEndian(h.AsSpan(68), zMin);
        BinaryPrimitives.WriteDoubleLittleEndian(h.AsSpan(76), zMax);
        s.Write(h);
    }

    private static byte[] EncodeShape(NtsGeometry g, ShapeType type)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((int)type);
        bool z = type is ShapeType.PointZ or ShapeType.MultiPointZ or ShapeType.PolyLineZ or ShapeType.PolygonZ;
        switch (type)
        {
            case ShapeType.Point or ShapeType.PointZ:
                var c = ((Point)g).Coordinate;
                w.Write(c.X); w.Write(c.Y);
                if (z) { w.Write(double.IsNaN(c.Z) ? 0 : c.Z); w.Write(0.0); }
                break;
            case ShapeType.MultiPoint or ShapeType.MultiPointZ:
            {
                var pts = g.Coordinates;
                WriteBox(w, g.EnvelopeInternal);
                w.Write(pts.Length);
                foreach (var p in pts) { w.Write(p.X); w.Write(p.Y); }
                if (z) WriteZ(w, pts);
                break;
            }
            default:
            {
                var parts = new List<Coordinate[]>();
                if (type is ShapeType.PolyLine or ShapeType.PolyLineZ)
                {
                    for (int i = 0; i < g.NumGeometries; i++) if (g.GetGeometryN(i) is LineString ls) parts.Add(ls.Coordinates);
                }
                else
                {
                    for (int i = 0; i < g.NumGeometries; i++)
                    {
                        if (g.GetGeometryN(i) is not Polygon p) continue;
                        var shell = p.ExteriorRing.Coordinates;
                        parts.Add(Orientation.IsCCW(shell) ? shell.Reverse().ToArray() : shell);           // shells clockwise
                        foreach (var h in p.InteriorRings)
                        {
                            var hc = h.Coordinates;
                            parts.Add(Orientation.IsCCW(hc) ? hc : hc.Reverse().ToArray());                // holes counter-clockwise
                        }
                    }
                }
                WriteBox(w, g.EnvelopeInternal);
                w.Write(parts.Count);
                w.Write(parts.Sum(p => p.Length));
                int idx = 0;
                foreach (var p in parts) { w.Write(idx); idx += p.Length; }
                foreach (var p in parts) foreach (var pt in p) { w.Write(pt.X); w.Write(pt.Y); }
                if (z) WriteZ(w, parts.SelectMany(p => p).ToArray());
                break;
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteBox(BinaryWriter w, Envelope e) { w.Write(e.MinX); w.Write(e.MinY); w.Write(e.MaxX); w.Write(e.MaxY); }

    private static void WriteZ(BinaryWriter w, Coordinate[] pts)
    {
        var zs = pts.Select(p => double.IsNaN(p.Z) ? 0 : p.Z).ToArray();
        w.Write(zs.Min()); w.Write(zs.Max());
        foreach (var v in zs) w.Write(v);
    }

    private static void WriteDbf(GeoDataFrame gdf, string path)
    {
        var enc = Encoding.UTF8;
        var cols = gdf.Columns.Where(c => c is not GeoSeries).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<(string Name, char Type, int Len, int Dec, Column Col)>();
        foreach (var c in cols)
        {
            var baseName = new string(c.Name.Where(ch => ch < 128).ToArray());
            if (baseName.Length == 0) baseName = "field";
            var name = baseName.Length > 10 ? baseName[..10] : baseName;
            for (int k = 1; !names.Add(name); k++) name = (baseName.Length > 8 ? baseName[..8] : baseName) + "_" + k;
            var t = c.DataType;
            if (t == typeof(long) || t == typeof(int)) fields.Add((name, 'N', 18, 0, c));
            else if (t == typeof(double) || t == typeof(float)) fields.Add((name, 'N', 24, 10, c));
            else if (t == typeof(bool)) fields.Add((name, 'L', 1, 0, c));
            else if (t == typeof(DateTime)) fields.Add((name, 'D', 8, 0, c));
            else
            {
                int max = 1;
                for (int i = 0; i < c.Length; i++) max = Math.Max(max, enc.GetByteCount(c.GetValue(i)?.ToString() ?? ""));
                fields.Add((name, 'C', Math.Min(254, max), 0, c));
            }
        }

        int headerLen = 32 + 32 * fields.Count + 1;
        int recLen = 1 + fields.Sum(f => f.Len);
        using var fs = new FileStream(path, FileMode.Create);
        var header = new byte[32];
        header[0] = 0x03;
        var now = DateTime.UtcNow;
        header[1] = (byte)(now.Year - 1900); header[2] = (byte)now.Month; header[3] = (byte)now.Day;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), gdf.RowCount);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(8), (short)headerLen);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), (short)recLen);
        header[29] = 0x00; // language driver: none (encoding given by .cpg)
        fs.Write(header);
        foreach (var f in fields)
        {
            var d = new byte[32];
            Encoding.ASCII.GetBytes(f.Name, 0, f.Name.Length, d, 0);
            d[11] = (byte)f.Type; d[16] = (byte)f.Len; d[17] = (byte)f.Dec;
            fs.Write(d);
        }
        fs.WriteByte(0x0D);
        var rec = new byte[recLen];
        for (int i = 0; i < gdf.RowCount; i++)
        {
            Array.Fill(rec, (byte)' ');
            int off = 1;
            foreach (var f in fields)
            {
                string s = FormatField(f.Col, i, f.Type, f.Len, f.Dec);
                var bytes = enc.GetBytes(s);
                int n = Math.Min(bytes.Length, f.Len);
                if (f.Type == 'N') Array.Copy(bytes, 0, rec, off + f.Len - n, n); // right-align numbers
                else Array.Copy(bytes, 0, rec, off, n);
                off += f.Len;
            }
            fs.Write(rec);
        }
        fs.WriteByte(0x1A);
    }

    private static string FormatField(Column c, int i, char type, int len, int dec)
    {
        if (c.IsNull(i)) return type == 'L' ? "?" : "";
        var v = c.GetValue(i);
        return type switch
        {
            'N' when dec == 0 => Convert.ToInt64(v, Inv).ToString(Inv),
            'N' => FitNumber(Convert.ToDouble(v, Inv), len, dec),
            'L' => (bool)v! ? "T" : "F",
            'D' => ((DateTime)v!).ToString("yyyyMMdd", Inv),
            _ => v?.ToString() ?? ""
        };
    }

    private static string FitNumber(double d, int len, int dec)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "";
        for (int k = dec; k >= 0; k--)
        {
            var s = d.ToString("F" + k, Inv);
            if (s.Length <= len) return s;
        }
        return d.ToString("E6", Inv);
    }
}
