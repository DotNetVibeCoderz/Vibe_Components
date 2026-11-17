using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Apis.Bigquery.v2.Data;
using Google.Cloud.BigQuery.V2;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Sql.BigQuery;

/// <summary>
/// Google BigQuery GIS: GEOGRAPHY query results → GeoDataFrame (WGS 84), and GeoDataFrame → BigQuery table
/// (reprojected to WGS 84, loaded as newline-delimited JSON with WKT geographies).
/// </summary>
/// <example>
/// <code>
/// var client = BigQueryClient.Create("my-project");
/// var zips = BigQueryGis.ReadQuery(client,
///     "SELECT zip_code, zip_code_geom FROM `bigquery-public-data.geo_us_boundaries.zip_codes` WHERE state_code = 'CA'");
/// districts.ToBigQuery(client, "analytics", "districts", IfExists.Replace);
/// </code>
/// </example>
public static class BigQueryGis
{
    /// <summary>Runs a query; GEOGRAPHY columns become geometries (the first one is the active geometry).</summary>
    public static GeoDataFrame ReadQuery(BigQueryClient client, string sql, IEnumerable<BigQueryParameter>? parameters = null, string? geometryColumn = null)
    {
        var results = client.ExecuteQuery(sql, parameters);
        var schema = results.Schema;
        var rows = results.Select(r => schema.Fields.Select(f => r[f.Name]).ToArray());
        return FromRows(schema, rows, geometryColumn);
    }

