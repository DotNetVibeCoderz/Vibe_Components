# Vector

This page covers layers, fields and features, building and exporting geometries, GEOS geoprocessing, filters, transactions, SQL and layer overlays (namespace `GDAL.Net.Vector`).

## Open and explore

```csharp
using GDAL.Net;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

using var ds = Gdal.OpenVector("cities.gpkg");          // update: true for editing
foreach (var layer in ds.Layers)
{
    Console.WriteLine($"{layer.Name}: {layer.GeometryType}, {layer.FeatureCount} features, extent {layer.Extent}");
    foreach (var f in layer.Fields) Console.WriteLine($"  {f.Name} {f.Type}({f.Width})");
}
var cities = ds.GetLayer("cities");                     // or GetLayer(0), TryGetLayer(name)
Console.WriteLine(cities.SpatialReference);              // a managed copy
```

## Read features

```csharp
// Disposes each feature for you
cities.ForEach(f => Console.WriteLine($"{f.Id}: {f.GetString("name")} {f.GetInt64("population"):N0}"));

// Manual iteration: features are owned by you
foreach (var f in cities)
{
    using (f)
    {
        object? value = f["population"];             // natural CLR type (long here)
        Geometry? g = f.Geometry;                     // borrowed: valid while f lives
        Console.WriteLine($"{value} {g?.ToWkt()}");
    }
}

// Random access and detached snapshots
using var first = cities.GetFeature(1);
List<FeatureRecord> records = cities.ReadRecords(maxFeatures: 1000);
```

`FeatureRecord` holds `Id`, `Attributes`, `GeometryWkt`, `Envelope` and `GeometryType` without any native handles, which makes it ideal for UI binding and LINQ.

Typed getters: `GetString`, `GetInt`, `GetInt64`, `GetDouble`, `GetDateTime`, `GetBinary`, `IsNull`, `GetValue`, `GetAttributes()`.

## Create layers, fields and features

```csharp
using var gpkg = Gdal.GetDriver("GPKG").CreateVector("places.gpkg");
using var wgs84 = SpatialReference.Wgs84;
var layer = gpkg.CreateLayer("places", wgs84, GeometryType.Point);
layer.CreateField("name", FieldType.String, width: 50);
layer.CreateField("population", FieldType.Integer64);
layer.CreateField("founded", FieldType.Date);

// Convenience: geometry + attribute dictionary
using (var p = Geometry.Point(106.8456, -6.2088))
{
    long fid = layer.AddFeature(p, new Dictionary<string, object?>
    {
        ["name"] = "Jakarta",
        ["population"] = 10_679_951L,
        ["founded"] = new DateOnly(1527, 6, 22),
    });
}

// Full control
using (var defn = layer.Definition)
using (var feature = new Feature(defn))
{
    feature["name"] = "Bandung";
    using var g = Geometry.Point(107.6191, -6.9175);
    feature.Geometry = g;                 // copies
    layer.CreateFeature(feature);
}
```

Update with `layer.SetFeature(feature)`, delete with `layer.DeleteFeature(fid)`, and remove fields with `layer.DeleteField(index)`.

## Geometry

```csharp
using var a = Geometry.FromWkt("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))");
using var b = Geometry.Polygon([(5, 5), (15, 5), (15, 15), (5, 15)]);   // ring closed automatically
using var line = Geometry.LineString((0, 0), (1, 1), (2, 0));
using var pt = Geometry.Point(3, 4);
using var rect = Geometry.FromEnvelope(new Envelope(0, 0, 2, 1));
using var fromJson = Geometry.FromGeoJson("""{"type":"Point","coordinates":[107.6,-6.9]}""");
using var fromWkb = Geometry.FromWkb(a.ToWkb());

string wkt = a.ToWkt();               // ISO WKT
byte[] wkb = a.ToWkb();               // ISO WKB, little endian
string json = a.ToGeoJson(coordinatePrecision: 6);
string gml = a.ToGml();
string kml = a.ToKml();

Console.WriteLine($"{a.GeometryType} area={a.Area} length={line.Length} points={line.PointCount} valid={a.IsValid}");
(double X, double Y)[] vertices = line.GetPoints();
foreach (var ring in a.Parts) Console.WriteLine(ring.PointCount);
```

### Predicates and operations (GEOS)

