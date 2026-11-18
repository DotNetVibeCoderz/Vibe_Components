using System.Buffers.Binary;
using System.Text;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.IO;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.PointCloud;

/// <summary>LAS/LAZ header summary.</summary>
public sealed record LasInfo(
    string Path, int VersionMajor, int VersionMinor, int PointFormat, int PointRecordLength, long PointCount, bool Compressed,
    double ScaleX, double ScaleY, double ScaleZ, double OffsetX, double OffsetY, double OffsetZ,
    double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ,
    CoordinateReferenceSystem? Crs, string SystemIdentifier, string GeneratingSoftware, DateOnly? CreationDate,
    long OffsetToPoints, IReadOnlyList<(string UserId, int RecordId, string Description, int Length)> Vlrs)
{
    /// <summary>Payload of the "laszip encoded" (22204) VLR of a LAZ file.</summary>
    internal byte[]? LaszipVlr { get; init; }
    /// <summary>Payload of the COPC info VLR ("copc", 1) when the file is a Cloud-Optimized Point Cloud.</summary>
    internal byte[]? CopcInfoVlr { get; init; }
    /// <summary>Position of the first extended VLR and their count (LAS 1.4).</summary>
    internal long EvlrStart { get; init; }
    internal int EvlrCount { get; init; }
    public bool IsCopc => CopcInfoVlr is not null;
    public string Version => $"{VersionMajor}.{VersionMinor}";
    public override string ToString() =>
        $"{(Compressed ? "LAZ" : "LAS")} {Version} format {PointFormat} ({PointRecordLength} B/pt), {PointCount:N0} points, crs={Crs?.ToString() ?? "None"}, " +
        $"x [{MinX:F2}, {MaxX:F2}] y [{MinY:F2}, {MaxY:F2}] z [{MinZ:F2}, {MaxZ:F2}]";
}

/// <summary>Read options: spatial / class filters and decimation applied while decoding (points that fail are never materialised).</summary>
public sealed record LasReadOptions
{
    public Envelope? Bbox { get; init; }
    public byte[]? Classes { get; init; }
    /// <summary>Keep every n-th point (1 = all).</summary>
    public int Step { get; init; } = 1;
    public long? MaxPoints { get; init; }
}

/// <summary>
/// ASPRS LAS 1.0–1.4 reader/writer (point formats 0–10, memory-mapped, own codec) with LAZ support through laszip.net
/// (LASzip 2.x port, point formats 0–5). CRS from the OGC WKT VLR (2112) or GeoTIFF GeoKeys (34735).
/// </summary>
public static class Las
{
    private static readonly int[] s_baseLength = [20, 28, 26, 34, 57, 63, 30, 36, 38, 59, 67];

    // ================================================================== header

    public static LasInfo ReadInfo(string path)
    {
        using var f = new MappedFile(path);
        return ReadHeader(f, path);
    }

