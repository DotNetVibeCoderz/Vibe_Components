using GeoPandas.Net.CAD;
using GeoPandas.Net.Data;
using GeoPandas.Net.Datasets;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

public sealed class IoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gpdnet-io-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static GeoDataFrame Mixed()
    {
        var donut = Geo.Polygon([(0, 0), (10, 0), (10, 10), (0, 10)], [(3, 3), (3, 6), (6, 6), (6, 3)]);
        return GeoDataFrame.FromGeometries([donut, Geo.Box(20, 20, 25, 30), Geo.MultiPolygon(Geo.Box(40, 0, 41, 1), Geo.Box(43, 0, 44, 1))],
            CoordinateReferenceSystem.FromEpsg(32748),
            Column.Create("name", new[] { "donut", "rect", "multi ✓" }),
            Column<long>.FromNullable("count", new long?[] { 1, null, 3 }),
            Column.Create("value", new[] { 1.5, 2.25, -3.125 }),
            Column.Create("flag", new[] { true, false, true }),
            Column.Create("date", new[] { new DateTime(2024, 1, 2), new DateTime(2025, 6, 30), new DateTime(2026, 11, 17) }));
    }

    private static void AssertSame(GeoDataFrame a, GeoDataFrame b, bool checkDate = true)
    {
        Assert.Equal(a.RowCount, b.RowCount);
        for (int i = 0; i < a.RowCount; i++)
        {
            Assert.True(a.Geometry[i]!.EqualsTopologically(b.Geometry[i]!), $"geometry {i} differs");
            Assert.Equal(a["name"].GetValue(i), b["name"].GetValue(i));
            Assert.Equal(a["value"].GetDouble(i), b["value"].GetDouble(i), 9);
            Assert.Equal(a["flag"].GetValue(i), b["flag"].GetValue(i));
            Assert.Equal(a["count"].IsNull(i), b["count"].IsNull(i));
            if (checkDate) Assert.Equal(((DateTime)a["date"].GetValue(i)!).Date, ((DateTime)b["date"].GetValue(i)!).Date);
        }
        Assert.Equal(a.Crs, b.Crs);
    }

    [Fact]
    public void GeoJson_RoundTrip()
    {
        var path = Path.Combine(_dir, "t.geojson");
        Mixed().ToFile(path);
        AssertSame(Mixed(), GeoIO.Read(path));
    }

    [Fact]
    public void Shapefile_RoundTrip_WithHolesAndMultiparts()
    {
        var path = Path.Combine(_dir, "t.shp");
        Mixed().ToFile(path);
        foreach (var ext in new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" }) Assert.True(File.Exists(Path.ChangeExtension(path, ext)));
        var back = GeoIO.Read(path);
        AssertSame(Mixed(), back);
        Assert.Equal(1, ((Polygon)back.Geometry[0]!).NumInteriorRings);
        Assert.IsType<MultiPolygon>(back.Geometry[2]);
    }

    [Fact]
    public void Shapefile_Points_And_Lines()
    {
        var roads = SampleData.Roads().Head(50);
        var path = Path.Combine(_dir, "roads.shp");
        Shapefile.Write(roads, path);
        var back = Shapefile.Read(path);
        Assert.Equal(50, back.RowCount);
        Assert.Equal(roads.Geometry.GeomLength().Sum(), back.Geometry.GeomLength().Sum(), 6);
        var pois = SampleData.PointsOfInterest(40);
        Shapefile.Write(pois, Path.Combine(_dir, "pois.shp"));
        var pb = Shapefile.Read(Path.Combine(_dir, "pois.shp"));
        Assert.Equal(pois.Geometry.X.ToDoubleArray(), pb.Geometry.X.ToDoubleArray());
        Assert.Equal(pois["category"].GetValue(7), pb["category"].GetValue(7));
    }

    [Fact]
    public void GeoPackage_RoundTrip_MultipleLayers()
    {
        var path = Path.Combine(_dir, "t.gpkg");
        GeoPackage.Write(Mixed(), path, "shapes");
        GeoPackage.Write(SampleData.PointsOfInterest(25), path, "pois");
        Assert.Equal(["pois", "shapes"], GeoPackage.ListLayers(path));
        AssertSame(Mixed(), GeoPackage.Read(path, "shapes"));
        Assert.Equal(25, GeoPackage.Read(path, "pois").RowCount);
    }

    [Fact]
    public void Csv_RoundTrip_Wkt()
    {
        var path = Path.Combine(_dir, "t.csv");
        Mixed().ToCsv(path);
        var back = Csv.Read(path, crs: CoordinateReferenceSystem.FromEpsg(32748));
        Assert.Equal(3, back.RowCount);
        Assert.True(Mixed().Geometry[0]!.EqualsTopologically(back.Geometry[0]!));
        Assert.Equal(typeof(double), back["value"].DataType);
        Assert.Equal(typeof(bool), back["flag"].DataType);
    }

    [Fact]
    public void Csv_LonLat_AutoDetect()
    {
        var path = Path.Combine(_dir, "pts.csv");
        File.WriteAllText(path, "name,lon,lat\n\"Jakarta, DKI\",106.8456,-6.2088\nBandung,107.6191,-6.9175\n");
        var gdf = Csv.Read(path);
        Assert.Equal(4326, gdf.Crs!.Epsg);
        Assert.Equal("Jakarta, DKI", gdf["name"].GetValue(0));
    }

    [Fact]
    public void Dxf_Parse_SitePlan()
    {
        var gdf = Dxf.Parse(SampleData.SitePlanDxf());
        Assert.Equal(8, gdf.Filter<string?>("layer", l => l == "PARCEL").RowCount);
        Assert.Equal(8, gdf.Filter<string?>("layer", l => l == "BUILDING").RowCount);
        Assert.All(gdf.Filter<string?>("layer", l => l == "BUILDING").Geometry, g => Assert.Equal(22 * 28, g!.Area, 6));
        var texts = gdf.Filter<string?>("entity", e => e == "TEXT");
        Assert.Contains("Lot 1", texts["text"].AsObjects());
        var arc = gdf.Filter<string?>("entity", e => e == "ARC");
        Assert.Equal(Math.PI * 20 / 2, arc.Geometry[0]!.Length, 0);
    }

    [Fact]
    public void Dxf_Georeference_And_WriteRead()
    {
        var georef = CadGeoreference.FromControlPoints((0, 0), (SampleData.CenterX, SampleData.CenterY), (100, 0), (SampleData.CenterX, SampleData.CenterY + 100));
        var gdf = Dxf.Parse(SampleData.SitePlanDxf(), new DxfReadOptions { Georeference = georef, Crs = SampleData.Crs });
        var b = gdf.Filter<string?>("layer", l => l == "BUILDING");
        Assert.Equal(22 * 28, b.Geometry[0]!.Area, 4); // similarity transform keeps area at scale 1
        Assert.InRange(gdf.TotalBounds.MinX, SampleData.CenterX - 200, SampleData.CenterX + 200);

        var path = Path.Combine(_dir, "out.dxf");
        Dxf.Write(gdf, path, layerColumn: "layer");
        var back = Dxf.Read(path);
        Assert.Equal(gdf.Filter<string?>("layer", l => l == "BUILDING").RowCount, back.Filter<string?>("layer", l => l == "BUILDING").RowCount);
        Assert.Equal(b.Area().Sum(), back.Filter<string?>("layer", l => l == "BUILDING").Area().Sum(), 3);
    }
}