```csharp
bool hit = a.Intersects(b);            // also Contains, Within, Touches, Crosses, Overlaps, Disjoint, Equals
double dist = a.Distance(pt);

using var inter = a.Intersection(b);   // 25
using var union = a.Union(b);          // 175
using var diff = a.Difference(b);
using var sym = a.SymDifference(b);
using var buffer = pt.Buffer(1, quadrantSegments: 64);
using var hull = union.ConvexHull();
using var concave = union.ConcaveHull(ratio: 0.3);
using var centroid = a.Centroid();
using var inside = a.PointOnSurface();
using var simple = line.Simplify(0.01);           // or SimplifyPreserveTopology
using var valid = Geometry.FromWkt("POLYGON ((0 0, 10 10, 10 0, 0 10, 0 0))").MakeValid();
using var dissolved = Geometry.UnionAll([a, b]);  // dissolve many geometries
using var tin = Geometry.FromWkt("MULTIPOINT ((0 0), (4 0), (2 3))").DelaunayTriangulation();
```

Reproject geometries with `TransformTo(srs)` (requires an assigned `SpatialReference`) or `Transform(coordinateTransformation)`:

```csharp
using var city = Geometry.Point(107.6191, -6.9175);
city.SpatialReference = SpatialReference.Wgs84;
city.TransformTo(SpatialReference.FromEpsg(32748));   // UTM 48S, metres
```

## Filters

```csharp
cities.SetAttributeFilter("population > 2000000 AND island = 'Java'");
cities.SetSpatialFilter(new Envelope(107, -8, 111, -6));   // or a Geometry
long n = cities.FeatureCount;
cities.SetAttributeFilter(null);
cities.SetSpatialFilter((Geometry?)null);
cities.SetIgnoredFields(["province"]);                     // speed up wide tables
```

## Transactions

```csharp
using var ds2 = Gdal.OpenVector("places.gpkg", update: true);
var places = ds2.GetLayer("places");
using (var tx = ds2.BeginTransaction())
{
    using var p = Geometry.Point(119.4, -5.1);
    places.AddFeature(p, new Dictionary<string, object?> { ["name"] = "Makassar" });
    tx.Commit();               // disposing without Commit rolls back
}
```

Batching thousands of inserts in one transaction is dramatically faster for GeoPackage, SQLite and PostGIS.

## SQL

```csharp
// OGR SQL (works on every format)
using (var r = ds.ExecuteSql("SELECT name, population FROM cities WHERE population > 1000000 ORDER BY population DESC"))
{
    foreach (var row in r!.ReadRecords()) Console.WriteLine($"{row.Attributes["name"]}");
}

// SQLite / SpatiaLite dialect: aggregates, joins, spatial functions on any format
using (var r = ds.ExecuteSql(
    "SELECT island, COUNT(*) AS cities, SUM(population) AS people FROM cities GROUP BY island ORDER BY people DESC",
    dialect: "SQLITE"))
{
    var rows = r!.ReadRecords(includeGeometry: false);
}

// Spatial filter geometry
using var area = Geometry.FromEnvelope(new Envelope(105, -8, 110, -6));
using var inArea = ds.ExecuteSql("SELECT * FROM cities", spatialFilter: area);
```

`ExecuteSql` returns a `SqlResultLayer` (dispose it) or `null` for statements without a result set (`CREATE INDEX`, `UPDATE`, …).

## Layer overlay

Overlay operations between two layers write into a result layer: `Intersection`, `Union`, `SymDifference`, `Identity`, `Update`, `Clip` and `Erase`.

```csharp
using var mem = Gdal.CreateMemoryVector();
var input = mem.CreateLayer("input", null, GeometryType.Polygon);
var method = mem.CreateLayer("method", null, GeometryType.Polygon);
var result = mem.CreateLayer("result", null, GeometryType.Polygon);
using (var g = Geometry.FromWkt("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))")) input.AddFeature(g);
using (var g = Geometry.FromWkt("POLYGON ((5 0, 15 0, 15 10, 5 10, 5 0))")) method.AddFeature(g);
input.Overlay(LayerOverlay.Intersection, method, result);
```

Other helpers: `layer.Dissolve()` unions all geometries of a layer, `layer.CollectGeometries()` returns clones, and `ds.CopyLayer(source, newName)` copies schema and features from any dataset.

## Spatial index

Use SQL: for Shapefiles `ds.ExecuteSql("CREATE SPATIAL INDEX ON cities")`. GeoPackage layers created by GDAL already have an R-tree index.

**Next:** [Coordinate systems](srs.md) · [Utilities](utilities.md) · [Analysis](analysis.md)
