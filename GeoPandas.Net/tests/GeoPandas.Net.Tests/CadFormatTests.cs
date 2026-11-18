using GeoPandas.Net.CAD;
using GeoPandas.Net.Geometry;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

/// <summary>DWG (ACadSharp) and DGN v7 readers/writers. DGN is checked against GDAL 3.12; DWG reading was checked against ezdxf on
/// ACadSharp's AC1018 sample pair (see Progress.md).</summary>
public sealed class CadFormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gpdnet-cad-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);

    [Fact]
    public void Dgn_ReadsGdalWrittenFile()
    {
        // written by GDAL 3.12's DGN driver (seed file: 0.01 master-unit resolution)
        var g = Dgn.Read(Data("gdal_shapes.dgn"));
        Assert.Equal(["LINE_STRING", "SHAPE", "LINE_STRING", "LINE"], Enumerable.Range(0, g.RowCount).Select(i => (string)g["type"].GetValue(i)!));
        Assert.Equal("LINESTRING (1000 2000, 1100 2050, 1200 2000)", g.Geometry[0]!.AsText());
        Assert.True(g.Geometry[1]!.EqualsExact(Geo.Box(0, 0, 50, 30)));
        // GDAL reads (600.75 520.1299999989569): the same integer and scale
        Assert.Equal(520.1299999989569, g.Geometry[2]!.Coordinates[1].Y, 9);
        Assert.Equal(123.46000000089407, g.Geometry[3]!.Coordinates[0].X, 9);
    }

    [Fact]
    public void Dgn_WriteReadRoundTrip()
    {
        var wave = Geo.LineString(Enumerable.Range(0, 250).Select(k => new Coordinate(k * 2.0, Math.Sin(k / 10.0) * 40)));
        var src = GeoDataFrame.FromGeometries([Geo.LineString((1000, 2000), (1100, 2050), (1200, 2000)), Geo.Box(0, 0, 50, 30), Geo.Point(123.456, 789.012), wave], null,
            Column.Create("level", new long[] { 1, 2, 3, 4 }), Column.Create("color", new long[] { 1, 3, 5, 7 }), Column.Create("text", new[] { null, null, "Hello DGN", null }));
        var path = Path.Combine(_dir, "out.dgn");
        Dgn.Write(src, path);
        var back = Dgn.Read(path);
        Assert.Equal(["LINE_STRING", "SHAPE", "TEXT", "COMPLEX_CHAIN"], Enumerable.Range(0, 4).Select(i => (string)back["type"].GetValue(i)!));
        Assert.Equal([1L, 2, 3, 4], Enumerable.Range(0, 4).Select(i => (long)back["level"].GetValue(i)!));
        Assert.Equal([1L, 3, 5, 7], Enumerable.Range(0, 4).Select(i => (long)back["color"].GetValue(i)!));
        Assert.Equal("Hello DGN", back["text"].GetValue(2));
        Assert.Equal(2.5, back["height"].GetDouble(2), 5);   // stored as an integer multiplier (UOR × 6 / 1000)
        for (int i = 0; i < 4; i++) Assert.True(back.Geometry[i]!.EqualsExact(src.Geometry[i]!, 0.0005), $"row {i}");   // 1 mm resolution
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-2.5)]
    [InlineData(123456.789012)]
    [InlineData(1e-12)]
    [InlineData(6.02214076e23)]
    public void Dgn_VaxDoubleRoundTrip(double v)
    {
        var buf = new byte[8];
        Dgn.IeeeToVax(v, buf);
        Assert.Equal(v, Dgn.VaxToIeee(buf), Math.Abs(v) * 1e-15);
        var ib = new byte[4];
        Dgn.PutInt32(ib, -123456789);
        Assert.Equal(-123456789, Dgn.Int32(ib));
    }

    [Fact]
    public void Dwg_WriteReadRoundTrip()
    {
        var hole = (Polygon)Geo.FromWkt("POLYGON ((0 0, 40 0, 40 30, 0 30, 0 0), (10 10, 20 10, 20 20, 10 20, 10 10))");
        var src = GeoDataFrame.FromGeometries([Geo.LineString((0, 0), (100, 50), (150, 20)), hole, Geo.Point(5, 6), Geo.Point(7, 8)], null,
            Column.Create("layer", new[] { "ROADS", "BUILDINGS", "POINTS", "LABELS" }), Column.Create("color", new long[] { 1, 3, 5, 7 }),
            Column.Create("label", new[] { null, null, null, "Tugu" }));
        var path = Path.Combine(_dir, "out.dwg");
        Dwg.Write(src, path, layerColumn: "layer", textColumn: "label", colorColumn: "color");
        var back = Dwg.Read(path);
        Assert.Equal(5, back.RowCount);    // the polygon's hole is a second closed polyline
        Assert.Equal(["ROADS", "BUILDINGS", "BUILDINGS", "POINTS", "LABELS"], Enumerable.Range(0, 5).Select(i => (string)back["layer"].GetValue(i)!));
        Assert.Equal(["LWPOLYLINE", "LWPOLYLINE", "LWPOLYLINE", "POINT", "TEXT"], Enumerable.Range(0, 5).Select(i => (string)back["entity"].GetValue(i)!));
        Assert.Equal(3L, back["color"].GetValue(1));
        Assert.Equal("Tugu", back["text"].GetValue(4));
        Assert.True(back.Geometry[0]!.EqualsExact(src.Geometry[0]!));
        Assert.True(back.Geometry[1]!.EqualsTopologically(Geo.Factory.CreatePolygon(hole.Shell)));   // closed polylines read as polygons
        // also via the generic IO registry
        var viaRegistry = Gpd.ReadFile(path);
        Assert.Equal(5, viaRegistry.RowCount);
    }
}
