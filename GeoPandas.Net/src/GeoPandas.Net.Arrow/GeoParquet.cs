using System.Globalization;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Lazy;
using NetTopologySuite.Geometries;
using Parquet;
using Parquet.Schema;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Arrow;

/// <summary>Options for writing GeoParquet.</summary>
public sealed record GeoParquetWriteOptions
{
    /// <summary>Rows per row group (the unit of bbox pruning).</summary>
    public int RowGroupSize { get; init; } = 65_536;
    /// <summary>Add the GeoParquet 1.1 <c>bbox</c> covering struct column (xmin, ymin, xmax, ymax) used for spatial row-group pruning.</summary>
    public bool BboxCovering { get; init; } = true;
    public CompressionMethod Compression { get; init; } = CompressionMethod.Snappy;
    /// <summary>WKB (default, any geometry) or native GeoArrow (one geometry family; faster, columnar coordinates).</summary>
    public GeoParquetEncoding Encoding { get; init; } = GeoParquetEncoding.Wkb;
}

/// <summary>
/// GeoParquet 1.1 reader and writer (WKB geometry, PROJJSON CRS, bbox covering column). Interoperable with
/// GeoPandas <c>to_parquet/read_parquet</c>, GDAL, DuckDB Spatial, Apache Sedona and BigQuery.
/// Reads prune row groups with the bbox covering statistics and attribute statistics, and skip unrequested columns.
/// </summary>
public static class GeoParquet
{
    // ================================================================== writing

    public static void Write(GeoDataFrame gdf, string path, GeoParquetWriteOptions? options = null) =>
        Sync.Run(() => WriteAsync(gdf, path, options));

