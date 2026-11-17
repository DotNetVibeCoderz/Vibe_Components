using System.Text.Json.Nodes;
using GeoPandas.Net.Arrow;
using GeoPandas.Net.Data;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Lazy;
using GeoPandas.Net.PointCloud;
using GeoPandas.Net.Raster;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

/// <summary>Phase 2 formats: FlatGeobuf, GeoParquet, Arrow IPC, NetCDF, LAS/LAZ — round trips and interop fixtures.</summary>
public sealed class FormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gpdnet-fmt-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);
    private static JsonNode Reference => JsonNode.Parse(File.ReadAllText(Data("reference.json")))!;

    private static GeoDataFrame Mixed() => GeoDataFrame.FromGeometries(
        [
            Geo.Polygon([(0, 0), (10, 0), (10, 10), (0, 10)], [(3, 3), (3, 6), (6, 6), (6, 3)]),
            Geo.MultiPolygon(Geo.Box(40, 0, 41, 1), Geo.Box(43, 0, 44, 1)),
            Geo.LineString((0, 0), (5, 5), (7, 1)),
            null,
            Geo.Point(1, 2),
            Geo.MultiLineString(Geo.LineString((0, 0), (1, 1)), Geo.LineString((2, 2), (3, 5)))
        ],
        CoordinateReferenceSystem.FromEpsg(32748),
        Column.Create("name", new string?[] { "donut", "multi ✓", "line", null, "pt", "" }),
        Column<long>.FromNullable("count", new long?[] { 1, null, 3, 4, 5, 6 }),
        Column<double>.FromNullable("value", new double?[] { 1.5, 2.25, null, -3.125, 0, 1e300 }),
        Column.Create("flag", new[] { true, false, true, false, true, false }),
        Column.Create("date", new[] { new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2025, 6, 30), new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), new DateTime(2000, 1, 1), new DateTime(1999, 12, 31), new DateTime(2020, 2, 29) }));

    private static void AssertSameRows(GeoDataFrame a, GeoDataFrame b, string key = "name")
    {
        Assert.Equal(a.RowCount, b.RowCount);
        var byKey = Enumerable.Range(0, b.RowCount).ToDictionary(i => b[key].GetValue(i)?.ToString() ?? "<null>", i => i);
        for (int i = 0; i < a.RowCount; i++)
        {
            int j = byKey[a[key].GetValue(i)?.ToString() ?? "<null>"];
            Assert.Equal(a.Geometry[i] is null, b.Geometry[j] is null);
            if (a.Geometry[i] is { } g) Assert.True(g.EqualsExact(b.Geometry[j]!), $"geometry of row {i} differs");
            foreach (var c in a.ColumnNames.Where(n => n != a.GeometryColumnName))
            {
                var va = a[c].GetValue(i); var vb = b[c].GetValue(j);
                if (va is DateTime da) Assert.Equal(da.Ticks, ((DateTime)vb!).Ticks);
                else if (va is double or long) Assert.Equal(System.Convert.ToDouble(va), System.Convert.ToDouble(vb), 9);
                else Assert.Equal(va, vb);
            }
        }
        Assert.Equal(a.Crs, b.Crs);
    }

    // ------------------------------------------------------------------ FlatGeobuf

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FlatGeobuf_RoundTrip_KeepsTypesNullsAndCrs(bool index)
    {
        var src = Mixed();
        FlatGeobuf.Write(src, P("m.fgb"), new FlatGeobufWriteOptions { SpatialIndex = index });
        var back = FlatGeobuf.Read(P("m.fgb"));
        AssertSameRows(src, back);
        var info = FlatGeobuf.ReadInfo(P("m.fgb"));
        Assert.Equal((ulong)6, info.FeatureCount);
        Assert.Equal("Unknown", info.GeometryType); // mixed types
        Assert.Equal(index, info.HasIndex);
        if (!index) Assert.Equal(src["name"].AsObjects(), back["name"].AsObjects()); // row order kept without index
    }

    [Fact]
    public void FlatGeobuf_BboxQuery_MatchesBruteForce_OnManyPoints()
    {
        var rng = new Random(5);
        var pts = Enumerable.Range(0, 20_000).Select(_ => (NetTopologySuite.Geometries.Geometry?)Geo.Point(rng.NextDouble() * 1000, rng.NextDouble() * 1000)).ToArray();
        var gdf = GeoDataFrame.FromGeometries(pts, null, Column.Create("id", Enumerable.Range(0, pts.Length).Select(i => (long)i).ToArray()));
        FlatGeobuf.Write(gdf, P("pts.fgb"));
        for (int k = 0; k < 20; k++)
        {
            double x = rng.NextDouble() * 900, y = rng.NextDouble() * 900, w = rng.NextDouble() * 200;
            var box = new Envelope(x, x + w, y, y + w);
            var hit = FlatGeobuf.Read(P("pts.fgb"), box);
            var expect = gdf.Geometry.Count(g => g!.EnvelopeInternal.Intersects(box));
            Assert.Equal(expect, hit.RowCount);
            Assert.All(hit.Geometry, g => Assert.True(g!.EnvelopeInternal.Intersects(box)));
        }
    }

    [Fact]
    public void FlatGeobuf_ReadsGdalFiles()
    {
        var mixed = FlatGeobuf.Read(Data("gdal_mixed.fgb"));
        Assert.Equal(3, mixed.RowCount);
        Assert.Equal(32748, mixed.Crs!.Epsg);
        var donut = mixed.Filter(r => (string?)r["k"] == "donut").Geometry[0]!;
        Assert.Equal(91, donut.Area, 9); // 10×10 minus 3×3 hole
        var multi = (MultiPolygon)mixed.Filter(r => (string?)r["k"] == "multi").Geometry[0]!;
        Assert.Equal(2, multi.NumGeometries);
        Assert.Equal(1, ((Polygon)multi.GetGeometryN(1)).NumInteriorRings);

        var pts = FlatGeobuf.Read(Data("gdal_points.fgb"));
        Assert.Equal(200, pts.RowCount);
        Assert.Equal(4326, pts.Crs!.Epsg);
        Assert.True(FlatGeobuf.ReadInfo(Data("gdal_points.fgb")).HasIndex);
        var box = new Envelope(106.75, 106.8, -6.25, -6.15);
        Assert.Equal(Reference["points_bbox_106.75_-6.25_106.8_-6.15"]!.GetValue<int>(), FlatGeobuf.Read(Data("gdal_points.fgb"), box).RowCount);
        Assert.IsType<DateTime>(pts["when"].GetValue(0));
        var id0 = Enumerable.Range(0, pts.RowCount).Single(i => pts["id"].GetDouble(i) == 0); // GDAL stores rows in Hilbert order
        Assert.True(pts["name"].IsNull(id0)); // i % 7 == 0 → null
    }

    [Fact]
    public void FlatGeobuf_IsRegisteredInGeoIO_AndScansLazily()
    {
        var d = SampleData.Districts();
        d.ToFile(P("d.fgb"));
        Assert.Equal(d.RowCount, GeoIO.Read(P("d.fgb")).RowCount);
        var b = d.TotalBounds;
        var box = new Envelope(b.MinX, b.Centre.X, b.MinY, b.Centre.Y);
        var lazy = GeoIO.Scan(P("d.fgb")).FilterBbox(box).Select("name");
        Assert.Contains("R-tree", lazy.Explain());
        Assert.Contains("columns: [name]", lazy.Explain());
        var res = lazy.Collect();
        Assert.Equal(d.Geometry.Count(g => g!.EnvelopeInternal.Intersects(box)), res.RowCount);
        Assert.Equal(["name", "geometry"], res.ColumnNames);
    }

    // ------------------------------------------------------------------ GeoParquet & Arrow

    [Fact]
    public void GeoParquet_RoundTrip_WithCoveringAndRowGroups()
    {
        var src = Mixed();
        GeoParquet.Write(src, P("m.parquet"), new GeoParquetWriteOptions { RowGroupSize = 2 });
        var info = GeoParquet.ReadInfo(P("m.parquet"));
        Assert.Equal(3, info.RowGroups);
        Assert.Equal("bbox", info.CoveringColumn);
        Assert.Equal(32748, info.Crs!.Epsg);
        AssertSameRows(src, GeoParquet.Read(P("m.parquet")));
        var geo = JsonNode.Parse(info.GeoMetadataJson!)!;
        Assert.Equal("1.1.0", geo["version"]!.GetValue<string>());
        Assert.Equal("ProjectedCRS", geo["columns"]!["geometry"]!["crs"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void GeoParquet_BboxAndAttributePushdown_PruneRowGroups()
    {
        var roads = SampleData.Roads();
        GeoParquet.Write(roads, P("roads.parquet"), new GeoParquetWriteOptions { RowGroupSize = 25 });
        var b = roads.TotalBounds;
        var box = new Envelope(b.MinX, b.Centre.X, b.MinY, b.Centre.Y);
        Assert.Equal(roads.Geometry.Count(g => g!.EnvelopeInternal.Intersects(box)), GeoParquet.Read(P("roads.parquet"), box).RowCount);

        var lazy = GeoIO.Scan(P("roads.parquet")).Where("highway", CompareOp.Eq, "primary").Select("name");
        Assert.Contains("where: highway = 'primary'", lazy.Explain());
        Assert.Equal(roads.Filter(r => (string?)r["highway"] == "primary").RowCount, lazy.Count());
        Assert.Equal(5, GeoIO.Scan(P("roads.parquet")).Limit(5).Collect().RowCount);
    }

    [Fact]
    public void GeoParquet_ReadsGeoPandasFile()
    {
        var info = GeoParquet.ReadInfo(Data("geopandas_points.parquet"));
        Assert.Equal(200, info.Rows);
        Assert.Equal(4, info.RowGroups);
        Assert.Equal("bbox", info.CoveringColumn);
        Assert.Equal(4326, info.Crs!.Epsg);
        var g = GeoParquet.Read(Data("geopandas_points.parquet"));
        Assert.Equal(["id", "v", "name", "cat", "when", "geometry"], g.ColumnNames);
        Assert.Equal("a", g["cat"].GetValue(0));
        Assert.Equal(new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc), (DateTime)g["when"].GetValue(1)!);
        var box = new Envelope(106.75, 106.8, -6.25, -6.15);
        Assert.Equal(Reference["points_bbox_106.75_-6.25_106.8_-6.15"]!.GetValue<int>(), GeoParquet.Read(Data("geopandas_points.parquet"), box).RowCount);
    }

    [Fact]
    public void ArrowIpc_RoundTrip_FileAndStream_AndReadsPyArrowLz4()
    {
        var src = Mixed();
        src.WriteArrowFile(P("m.arrow"));
        AssertSameRows(src, ArrowInterop.ReadGeoArrowFile(P("m.arrow")));

        using var ms = new MemoryStream();
        src.WriteArrowStream(ms);
        ms.Position = 0;
        AssertSameRows(src, (GeoDataFrame)ArrowInterop.ReadArrowStream(ms));

        var batch = src.ToRecordBatch();
        Assert.Equal(src.RowCount, batch.Length);
        Assert.Equal("geoarrow.wkb", batch.Schema.GetFieldByName("geometry").Metadata["ARROW:extension:name"]);

        var py = ArrowInterop.ReadGeoArrowFile(Data("geopandas_points.feather"));
        Assert.Equal(200, py.RowCount);
        Assert.Equal(4326, py.Crs!.Epsg);
    }

    // ------------------------------------------------------------------ NetCDF

    [Theory]
    [InlineData("cf_grid_nc4.nc", "NetCDF-4 (HDF5)")]
    [InlineData("cf_grid_cdf5.nc", "CDF-5 (64-bit data)")]
    public void NetCdf_ReadsCfGrids(string file, string format)
    {
        using var nc = NetCdfDataset.Open(Data(file));
        Assert.Equal(format, nc.Format);
        Assert.Equal(2, nc.Dimensions.Single(d => d.Name == "time").Length);
        var times = nc.Times()!;
        Assert.Equal(new DateTime(2024, 1, 1, 6, 0, 0, DateTimeKind.Utc), times[1]);
        var r = nc.ToRaster("t2m", 1);
        Assert.Equal((8, 6), (r.Width, r.Height));
        Assert.Equal(4326, r.Crs!.Epsg);
        // north-up: row 0 is lat index 5; value = 273.15 + (100 + 10·5 + 0)·0.01
        Assert.Equal(273.15 + 1.50, r[0, 0, 0], 3);
        Assert.Equal(273.15 + 1.07, r[0, 5, 7], 3);
        Assert.Equal(105.75, r.Transform.OriginX, 9);
        Assert.Equal(0.5, r.CellSizeX, 9);
        var all = nc.ToRaster("t2m");
        Assert.Equal(2, all.BandCount);
        Assert.True(float.IsNaN(all[0, 5, 0])); // masked fill value (time 0, lat 0, lon 0)
    }

    [Fact]
    public void NetCdf_ClassicWriter_RoundTrips()
    {
        var dem = SampleData.Dem(64, 48);
        NetCdf.WriteClassic(dem, P("dem.nc"), "elevation", units: "m");
        using var nc = NetCdfDataset.Open(P("dem.nc"));
        Assert.Equal("CDF-1 (classic)", nc.Format);
        Assert.Equal("m", nc.Variable("elevation").Units);
        var back = nc.ToRaster("elevation");
        Assert.Equal(dem.Transform, back.Transform);
        Assert.Equal(dem.Crs, back.Crs);
        Assert.Equal(dem.Bands[0], back.Bands[0]);
        Assert.Contains("elevation(y, x)", nc.Describe());
    }

    // ------------------------------------------------------------------ LAS / LAZ

    private static PointCloud.PointCloud Cloud(int n = 5000)
    {
        var rng = new Random(9);
        var x = new double[n]; var y = new double[n]; var z = new double[n];
        for (int i = 0; i < n; i++) { x[i] = 700000 + rng.NextDouble() * 200; y[i] = 9300000 + rng.NextDouble() * 100; z[i] = 20 + rng.NextDouble() * 15; }
        var cls = Enumerable.Range(0, n).Select(i => (byte)(i % 3 == 0 ? LasClass.Ground : i % 3 == 1 ? LasClass.HighVegetation : LasClass.Building)).ToArray();
        for (int i = 0; i < n; i++) if (cls[i] == LasClass.Ground) z[i] = 20 + x[i] * 0 + (y[i] - 9300000) * 0.01;
        return new PointCloud.PointCloud(x, y, z, CoordinateReferenceSystem.FromEpsg(32748))
        {
            Classification = cls,
            Intensity = Enumerable.Range(0, n).Select(i => (ushort)(i % 4096)).ToArray(),
            GpsTime = Enumerable.Range(0, n).Select(i => i * 0.25).ToArray(),
            Red = Enumerable.Repeat((ushort)100, n).ToArray(), Green = Enumerable.Repeat((ushort)200, n).ToArray(), Blue = Enumerable.Repeat((ushort)300, n).ToArray()
        };
    }

    [Theory]
    [InlineData(0, "las")]
    [InlineData(1, "las")]
    [InlineData(2, "las")]
    [InlineData(3, "las")]
    [InlineData(6, "las")]
    [InlineData(7, "las")]
    [InlineData(8, "las")]
    [InlineData(1, "laz")]
    [InlineData(3, "laz")]
    public void Las_RoundTrip_AllWritableFormats(int format, string ext)
    {
        var src = Cloud();
        Las.Write(src, P($"c{format}.{ext}"), format);
        var info = Las.ReadInfo(P($"c{format}.{ext}"));
        Assert.Equal(format, info.PointFormat);
        Assert.Equal(ext == "laz", info.Compressed);
        Assert.Equal(32748, info.Crs!.Epsg);
        var back = Las.Read(P($"c{format}.{ext}"));
        Assert.Equal(src.Count, back.Count);
        for (int i = 0; i < src.Count; i += 97)
        {
            Assert.Equal(src.X[i], back.X[i], 3);
            Assert.Equal(src.Z[i], back.Z[i], 3);
            Assert.Equal(src.Classification[i], back.Classification[i]);
            Assert.Equal(src.Intensity[i], back.Intensity[i]);
        }
        Assert.Equal(format is 1 or 3 or 6 or 7 or 8, back.GpsTime is not null);
        Assert.Equal(format is 2 or 3 or 7 or 8, back.HasColor);
        Assert.Equal(src.Count / 3 + 1, Las.Read(P($"c{format}.{ext}"), new LasReadOptions { Classes = [LasClass.Ground] }).Count);
    }

    [Fact]
    public void Las_ReadsLaspyFiles()
    {
        var f6 = Las.Read(Data("laspy_f6.las"));
        Assert.Equal(300, f6.Count);
        Assert.Equal("1.4", f6.Source!.Version);
        Assert.Equal(32748, f6.Crs!.Epsg);
        Assert.Equal(150, f6.ByClass(LasClass.Ground).Count);
        Assert.Equal(0.5, f6.GpsTime![1], 9);

        var f3 = Las.Read(Data("laspy_f3.laz"));
        Assert.True(f3.Source!.Compressed);
        Assert.Equal(300, f3.Count);
        Assert.Equal((ushort)2000, f3.Green![10]);
        Assert.Equal(32748, f3.Crs!.Epsg);
    }

    [Fact]
    public void PointCloud_Rasterises_Indexes_AndConverts()
    {
        var pc = Cloud();
        var dsm = pc.Dsm(5);
        var dtm = pc.Dtm(5);
        Assert.True(dsm.Statistics().Max >= dtm.Statistics().Max);
        Assert.InRange(dtm.SampleBilinear(700100, 9300050), 20.45, 20.56); // ground plane z = 20 + 0.01·dy
        var hag = pc.HeightAboveGround(dtm);
        Assert.True(hag.ByClass(LasClass.Ground).Z.Where(v => !double.IsNaN(v)).All(v => Math.Abs(v) < 0.2));
        var crop = pc.Crop(new Envelope(700000, 700050, 9300000, 9300050));
        Assert.Equal(Enumerable.Range(0, pc.Count).Count(i => pc.X[i] <= 700050 && pc.Y[i] <= 9300050), crop.Count);
        Assert.True(pc.Thin(10).Count < pc.Count);
        var near = pc.Radius(pc.X[0], pc.Y[0], pc.Z[0], 3);
        Assert.Contains(0, near);
        var gdf = pc.Crop(new Envelope(700000, 700020, 9300000, 9300020)).ToGeoDataFrame();
        Assert.True(Geo.HasZ(gdf.Geometry[0]!));
        Assert.Equal(gdf.RowCount, PointCloud.PointCloud.FromGeoDataFrame(gdf, classColumn: "classification").Count);
        Assert.Equal(3, pc.ClassCounts().Count);
    }
}
