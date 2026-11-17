using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using GeoPandas.Net.Data.FlatGeobufInternal;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.IO;
using GeoPandas.Net.Lazy;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Data;

/// <summary>Options for <see cref="FlatGeobuf.Write(GeoDataFrame, string, FlatGeobufWriteOptions?)"/>.</summary>
public sealed record FlatGeobufWriteOptions
{
    /// <summary>Write a packed Hilbert R-tree. Rows are then stored in Hilbert order (as GDAL does); set false to keep row order.</summary>
    public bool SpatialIndex { get; init; } = true;
    /// <summary>R-tree node fan-out (FlatGeobuf default 16).</summary>
    public ushort NodeSize { get; init; } = 16;
    public string? LayerName { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    /// <summary>Free-form metadata string stored in the header (JSON recommended).</summary>
    public string? Metadata { get; init; }
}

/// <summary>Header summary of a FlatGeobuf file.</summary>
public sealed record FlatGeobufInfo(
    string? Name, ulong FeatureCount, string GeometryType, bool HasZ, Envelope? Envelope, ushort IndexNodeSize,
    CoordinateReferenceSystem? Crs, IReadOnlyList<(string Name, string Type)> Columns, string? Title, string? Description, string? Metadata,
    long HeaderSize, long IndexSize)
{
    public bool HasIndex => IndexNodeSize > 0 && IndexSize > 0;
}

/// <summary>
/// FlatGeobuf (.fgb) reader and writer: FlatBuffers features plus an optional packed Hilbert R-tree for fast bounding-box reads.
/// Reading is memory-mapped: a bbox query touches only the index pages and the matching features.
/// Interoperable with GDAL/OGR, QGIS, pyogrio/GeoPandas and the flatgeobuf JS/TS/Rust libraries.
/// </summary>
public static class FlatGeobuf
{
    internal static readonly byte[] Magic = [0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x00];

    internal enum GType : byte { Unknown = 0, Point, LineString, Polygon, MultiPoint, MultiLineString, MultiPolygon, GeometryCollection }
    internal enum CType : byte { Byte, UByte, Bool, Short, UShort, Int, UInt, Long, ULong, Float, Double, String, Json, DateTime, Binary }

    // ================================================================== reading

    public static GeoDataFrame Read(string path, Envelope? bbox = null, IReadOnlyList<string>? columns = null, long? limit = null)
    {
        using var f = new MappedFile(path);
        var src = new MappedBytes(f);
        return Concat(ReadCore(src, bbox, columns, limit, int.MaxValue), src);
    }

    public static GeoDataFrame Read(byte[] data, Envelope? bbox = null, IReadOnlyList<string>? columns = null, long? limit = null)
    {
        var src = new ArrayBytes(data);
        return Concat(ReadCore(src, bbox, columns, limit, int.MaxValue), src);
    }

    /// <summary>Streams the file in batches (memory-mapped; the file stays open while enumerating).</summary>
    public static IEnumerable<GeoDataFrame> ReadBatches(string path, Envelope? bbox = null, IReadOnlyList<string>? columns = null, long? limit = null, int batchSize = 65_536)
    {
        using var f = new MappedFile(path);
        foreach (var b in ReadCore(new MappedBytes(f), bbox, columns, limit, batchSize)) yield return b;
    }

    public static FlatGeobufInfo ReadInfo(string path)
    {
        using var f = new MappedFile(path);
        return ReadHeader(new MappedBytes(f)).Info;
    }

    /// <summary>Lazy, pushdown-aware source: bbox via the R-tree, column pruning and limits.</summary>
    public static IGeoScanSource ScanSource(string path) => new FlatGeobufScanSource(path);

    private static GeoDataFrame Concat(IEnumerable<GeoDataFrame> batches, IBytes src)
    {
        var list = batches.ToList();
        if (list.Count == 1) return list[0];
        if (list.Count > 1) return GeoDataFrame.Concat(list.ToArray());
        var h = ReadHeader(src);
        return new RowBuilder(h, null).Build();
    }

    internal sealed class HeaderData
    {
        public required FlatGeobufInfo Info { get; init; }
        public required ColumnDef[] Columns { get; init; }
        public GType GeometryType { get; init; }
        public bool HasZ { get; init; }
        public long FeaturesStart { get; init; }
        public long IndexStart { get; init; }
    }

    internal readonly record struct ColumnDef(string Name, CType Type);

