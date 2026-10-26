using GDAL.Net.Srs;
using GDAL.Net.Vector;

namespace GDAL.Net.Tests;

public class VectorTests
{
    internal static readonly (string Name, double Lon, double Lat, int Population)[] Cities =
    [
        ("Jakarta", 106.8456, -6.2088, 10_560_000),
        ("Bandung", 107.6191, -6.9175, 2_444_000),
        ("Surabaya", 112.7521, -7.2575, 2_874_000),
        ("Yogyakarta", 110.3695, -7.7956, 373_000),
        ("Semarang", 110.4203, -6.9667, 1_653_000),
    ];

    internal static Dataset CreateCities(string path, string driver = "GPKG")
    {
        var ds = Gdal.GetDriver(driver).CreateVector(path);
        using var srs = SpatialReference.Wgs84;
        var layer = ds.CreateLayer("cities", srs, GeometryType.Point);
        layer.CreateField("name", FieldType.String, 50);
        layer.CreateField("population", FieldType.Integer64);
        layer.CreateField("founded", FieldType.Date);
        foreach (var c in Cities)
        {
            using var p = Geometry.Point(c.Lon, c.Lat);
            layer.AddFeature(p, new Dictionary<string, object?> { ["name"] = c.Name, ["population"] = (long)c.Population, ["founded"] = new DateOnly(1527, 6, 22) });
        }
        return ds;
    }

    [Fact]
    public void CreateAndReadGeoPackage()
    {
        var path = Path.Combine(TestData.NewTempDir(), "cities.gpkg");
        using (CreateCities(path)) { }

        using var ds = Gdal.OpenVector(path);
        Assert.Equal(1, ds.LayerCount);
        var layer = ds.GetLayer("cities");
        Assert.Equal(5, layer.FeatureCount);
        Assert.Equal(GeometryType.Point, layer.GeometryType);
        Assert.Equal(["name", "population", "founded"], layer.Fields.Select(f => f.Name));
        Assert.Equal(4326, layer.SpatialReference!.Epsg);

        var names = new List<string>();
        layer.ForEach(f => names.Add(f.GetString("name")));
        Assert.Contains("Bandung", names);

        layer.SetAttributeFilter("population > 2000000");
        Assert.Equal(3, layer.FeatureCount);
        layer.SetAttributeFilter(null);

        layer.SetSpatialFilter(new Envelope(107, -8, 111, -6));
        Assert.Equal(3, layer.FeatureCount);
        layer.SetSpatialFilter((Geometry?)null);

        var records = layer.ReadRecords();
        var jakarta = records.Single(r => (string)r.Attributes["name"]! == "Jakarta");
        Assert.Equal(10_560_000L, jakarta.Attributes["population"]);
        Assert.StartsWith("POINT", jakarta.GeometryWkt);
        Assert.Equal(new DateTimeOffset(1527, 6, 22, 0, 0, 0, TimeSpan.Zero), records[0].Attributes["founded"]);
    }

    [Fact]
    public void SqlQueries()
    {
        using var ds = CreateCities(Path.Combine(TestData.NewTempDir(), "sql.gpkg"));

        using (var r = ds.ExecuteSql("SELECT name, population FROM cities WHERE population > 1000000 ORDER BY population DESC")!)
        {
            var rows = r.ReadRecords();
            Assert.Equal(4, rows.Count);
            Assert.Equal("Jakarta", rows[0].Attributes["name"]);
        }

        using (var r = ds.ExecuteSql("SELECT COUNT(*) AS n, SUM(population) AS total FROM cities", dialect: "SQLITE")!)
        {
            var row = r.ReadRecords().Single();
            Assert.Equal(5L, Convert.ToInt64(row.Attributes["n"]));
        }

        using (var r = ds.ExecuteSql("SELECT name, ST_Buffer(geom, 0.5) AS geom FROM cities WHERE name = 'Bandung'", dialect: "SQLITE")!)
        {
            Assert.Equal(1, r.FeatureCount);
        }
    }

    [Fact]
    public void Transactions()
    {
        var path = Path.Combine(TestData.NewTempDir(), "tx.gpkg");
        using (CreateCities(path)) { }
        using var ds = Gdal.OpenVector(path, update: true);
        var layer = ds.GetLayer("cities");
        using (var tx = ds.BeginTransaction())
        {
            using var p = Geometry.Point(119.4, -5.1);
            layer.AddFeature(p, new Dictionary<string, object?> { ["name"] = "Makassar" });
            // disposed without commit → rollback
        }
        Assert.Equal(5, layer.FeatureCount);
        using (var tx = ds.BeginTransaction())
        {
            using var p = Geometry.Point(119.4, -5.1);
            layer.AddFeature(p, new Dictionary<string, object?> { ["name"] = "Makassar" });
            tx.Commit();
        }
        Assert.Equal(6, layer.FeatureCount);
    }

