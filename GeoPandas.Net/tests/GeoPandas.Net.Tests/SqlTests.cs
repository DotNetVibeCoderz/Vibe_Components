using System.Data.Common;
using Google.Apis.Bigquery.v2.Data;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Lazy;
using GeoPandas.Net.Sql;
using GeoPandas.Net.Sql.BigQuery;
using NetTopologySuite.Geometries;
using Xunit.Abstractions;

namespace GeoPandas.Net.Tests;

/// <summary>
/// Spatial databases. DuckDB (in-process, spatial extension) and SQL Server LocalDB run live when available
/// (the tests report and return otherwise); PostGIS / Oracle / MySQL / BigQuery are covered at the SQL / payload level.
/// </summary>
public sealed class SqlTests(ITestOutputHelper output)
{
    private static GeoDataFrame Districts() => SampleData.Districts();

    private void RoundTrip(DbConnection con, string label, bool expectCrs)
    {
        var d = Districts();
        d.ToSql(con, "gpd_districts", IfExists.Replace);
        Assert.Throws<InvalidOperationException>(() => d.ToSql(con, "gpd_districts"));
        Assert.Contains(SpatialSql.ListGeometryColumns(con), t => t.Table.Equals("gpd_districts", StringComparison.OrdinalIgnoreCase));

        var back = SpatialSql.ReadTable(con, "gpd_districts", crs: expectCrs ? null : d.Crs);
        Assert.Equal(d.RowCount, back.RowCount);
        Assert.Equal(d.Crs, back.Crs);
        var byId = Enumerable.Range(0, back.RowCount).ToDictionary(i => back["district_id"].GetDouble(i), i => i);
        for (int i = 0; i < d.RowCount; i++)
        {
            int j = byId[d["district_id"].GetDouble(i)];
            // compare normalised geometries: Oracle stores exterior rings counter-clockwise and may change the start vertex
            var (expected, actual) = (d.Geometry[i]!.Normalized(), back.Geometry[j]!.Normalized());
            Assert.True(expected.EqualsExact(actual, 1e-6), $"{label}: geometry {i} differs; expected {d.Geometry[i]}, actual {back.Geometry[j]}");
            Assert.Equal(d["name"].GetValue(i), back["name"].GetValue(j));
            Assert.Equal(d["area_km2"].GetDouble(i), back["area_km2"].GetDouble(j), 9);
        }

        var q = SpatialSql.ReadSql(con, "SELECT name, population, geom FROM gpd_districts WHERE population > 50000", crs: expectCrs ? null : d.Crs);
        Assert.Equal(d.Filter<long>("population", p => p > 50000).RowCount, q.RowCount);

        var b = d.TotalBounds;
        var box = new Envelope(b.MinX, b.Centre.X, b.MinY, b.Centre.Y);
        var lazy = SpatialSql.ScanTable(con, "gpd_districts").FilterBbox(box).Where("region", CompareOp.Eq, "Barat").Select("name");
        var expect = d.Lazy().FilterBbox(box).Where("region", CompareOp.Eq, "Barat").Select("name").Collect();
        Assert.Equal(expect["name"].AsObjects().Order(), lazy.Collect()["name"].AsObjects().Order());
        Assert.Equal(3, SpatialSql.ScanTable(con, "gpd_districts").Limit(3).Collect().RowCount);

        // types and nulls: DateTime (UTC), bool, nullable long/double/string, null geometry
        var typed = GeoDataFrame.FromGeometries([Geo.Point(700000, 9300000), null, Geo.Point(700010, 9300020)], d.Crs,
            Column.Create("k", new long[] { 1, 2, 3 }),
            Column.Create("when", new[] { new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc) }),
            Column.Create("flag", new[] { true, false, true }),
            Column<double>.FromNullable("v", new double?[] { 1.5, null, -2 }),
            Column.Create("s", new string?[] { "a", null, "ü" }));
        typed.ToSql(con, "gpd_types", IfExists.Replace, spatialIndex: false);
        var tb = SpatialSql.ReadTable(con, "gpd_types", crs: d.Crs).SortBy("k");
        Assert.Equal(3, tb.RowCount);
        Assert.Null(tb.Geometry[1]);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5), DateTime.SpecifyKind((DateTime)tb["when"].GetValue(0)!, DateTimeKind.Unspecified));
        Assert.False(System.Convert.ToBoolean(tb["flag"].GetValue(1)));
        Assert.True(tb["v"].IsNull(1));
        Assert.True(tb["s"].IsNull(1));
        Assert.Equal("ü", tb["s"].GetValue(2));

        d.Head(2).ToSql(con, "gpd_districts", IfExists.Append);
        Assert.Equal(d.RowCount + 2, SpatialSql.ReadTable(con, "gpd_districts").RowCount);
        output.WriteLine($"{label}: round trip, SQL, lazy pushdown and append OK");
    }

    [Fact]
    public void DuckDb_Spatial_Live()
    {
        using var con = new DuckDB.NET.Data.DuckDBConnection("Data Source=:memory:");
        con.Open();
        void Exec(string sql) { using var c = con.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        try { Exec("INSTALL spatial"); } catch (DbException) { }
        try { Exec("LOAD spatial"); }
        catch (DbException e) { output.WriteLine($"DuckDB spatial extension unavailable ({e.Message}); skipped."); return; }
        RoundTrip(con, "DuckDB", expectCrs: false);
    }

    [Fact]
    public void SqlServer_LocalDb_Live()
    {
        if (!OperatingSystem.IsWindows()) { output.WriteLine("LocalDB is Windows-only; skipped."); return; }
        var con = new Microsoft.Data.SqlClient.SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Database=tempdb;Connect Timeout=30");
        try { con.Open(); }
        catch (Exception e) when (e is DbException or InvalidOperationException) { output.WriteLine($"SQL Server LocalDB unavailable ({e.Message}); skipped."); con.Dispose(); return; }
        using (con) RoundTrip(con, "SQL Server", expectCrs: true);
    }

    /// <summary>PostGIS (CI service container): set GPD_POSTGIS_CS, e.g. "Host=localhost;Username=postgres;Password=gpd;Database=postgres".</summary>
    [Fact]
    public void PostGis_Live()
    {
        var cs = Environment.GetEnvironmentVariable("GPD_POSTGIS_CS");
        if (string.IsNullOrEmpty(cs)) { output.WriteLine("GPD_POSTGIS_CS not set; skipped."); return; }
        using var con = new Npgsql.NpgsqlConnection(cs);
        con.Open();
        using (var c = con.CreateCommand()) { c.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis"; c.ExecuteNonQuery(); }
        RoundTrip(con, "PostGIS", expectCrs: true);
    }

    /// <summary>Oracle Spatial (CI service container): set GPD_ORACLE_CS, e.g. "User Id=gpd;Password=gpd;Data Source=localhost:1521/FREEPDB1".</summary>
    [Fact]
    public void Oracle_Live()
    {
        var cs = Environment.GetEnvironmentVariable("GPD_ORACLE_CS");
        if (string.IsNullOrEmpty(cs)) { output.WriteLine("GPD_ORACLE_CS not set; skipped."); return; }
        using var con = new Oracle.ManagedDataAccess.Client.OracleConnection(cs);
        con.Open();
        foreach (var t in new[] { "GPD_DISTRICTS", "GPD_TYPES" })
        {
            using var c = con.CreateCommand();
            c.CommandText = $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE \"{t}\" PURGE'; EXCEPTION WHEN OTHERS THEN NULL; END;";
            c.ExecuteNonQuery();
        }
        RoundTrip(con, "Oracle", expectCrs: true);
    }

    [Fact]
    public void Dialects_GenerateExpectedSql()
    {
        var box = new Envelope(1, 3, 2, 4);
        Assert.Equal("\"geom\" && ST_MakeEnvelope(1, 2, 3, 4, 4326)", SpatialDialect.PostGis.BboxPredicate("\"geom\"", box, 4326));
        Assert.Equal("ST_GeomFromWKB(@p3, 32748)", SpatialDialect.PostGis.FromWkb("@p3", 32748));
        Assert.Equal("SELECT TOP (5) a FROM t", SpatialDialect.SqlServer.Limit("SELECT a FROM t", 5));
        Assert.Equal("SDO_UTIL.TO_WKBGEOMETRY(g)", SpatialDialect.Oracle.ToWkb("g"));
        Assert.Equal("SDO_UTIL.FROM_WKBGEOMETRY(:p0)", SpatialDialect.Oracle.FromWkb(":p0", 4326));
        Assert.Equal("UPDATE \"GPD\" t SET t.\"G\".SDO_SRID = 4326 WHERE t.\"G\" IS NOT NULL", Assert.Single(SpatialDialect.Oracle.AfterInsert("gpd", "g", 4326)));
        Assert.Equal("SELECT a FROM t FETCH FIRST 7 ROWS ONLY", SpatialDialect.Oracle.Limit("SELECT a FROM t", 7));
        Assert.Contains("SPATIAL_INDEX_V2", string.Join(";", SpatialDialect.Oracle.CreateSpatialIndex("T", "G", box, 4326)));
        Assert.Equal("ST_AsWKB(g, 'axis-order=long-lat')", SpatialDialect.MySql.ToWkb("g"));
        Assert.Equal("`we``ird`", SpatialDialect.MySql.Quote("we`ird"));
        Assert.Equal("[a]]b]", SpatialDialect.SqlServer.Quote("a]b"));
    }

    [Fact]
    public void BigQuery_RowsAndLoadPayload()
    {
        var schema = new TableSchema
        {
            Fields =
            [
                new TableFieldSchema { Name = "zip", Type = "STRING" },
                new TableFieldSchema { Name = "pop", Type = "INTEGER" },
                new TableFieldSchema { Name = "area", Type = "GEOGRAPHY" },
            ]
        };
        var rows = new[]
        {
            new object?[] { "10110", 22000L, "POLYGON((106.8 -6.2, 106.9 -6.2, 106.9 -6.1, 106.8 -6.2))" },
            new object?[] { "10120", null, null },
        };
        var g = BigQueryGis.FromRows(schema, rows);
        Assert.Equal(2, g.RowCount);
        Assert.Equal(4326, g.Crs!.Epsg);
        Assert.Equal("Polygon", g.Geometry[0]!.GeometryType);
        Assert.Null(g.Geometry[1]);

        var d = Districts().Head(3);
        var bqSchema = BigQueryGis.BuildSchema(d);
        Assert.Equal("GEOGRAPHY", bqSchema.Fields.Last().Type);
        var lines = BigQueryGis.ToJsonLines(d).ToArray();
        Assert.Equal(3, lines.Length);
        var first = System.Text.Json.JsonDocument.Parse(lines[0]).RootElement;
        var wkt = first.GetProperty("geog").GetString()!;
        var lonlat = Geo.FromWkt(wkt).Coordinate;
        Assert.InRange(lonlat.X, 106, 108); // reprojected from UTM 48S to WGS 84 lon/lat
        Assert.InRange(lonlat.Y, -7, -5);
        Assert.Equal(d["name"].GetValue(0), first.GetProperty("name").GetString());
    }
}