    public static async Task WriteAsync(GeoDataFrame gdf, string path, GeoParquetWriteOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true);
        await WriteAsync(gdf, fs, options, ct).ConfigureAwait(false);
    }

    public static async Task WriteAsync(GeoDataFrame gdf, Stream output, GeoParquetWriteOptions? options = null, CancellationToken ct = default)
    {
        options ??= new GeoParquetWriteOptions();
        var geomName = gdf.GeometryColumnName;
        var attrs = gdf.Columns.Where(c => c.Name != geomName && c is not GeoSeries).ToArray();
        bool covering = options.BboxCovering && !attrs.Any(a => a.Name == "bbox");

        var fields = new List<Field>();
        var attrFields = attrs.Select(FieldFor).ToArray();
        fields.AddRange(attrFields);
        bool native = options.Encoding == GeoParquetEncoding.Native;
        string nativeEnc = native ? GeoArrowNative.EncodingFor(gdf.Geometry) : "WKB";
        bool hasZ = gdf.Geometry.Any(g => g is not null && Geo.HasZ(g));
        DataField[] nativeLeaves = [];
        var geomField = new DataField<byte[]>(geomName);
        if (native) fields.Add(GeoArrowNative.SchemaField(geomName, nativeEnc, hasZ, out nativeLeaves));
        else fields.Add(geomField);
        DataField<double?>[] bboxFields = [new("xmin"), new("ymin"), new("xmax"), new("ymax")];
        if (covering) fields.Add(new StructField("bbox", bboxFields));
        var schema = new ParquetSchema(fields);

        var geo = GeoMetadata.Build(gdf.Geometry, geomName, covering);
        if (native) geo["columns"]![geomName]!["encoding"] = nativeEnc;
        var popts = new ParquetOptions { CompressionMethod = options.Compression };
        await using var writer = await ParquetWriter.CreateAsync(schema, output, popts, false, ct).ConfigureAwait(false);
        writer.CustomMetadata = new Dictionary<string, string> { ["geo"] = geo.ToJsonString() };

        int n = gdf.RowCount;
        for (int start = 0; start < Math.Max(n, 1); start += options.RowGroupSize)
        {
            int len = Math.Min(options.RowGroupSize, n - start);
            if (len <= 0 && n > 0) break;
            using var rg = writer.CreateRowGroup();
            for (int c = 0; c < attrs.Length; c++)
                await WriteColumn(rg, attrFields[c], attrs[c], start, len).ConfigureAwait(false);
            var wkb = new byte[len][];
            var env = new double?[4][];
            for (int k = 0; k < 4; k++) env[k] = new double?[len];
            for (int i = 0; i < len; i++)
            {
                var g = gdf.Geometry[start + i];
                wkb[i] = g is null ? null! : Geo.ToWkb(g);
                if (g is { IsEmpty: false })
                {
                    var e = g.EnvelopeInternal;
                    env[0][i] = e.MinX; env[1][i] = e.MinY; env[2][i] = e.MaxX; env[3][i] = e.MaxY;
                }
            }
            if (native)
                await GeoArrowNative.WriteAsync(rg, nativeLeaves, nativeEnc, Enumerable.Range(start, len).Select(i => gdf.Geometry[i]).ToArray(), nativeLeaves[0].MaxDefinitionLevel).ConfigureAwait(false);
            else await rg.WriteAsync(geomField, wkb).ConfigureAwait(false);
            if (covering)
                for (int k = 0; k < 4; k++)
                    await rg.WriteAsync<double>(bboxFields[k], env[k].AsMemory()).ConfigureAwait(false);
        }
    }

    private static DataField FieldFor(Column c)
    {
        var t = c.DataType;
        if (t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte)) return new DataField<long?>(c.Name);
        if (t == typeof(double) || t == typeof(float)) return new DataField<double?>(c.Name);
        if (t == typeof(bool)) return new DataField<bool?>(c.Name);
        if (t == typeof(DateTime)) return new DateTimeDataField(c.Name, DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Micros, isNullable: true);
        if (t == typeof(byte[])) return new DataField<byte[]>(c.Name);
        return new DataField<string>(c.Name);
    }

    private static async Task WriteColumn(ParquetRowGroupWriter rg, DataField f, Column c, int start, int len)
    {
        var t = c.DataType;
        if (t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte))
        {
            var v = new long?[len];
            for (int i = 0; i < len; i++) v[i] = c.IsNull(start + i) ? null : Convert.ToInt64(c.GetValue(start + i), CultureInfo.InvariantCulture);
            await rg.WriteAsync<long>(f, v.AsMemory()).ConfigureAwait(false);
        }
        else if (t == typeof(double) || t == typeof(float))
        {
            var v = new double?[len];
            for (int i = 0; i < len; i++) { double d = c.GetDouble(start + i); v[i] = double.IsNaN(d) ? null : d; }
            await rg.WriteAsync<double>(f, v.AsMemory()).ConfigureAwait(false);
        }
        else if (t == typeof(bool))
        {
            var v = new bool?[len];
            for (int i = 0; i < len; i++) v[i] = (bool?)c.GetValue(start + i);
            await rg.WriteAsync<bool>(f, v.AsMemory()).ConfigureAwait(false);
        }
        else if (t == typeof(DateTime))
        {
            var v = new DateTime?[len];
            for (int i = 0; i < len; i++)
                v[i] = c.GetValue(start + i) is DateTime dt ? (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
            await rg.WriteAsync<DateTime>(f, v.AsMemory()).ConfigureAwait(false);
        }
        else if (t == typeof(byte[]))
        {
            var v = new byte[len][];
            for (int i = 0; i < len; i++) v[i] = (c.GetValue(start + i) as byte[])!;
            await rg.WriteAsync(f, v).ConfigureAwait(false);
        }
        else
        {
            var v = new string[len];
            for (int i = 0; i < len; i++)
                v[i] = c.GetValue(start + i) is { } o ? Convert.ToString(o, CultureInfo.InvariantCulture)! : null!;
            await rg.WriteAsync(f, v).ConfigureAwait(false);
        }
    }

    // ================================================================== reading

    public static GeoDataFrame Read(string path, Envelope? bbox = null, IReadOnlyList<string>? columns = null) =>
        Concat(Sync.Enumerate(() => ReadBatchesAsync(path, bbox, columns)), path);

    public static async Task<GeoDataFrame> ReadAsync(string path, Envelope? bbox = null, IReadOnlyList<string>? columns = null, CancellationToken ct = default)
    {
        var list = new List<GeoDataFrame>();
        await foreach (var b in ReadBatchesAsync(path, bbox, columns, ct: ct).ConfigureAwait(false)) list.Add(b);
        return Concat(list, path);
    }

    private static GeoDataFrame Concat(IEnumerable<GeoDataFrame> parts, string path)
    {
        var list = parts.ToList();
        if (list.Count == 1) return list[0];
        if (list.Count > 1) return GeoDataFrame.Concat(list.ToArray());
        // no rows matched: return the typed, empty schema
        return Sync.Enumerate(() => ReadBatchesAsync(path, null, null, limit: 0, emptySchema: true)).First();
    }

    /// <summary>Lazy, pushdown-aware source (row-group pruning by bbox covering and attribute statistics).</summary>
    public static IGeoScanSource ScanSource(string path) => new GeoParquetScanSource(path);

    /// <summary>File-level summary: row count, row groups, CRS, geometry types and covering column.</summary>
    public static GeoParquetInfo ReadInfo(string path) => Sync.Run(() => Task.FromResult(ReadInfoCore(path)));

    private static GeoParquetInfo ReadInfoCore(string path)
    {
        using var fs = File.OpenRead(path);
        var reader = ParquetReader.CreateAsync(fs).GetAwaiter().GetResult();
        try
        {
        reader.CustomMetadata.TryGetValue("geo", out var geoJson);
        var (primary, cols) = geoJson is null ? ("geometry", new Dictionary<string, GeoMetadata.ColumnInfo>()) : GeoMetadata.Parse(geoJson);
        cols.TryGetValue(primary, out var ci);
        long rows = 0;
        for (int i = 0; i < reader.RowGroupCount; i++) { using var rg = reader.OpenRowGroupReader(i); rows += rg.RowCount; }
        var attrNames = reader.Schema.Fields.Where(f => f is DataField && f.Name != primary && f.Name != ci?.CoveringBbox).Select(f => f.Name).ToArray();
        return new GeoParquetInfo(rows, reader.RowGroupCount, primary, ci?.Encoding ?? "WKB", ci?.Crs, ci?.Bbox, ci?.CoveringBbox, attrNames, geoJson);
        }
        finally { reader.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    /// <summary>Streams row groups as GeoDataFrames, skipping row groups that cannot match.</summary>
    public static async IAsyncEnumerable<GeoDataFrame> ReadBatchesAsync(string path, Envelope? bbox = null, IReadOnlyList<string>? columns = null,
        IReadOnlyList<AttributePredicate>? predicates = null, long? limit = null, bool emptySchema = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using var reader = await ParquetReader.CreateAsync(fs, null, true, ct).ConfigureAwait(false);
        reader.CustomMetadata.TryGetValue("geo", out var geoJson);
        var (primary, geoCols) = geoJson is null ? (GuessGeometryColumn(reader.Schema), new Dictionary<string, GeoMetadata.ColumnInfo>()) : GeoMetadata.Parse(geoJson);
        geoCols.TryGetValue(primary, out var gi);
        string enc = (gi?.Encoding ?? "WKB").ToLowerInvariant();
        bool nativeGeom = GeoArrowNative.Encodings.Contains(enc);
        if (!nativeGeom && enc != "wkb")
            throw new NotSupportedException($"GeoParquet geometry encoding '{gi!.Encoding}' is not supported (WKB and the native GeoArrow encodings are).");
        var crs = gi?.Crs;

        var top = reader.Schema.Fields;
        var geomField = top.OfType<DataField>().FirstOrDefault(f => f.Name == primary);
        DataField? nx = null, ny = null, nz = null;
        if (nativeGeom)
        {
            var leaves = reader.Schema.GetDataFields().Where(f => f.Path.ToString().StartsWith(primary + "/", StringComparison.Ordinal) || f.Path.ToString().StartsWith(primary + ".", StringComparison.Ordinal) || f.Path.ToString().StartsWith(primary + ParquetSchema.PathSeparator, StringComparison.Ordinal)).ToArray();
            nx = leaves.FirstOrDefault(f => f.Name == "x"); ny = leaves.FirstOrDefault(f => f.Name == "y"); nz = leaves.FirstOrDefault(f => f.Name == "z");
            if (nx is null || ny is null) throw new InvalidDataException($"Native geometry column '{primary}' has no x/y leaves.");
        }
        else if (geomField is null) throw new InvalidDataException($"Geometry column '{primary}' not found.");
        DataField?[] cov = [null, null, null, null];
        if (gi?.CoveringBbox is { } covName && top.OfType<StructField>().FirstOrDefault(s => s.Name == covName) is { } sf)
        {
            string[] names = ["xmin", "ymin", "xmax", "ymax"];
            for (int k = 0; k < 4; k++) cov[k] = sf.Fields.OfType<DataField>().FirstOrDefault(f => f.Name == names[k]);
        }
        bool hasCov = cov.All(f => f is not null);
        var otherGeo = new HashSet<string>(geoCols.Keys);
        var attrFields = top.OfType<DataField>()
            .Where(f => f.Name != primary && !f.IsArray && (gi?.CoveringBbox is null || f.Name != gi.CoveringBbox))
            .ToArray();
        var preds = predicates ?? [];
        var wanted = attrFields.Where(f => columns is null || columns.Contains(f.Name) || preds.Any(p => p.Column == f.Name)).ToArray();
        var outputNames = new HashSet<string>(attrFields.Where(f => columns is null || columns.Contains(f.Name)).Select(f => f.Name));

        long produced = 0, max = limit ?? long.MaxValue;
        if (emptySchema)
        {
            var cols0 = wanted.Where(f => outputNames.Contains(f.Name)).Select(f => EmptyColumn(f)).ToList();
            cols0.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, [], crs));
            yield return new GeoDataFrame(cols0);
            yield break;
        }

        for (int g = 0; g < reader.RowGroupCount && produced < max; g++)
        {
            ct.ThrowIfCancellationRequested();
            using var rg = reader.OpenRowGroupReader(g);
            int n = (int)rg.RowCount;
            if (n == 0) continue;
            if (bbox is not null && hasCov && !GroupMayIntersect(rg, cov!, bbox)) continue;
            if (preds.Any(p => wanted.FirstOrDefault(f => f.Name == p.Column) is { } pf && !GroupMayMatch(rg, pf, p))) continue;

            var mask = new bool[n];
            Array.Fill(mask, true);
            if (bbox is not null && hasCov)
            {
                var b = new double?[4][];
                for (int k = 0; k < 4; k++) b[k] = await ReadNullable<double>(rg, cov[k]!, n).ConfigureAwait(false);
                for (int i = 0; i < n; i++)
                    mask[i] = b[0][i] is { } x0 && b[1][i] is { } y0 && b[2][i] is { } x1 && b[3][i] is { } y1
                              && !(x0 > bbox.MaxX || x1 < bbox.MinX || y0 > bbox.MaxY || y1 < bbox.MinY);
            }
            var columnsRead = new Dictionary<string, Column>();
            foreach (var f in wanted) columnsRead[f.Name] = await ReadColumn(rg, f, n).ConfigureAwait(false);
            foreach (var p in preds)
                if (columnsRead.TryGetValue(p.Column, out var pc))
                    for (int i = 0; i < n; i++) if (mask[i] && !p.Matches(pc.GetValue(i))) mask[i] = false;
            if (!mask.Any(m => m)) continue;

            NtsGeometry?[] geoms;
            byte[]?[]? wkb = null;
            if (nativeGeom) geoms = await GeoArrowNative.ReadAsync(rg, enc, nx!, ny!, nz, n).ConfigureAwait(false);
            else
            {
                wkb = new byte[]?[n];
                await rg.ReadAsync(geomField!, wkb.AsMemory()).ConfigureAwait(false);
                geoms = new NtsGeometry?[n];
            }
            for (int i = 0; i < n; i++)
            {
                if (!mask[i]) { geoms[i] = null; continue; }
                if (!nativeGeom) geoms[i] = wkb![i] is { Length: > 0 } w ? Geo.FromWkb(w) : null;
                if (bbox is not null && !hasCov && (geoms[i] is not { } gg || !gg.EnvelopeInternal.Intersects(bbox))) mask[i] = false;
            }
            var rows = Enumerable.Range(0, n).Where(i => mask[i]).ToArray();
            if (rows.Length == 0) continue;
            if (produced + rows.Length > max) rows = rows[..(int)(max - produced)];
            produced += rows.Length;

            var cols = new List<Column>();
            foreach (var f in wanted)
                if (outputNames.Contains(f.Name)) cols.Add(columnsRead[f.Name].Take(rows));
            cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, rows.Select(i => geoms[i]).ToArray(), crs));
            yield return new GeoDataFrame(cols);
        }
    }

    private static string GuessGeometryColumn(ParquetSchema schema) =>
        schema.Fields.OfType<DataField>().FirstOrDefault(f => ClrOf(f) == typeof(byte[]) && f.Name is "geometry" or "geom" or "wkb_geometry")?.Name
        ?? throw new InvalidDataException("Not a GeoParquet file: no 'geo' metadata and no WKB geometry column.");

    private static bool GroupMayIntersect(ParquetRowGroupReader rg, DataField[] cov, Envelope box)
    {
        try
        {
            double? minX = Stat(rg, cov[0], true), minY = Stat(rg, cov[1], true), maxX = Stat(rg, cov[2], false), maxY = Stat(rg, cov[3], false);
            if (minX is null || minY is null || maxX is null || maxY is null) return true;
            return !(minX > box.MaxX || maxX < box.MinX || minY > box.MaxY || maxY < box.MinY);
        }
        catch (Exception) { return true; }
    }

    private static double? Stat(ParquetRowGroupReader rg, DataField f, bool min)
    {
        var s = rg.GetStatistics(f);
        var v = min ? s?.MinValue : s?.MaxValue;
        return v is null ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    private static bool GroupMayMatch(ParquetRowGroupReader rg, DataField f, AttributePredicate p)
    {
        try
        {
            var s = rg.GetStatistics(f);
            if (s?.MinValue is not { } lo || s.MaxValue is not { } hi) return true;
            var min = new AttributePredicate(p.Column, CompareOp.Ge, p.Value);
            return p.Op switch
            {
                CompareOp.Eq => new AttributePredicate(p.Column, CompareOp.Le, p.Value).Matches(lo) && min.Matches(hi),
                CompareOp.Lt => new AttributePredicate(p.Column, CompareOp.Lt, p.Value).Matches(lo),
                CompareOp.Le => new AttributePredicate(p.Column, CompareOp.Le, p.Value).Matches(lo),
                CompareOp.Gt => new AttributePredicate(p.Column, CompareOp.Gt, p.Value).Matches(hi),
                CompareOp.Ge => min.Matches(hi),
                _ => true
            };
        }
        catch (Exception) { return true; }
    }

    private static async Task<T?[]> ReadNullable<T>(ParquetRowGroupReader rg, DataField f, int n) where T : struct
    {
        if (f.IsNullable)
        {
            var v = new T?[n];
            await rg.ReadAsync<T>(f, v.AsMemory()).ConfigureAwait(false);
            return v;
        }
        var raw = new T[n];
        await rg.ReadAsync<T>(f, raw.AsMemory()).ConfigureAwait(false);
        return raw.Select(x => (T?)x).ToArray();
    }

    /// <summary>CLR type of a field, mapping Parquet.Net 6's ReadOnlyMemory&lt;char/byte&gt; to string / byte[].</summary>
    private static Type ClrOf(DataField f)
    {
        var t = Nullable.GetUnderlyingType(f.ClrType) ?? f.ClrType;
        if (t == typeof(ReadOnlyMemory<char>)) return typeof(string);
        if (t == typeof(ReadOnlyMemory<byte>)) return typeof(byte[]);
        return t;
    }

    private static async Task<Column> ReadColumn(ParquetRowGroupReader rg, DataField f, int n)
    {
        var t = ClrOf(f);
        if (t == typeof(long)) return Column<long>.FromNullable(f.Name, await ReadNullable<long>(rg, f, n));
        if (t == typeof(int)) return Column<long>.FromNullable(f.Name, (await ReadNullable<int>(rg, f, n)).Select(v => (long?)v).ToArray());
        if (t == typeof(short)) return Column<long>.FromNullable(f.Name, (await ReadNullable<short>(rg, f, n)).Select(v => (long?)v).ToArray());
        if (t == typeof(sbyte)) return Column<long>.FromNullable(f.Name, (await ReadNullable<sbyte>(rg, f, n)).Select(v => (long?)v).ToArray());
        if (t == typeof(byte)) return Column<long>.FromNullable(f.Name, (await ReadNullable<byte>(rg, f, n)).Select(v => (long?)v).ToArray());
        if (t == typeof(ushort)) return Column<long>.FromNullable(f.Name, (await ReadNullable<ushort>(rg, f, n)).Select(v => (long?)v).ToArray());
        if (t == typeof(uint)) return Column<long>.FromNullable(f.Name, (await ReadNullable<uint>(rg, f, n)).Select(v => (long?)v).ToArray());
        if (t == typeof(ulong)) return Column<double>.FromNullable(f.Name, (await ReadNullable<ulong>(rg, f, n)).Select(v => (double?)v).ToArray());
        if (t == typeof(double)) return Column<double>.FromNullable(f.Name, await ReadNullable<double>(rg, f, n));
        if (t == typeof(float)) return Column<double>.FromNullable(f.Name, (await ReadNullable<float>(rg, f, n)).Select(v => (double?)v).ToArray());
        if (t == typeof(decimal)) return Column<double>.FromNullable(f.Name, (await ReadNullable<decimal>(rg, f, n)).Select(v => (double?)v).ToArray());
        if (t == typeof(bool)) return Column<bool>.FromNullable(f.Name, await ReadNullable<bool>(rg, f, n));
        if (t == typeof(DateTime)) return Column<DateTime>.FromNullable(f.Name, await ReadNullable<DateTime>(rg, f, n));
        if (t == typeof(DateTimeOffset)) return Column<DateTime>.FromNullable(f.Name, (await ReadNullable<DateTimeOffset>(rg, f, n)).Select(v => v?.UtcDateTime).ToArray());
        if (t == typeof(DateOnly)) return Column<DateTime>.FromNullable(f.Name, (await ReadNullable<DateOnly>(rg, f, n)).Select(v => v?.ToDateTime(TimeOnly.MinValue)).ToArray());
        if (t == typeof(byte[]))
        {
            var v = new byte[]?[n];
            await rg.ReadAsync(f, v.AsMemory()).ConfigureAwait(false);
            return new Column<byte[]?>(f.Name, v);
        }
        if (t == typeof(string))
        {
            var v = new string?[n];
            await rg.ReadAsync(f, v.AsMemory()).ConfigureAwait(false);
            return new Column<string?>(f.Name, v);
        }
        throw new NotSupportedException($"Parquet column '{f.Name}' of type {t.Name} is not supported.");
    }

    private static Column EmptyColumn(DataField f)
    {
        var t = ClrOf(f);
        if (t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(ushort) || t == typeof(uint)) return new Column<long>(f.Name, []);
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal) || t == typeof(ulong)) return new Column<double>(f.Name, []);
        if (t == typeof(bool)) return new Column<bool>(f.Name, []);
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)) return new Column<DateTime>(f.Name, []);
        if (t == typeof(byte[])) return new Column<byte[]?>(f.Name, []);
        return new Column<string?>(f.Name, []);
    }
}

