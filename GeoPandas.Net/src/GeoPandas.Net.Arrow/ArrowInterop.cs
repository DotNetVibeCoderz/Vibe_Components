using System.Globalization;
using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Arrow;

/// <summary>
/// Zero-friction exchange with the Apache Arrow ecosystem: <see cref="DataFrame"/> ⇄ <see cref="RecordBatch"/>, and Arrow IPC files
/// (Feather v2, <c>.arrow</c>) / streams. Geometry columns use the GeoArrow <c>geoarrow.wkb</c> extension with PROJJSON CRS metadata,
/// plus GeoParquet-style <c>"geo"</c> schema metadata so <c>geopandas.read_feather</c> recognises them.
/// </summary>
public static class ArrowInterop
{
    private const string ExtName = "ARROW:extension:name";
    private const string ExtMeta = "ARROW:extension:metadata";
    private static readonly Apache.Arrow.Compression.CompressionCodecFactory s_codecs = new();

    // ================================================================== DataFrame → Arrow

    /// <summary>Converts a frame to one Arrow record batch (columns are copied into Arrow buffers).</summary>
    public static RecordBatch ToRecordBatch(this DataFrame df)
    {
        var fields = new List<Field>();
        var arrays = new List<IArrowArray>();
        foreach (var c in df.Columns)
        {
            var (f, a) = Convert(c);
            fields.Add(f); arrays.Add(a);
        }
        var meta = new Dictionary<string, string>();
        if (df is GeoDataFrame g) meta["geo"] = GeoMetadata.Build(g.Geometry, g.GeometryColumnName, covering: false).ToJsonString();
        var schema = new Schema(fields, meta);
        return new RecordBatch(schema, arrays, df.RowCount);
    }