    private static LasInfo ReadHeader(MappedFile f, string path)
    {
        if (f.Length < 227) throw new InvalidDataException("Not a LAS file (too short).");
        var h = f.Span(0, (int)Math.Min(f.Length, 375)).ToArray().AsSpan();
        var hb = h.ToArray();
        if (h[0] != 'L' || h[1] != 'A' || h[2] != 'S' || h[3] != 'F') throw new InvalidDataException("Not a LAS/LAZ file (missing 'LASF').");
        int major = h[24], minor = h[25];
        string sys = Ascii(h.Slice(26, 32)), soft = Ascii(h.Slice(58, 32));
        int doy = BinaryPrimitives.ReadUInt16LittleEndian(h[90..]), year = BinaryPrimitives.ReadUInt16LittleEndian(h[92..]);
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(h[94..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(h[96..]);
        uint nvlr = BinaryPrimitives.ReadUInt32LittleEndian(h[100..]);
        byte fmtByte = h[104];
        bool compressed = (fmtByte & 0xC0) != 0;
        int fmt = fmtByte & 0x3F;
        int recLen = BinaryPrimitives.ReadUInt16LittleEndian(h[105..]);
        long count = BinaryPrimitives.ReadUInt32LittleEndian(h[107..]);
        double D(int o) => BinaryPrimitives.ReadDoubleLittleEndian(hb.AsSpan(o));
        if (major == 1 && minor >= 4 && headerSize >= 375) { long c14 = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[247..]); if (c14 > 0) count = c14; }

        var vlrs = new List<(string, int, string, int)>();
        CoordinateReferenceSystem? crs = null;
        byte[]? laszip = null, copc = null;
        long pos = headerSize;
        for (uint i = 0; i < nvlr && pos + 54 <= f.Length; i++)
        {
            var vh = f.Span(pos, 54);
            string user = Ascii(vh.Slice(2, 16));
            int rid = BinaryPrimitives.ReadUInt16LittleEndian(vh[18..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(vh[20..]);
            string desc = Ascii(vh.Slice(22, 32));
            vlrs.Add((user, rid, desc, len));
            if (user == "laszip encoded" && rid == 22204 && pos + 54 + len <= f.Length) laszip = f.Span(pos + 54, len).ToArray();
            if (user == "copc" && rid == 1 && pos + 54 + len <= f.Length) copc = f.Span(pos + 54, len).ToArray();
            if (user == "LASF_Projection" && pos + 54 + len <= f.Length)
            {
                var data = f.Span(pos + 54, len);
                if (rid == 2112 && crs is null) { var wkt = Ascii(data); if (CoordinateReferenceSystem.TryParse(wkt, out var c)) crs = c; }
                else if (rid == 34735 && crs is null) crs = FromGeoKeys(data);
            }
            pos += 54 + len;
        }
        if (crs is null && major == 1 && minor >= 4 && headerSize >= 375)
        {
            // CRS may live in an extended VLR (after the points)
            long evlr = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[235..]);
            uint nev = BinaryPrimitives.ReadUInt32LittleEndian(h[243..]);
            for (uint i = 0; i < nev && evlr > 0 && evlr + 60 <= f.Length; i++)
            {
                var eh = f.Span(evlr, 60);
                string user = Ascii(eh.Slice(2, 16));
                int rid = BinaryPrimitives.ReadUInt16LittleEndian(eh[18..]);
                long len = (long)BinaryPrimitives.ReadUInt64LittleEndian(eh[20..]);
                if (user == "LASF_Projection" && rid == 2112 && evlr + 60 + len <= f.Length && CoordinateReferenceSystem.TryParse(Ascii(f.Span(evlr + 60, (int)len)), out var c)) { crs = c; break; }
                evlr += 60 + len;
            }
        }
        long evlrStart = 0; int evlrCount = 0;
        if (major == 1 && minor >= 4 && headerSize >= 375)
        {
            evlrStart = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[235..]);
            evlrCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[243..]);
        }
        DateOnly? date = year > 0 && doy > 0 && doy <= 366 ? DateOnly.FromDateTime(new DateTime(year, 1, 1).AddDays(doy - 1)) : null;
        return new LasInfo(path, major, minor, fmt, recLen, count, compressed,
            D(131), D(139), D(147), D(155), D(163), D(171), D(187), D(203), D(219), D(179), D(195), D(211),
            crs, sys, soft, date, offset, vlrs) { LaszipVlr = laszip, CopcInfoVlr = copc, EvlrStart = evlrStart, EvlrCount = evlrCount };
    }

    internal static LasInfo ReadHeader(string path)
    {
        using var f = new MappedFile(path);
        return ReadHeader(f, path);
    }

    private static string Ascii(ReadOnlySpan<byte> s)
    {
        int z = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(z >= 0 ? s[..z] : s).Trim();
    }