/// <summary>GeoParquet file summary.</summary>
public sealed record GeoParquetInfo(long Rows, int RowGroups, string GeometryColumn, string Encoding, CoordinateReferenceSystem? Crs,
    Envelope? Bbox, string? CoveringColumn, IReadOnlyList<string> Columns, string? GeoMetadataJson);

/// <summary>Lazy scan source over GeoParquet: exact bbox (covering column or WKB envelope), column pruning, attribute predicates, limit.</summary>
public sealed class GeoParquetScanSource : IGeoScanSource
{
    private readonly string _path;
    private readonly GeoParquetInfo _info;

    public GeoParquetScanSource(string path) { _path = path; _info = GeoParquet.ReadInfo(path); }

    public GeoParquetInfo Info => _info;
    public string Name => $"GeoParquet '{Path.GetFileName(_path)}' ({_info.Rows} rows in {_info.RowGroups} row groups{(_info.CoveringColumn is null ? "" : ", bbox covering")})";
    public CoordinateReferenceSystem? Crs => _info.Crs;
    public IReadOnlyList<string> ColumnNames => _info.Columns;
    public long? RowCountHint => _info.Rows;
    public ScanPushdown Pushdown => ScanPushdown.Bbox | ScanPushdown.ExactBbox | ScanPushdown.Columns | ScanPushdown.Limit | ScanPushdown.Attributes | ScanPushdown.ExactAttributes;

    public IEnumerable<GeoDataFrame> Scan(ScanRequest request) =>
        Sync.Enumerate(() => GeoParquet.ReadBatchesAsync(_path, request.Bbox, request.Columns, request.Predicates, request.Limit));
}

/// <summary>
/// Sync-over-async without deadlocks: the async work always runs on the thread pool, so a UI or legacy ASP.NET
/// SynchronizationContext on the calling thread is never captured by Parquet.Net's awaits.
/// </summary>
internal static class Sync
{
    public static void Run(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();
    public static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    public static IEnumerable<T> Enumerate<T>(Func<IAsyncEnumerable<T>> source)
    {
        var e = Task.Run(() => source().GetAsyncEnumerator()).GetAwaiter().GetResult();
        try
        {
            while (Task.Run(() => e.MoveNextAsync().AsTask()).GetAwaiter().GetResult()) yield return e.Current;
        }
        finally { Task.Run(() => e.DisposeAsync().AsTask()).GetAwaiter().GetResult(); }
    }
}