    /// <summary>Builds a GeoDataFrame from BigQuery rows (values in schema order). Exposed for testing and custom pipelines.</summary>
    public static GeoDataFrame FromRows(TableSchema schema, IEnumerable<object?[]> rows, string? geometryColumn = null)
    {
        var fields = schema.Fields;
        var values = fields.Select(_ => new List<object?>()).ToArray();
        foreach (var row in rows)
            for (int i = 0; i < fields.Count; i++) values[i].Add(Convert(row[i], fields[i].Type));
        var geoIdx = Enumerable.Range(0, fields.Count).Where(i => string.Equals(fields[i].Type, "GEOGRAPHY", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (geoIdx.Length == 0) throw new InvalidOperationException("The query returned no GEOGRAPHY column.");
        int primary = geometryColumn is null ? geoIdx[0] : Array.FindIndex(fields.ToArray(), f => f.Name == geometryColumn);
        if (primary < 0) throw new ArgumentException($"Column '{geometryColumn}' not found.");
        var cols = new List<Column>();
        for (int i = 0; i < fields.Count; i++)
        {
            if (geoIdx.Contains(i))
            {
                var geoms = values[i].Select(v => v is string wkt && wkt.Length > 0 ? Geo.FromWkt(wkt) : null).ToArray();
                cols.Add(new GeoSeries(i == primary ? GeoDataFrame.DefaultGeometryColumn : fields[i].Name, geoms, CoordinateReferenceSystem.Wgs84));
            }
            else cols.Add(Column.FromObjects(fields[i].Name, values[i]));
        }
        return new GeoDataFrame(cols);
    }

    private static object? Convert(object? v, string type) => v switch
    {
        null => null,
        BigQueryNumeric n => double.Parse(n.ToString(), CultureInfo.InvariantCulture),
        BigQueryBigNumeric n => double.Parse(n.ToString(), CultureInfo.InvariantCulture),
        DateTimeOffset o => o.UtcDateTime,
        BigQueryGeography g => g.Text,
        int i => (long)i,
        _ when type == "GEOGRAPHY" => v.ToString(),
        _ => v
    };

    /// <summary>BigQuery schema for a frame (GEOGRAPHY for the geometry).</summary>
    public static TableSchema BuildSchema(GeoDataFrame gdf, string geometryColumn = "geog")
    {
        var b = new TableSchemaBuilder();
        foreach (var c in gdf.Columns.Where(c => c.Name != gdf.GeometryColumnName && c is not GeoSeries))
        {
            var t = c.DataType;
            var type = t == typeof(long) || t == typeof(int) ? BigQueryDbType.Int64 : t == typeof(double) || t == typeof(float) ? BigQueryDbType.Float64
                : t == typeof(bool) ? BigQueryDbType.Bool : t == typeof(DateTime) ? BigQueryDbType.Timestamp : t == typeof(byte[]) ? BigQueryDbType.Bytes : BigQueryDbType.String;
            b.Add(c.Name, type, BigQueryFieldMode.Nullable);
        }
        b.Add(geometryColumn, BigQueryDbType.Geography, BigQueryFieldMode.Nullable);
        return b.Build();
    }

    /// <summary>Newline-delimited JSON rows (geometry as WKT in WGS 84) for a load job.</summary>
    public static IEnumerable<string> ToJsonLines(GeoDataFrame gdf, string geometryColumn = "geog")
    {
        var wgs = gdf.Crs is null || gdf.Crs == CoordinateReferenceSystem.Wgs84 ? gdf : gdf.ToCrs(CoordinateReferenceSystem.Wgs84);
        var attrs = wgs.Columns.Where(c => c.Name != wgs.GeometryColumnName && c is not GeoSeries).ToArray();
        for (int r = 0; r < wgs.RowCount; r++)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var c in attrs)
                {
                    switch (c.GetValue(r))
                    {
                        case null: w.WriteNull(c.Name); break;
                        case double d when double.IsNaN(d) || double.IsInfinity(d): w.WriteNull(c.Name); break;
                        case double d: w.WriteNumber(c.Name, d); break;
                        case float f: w.WriteNumber(c.Name, f); break;
                        case long l: w.WriteNumber(c.Name, l); break;
                        case int i: w.WriteNumber(c.Name, i); break;
                        case bool bo: w.WriteBoolean(c.Name, bo); break;
                        case DateTime dt: w.WriteString(c.Name, (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture)); break;
                        case byte[] bytes: w.WriteBase64String(c.Name, bytes); break;
                        case var o: w.WriteString(c.Name, System.Convert.ToString(o, CultureInfo.InvariantCulture)); break;
                    }
                }
                var g = wgs.Geometry[r];
                if (g is null) w.WriteNull(geometryColumn); else w.WriteString(geometryColumn, g.AsText());
                w.WriteEndObject();
            }
            yield return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    /// <summary>Loads a frame into <c>dataset.table</c> (created when missing; replaced or appended per <paramref name="ifExists"/>).</summary>
    public static void ToBigQuery(this GeoDataFrame gdf, BigQueryClient client, string datasetId, string tableId,
        IfExists ifExists = IfExists.Fail, string geometryColumn = "geog")
    {
        var schema = BuildSchema(gdf, geometryColumn);
        bool exists;
        try { client.GetTable(datasetId, tableId); exists = true; }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { exists = false; }
        if (exists && ifExists == IfExists.Fail) throw new InvalidOperationException($"Table {datasetId}.{tableId} already exists.");
        var options = new UploadJsonOptions
        {
            WriteDisposition = ifExists == IfExists.Replace ? WriteDisposition.WriteTruncate : WriteDisposition.WriteAppend,
            CreateDisposition = CreateDisposition.CreateIfNeeded
        };
        var job = client.UploadJson(datasetId, tableId, schema, ToJsonLines(gdf, geometryColumn), options);
        job.PollUntilCompleted().ThrowOnAnyError();
    }

    /// <summary>Envelope filter in BigQuery SQL (<c>ST_INTERSECTSBOX</c>) for WGS 84 coordinates.</summary>
    public static string BboxPredicate(string column, double minLon, double minLat, double maxLon, double maxLat) =>
        string.Create(CultureInfo.InvariantCulture, $"ST_INTERSECTSBOX({column}, {minLon:R}, {minLat:R}, {maxLon:R}, {maxLat:R})");

    internal static NtsGeometry? Parse(string? wkt) => string.IsNullOrEmpty(wkt) ? null : Geo.FromWkt(wkt);
}
