# Analysis

## Spatial statistics (`GeoPandas.Net.Analytics`)

```csharp
var w  = SpatialWeights.Queen(districts);              // also Rook, KNearest(k), DistanceBand(d, binary, alpha)
var mi = Autocorrelation.MoransI(districts["income_mio_idr"].ToDoubleArray(), w.RowStandardize(), permutations: 999);
Console.WriteLine(mi);   // Moran's I = 0.405 (E = -0.032), z = 3.90, p(norm) = …, p(sim) = 0.002

var lisa = Autocorrelation.LocalMoran(values, w);      // Is, z-scores, pseudo p-values, HH/LL/HL/LH/ns
var hot  = Autocorrelation.HotSpots(districts, "income_mio_idr", w, significance: 0.05);  // gi_z, gi_p, hotspot
var gc   = Autocorrelation.GearysC(values, w);
```

Clustering and interpolation:

```csharp
var hubs = Clustering.Dbscan(pois, eps: 300, minPoints: 4);           // "cluster" column, -1 = noise
var km   = Clustering.KMeans(pois, k: 6);
var idw  = Interpolation.Idw(xs, ys, zs, gridX, gridY, power: 2, neighbors: 12);
var vg   = Variogram.Fit(xs, ys, zs, VariogramModel.Spherical);
var (z, variance) = Interpolation.OrdinaryKriging(xs, ys, zs, gridX, gridY, vg, neighbors: 16);
```

## Networks (`GeoPandas.Net.Network`)

```csharp
var graph = NetworkGraph.FromLines(roads, costColumn: "travel_time_s", onewayColumn: "oneway");
var route = graph.ShortestPath(Geo.Point(x0, y0), Geo.Point(x1, y1), PathAlgorithm.AStar);
Console.WriteLine($"{route.Cost / 60:F1} min, {route.Length / 1000:F2} km");

var ch = graph.BuildContractionHierarchy();       // preprocessing for many queries
double seconds = ch.Distance(a, b);
Route fast = ch.Route(a, b);                      // shortcuts unpacked to original edges

var (edges, isochrone) = graph.ServiceArea(graph.NearestNode(hx, hy), maxCost: 600, bufferDistance: 150);
int[] component = graph.ConnectedComponents();
```

Utility networks (water, gas, electricity, telecom): build a directed graph and trace through passable edges.

```csharp
var net = NetworkGraph.FromLines(pipes, directed: true);
var (nodes, servedEdges) = net.Trace(plantNode, e => (string?)pipes["valve"].GetValue(e.SourceRow) == "open", downstreamOnly: true);
```

Graphs are stored as CSR arrays. The tests check that Dijkstra, A*, bidirectional Dijkstra and Contraction Hierarchies return the same costs on the city network.

## Raster & terrain (`GeoPandas.Net.Raster`)

```csharp
var dem = GeoTiff.Read("dem.tif");                 // strips/tiles, Deflate/LZW/PackBits, BigTIFF, COG
var slope = Terrain.Slope(dem);                    // degrees (Horn)
var aspect = Terrain.Aspect(dem);
var shade = Terrain.Hillshade(dem, azimuth: 315, altitude: 45);
var view = Terrain.Viewshed(dem, towerX, towerY, observerHeight: 40, maxDistance: 12_000);
var contours = dem.Contours(interval: 25);         // GeoDataFrame of lines with "elevation"
GeoTiff.Write(slope, "slope.tif", TiffCompression.Deflate, tileSize: 256);
```

Hydrology for civil engineering:

```csharp
var filled = Hydrology.FillSinks(dem);             // priority-flood
var dir    = Hydrology.FlowDirection(filled);      // D8, ESRI codes
var acc    = Hydrology.FlowAccumulation(dir);
var basin  = Hydrology.Watershed(dir, outletX, outletY);
var rivers = Hydrology.Streams(acc, threshold: 400);
```

Remote sensing:

```csharp
var img  = GeoTiff.Read("sentinel2_l2a.tif");
var ndvi = SpectralIndex.Ndvi(SpectralIndex.Band(img, 7), SpectralIndex.Band(img, 3));   // NIR B8, red B4
var ndwi = SpectralIndex.Ndwi(green, nir);
var ndbi = SpectralIndex.Ndbi(swir, nir);
var (difference, change) = SpectralIndex.ChangeDetection(ndviBefore, ndviAfter);         // Otsu threshold
```

Raster ⇄ vector:

```csharp
var stats   = dem.ZonalStatistics(districts);                  // count/min/max/mean/sum/std columns
var sampled = dem.SamplePoints(wells, "elevation", bilinear: true);
var burned  = RasterVector.Rasterize(landuse, dem, "class_id");
new MapScene().AddRaster(dem, ColorMaps.Terrain, legendTitle: "m").AddHillshade(shade).SaveSvg("dem.svg");
```