    internal static HeaderData ReadHeader(IBytes src)
    {
        if (src.Length < 12) throw new InvalidDataException("Not a FlatGeobuf file (too short).");
        var magic = src.Span(0, 8);
        if (magic[0] != 0x66 || magic[1] != 0x67 || magic[2] != 0x62 || magic[4] != 0x66 || magic[5] != 0x67 || magic[6] != 0x62)
            throw new InvalidDataException("Not a FlatGeobuf file (bad magic bytes).");
        if (magic[3] != 3) throw new NotSupportedException($"FlatGeobuf major version {magic[3]} is not supported (expected 3).");
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Span(8, 4));
        var hb = src.Span(12, headerSize);
        var h = FbTable.Root(hb);

        string? name = h.GetString(0);
        Envelope? env = null;
        var envBytes = h.GetVectorBytes(1, 8, out int envCount);
        if (envCount >= 4)
            env = new Envelope(Rd(envBytes, 0), Rd(envBytes, 2), Rd(envBytes, 1), Rd(envBytes, 3));
        var gtype = (GType)h.GetByte(2);
        bool hasZ = h.GetBool(3);
        int ncols = h.GetVectorLength(7);
        var cols = new ColumnDef[ncols];
        for (int i = 0; i < ncols; i++)
        {
            var c = h.GetTableElement(7, i);
            cols[i] = new ColumnDef(c.GetString(0) ?? $"column{i}", (CType)c.GetByte(1));
        }
        ulong count = h.GetULong(8);
        ushort nodeSize = h.GetUShort(9, 16);
        CoordinateReferenceSystem? crs = null;
        if (h.TryGetTable(10, out var ct))
        {
            string? org = ct.GetString(0);
            int code = ct.GetInt(1);
            string? wkt = ct.GetString(4);
            if (code > 0 && (org is null || org.Equals("EPSG", StringComparison.OrdinalIgnoreCase)) && CrsRegistry.TryGet(code, out var known)) crs = known;
            else if (wkt is not null && CoordinateReferenceSystem.TryParse(wkt, out var parsed)) crs = parsed;
            else if (code > 0) crs = CoordinateReferenceSystem.TryParse($"EPSG:{code}", out var p2) ? p2 : null;
        }
        long indexSize = PackedRTree.Size(count, nodeSize);
        long indexStart = 12 + headerSize;
        var info = new FlatGeobufInfo(name, count, gtype.ToString(), hasZ, env, nodeSize, crs,
            cols.Select(c => (c.Name, c.Type.ToString())).ToArray(), h.GetString(11), h.GetString(12), h.GetString(13), headerSize, indexSize);
        return new HeaderData
        {
            Info = info, Columns = cols, GeometryType = gtype, HasZ = hasZ,
            IndexStart = indexStart, FeaturesStart = indexStart + indexSize
        };
    }

    private static double Rd(ReadOnlySpan<byte> b, int i) => BinaryPrimitives.ReadDoubleLittleEndian(b[(i * 8)..]);

    internal static IEnumerable<GeoDataFrame> ReadCore(IBytes src, Envelope? bbox, IReadOnlyList<string>? columns, long? limit, int batchSize)
    {
        var h = ReadHeader(src);
        var want = columns is null ? null : new HashSet<string>(columns);
        long max = limit ?? long.MaxValue;
        if (max <= 0) yield break;

        IEnumerable<long> offsets;
        bool needEnvelopeCheck = false;
        if (bbox is not null && h.Info.HasIndex && h.Info.FeatureCount > 0)
        {
            long idx = h.IndexStart;
            var hits = PackedRTree.Search(h.Info.FeatureCount, h.Info.IndexNodeSize, bbox,
                i => PackedRTree.NodeItem.Read(src.Span(idx + i * PackedRTree.NodeItemSize, PackedRTree.NodeItemSize)));
            offsets = hits.Select(x => h.FeaturesStart + (long)x.Offset);
        }
        else
        {
            offsets = Sequential(src, h.FeaturesStart);
            needEnvelopeCheck = bbox is not null;
        }

        var rb = new RowBuilder(h, want);
        long produced = 0;
        foreach (var off in offsets)
        {
            if (!rb.AddFeatureAt(src, off, needEnvelopeCheck ? bbox : null)) continue;
            produced++;
            if (rb.Count >= batchSize) { yield return rb.Build(); rb = new RowBuilder(h, want); }
            if (produced >= max) break;
        }
        if (rb.Count > 0) yield return rb.Build();
    }

