# Vector data, CRS & IO

## Data model

- **`Column<T>`**: an immutable typed buffer (`double`, `long`, `bool`, `string?`, `DateTime`, geometry) with a bit-packed validity mask for value types. Create one with `Column.Create(name, values)`, or with `Column<long>.FromNullable(name, long?[])` when you need nulls.
- **`DataFrame`**: an ordered set of equal-length columns. Every operation returns a new frame, and projections share buffers, so `Select`/`Drop`/`Rename` don't copy data.
- **`GeoSeries`**: a `Column<Geometry?>` with a `Crs` and a lazily built STR-tree (`SIndex`).
- **`GeoDataFrame`**: a `DataFrame` with an active geometry column. Typed overloads (`Head`, `Filter`, `SortBy`, `WithColumn`, …) return `GeoDataFrame`.

```csharp
var top = districts
    .Filter<double>("green_ratio", g => g > 0.2)
    .WithColumn<double>("density", r => r.GetDouble("population") / r.GetDouble("area_km2"))
    .SortBy("density", ascending: false)
    .Head(10);

var byRegion = districts.GroupBy("region").Agg(("population", AggFunc.Sum, "pop"), ("area_km2", AggFunc.Mean, "mean_area"));
var joined   = districts.Merge(byRegion, "region", JoinHow.Left);
Console.WriteLine(districts.Describe());
```

## Geometry operations

All of these run in parallel above about 2 000 rows: `Area()`, `GeomLength()`, `GeodesicArea()`, `GeodesicLength()`, `Centroid()`, `RepresentativePoint()`, `Buffer(d)`, `Simplify(tol)`, `ConvexHull()`, `Envelope()`, `Boundary()`, `MakeValid()`, `Translate/Rotate/Scale`, `Interpolate(d)`, `Intersection/Union/Difference(geom | series)`, and predicates such as `Intersects(geom)` that return `bool[]`.

> On a geographic CRS, planar `Area()`/`GeomLength()` return degrees (a trace warning is emitted). Reproject with `ToCrs(gdf.EstimateUtmCrs())`, or use the `Geodesic*` methods.

## Spatial joins and overlay

```csharp
// which district contains each school?
var s = schools.SJoin(districts, SpatialPredicate.Within, JoinHow.Left);   // adds index_right, suffixes _left/_right
// nearest hospital (exact distance), within 5 km
var n = schools.SJoinNearest(hospitals, maxDistance: 5000, distanceColumn: "dist_m");
// polygon overlay
var inter = landuse.Overlay(floodZones, OverlayHow.Intersection);
var diff  = landuse.Overlay(floodZones, OverlayHow.Difference);
// dissolve, clip, explode, bbox selection
var regions = districts.Dissolve("region", AggFunc.Sum);
var inBox   = pois.Clip(Geo.Box(700000, 9308000, 704000, 9312000));
var parts   = regions.Explode();
var window  = pois.Cx(700000, 9308000, 704000, 9312000);
```

All of these use the STR-tree, prepared geometries and robust overlay (OverlayNG with snap-rounding fallback). CRS mismatches raise an exception instead of silently producing wrong results.

## Coordinate reference systems

```csharp
var crs  = CoordinateReferenceSystem.Parse("EPSG:23834");                       // DGN95 / Indonesia TM-3 zone 48.2
var crs2 = CoordinateReferenceSystem.Parse("+proj=utm +zone=48 +south +datum=WGS84");
var crs3 = CoordinateReferenceSystem.Parse(File.ReadAllText("parcels.prj"));    // ESRI / OGC WKT / WKT2
Console.WriteLine(crs.ToProj4());
Console.WriteLine(crs.ToWkt());

var t = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, crs);
var (x, y) = t.Transform(106.8456, -6.2088);
```

| Feature | Details |
|---|---|
| Projections | Transverse Mercator (UTM, TM-3, BNG, matches the Krüger series to < 1 mm in zone), Mercator, Web Mercator, Lambert Conformal Conic 1SP/2SP, Albers Equal-Area |
| Datums | WGS 84, GRS80 (NAD83/ETRS89/RGF93), Clarke 1866 (NAD27), International 1924 (ED50), Airy (OSGB36), DGN95; 7-parameter position-vector Helmert |
| Registry | 4326, 4269, 4258, 4267, 4230, 4755, 4171, 4277, 3857, 3395, 2154, 5070, 27700, 23830–23845, 32601–32660, 32701–32760, 26901–26923; add more with `CrsRegistry.Register` |
| Parsing | `EPSG:x`, URNs, OGC URLs, PROJ.4, OGC WKT1, ESRI WKT, WKT2 (PROJCRS/CONVERSION/METHOD) |

## File formats

| Format | Read | Write | Notes |
|---|---|---|---|
| GeoJSON | ✔ | ✔ | RFC 7946 winding order, legacy `crs` member for non-WGS 84 data |
| ESRI Shapefile | ✔ | ✔ | .shp/.shx/.dbf/.prj/.cpg, Z values, holes ↔ ring orientation, UTF-8 / Latin-1 |
| GeoPackage | ✔ | ✔ | multiple layers (`GeoPackage.ListLayers`), spec 1.4 blobs, SRS table |
| CSV | ✔ | ✔ | WKT column or lon/lat auto-detection, RFC 4180 quoting |
| DXF | ✔ | ✔ | via `GeoPandas.Net.CAD` (registered automatically by `Gpd`) |

Register your own driver: `GeoIO.Register(new GeoDriver("MyFormat", [".myf"], reader, writer));`

## Plotting

```csharp
var scene = new MapScene { Title = "Coverage", ShowFrame = true, Padding = 46 }
    .AddLayer(districts, new LayerStyle { Column = "population", ColorMap = ColorMaps.YlOrRd, Scheme = ClassificationScheme.NaturalBreaks })
    .AddLayer(stations, new LayerStyle { Fill = "#b0306a", SizeColumn = "riders", LabelColumn = "name" });
scene.SaveSvg("coverage.svg", 1200, 800);
```

Classification schemes are `Quantiles`, `EqualInterval`, `NaturalBreaks` (Jenks) and `Continuous`. Colour maps include viridis, magma, plasma, inferno, cividis, YlOrRd, YlGnBu, Blues, Greens, RdYlGn, RdBu, Spectral, terrain, gray and ndvi, and any of them can be reversed with `_r`.