    [Fact]
    public void GeometryOperations()
    {
        using var a = Geometry.FromWkt("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))");
        using var b = Geometry.FromWkt("POLYGON ((5 5, 15 5, 15 15, 5 15, 5 5))");
        Assert.Equal(100, a.Area, 6);
        Assert.True(a.Intersects(b));
        Assert.False(a.Contains(b));

        using var inter = a.Intersection(b);
        Assert.Equal(25, inter.Area, 6);
        using var union = a.Union(b);
        Assert.Equal(175, union.Area, 6);
        using var diff = a.Difference(b);
        Assert.Equal(75, diff.Area, 6);
        using var sym = a.SymDifference(b);
        Assert.Equal(150, sym.Area, 6);

        using var c = a.Centroid();
        Assert.Equal(5, c.X, 9);
        using var hull = Geometry.FromWkt("MULTIPOINT ((0 0), (4 0), (2 3), (2 1))").ConvexHull();
        Assert.Equal(6, hull.Area, 6);

        using var buf = Geometry.Point(0, 0).Buffer(1, 64);
        Assert.Equal(Math.PI, buf.Area, 2);

        using var line = Geometry.LineString((0, 0), (1, 0.001), (2, 0), (3, 0.001), (4, 0));
        using var simple = line.Simplify(0.01);
        Assert.Equal(2, simple.PointCount);

        using var dissolved = Geometry.UnionAll([a, b]);
        Assert.Equal(175, dissolved.Area, 6);

        using var invalid = Geometry.FromWkt("POLYGON ((0 0, 10 10, 10 0, 0 10, 0 0))");
        Assert.False(invalid.IsValid);
        using var valid = invalid.MakeValid();
        Assert.True(valid.IsValid);
    }

    [Fact]
    public void GeometrySerialization()
    {
        using var poly = Geometry.Polygon([(0, 0), (4, 0), (4, 3), (0, 3)]);
        var wkb = poly.ToWkb();
        using var fromWkb = Geometry.FromWkb(wkb);
        Assert.True(poly.Equals(fromWkb));
        var json = poly.ToGeoJson(2);
        Assert.Contains("\"Polygon\"", json);
        using var fromJson = Geometry.FromGeoJson(json);
        Assert.Equal(12, fromJson.Area, 9);
        Assert.Contains("<gml:", poly.ToGml());
        Assert.Contains("<Polygon>", poly.ToKml());
        Assert.Equal(5, poly.GetGeometry(0).PointCount);
        Assert.Equal((4.0, 3.0), poly.GetGeometry(0).GetPoints()[2]);
    }

    [Fact]
    public void ReprojectGeometry()
    {
        using var p = Geometry.Point(107.6191, -6.9175);
        p.SpatialReference = SpatialReference.Wgs84;
        p.TransformTo(SpatialReference.FromEpsg(32748));
        Assert.InRange(p.X, 788_000, 791_000);
        Assert.InRange(p.Y, 9_234_000, 9_236_000);
    }

    [Fact]
    public void LayerOverlay()
    {
        using var ds = Gdal.CreateMemoryVector();
        var input = ds.CreateLayer("input", null, GeometryType.Polygon);
        var method = ds.CreateLayer("method", null, GeometryType.Polygon);
        var result = ds.CreateLayer("result", null, GeometryType.Polygon);
        using (var g = Geometry.FromWkt("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))")) input.AddFeature(g);
        using (var g = Geometry.FromWkt("POLYGON ((5 0, 15 0, 15 10, 5 10, 5 0))")) method.AddFeature(g);
        input.Overlay(GDAL.Net.LayerOverlay.Intersection, method, result);
        Assert.Equal(1, result.FeatureCount);
        using var f = result.GetNextFeature()!;
        Assert.Equal(50, f.Geometry!.Area, 6);
    }

    [Fact]
    public void FeatureOutlivesItsGeometryAccess()
    {
        using var ds = CreateCities(Path.Combine(TestData.NewTempDir(), "life.gpkg"));
        var layer = ds.GetLayer(0);
        Geometry clone;
        using (var f = layer.GetFeature(1)!)
        {
            clone = f.Geometry!.Clone();
            var borrowed = f.Geometry!;
            f.Dispose();
            Assert.Throws<ObjectDisposedException>(() => borrowed.X);
        }
        Assert.Equal(106.8456, clone.X, 4);
        clone.Dispose();
    }
}