    private static CoordinateReferenceSystem? FromGeoKeys(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        int? projected = null, geographic = null;
        for (int k = 0; k < n && 8 + k * 8 + 8 <= data.Length; k++)
        {
            var e = data.Slice(8 + k * 8, 8);
            int id = BinaryPrimitives.ReadUInt16LittleEndian(e), loc = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]), val = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            if (loc != 0) continue;
            if (id == 3072) projected = val;
            else if (id == 2048) geographic = val;
        }
        int? code = projected is { } p && p != 32767 ? p : geographic is { } g && g != 32767 ? g : null;
        return code is { } c && CrsRegistry.TryGet(c, out var crs) ? crs : null;
    }

    // ================================================================== reading

    /// <summary>Reads a .las or .laz file.</summary>
    public static PointCloud Read(string path, LasReadOptions? options = null)
    {
        options ??= new LasReadOptions();
        LasInfo info;
        using (var f = new MappedFile(path)) info = ReadHeader(f, path);
        if (info.Compressed || path.EndsWith(".laz", StringComparison.OrdinalIgnoreCase)) return Laz.Read(path, info, options);
        using var mf = new MappedFile(path);
        return ReadUncompressed(mf, info, options);
    }

    private static PointCloud ReadUncompressed(MappedFile f, LasInfo info, LasReadOptions opt)
    {
        int rec = CheckFormat(info);
        long n = Math.Min(info.PointCount, (f.Length - info.OffsetToPoints) / rec);
        var b = new Builder(info.PointFormat, (int)Math.Min(n, opt.MaxPoints ?? n));
        const int chunk = 1 << 16;
        for (long start = 0; start < n; start += chunk)
        {
            int m = (int)Math.Min(chunk, n - start);
            if (!DecodeRecords(f.Span(info.OffsetToPoints + start * rec, m * rec), m, start, info, opt, b)) break;
        }
        return b.Build(info);
    }

    internal static int CheckFormat(LasInfo info)
    {
        int fmt = info.PointFormat;
        if (fmt > 10) throw new NotSupportedException($"LAS point format {fmt} is not defined.");
        int rec = info.PointRecordLength;
        if (rec < s_baseLength[fmt]) throw new InvalidDataException($"Point record length {rec} is shorter than format {fmt} requires ({s_baseLength[fmt]}).");
        return rec;
    }

    /// <summary>Decodes raw LAS point records into the builder; returns false once MaxPoints is reached.</summary>
    internal static bool DecodeRecords(ReadOnlySpan<byte> span, int m, long firstIndex, LasInfo info, LasReadOptions opt, Builder b)
    {
        int fmt = info.PointFormat, rec = info.PointRecordLength;
        bool ext = fmt >= 6;
        bool[]? classes = null;
        if (opt.Classes is not null) { classes = new bool[256]; foreach (var c in opt.Classes) classes[c] = true; }
        {
            for (int i = 0; i < m; i++)
            {
                if (opt.Step > 1 && (firstIndex + i) % opt.Step != 0) continue;
                var p = span.Slice(i * rec, rec);
                double x = BinaryPrimitives.ReadInt32LittleEndian(p) * info.ScaleX + info.OffsetX;
                double y = BinaryPrimitives.ReadInt32LittleEndian(p[4..]) * info.ScaleY + info.OffsetY;
                if (opt.Bbox is { } box && !box.Contains(x, y)) continue;
                byte cls = ext ? p[16] : (byte)(p[15] & 0x1F);
                if (classes is not null && !classes[cls]) continue;
                double z = BinaryPrimitives.ReadInt32LittleEndian(p[8..]) * info.ScaleZ + info.OffsetZ;
                ushort intensity = BinaryPrimitives.ReadUInt16LittleEndian(p[12..]);
                byte ret, nret; float angle; byte user; ushort src; double gps = 0; int colorAt = -1, nirAt = -1;
                if (!ext)
                {
                    ret = (byte)(p[14] & 0x07); nret = (byte)((p[14] >> 3) & 0x07);
                    angle = (sbyte)p[16]; user = p[17]; src = BinaryPrimitives.ReadUInt16LittleEndian(p[18..]);
                    if (fmt is 1 or 3 or 4 or 5) gps = BinaryPrimitives.ReadDoubleLittleEndian(p[20..]);
                    if (fmt == 2) colorAt = 20; else if (fmt is 3 or 5) colorAt = 28;
                }
                else
                {
                    ret = (byte)(p[14] & 0x0F); nret = (byte)(p[14] >> 4);
                    user = p[17]; angle = BinaryPrimitives.ReadInt16LittleEndian(p[18..]) * 0.006f;
                    src = BinaryPrimitives.ReadUInt16LittleEndian(p[20..]);
                    gps = BinaryPrimitives.ReadDoubleLittleEndian(p[22..]);
                    if (fmt is 7 or 8 or 10) colorAt = 30;
                    if (fmt is 8 or 10) nirAt = 36;
                }
                ushort r = 0, g = 0, bl = 0, nir = 0;
                if (colorAt >= 0) { r = BinaryPrimitives.ReadUInt16LittleEndian(p[colorAt..]); g = BinaryPrimitives.ReadUInt16LittleEndian(p[(colorAt + 2)..]); bl = BinaryPrimitives.ReadUInt16LittleEndian(p[(colorAt + 4)..]); }
                if (nirAt >= 0) nir = BinaryPrimitives.ReadUInt16LittleEndian(p[nirAt..]);
                b.Add(x, y, z, intensity, ret, nret, cls, angle, user, src, gps, r, g, bl, nir);
                if (opt.MaxPoints is { } mx && b.Count >= mx) return false;
            }
        }
        return true;
    }

    /// <summary>Accumulates decoded points into growable columns.</summary>
    internal sealed class Builder(int format, int capacity)
    {
        private readonly List<double> _x = new(capacity), _y = new(capacity), _z = new(capacity), _t = new();
        private readonly List<ushort> _i = new(capacity), _src = new(capacity), _r = new(), _g = new(), _b = new(), _n = new();
        private readonly List<byte> _ret = new(capacity), _nret = new(capacity), _cls = new(capacity), _user = new(capacity);
        private readonly List<float> _ang = new(capacity);
        private readonly bool _gps = format is 1 or 3 or 4 or 5 or >= 6, _rgb = format is 2 or 3 or 5 or 7 or 8 or 10, _nir = format is 8 or 10;

        public int Count => _x.Count;

        public void Add(double x, double y, double z, ushort intensity, byte ret, byte nret, byte cls, float angle, byte user, ushort src, double gps, ushort r, ushort g, ushort b, ushort nir)
        {
            _x.Add(x); _y.Add(y); _z.Add(z); _i.Add(intensity); _ret.Add(ret); _nret.Add(nret); _cls.Add(cls); _ang.Add(angle); _user.Add(user); _src.Add(src);
            if (_gps) _t.Add(gps);
            if (_rgb) { _r.Add(r); _g.Add(g); _b.Add(b); }
            if (_nir) _n.Add(nir);
        }

        public PointCloud Build(LasInfo info) => new([.. _x], [.. _y], [.. _z], info.Crs)
        {
            Intensity = [.. _i], ReturnNumber = [.. _ret], NumberOfReturns = [.. _nret], Classification = [.. _cls], ScanAngle = [.. _ang],
            UserData = [.. _user], PointSourceId = [.. _src],
            GpsTime = _gps ? [.. _t] : null, Red = _rgb ? [.. _r] : null, Green = _rgb ? [.. _g] : null, Blue = _rgb ? [.. _b] : null, Nir = _nir ? [.. _n] : null,
            Source = info
        };
    }

    // ================================================================== writing

    /// <summary>
    /// Writes LAS (or LAZ when the path ends in .laz). The point format is chosen from the attributes present
    /// (0/1/2/3 → LAS 1.2, 6/7/8 → LAS 1.4) unless <paramref name="pointFormat"/> is given.
    /// Coordinates are stored with a 1 mm scale (0.001) relative to the cloud minimum.
    /// </summary>
    public static void Write(PointCloud pc, string path, int? pointFormat = null, double scale = 0.001)
    {
        int fmt = pointFormat ?? (pc.Nir is not null ? 8 : (pc.GpsTime, pc.HasColor) switch { (null, false) => 0, (not null, false) => 1, (null, true) => 2, _ => 3 });
        if (fmt is 4 or 5 or 9 or 10) throw new NotSupportedException("Waveform point formats (4, 5, 9, 10) cannot be written.");
        if (path.EndsWith(".laz", StringComparison.OrdinalIgnoreCase)) { Laz.Write(pc, path, fmt > 5 ? (pc.HasColor ? 3 : 1) : fmt, scale); return; }

        var b = pc.Bounds;
        double ox = pc.Count == 0 ? 0 : Math.Floor(b.MinX), oy = pc.Count == 0 ? 0 : Math.Floor(b.MinY), oz = pc.Count == 0 ? 0 : Math.Floor(b.MinZ);
        bool v14 = fmt >= 6;
        int headerSize = v14 ? 375 : 227;
        var vlrs = new List<(string User, ushort Id, string Desc, byte[] Data)>();
        if (pc.Crs is { } crs)
        {
            if (v14) vlrs.Add(("LASF_Projection", 2112, "OGC WKT", Encoding.ASCII.GetBytes(crs.ToWkt() + "\0")));
            if (crs.Epsg is { } code)
            {
                var keys = new List<ushort> { 1, 1, 0, 2, 1024, 0, 1, (ushort)(crs.IsGeographic ? 2 : 1) };
                keys.AddRange([(ushort)(crs.IsGeographic ? 2048 : 3072), 0, 1, (ushort)code]);
                vlrs.Add(("LASF_Projection", 34735, "GeoKeyDirectoryTag", keys.SelectMany(k => BitConverter.GetBytes(k)).ToArray()));
            }
        }
        int rec = s_baseLength[fmt];
        uint offset = (uint)(headerSize + vlrs.Sum(v => 54 + v.Data.Length));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var h = new byte[headerSize];
        "LASF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), (ushort)(v14 ? 0x10 : 0)); // WKT bit for 1.4
        h[24] = 1; h[25] = (byte)(v14 ? 4 : 2);
        Encoding.ASCII.GetBytes("GeoPandas.Net").CopyTo(h, 26);
        Encoding.ASCII.GetBytes("GeoPandas.Net (Gravicode Studios)").AsSpan(0, 32).CopyTo(h.AsSpan(58));
        var now = DateTime.UtcNow;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(90), (ushort)now.DayOfYear);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(92), (ushort)now.Year);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(94), (ushort)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(96), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(100), (uint)vlrs.Count);
        h[104] = (byte)fmt;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(105), (ushort)rec);
        var byReturn = new long[15];
        for (int i = 0; i < pc.Count; i++) { int r = pc.ReturnNumber[i]; if (r >= 1 && r <= 15) byReturn[r - 1]++; }
        if (!v14 || (long)pc.Count <= uint.MaxValue)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(107), (uint)pc.Count);
            for (int k = 0; k < 5; k++) BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(111 + 4 * k), (uint)byReturn[k]);
        }
        foreach (var (o, v) in new[] { (131, scale), (139, scale), (147, scale), (155, ox), (163, oy), (171, oz),
                     (179, b.MaxX), (187, b.MinX), (195, b.MaxY), (203, b.MinY), (211, b.MaxZ), (219, b.MinZ) })
            BinaryPrimitives.WriteDoubleLittleEndian(h.AsSpan(o), pc.Count == 0 ? 0 : v);
        if (v14)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(247), (ulong)pc.Count);
            for (int k = 0; k < 15; k++) BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(255 + 8 * k), (ulong)byReturn[k]);
        }
        fs.Write(h);
        foreach (var (user, id, desc, data) in vlrs)
        {
            var vh = new byte[54];
            Encoding.ASCII.GetBytes(user).CopyTo(vh, 2);
            BinaryPrimitives.WriteUInt16LittleEndian(vh.AsSpan(18), id);
            BinaryPrimitives.WriteUInt16LittleEndian(vh.AsSpan(20), (ushort)data.Length);
            Encoding.ASCII.GetBytes(desc).CopyTo(vh, 22);
            fs.Write(vh); fs.Write(data);
        }
        var p = new byte[rec];
        for (int i = 0; i < pc.Count; i++)
        {
            Array.Clear(p);
            BinaryPrimitives.WriteInt32LittleEndian(p, (int)Math.Round((pc.X[i] - ox) / scale));
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), (int)Math.Round((pc.Y[i] - oy) / scale));
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(8), (int)Math.Round((pc.Z[i] - oz) / scale));
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(12), pc.Intensity[i]);
            int colorAt = -1;
            if (!v14)
            {
                p[14] = (byte)((pc.ReturnNumber[i] & 0x07) | ((pc.NumberOfReturns[i] & 0x07) << 3));
                p[15] = (byte)(pc.Classification[i] & 0x1F);
                p[16] = (byte)(sbyte)Math.Clamp(Math.Round(pc.ScanAngle[i]), -90, 90);
                p[17] = pc.UserData[i];
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(18), pc.PointSourceId[i]);
                if (fmt is 1 or 3) BinaryPrimitives.WriteDoubleLittleEndian(p.AsSpan(20), pc.GpsTime?[i] ?? 0);
                colorAt = fmt == 2 ? 20 : fmt == 3 ? 28 : -1;
            }
            else
            {
                p[14] = (byte)((pc.ReturnNumber[i] & 0x0F) | ((pc.NumberOfReturns[i] & 0x0F) << 4));
                p[16] = pc.Classification[i];
                p[17] = pc.UserData[i];
                BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(18), (short)Math.Round(pc.ScanAngle[i] / 0.006f));
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(20), pc.PointSourceId[i]);
                BinaryPrimitives.WriteDoubleLittleEndian(p.AsSpan(22), pc.GpsTime?[i] ?? 0);
                colorAt = fmt is 7 or 8 ? 30 : -1;
                if (fmt == 8) BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(36), pc.Nir?[i] ?? 0);
            }
            if (colorAt >= 0 && pc.HasColor)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(colorAt), pc.Red![i]);
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(colorAt + 2), pc.Green![i]);
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(colorAt + 4), pc.Blue![i]);
            }
            fs.Write(p);
        }
    }
}