    private static IEnumerable<long> Sequential(IBytes src, long start)
    {
        long pos = start;
        while (pos + 4 <= src.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(src.Span(pos, 4));
            if (size == 0 || pos + 4 + size > src.Length) yield break;
            yield return pos;
            pos += 4 + size;
        }
    }

    /// <summary>Accumulates decoded features into typed column buffers.</summary>
    internal sealed class RowBuilder
    {
        private readonly HeaderData _h;
        private readonly bool[] _keep;
        private readonly List<object?>[] _values;
        private readonly List<NtsGeometry?> _geoms = [];

        public RowBuilder(HeaderData h, HashSet<string>? want)
        {
            _h = h;
            _keep = h.Columns.Select(c => want is null || want.Contains(c.Name)).ToArray();
            _values = h.Columns.Select(_ => new List<object?>()).ToArray();
        }

        public int Count => _geoms.Count;

        public bool AddFeatureAt(IBytes src, long off, Envelope? check)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Span(off, 4));
            return AddFeature(src.Span(off + 4, size), check);
        }

        public bool AddFeature(ReadOnlySpan<byte> fb, Envelope? check)
        {
            var f = FbTable.Root(fb);
            NtsGeometry? g = f.TryGetTable(0, out var gt) ? ReadGeometry(gt, _h.GeometryType, _h.HasZ) : null;
            if (check is not null && (g is null || !g.EnvelopeInternal.Intersects(check))) return false;
            _geoms.Add(g);
            int row = _geoms.Count - 1;
            foreach (var v in _values) v.Add(null);
            var props = f.GetVectorBytes(1, 1, out int plen);
            int p = 0;
            while (p + 2 <= plen)
            {
                int ci = BinaryPrimitives.ReadUInt16LittleEndian(props[p..]);
                p += 2;
                if (ci >= _h.Columns.Length) throw new InvalidDataException($"Property refers to column {ci}, header has {_h.Columns.Length}.");
                var type = _h.Columns[ci].Type;
                object? val = ReadValue(props, ref p, type);
                if (_keep[ci]) _values[ci][row] = val;
            }
            return true;
        }

        public GeoDataFrame Build()
        {
            var cols = new List<Column>();
            for (int i = 0; i < _h.Columns.Length; i++)
            {
                if (!_keep[i]) continue;
                cols.Add(MakeColumn(_h.Columns[i], _values[i]));
            }
            cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, _geoms.ToArray(), _h.Info.Crs));
            return new GeoDataFrame(cols);
        }

        private static Column MakeColumn(ColumnDef def, List<object?> v) => def.Type switch
        {
            CType.Bool => Column<bool>.FromNullable(def.Name, v.Select(x => (bool?)x).ToArray()),
            CType.Byte or CType.UByte or CType.Short or CType.UShort or CType.Int or CType.UInt or CType.Long =>
                Column<long>.FromNullable(def.Name, v.Select(x => x is null ? (long?)null : Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToArray()),
            CType.ULong => v.All(x => x is null || (ulong)x <= long.MaxValue)
                ? Column<long>.FromNullable(def.Name, v.Select(x => x is null ? (long?)null : (long)(ulong)x).ToArray())
                : Column<double>.FromNullable(def.Name, v.Select(x => x is null ? (double?)null : (ulong)x).ToArray()),
            CType.Float or CType.Double => Column<double>.FromNullable(def.Name, v.Select(x => x is null ? (double?)null : Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray()),
            CType.DateTime => Column<DateTime>.FromNullable(def.Name, v.Select(x => x as DateTime?).ToArray()),
            CType.Binary => new Column<byte[]?>(def.Name, v.Select(x => x as byte[]).ToArray()),
            _ => new Column<string?>(def.Name, v.Select(x => x as string).ToArray()),
        };
    }

    private static object? ReadValue(ReadOnlySpan<byte> b, ref int p, CType type)
    {
        object? v;
        switch (type)
        {
            case CType.Byte: v = (long)(sbyte)b[p]; p += 1; break;
            case CType.UByte: v = (long)b[p]; p += 1; break;
            case CType.Bool: v = b[p] != 0; p += 1; break;
            case CType.Short: v = (long)BinaryPrimitives.ReadInt16LittleEndian(b[p..]); p += 2; break;
            case CType.UShort: v = (long)BinaryPrimitives.ReadUInt16LittleEndian(b[p..]); p += 2; break;
            case CType.Int: v = (long)BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4; break;
            case CType.UInt: v = (long)BinaryPrimitives.ReadUInt32LittleEndian(b[p..]); p += 4; break;
            case CType.Long: v = BinaryPrimitives.ReadInt64LittleEndian(b[p..]); p += 8; break;
            case CType.ULong: v = BinaryPrimitives.ReadUInt64LittleEndian(b[p..]); p += 8; break;
            case CType.Float: v = (double)BinaryPrimitives.ReadSingleLittleEndian(b[p..]); p += 4; break;
            case CType.Double: v = BinaryPrimitives.ReadDoubleLittleEndian(b[p..]); p += 8; break;
            case CType.String or CType.Json or CType.DateTime or CType.Binary:
            {
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
                p += 4;
                var bytes = b.Slice(p, len);
                p += len;
                if (type == CType.Binary) return bytes.ToArray();
                var s = Encoding.UTF8.GetString(bytes);
                if (type == CType.DateTime)
                    return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
                return s;
            }
            default: throw new InvalidDataException($"Unknown FlatGeobuf column type {(int)type}.");
        }
        return v;
    }

    private static NtsGeometry ReadGeometry(FbTable g, GType declared, bool hasZ)
    {
        var type = declared == GType.Unknown ? (GType)g.GetByte(6) : declared;
        var f = Geo.Factory;
        switch (type)
        {
            case GType.MultiPolygon:
            {
                int n = g.GetVectorLength(7);
                var polys = new Polygon[n];
                for (int i = 0; i < n; i++) polys[i] = (Polygon)ReadGeometry(g.GetTableElement(7, i), GType.Polygon, hasZ);
                return f.CreateMultiPolygon(polys);
            }
            case GType.GeometryCollection:
            {
                int n = g.GetVectorLength(7);
                var parts = new NtsGeometry[n];
                for (int i = 0; i < n; i++) parts[i] = ReadGeometry(g.GetTableElement(7, i), GType.Unknown, hasZ);
                return f.CreateGeometryCollection(parts);
            }
        }
        var xy = g.GetVectorBytes(1, 8, out int nxy);
        int np = nxy / 2;
        var zb = g.GetVectorBytes(2, 8, out int nz);
        bool z = nz == np && nz > 0;
        var all = new Coordinate[np];
        for (int i = 0; i < np; i++)
        {
            double x = BinaryPrimitives.ReadDoubleLittleEndian(xy[(i * 16)..]);
            double y = BinaryPrimitives.ReadDoubleLittleEndian(xy[(i * 16 + 8)..]);
            all[i] = z ? new CoordinateZ(x, y, BinaryPrimitives.ReadDoubleLittleEndian(zb[(i * 8)..])) : new Coordinate(x, y);
        }
        Coordinate[] Coords(int start, int end) => all[start..end];
        var endsBytes = g.GetVectorBytes(0, 4, out int ne);
        uint[] ends = new uint[ne];
        for (int i = 0; i < ne; i++) ends[i] = BinaryPrimitives.ReadUInt32LittleEndian(endsBytes[(i * 4)..]);
        IEnumerable<(int, int)> Parts()
        {
            if (ne == 0) { yield return (0, np); yield break; }
            int s = 0;
            foreach (var e in ends) { yield return (s, (int)e); s = (int)e; }
        }
        switch (type)
        {
            case GType.Point: return np == 0 ? f.CreatePoint() : f.CreatePoint(Coords(0, 1)[0]);
            case GType.MultiPoint: return f.CreateMultiPointFromCoords(Coords(0, np));
            case GType.LineString: return f.CreateLineString(Coords(0, np));
            case GType.MultiLineString: return f.CreateMultiLineString(Parts().Select(p => f.CreateLineString(Coords(p.Item1, p.Item2))).ToArray());
            case GType.Polygon:
            {
                if (np == 0) return f.CreatePolygon();
                var rings = Parts().Select(p => f.CreateLinearRing(Coords(p.Item1, p.Item2))).ToArray();
                return f.CreatePolygon(rings[0], rings[1..]);
            }
            default: throw new NotSupportedException($"FlatGeobuf geometry type {type} is not supported (curves, TIN and polyhedral surfaces are not).");
        }
    }

    // ================================================================== writing

    public static void Write(GeoDataFrame gdf, string path, FlatGeobufWriteOptions? options = null)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Write(gdf, fs, options);
    }

    public static byte[] WriteBytes(GeoDataFrame gdf, FlatGeobufWriteOptions? options = null)
    {
        using var ms = new MemoryStream();
        Write(gdf, ms, options);
        return ms.ToArray();
    }

    public static void Write(GeoDataFrame gdf, Stream output, FlatGeobufWriteOptions? options = null)
    {
        options ??= new FlatGeobufWriteOptions();
        var geoms = gdf.Geometry;
        int n = gdf.RowCount;
        var attrs = gdf.Columns.Where(c => c.Name != gdf.GeometryColumnName && c is not GeoSeries).ToArray();
        var defs = attrs.Select(c => new ColumnDef(c.Name, ColumnTypeOf(c))).ToArray();
        var gtype = HeaderGeometryType(geoms);
        bool hasZ = geoms.Any(g => g is not null && Geo.HasZ(g));

        var envs = new Envelope?[n];
        var extent = new Envelope();
        for (int i = 0; i < n; i++)
            if (geoms[i] is { IsEmpty: false } g) { envs[i] = g.EnvelopeInternal; extent.ExpandToInclude(envs[i]!); }

        bool index = options.SpatialIndex && n > 0 && options.NodeSize >= 2;
        int[] order = index ? PackedRTree.HilbertOrder(envs, extent) : Enumerable.Range(0, n).ToArray();

        // serialise features first (their sizes give the leaf offsets)
        var b = new FbBuilder(4096);
        var featureBytes = new List<byte[]>(n);
        var offsets = new ulong[n];
        ulong pos = 0;
        for (int k = 0; k < n; k++)
        {
            int row = order[k];
            b.Clear();
            int geomOff = geoms[row] is { } g ? WriteGeometry(b, g, gtype, hasZ, 0) : 0;
            var props = EncodeProperties(attrs, defs, row);
            int propsOff = props.Length > 0 ? b.CreateByteVector(props) : 0;
            b.StartTable(3);
            b.AddFieldOffset(1, propsOff);
            b.AddFieldOffset(0, geomOff);
            var body = b.Finish(b.EndTable());
            var fb = new byte[body.Length + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(fb, (uint)body.Length);
            body.CopyTo(fb.AsSpan(4));
            featureBytes.Add(fb);
            offsets[k] = pos;
            pos += (ulong)fb.Length;
        }

        // header
        b.Clear();
        var colOffs = new int[defs.Length];
        for (int i = 0; i < defs.Length; i++)
        {
            int nameOff = b.CreateString(defs[i].Name);
            b.StartTable(11);
            b.AddFieldOffset(0, nameOff);
            b.AddFieldByte(1, (byte)defs[i].Type, 0);
            colOffs[i] = b.EndTable();
        }
        int colsVec = defs.Length > 0 ? b.CreateOffsetVector(colOffs) : 0;
        int envVec = extent.IsNull ? 0 : b.CreateDoubleVector([extent.MinX, extent.MinY, extent.MaxX, extent.MaxY]);
        int nameStr = b.CreateString(options.LayerName ?? "layer");
        int titleStr = options.Title is null ? 0 : b.CreateString(options.Title);
        int descStr = options.Description is null ? 0 : b.CreateString(options.Description);
        int metaStr = options.Metadata is null ? 0 : b.CreateString(options.Metadata);
        int crsOff = 0;
        if (gdf.Crs is { } crs)
        {
            int wktOff = b.CreateString(crs.ToWkt());
            int orgOff = crs.Epsg is not null ? b.CreateString("EPSG") : 0;
            int crsName = b.CreateString(crs.Name);
            b.StartTable(6);
            b.AddFieldOffset(0, orgOff);
            b.AddFieldInt(1, crs.Epsg ?? 0, 0);
            b.AddFieldOffset(2, crsName);
            b.AddFieldOffset(4, wktOff);
            crsOff = b.EndTable();
        }
        b.StartTable(14);
        b.AddFieldULong(8, (ulong)n, 0);
        b.AddFieldOffset(0, nameStr);
        b.AddFieldOffset(1, envVec);
        b.AddFieldOffset(7, colsVec);
        b.AddFieldOffset(10, crsOff);
        b.AddFieldOffset(11, titleStr);
        b.AddFieldOffset(12, descStr);
        b.AddFieldOffset(13, metaStr);
        b.AddFieldUShort(9, index ? options.NodeSize : (ushort)0, 16);
        b.AddFieldByte(2, (byte)gtype, 0);
        b.AddFieldBool(3, hasZ, false);
        var header = b.Finish(b.EndTable());

        output.Write(Magic);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)header.Length);
        output.Write(len);
        output.Write(header);
        if (index)
        {
            var leaves = order.Select(i => envs[i]).ToArray();
            output.Write(PackedRTree.Build(leaves, offsets, options.NodeSize));
        }
        foreach (var fb in featureBytes) output.Write(fb);
    }

    private static CType ColumnTypeOf(Column c)
    {
        var t = c.DataType;
        if (t == typeof(bool)) return CType.Bool;
        if (t == typeof(int)) return CType.Int;
        if (t == typeof(long)) return CType.Long;
        if (t == typeof(short)) return CType.Short;
        if (t == typeof(byte)) return CType.UByte;
        if (t == typeof(float)) return CType.Float;
        if (t == typeof(double)) return CType.Double;
        if (t == typeof(DateTime)) return CType.DateTime;
        if (t == typeof(byte[])) return CType.Binary;
        return CType.String;
    }

    private static byte[] EncodeProperties(Column[] attrs, ColumnDef[] defs, int row)
    {
        var ms = new List<byte>(64);
        Span<byte> tmp = stackalloc byte[8];
        for (int i = 0; i < attrs.Length; i++)
        {
            var v = attrs[i].GetValue(row);
            if (v is null || v is double d && double.IsNaN(d) || v is float fl && float.IsNaN(fl)) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(tmp, (ushort)i);
            ms.Add(tmp[0]); ms.Add(tmp[1]);
            switch (defs[i].Type)
            {
                case CType.Bool: ms.Add((bool)v ? (byte)1 : (byte)0); break;
                case CType.UByte: ms.Add((byte)v); break;
                case CType.Short: BinaryPrimitives.WriteInt16LittleEndian(tmp, (short)v); ms.AddRange(tmp[..2]); break;
                case CType.Int: BinaryPrimitives.WriteInt32LittleEndian(tmp, (int)v); ms.AddRange(tmp[..4]); break;
                case CType.Long: BinaryPrimitives.WriteInt64LittleEndian(tmp, (long)v); ms.AddRange(tmp[..8]); break;
                case CType.Float: BinaryPrimitives.WriteSingleLittleEndian(tmp, (float)v); ms.AddRange(tmp[..4]); break;
                case CType.Double: BinaryPrimitives.WriteDoubleLittleEndian(tmp, (double)v); ms.AddRange(tmp[..8]); break;
                default:
                {
                    byte[] bytes = v switch
                    {
                        byte[] raw => raw,
                        // GDAL cannot parse numeric offsets here: local times are stored as UTC ("Z"), unspecified ones without a suffix
                        DateTime dt => Encoding.UTF8.GetBytes(dt.Kind == DateTimeKind.Unspecified
                            ? dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)
                            : dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "Z"),
                        IFormattable fm => Encoding.UTF8.GetBytes(fm.ToString(null, CultureInfo.InvariantCulture)),
                        _ => Encoding.UTF8.GetBytes(v.ToString() ?? "")
                    };
                    BinaryPrimitives.WriteUInt32LittleEndian(tmp, (uint)bytes.Length);
                    ms.AddRange(tmp[..4]);
                    ms.AddRange(bytes);
                    break;
                }
            }
        }
        return ms.ToArray();
    }

    private static GType TypeOf(NtsGeometry g) => g switch
    {
        Point => GType.Point,
        LineString => GType.LineString,
        Polygon => GType.Polygon,
        MultiPoint => GType.MultiPoint,
        MultiLineString => GType.MultiLineString,
        MultiPolygon => GType.MultiPolygon,
        GeometryCollection => GType.GeometryCollection,
        _ => GType.Unknown
    };

    private static GType HeaderGeometryType(GeoSeries s)
    {
        GType? t = null;
        foreach (var g in s)
        {
            if (g is null) continue;
            var k = TypeOf(g);
            if (t is null) t = k;
            else if (t != k) return GType.Unknown;
        }
        return t ?? GType.Unknown;
    }

    private static int WriteGeometry(FbBuilder b, NtsGeometry g, GType header, bool hasZ, int depth)
    {
        var type = TypeOf(g);
        bool writeType = depth > 0 || header == GType.Unknown;
        int partsOff = 0;
        if (g is MultiPolygon or GeometryCollection && g is not MultiPoint and not MultiLineString)
        {
            var parts = new int[g.NumGeometries];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = WriteGeometry(b, g.GetGeometryN(i), g is MultiPolygon ? GType.Polygon : GType.Unknown, hasZ, depth + 1);
            partsOff = b.CreateOffsetVector(parts);
            b.StartTable(8);
            b.AddFieldOffset(7, partsOff);
            if (writeType) b.AddFieldByte(6, (byte)type, 0);
            return b.EndTable();
        }

        var coords = new List<Coordinate>();
        List<uint>? ends = null;
        switch (g)
        {
            case Point p: if (!p.IsEmpty) coords.Add(p.Coordinate); break;
            case LineString l: coords.AddRange(l.Coordinates); break;
            case MultiPoint mp: coords.AddRange(mp.Coordinates); break;
            case Polygon poly:
                if (!poly.IsEmpty)
                {
                    coords.AddRange(poly.ExteriorRing.Coordinates);
                    if (poly.NumInteriorRings > 0)
                    {
                        ends = [(uint)coords.Count];
                        foreach (var hole in poly.InteriorRings) { coords.AddRange(hole.Coordinates); ends.Add((uint)coords.Count); }
                    }
                }
                break;
            case MultiLineString ml:
                if (ml.NumGeometries > 1) ends = [];
                foreach (var line in ml.Geometries) { coords.AddRange(line.Coordinates); ends?.Add((uint)coords.Count); }
                break;
        }
        var xy = new double[coords.Count * 2];
        for (int i = 0; i < coords.Count; i++) { xy[2 * i] = coords[i].X; xy[2 * i + 1] = coords[i].Y; }
        int zOff = 0;
        if (hasZ)
        {
            var z = coords.Select(c => double.IsNaN(c.Z) ? 0 : c.Z).ToArray();
            zOff = b.CreateDoubleVector(z);
        }
        int xyOff = b.CreateDoubleVector(xy);
        int endsOff = ends is { Count: > 0 } ? b.CreateUIntVector(ends.ToArray()) : 0;
        b.StartTable(8);
        b.AddFieldOffset(0, endsOff);
        b.AddFieldOffset(1, xyOff);
        b.AddFieldOffset(2, zOff);
        if (writeType) b.AddFieldByte(6, (byte)type, 0);
        return b.EndTable();
    }

    // ================================================================== byte sources

    internal interface IBytes
    {
        long Length { get; }
        ReadOnlySpan<byte> Span(long offset, int length);
    }

    internal sealed class MappedBytes(MappedFile f) : IBytes
    {
        public long Length => f.Length;
        public ReadOnlySpan<byte> Span(long offset, int length) => f.Span(offset, length);
    }

    internal sealed class ArrayBytes(byte[] a) : IBytes
    {
        public long Length => a.Length;
        public ReadOnlySpan<byte> Span(long offset, int length) => a.AsSpan((int)offset, length);
    }
}