    private static (Field, IArrowArray) Convert(Column c)
    {
        if (c is GeoSeries gs)
        {
            var b = new BinaryArray.Builder();
            foreach (var g in gs) { if (g is null) b.AppendNull(); else b.Append(Geo.ToWkb(g).AsSpan()); }
            var ext = new JsonObject();
            if (gs.Crs is { } crs) ext["crs"] = ProjJson.ToNode(crs);
            var md = new Dictionary<string, string> { [ExtName] = "geoarrow.wkb", [ExtMeta] = ext.ToJsonString() };
            return (new Field(c.Name, BinaryType.Default, true, md), b.Build());
        }
        var t = c.DataType;
        if (t == typeof(double) || t == typeof(float))
        {
            var b = new DoubleArray.Builder();
            for (int i = 0; i < c.Length; i++) { double v = c.GetDouble(i); if (c.IsNull(i)) b.AppendNull(); else b.Append(v); }
            return (new Field(c.Name, DoubleType.Default, true), b.Build());
        }
        if (t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte))
        {
            var b = new Int64Array.Builder();
            for (int i = 0; i < c.Length; i++) { if (c.IsNull(i)) b.AppendNull(); else b.Append(System.Convert.ToInt64(c.GetValue(i), CultureInfo.InvariantCulture)); }
            return (new Field(c.Name, Int64Type.Default, true), b.Build());
        }
        if (t == typeof(bool))
        {
            var b = new BooleanArray.Builder();
            for (int i = 0; i < c.Length; i++) { if (c.GetValue(i) is bool v) b.Append(v); else b.AppendNull(); }
            return (new Field(c.Name, BooleanType.Default, true), b.Build());
        }
        if (t == typeof(DateTime))
        {
            var type = new TimestampType(TimeUnit.Microsecond, "UTC");
            var b = new TimestampArray.Builder(type);
            for (int i = 0; i < c.Length; i++)
            {
                if (c.GetValue(i) is DateTime dt)
                    b.Append(new DateTimeOffset(dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc)));
                else b.AppendNull();
            }
            return (new Field(c.Name, type, true), b.Build());
        }
        if (t == typeof(byte[]))
        {
            var b = new BinaryArray.Builder();
            for (int i = 0; i < c.Length; i++) { if (c.GetValue(i) is byte[] v) b.Append(v.AsSpan()); else b.AppendNull(); }
            return (new Field(c.Name, BinaryType.Default, true), b.Build());
        }
        var sb = new StringArray.Builder();
        for (int i = 0; i < c.Length; i++) { var v = c.GetValue(i); if (v is null) sb.AppendNull(); else sb.Append(System.Convert.ToString(v, CultureInfo.InvariantCulture)); }
        return (new Field(c.Name, StringType.Default, true), sb.Build());
    }

    // ================================================================== Arrow → DataFrame

    /// <summary>Converts a record batch back; returns a <see cref="GeoDataFrame"/> when a geometry column is present.</summary>
    public static DataFrame ToDataFrame(this RecordBatch batch)
    {
        var schema = batch.Schema;
        string? primary = null;
        var crsByColumn = new Dictionary<string, CoordinateReferenceSystem?>();
        if (schema.Metadata is { } md && md.TryGetValue("geo", out var geo))
        {
            var (p, cols) = GeoMetadata.Parse(geo);
            primary = p;
            foreach (var (k, v) in cols) crsByColumn[k] = v.Crs;
        }
        var columns = new List<Column>();
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            var f = schema.FieldsList[i];
            var a = batch.Column(i);
            bool isGeo = crsByColumn.ContainsKey(f.Name) ||
                         f.Metadata is { } fm && fm.TryGetValue(ExtName, out var en) && en == "geoarrow.wkb";
            if (isGeo && a is BinaryArray ba)
            {
                CoordinateReferenceSystem? crs = crsByColumn.GetValueOrDefault(f.Name);
                if (!crsByColumn.ContainsKey(f.Name) && f.Metadata is { } fmd && fmd.TryGetValue(ExtMeta, out var em) && JsonNode.Parse(em) is { } emn && emn["crs"] is { } cn)
                    crs = cn is JsonValue jv && jv.TryGetValue<string>(out var s) ? CoordinateReferenceSystem.TryParse(s, out var c1) ? c1 : null
                        : ProjJson.TryParse(cn.ToJsonString(), out var c2) ? c2 : null;
                var geoms = new NtsGeometry?[ba.Length];
                for (int r = 0; r < ba.Length; r++) geoms[r] = ba.IsNull(r) ? null : Geo.FromWkb(ba.GetBytes(r).ToArray());
                columns.Add(new GeoSeries(f.Name, geoms, crs));
                primary ??= f.Name;
                continue;
            }
            columns.Add(FromArrow(f.Name, a));
        }
        if (primary is not null && columns.Any(c => c.Name == primary && c is GeoSeries)) return new GeoDataFrame(columns, primary);
        return new DataFrame(columns);
    }

    private static Column FromArrow(string name, IArrowArray a)
    {
        int n = a.Length;
        switch (a)
        {
            case DoubleArray d: return Column<double>.FromNullable(name, Enumerable.Range(0, n).Select(i => d.GetValue(i)).ToArray());
            case FloatArray d: return Column<double>.FromNullable(name, Enumerable.Range(0, n).Select(i => (double?)d.GetValue(i)).ToArray());
            case Int64Array d: return Column<long>.FromNullable(name, Enumerable.Range(0, n).Select(i => d.GetValue(i)).ToArray());
            case Int32Array d: return Column<long>.FromNullable(name, Enumerable.Range(0, n).Select(i => (long?)d.GetValue(i)).ToArray());
            case Int16Array d: return Column<long>.FromNullable(name, Enumerable.Range(0, n).Select(i => (long?)d.GetValue(i)).ToArray());
            case Int8Array d: return Column<long>.FromNullable(name, Enumerable.Range(0, n).Select(i => (long?)d.GetValue(i)).ToArray());
            case UInt8Array d: return Column<long>.FromNullable(name, Enumerable.Range(0, n).Select(i => (long?)d.GetValue(i)).ToArray());
            case UInt16Array d: return Column<long>.FromNullable(name, Enumerable.Range(0, n).Select(i => (long?)d.GetValue(i)).ToArray());
            case UInt32Array d: return Column<long>.FromNullable(name, Enumerable.Range(0, n).Select(i => (long?)d.GetValue(i)).ToArray());
            case UInt64Array d: return Column<double>.FromNullable(name, Enumerable.Range(0, n).Select(i => (double?)d.GetValue(i)).ToArray());
            case BooleanArray d: return Column<bool>.FromNullable(name, Enumerable.Range(0, n).Select(i => d.GetValue(i)).ToArray());
            case TimestampArray d: return Column<DateTime>.FromNullable(name, Enumerable.Range(0, n).Select(i => d.GetTimestamp(i)?.UtcDateTime).ToArray());
            case Date32Array d: return Column<DateTime>.FromNullable(name, Enumerable.Range(0, n).Select(i => d.GetDateTime(i)).ToArray());
            case Date64Array d: return Column<DateTime>.FromNullable(name, Enumerable.Range(0, n).Select(i => d.GetDateTime(i)).ToArray());
            case StringArray d: return new Column<string?>(name, Enumerable.Range(0, n).Select(i => d.GetString(i)).ToArray());
            case LargeStringArray d: return new Column<string?>(name, Enumerable.Range(0, n).Select(i => d.GetString(i)).ToArray());
            case BinaryArray d: return new Column<byte[]?>(name, Enumerable.Range(0, n).Select(i => d.IsNull(i) ? null : d.GetBytes(i).ToArray()).ToArray());
            case DictionaryArray d:
            {
                var values = FromArrow(name, d.Dictionary);
                var idx = d.Indices;
                var rows = new int[n];
                for (int i = 0; i < n; i++)
                    rows[i] = idx.IsNull(i) ? -1 : idx switch
                    {
                        Int32Array i32 => i32.GetValue(i)!.Value, Int16Array i16 => i16.GetValue(i)!.Value, Int8Array i8 => i8.GetValue(i)!.Value,
                        Int64Array i64 => (int)i64.GetValue(i)!.Value, UInt8Array u8 => u8.GetValue(i)!.Value, _ => throw new NotSupportedException("Dictionary index type.")
                    };
                return values.Take(rows);
            }
            default:
                // nested or exotic types: keep as text
                return new Column<string?>(name, Enumerable.Range(0, n).Select(i => a.IsNull(i) ? null : a.GetType().Name).ToArray());
        }
    }

    // ================================================================== IPC files and streams

    /// <summary>Writes an Arrow IPC file (Feather v2). Large frames are split into record batches.</summary>
    public static void WriteArrowFile(this DataFrame df, string path, int batchSize = 262_144)
    {
        using var fs = File.Create(path);
        var first = df.Head(Math.Min(batchSize, df.RowCount)).ToRecordBatch();
        using var writer = new ArrowFileWriter(fs, first.Schema);
        writer.WriteRecordBatch(first);
        for (int start = first.Length; start < df.RowCount; start += batchSize)
        {
            var part = df.Take(Enumerable.Range(start, Math.Min(batchSize, df.RowCount - start)).ToArray());
            writer.WriteRecordBatch(part.ToRecordBatch());
        }
        writer.WriteEnd();
    }

    /// <summary>Writes an Arrow IPC stream (for sockets, pipes, Flight).</summary>
    public static void WriteArrowStream(this DataFrame df, Stream output)
    {
        var batch = df.ToRecordBatch();
        using var writer = new ArrowStreamWriter(output, batch.Schema, leaveOpen: true);
        writer.WriteRecordBatch(batch);
        writer.WriteEnd();
    }

    /// <summary>Reads an Arrow IPC file (Feather v2). The file is memory-mapped while reading.</summary>
    public static DataFrame ReadArrowFile(string path)
    {
        using var mapped = new GeoPandas.Net.IO.MappedFile(path);
        using var stream = mapped.OpenStream();
        using var reader = new ArrowFileReader(stream, s_codecs);
        var parts = new List<DataFrame>();
        RecordBatch? b;
        while ((b = reader.ReadNextRecordBatch()) is not null) using (b) parts.Add(b.ToDataFrame());
        return Combine(parts);
    }

    public static DataFrame ReadArrowStream(Stream input)
    {
        using var reader = new ArrowStreamReader(input, s_codecs, leaveOpen: true);
        var parts = new List<DataFrame>();
        RecordBatch? b;
        while ((b = reader.ReadNextRecordBatch()) is not null) using (b) parts.Add(b.ToDataFrame());
        return Combine(parts);
    }

    private static DataFrame Combine(List<DataFrame> parts) => parts.Count switch
    {
        0 => new DataFrame(),
        1 => parts[0],
        _ => parts.All(p => p is GeoDataFrame) ? GeoDataFrame.Concat(parts.Cast<GeoDataFrame>().ToArray()) : DataFrame.Concat(parts.ToArray())
    };

    /// <summary>Reads an Arrow IPC file that holds geometry.</summary>
    public static GeoDataFrame ReadGeoArrowFile(string path) =>
        ReadArrowFile(path) as GeoDataFrame ?? throw new InvalidDataException("The Arrow file has no geometry column (geoarrow.wkb or 'geo' metadata).");
}
