# Getting started

## Install

```bash
dotnet new console -n MyGisApp && cd MyGisApp
dotnet add package GeoPandas.Net
```

Or start from a template:

```bash
dotnet new install GeoPandas.Net.Templates
dotnet new gpd-console -n MyAnalysis     # analysis script
dotnet new gpd-blazor  -n MyWebMap       # Blazor Server map app
dotnet new gpd-avalonia -n MyDesktopMap  # Avalonia desktop map app
```

## Your first GeoDataFrame

```csharp
using GeoPandas.Net;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;

var cities = GeoDataFrame.FromGeometries(
    [Geo.Point(106.8456, -6.2088), Geo.Point(107.6191, -6.9175), Geo.Point(112.7521, -7.2575)],
    CoordinateReferenceSystem.Wgs84,
    Column.Create("name", new[] { "Jakarta", "Bandung", "Surabaya" }),
    Column.Create("pop_mio", new[] { 34.5, 8.6, 10.0 }));

Console.WriteLine(cities);          // pretty table + CRS
Console.WriteLine(cities.Info());   // schema
```

From a table with coordinates:

```csharp
var df  = GeoPandas.Net.Data.Csv.ReadDataFrame("stations.csv");     // lon, lat, name, ...
var gdf = GeoDataFrame.FromXY(df, "lon", "lat", CoordinateReferenceSystem.Wgs84);
```

## Read, transform, write

```csharp
var roads = Gpd.ReadFile("roads.shp");              // .prj → CRS automatically
var utm   = roads.ToCrs(roads.EstimateUtmCrs());    // WGS 84 / UTM zone 48S for Jakarta
var main  = utm.Filter<string?>("highway", h => h is "primary" or "secondary");
Console.WriteLine($"{main.GeomLength().Sum() / 1000:F1} km of main roads");
main.ToFile("main_roads.gpkg");                      // GeoPackage
main.ToCrs(4326).ToFile("main_roads.geojson");       // RFC 7946 GeoJSON
```

## Your first map

```csharp
using GeoPandas.Net.Plotting;

var map = utm.Plot(new LayerStyle { Column = "highway", Categorical = true, LegendTitle = "Road class" }, "Road network");
map.ShowFrame = true;        // map-sheet frame with coordinate ticks
map.SaveSvg("roads.svg", 1000, 700);
```

`MapScene` can stack layers (`AddLayer`, `AddRaster`, `AddHillshade`), and renders to SVG, to Blazor (`(MarkupString)scene.ToSvg()`), or natively in Avalonia (see the `MapView` control in the gallery or the `gpd-avalonia` template).

## Sample data

`GeoPandas.Net.Datasets.SampleData` generates reproducible data for a fictional city near Jakarta (EPSG:32748): `Districts()`, `Roads()`, `PointsOfInterest()`, `WaterNetwork()`, `Dem()`, `Multispectral()`, `SitePlanDxf()`, and real-world `Cities()` in WGS 84.

## Next

- [Vector data, CRS & IO](vector.md)
- [Analysis](analysis.md)