/// <summary>Lazy scan source over a FlatGeobuf file (exact bbox via the packed R-tree, column pruning, limit).</summary>
public sealed class FlatGeobufScanSource : IGeoScanSource
{
    private readonly string _path;
    private readonly FlatGeobufInfo _info;

    public FlatGeobufScanSource(string path)
    {
        _path = path;
        _info = FlatGeobuf.ReadInfo(path);
    }

    public FlatGeobufInfo Info => _info;
    public string Name => $"FlatGeobuf '{Path.GetFileName(_path)}' ({_info.FeatureCount} features, {(_info.HasIndex ? $"Hilbert R-tree node size {_info.IndexNodeSize}" : "no index")})";
    public CoordinateReferenceSystem? Crs => _info.Crs;
    public IReadOnlyList<string> ColumnNames => _info.Columns.Select(c => c.Name).ToArray();
    public long? RowCountHint => (long)_info.FeatureCount;
    public ScanPushdown Pushdown => ScanPushdown.Bbox | ScanPushdown.ExactBbox | ScanPushdown.Columns | ScanPushdown.Limit;

    public IEnumerable<GeoDataFrame> Scan(ScanRequest request) =>
        FlatGeobuf.ReadBatches(_path, request.Bbox, request.Columns, request.Limit, request.BatchSize);
}
