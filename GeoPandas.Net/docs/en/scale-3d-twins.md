# Scale, 3D, digital twins & cloud

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · Made by Gravicode Studios, led by Kang Fadhil · [🇮🇩 Bahasa Indonesia](../id/scale-3d-twins.md)

Phase 5 (v0.5) adds:

- **Global grids:** H3 and S2 cell indexes (`GeoPandas.Net.Geometry.Index`), with GeoDataFrame helpers in Core.
- **Partitioned processing:** `PartitionedGeoDataFrame` and partitioned GeoParquet datasets.
- **3D:** glTF and OGC 3D Tiles (`GeoPandas.Net.Scene`), Cesium.Net integration (`GeoPandas.Net.Cesium`) and interactive MapLibre maps (`Explore()`, `GeoPandas.Net.Blazor`).
- **Digital twins:** live IoT layers on a dynamic quadtree (`GeoPandas.Net.Twin`).
- **Cloud:** Azure Maps, AWS Location Service and Azure Digital Twins clients (`GeoPandas.Net.Cloud`).
- **Machine learning and GPU:** ONNX segmentation and ML.NET land cover (`GeoPandas.Net.ML`), ILGPU raster kernels (`GeoPandas.Net.Gpu`).

`Scene`, `Twin` and `Cloud` are part of the `GeoPandas.Net` meta-package. `Cesium`, `Blazor`, `ML` and `Gpu` are separate packages because they bring large dependencies (Blazor, ML.NET, ONNX Runtime, ILGPU).

Each result was checked against an independent implementation. The reference files are in `tests/GeoPandas.Net.Tests/data`:

| Feature | Reference | Agreement |
|---|---|---|
| H3 cells, centres, boundaries, disks, parents/children, polygon fill, compaction | h3-py 4.5 (Uber H3) | identical cell ids and sets; coordinates within 1e-10° |
| H3 cell areas | h3-py 4.5 | relative 1e-12 |
| S2 cell ids, tokens, neighbours, children, centres and vertices | s2sphere 0.2.5 | identical ids and tokens; coordinates within 1e-10° |
| S2 exact areas | s2sphere 0.2.5 | relative 1e-6 (s2sphere documents 6-digit accuracy) |
| glTF 2.0 / 3D Tiles 1.1 output | Khronos glTF-Validator, CesiumGS 3d-tiles-validator | 0 errors, 0 warnings |
| 3D Tiles georeferencing (CRS → ECEF) | pyproj | 0.06 mm |
| AWS SigV4 signatures | botocore 1.44.5 | identical `Authorization` headers |
| ONNX segmentation (single tile and overlapping tiles) | onnxruntime 1.32 (Python) | 1e-6, same arg-max class everywhere |
| GPU slope, aspect, hillshade, NDVI, convolution, IDW | managed implementations | float rounding |
| Partitioned spatial join | `GeoDataFrame.SJoin` | identical rows and order |

## H3 and S2

The H3 code is a port of Uber's H3 v4 C library; its lookup tables are generated from the C sources by `tools/h3/h3tables.py` (Apache 2.0). The S2 code follows the reference S2 geometry library (cell ids, Hilbert curve, quadratic projection).

```csharp
using GeoPandas.Net.Geometry.Index;

ulong cell = H3.LatLngToCell(-6.2, 106.8, res: 9);         // "898c106a48fffff"
var ring   = H3.GridDisk(cell, k: 1);                       // the cell and its 6 neighbours
Polygon hex = H3.CellToPolygon(cell);
double km2  = H3.CellAreaKm2(cell);
var cover   = H3.PolygonToCells(cityBoundaryWgs84, 8);      // cells whose centres fall inside
var fewer   = H3.Compact(cover);

var hexbins = H3Frame.Bin(pois, res: 8, valueColumn: "capacity");   // count, capacity_sum, capacity_mean per hexagon
var tagged  = H3Frame.IndexPoints(pois, 9);                         // adds an "h3" column

ulong s2 = S2.FromLatLng(-6.2, 106.8, level: 15);
string token = S2.ToToken(s2);                              // "2e69f6be4"
var covering = S2.Cover(polygonWgs84, level: 12);
```

H3 and S2 always work in WGS 84. The frame helpers reproject projected input themselves.

## Partitioned processing

`PartitionedGeoDataFrame` splits a frame into spatial partitions so the work can run in parallel and queries can skip partitions:

| Scheme | `size` means | Use it for |
|---|---|---|
| `KdTree` | maximum rows per partition (balanced median splits) | evenly sized work units |
| `Grid` | cells per side (n × n) | simple, predictable tiles |
| `H3` | resolution | joining with other H3 data |
| `Column` | — (uses `column`) | attribute partitions, e.g. per province |

```csharp
var parts  = PartitionedGeoDataFrame.Create(points, PartitionScheme.KdTree, size: 50_000);
var joined = parts.SJoin(districts, SpatialPredicate.Within).Collect();  // == points.SJoin(districts, Within)
var window = parts.Query(bbox);                                         // only partitions whose bounds intersect
double total = parts.MapReduce(f => f["capacity"].Sum(), (a, b) => a + b);
var buffered = parts.Map(f => f.Buffer(10));

GeoParquetDataset.Write(parts, "points_dataset/");     // partition=<key>/part-0.parquet + _dataset.json
var subset = GeoParquetDataset.ReadPartitioned("points_dataset/", bbox);   // opens only overlapping files
```

How it works:

- Every partition keeps the original row ids, so `Collect()` restores the source order.
- The partition-aware spatial join pairs each partition only with the right rows whose envelopes touch it. Its collected result is identical to `SJoin`, row for row.
- Each partition is an ordinary GeoDataFrame, so the same partitions can be sent to Orleans grains, Akka.NET actors or Spark executors. Ready-made adapters for those frameworks are not included.

A partitioned dataset is one GeoParquet file per partition plus a `_dataset.json` manifest that records each partition's row count and bounds. A read with a bounding box works in two steps:

1. It opens only the files whose bounds intersect the box.
2. Inside each file, it skips row groups using the GeoParquet bbox covering column.

## 3D: glTF, 3D Tiles and Cesium

`LocalFrame` places a model in a local east-north-up frame. Each vertex is converted exactly: CRS → WGS 84 → ECEF → ENU.

`MeshBuilders` produces four kinds of mesh:

- **Terrain:** DEM surfaces, with optional colours.
- **Extrude:** closed, outward-wound solids from polygons; holes are triangulated with constrained Delaunay.
- **Points:** point clouds.
- **Lines:** polylines.

`Gltf` writes GLB files. Feature attributes are stored with `EXT_mesh_features` and `EXT_structural_metadata`.

```csharp
using GeoPandas.Net.Scene;

var frame = LocalFrame.AtCentre(buildings.TotalBounds, buildings.Crs);
Gltf.WriteGlb("city.glb", MeshBuilders.Extrude(buildings, frame, "height"), MeshBuilders.Terrain(dem, frame, step: 2));

var tileset = Tiles3D.WriteBuildings(buildings, "tiles/", heightColumn: "height",
    options: new TilesetOptions { MaxFeaturesPerTile = 500 }, heightColors: ColorMaps.Viridis);
var terrain = Tiles3D.WriteTerrain(dem, "terrain/", chunk: 128);
```

Tilesets are 3D Tiles 1.1. The root transform is ENU → ECEF and bounding volumes are oriented boxes. Two layouts are used:

- **Buildings:** a quadtree with additive refinement.
- **Terrain:** a coarse tile refined into chunks with replacement refinement.

With Cesium.Net (`GeoPandas.Net.Cesium`, namespace `CesiumNet`):

```csharp
await viewer.AddGeoDataFrameAsync(districts, fill: "#2b8cbe55");
await viewer.AddPointsAsync(sensors, heightColumn: "z", colorColumn: "pm25");
await viewer.AddTilesetAsync("tiles/tileset.json",
    style: CesiumExtensions.ColorByProperty("height", [(20, "color('#fde725')"), (60, "color('#21918c')")]));
await viewer.FlyToAsync(districts);
```

## Interactive maps: `Explore()`

```csharp
districts.Explore("population").Open();                 // HTML page with MapLibre GL JS 4.7.1, opened in the browser
new ExploreMap { Basemap = "none", Title = "Assets" }
    .Add(pipes, new ExploreStyle { Column = "diameter" })
    .Add(valves, new ExploreStyle { Fill = "#b0306a" })
    .Save("assets.html");
```

The page contains the following:

- Data is GeoJSON reprojected to WGS 84.
- Choropleth and categorical styling use the same colour maps and classification as `Plot()`.
- Hovering shows a tooltip, and the legend is built in.
- Basemaps: OpenStreetMap, none, or any XYZ URL.

In Blazor, use `<ExploreView Map="@map" Height="520px" />`. It renders inside a sandboxed iframe, so its scripts never touch the page. `<MapSceneView Scene="@scene" />` shows the static SVG renderer instead.

## Digital twins

```csharp
using GeoPandas.Net.Twin;

var layer = new LiveLayer("fleet", districts.TotalBounds, districts.Crs, historyWindow: TimeSpan.FromHours(2))
    .AddZones(districts, "name")
    .AddRule(new ThresholdRule("speeding", "speed_kmh", Above: 60, Hysteresis: 5));
layer.EventRaised += e => Console.WriteLine(e);      // ZoneEnter / ZoneExit / ThresholdExceeded / ThresholdCleared / Stale

await layer.IngestAsync(readings);                   // IAsyncEnumerable<SensorReading> or ChannelReader<SensorReading>
var reading = TwinJson.Parse(mqttPayload, layer.Crs);   // {"id","time","lon","lat",...values}

var now    = layer.Snapshot();                               // one row per entity
var nearby = layer.Near(x, y, radius: 500);
var path   = layer.Trajectory("truck-7");
var stats  = layer.ZoneStatistics("pm25", TimeSpan.FromMinutes(15), DateTimeOffset.UtcNow);
var stale  = layer.CheckStale(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
```

How the layer behaves:

- Positions live in a dynamic quadtree.
- Zone membership uses *covers*, so a point on a zone boundary counts as inside.
- Threshold rules use hysteresis, so a value hovering at the limit does not flood the event stream.
- Events go to the `EventRaised` callback and to a `Channel` (`layer.Events`).

`DigitalTwin` combines static layers (buildings, pipes) with live layers. Its `RollUp` aggregates live readings per asset.

## Cloud location services

The `GeoPandas.Net.Cloud` clients use plain `HttpClient` and no cloud SDK. Results are WGS 84 GeoDataFrames.

```csharp
using GeoPandas.Net.Cloud;

var maps = new AzureMapsClient(http, Environment.GetEnvironmentVariable("AZURE_MAPS_KEY")!);
// or: new AzureMapsClient(http, ct => GetEntraTokenAsync(ct), mapsClientId)
var hits  = await maps.GeocodeAsync("Jl. Sudirman, Jakarta", top: 5, countryRegion: "ID");
var route = await maps.RouteAsync([(106.82, -6.17), (106.84, -6.22)]);
var iso   = await maps.IsochronesAsync(106.8, -6.2, [600, 1200, 1800]);

var aws = new AwsLocationClient(http, "ap-southeast-1", AwsCredentials.FromEnvironment()!);   // SigV4
var places = await aws.GeocodeAsync("my-place-index", "Monas, Jakarta");
var legs   = await aws.CalculateRouteAsync("my-route-calculator", (106.82, -6.17), (106.84, -6.22));
await aws.PutGeofencesAsync("zones", districts, "name");      // rings re-oriented as AWS requires
await aws.SyncTrackerAsync("fleet-tracker", layer);           // live layer → tracker (batches of 10)

var adt = new AzureDigitalTwinsClient(http, new Uri("https://my-twins.api.weu.digitaltwins.azure.net"), ct => GetAdtTokenAsync(ct));
var pumps = await adt.QueryGeoAsync("SELECT * FROM digitaltwins WHERE IS_OF_MODEL('dtmi:demo:Pump;1')");
await adt.UpdatePropertiesAsync("pump-1", new Dictionary<string, object?> { ["Pressure"] = 3.1 });
await adt.SyncAsync(layer);                                    // Longitude/Latitude, sensor values, zones
```

Authentication:

| Service | Options |
|---|---|
| Azure Maps | Subscription key, or a Microsoft Entra ID bearer token plus `x-ms-client-id` |
| AWS Location | SigV4 with `AwsCredentials`, or an API key (places and routes only) |
| Azure Digital Twins | A bearer token from your `AccessTokenProvider` |

Pass any token source as the `AccessTokenProvider`, for example Azure.Identity's `DefaultAzureCredential`.

Errors are raised as `CloudServiceException`, which carries the HTTP status and the response body.

## Machine learning and GPU

```csharp
using GeoPandas.Net.ML;

using var seg = new OnnxSegmenter("unet.onnx") { Bands = [2, 1, 0, 3], Mean = [...], Std = [...], TileSize = 512, Overlap = 32, Softmax = true,
                                                 ClassNames = ["water", "built", "vegetation"] };
var (classes, probabilities) = seg.Predict(image);         // overlapping tiles, centres kept

var clf = LandCoverClassifier.Train(image, trainingPolygons, "class", algorithm: LandCoverAlgorithm.RandomForest);
var map = clf.Predict(image);
var report = clf.Evaluate(LandCoverClassifier.SamplePixels(image, testPoints, "class", [0, 1, 2, 3]));
Console.WriteLine(report);                                 // overall accuracy, kappa; report.ToFrame() = confusion matrix
```

`OnnxSegmenter` accepts models with a fixed input size or with dynamic height and width. With a fixed size, edge tiles are zero-padded. `LandCoverClassifier` offers three algorithms:

- `RandomForest`: one-versus-all FastForest.
- `GradientBoosting`: one-versus-all FastTree.
- `MaximumEntropy`: SDCA.

```csharp
using GeoPandas.Net.Gpu;

using var gpu = GpuContext.Create();                        // CUDA → OpenCL → CPU accelerator
var shade = GpuRaster.Hillshade(gpu, dem, 315, 45);
var ndvi  = GpuRaster.NormalizedDifference(gpu, image, bandA: 3, bandB: 2);
var idw   = GpuRaster.Idw(gpu, xs, ys, zs, dem.Transform, dem.Width, dem.Height, power: 2);
```

## GeoLLM operations

`GeoAssistant` has four new whitelisted plan operations:

- `h3_bin`: hexagon binning.
- `within_distance`: rows within a distance of a point, sorted by distance.
- `simplify`: geometry simplification.
- `unique`: the distinct values of a column, with counts.

As before, the LLM only proposes a JSON plan. GeoPandas.Net validates every step against the whitelist before running it.

## Command line

```text
gpd h3 bin pois.gpkg 8 --value capacity --out hex.gpkg
gpd h3 cell -6.2 106.8 9            gpd h3 disk 898c106a48fffff 2
gpd s2cell -6.2 106.8 --level 20
gpd partition points.parquet points_ds --scheme kd --size 100000
gpd dataset read points_ds --bbox 700000,9310000,705000,9315000 --out window.gpkg
gpd tiles3d buildings buildings.gpkg tiles --height height
gpd tiles3d terrain dem.tif terrain
gpd explore districts.gpkg --column population --out map.html
gpd geocode "Monas, Jakarta" --provider azure         # AZURE_MAPS_KEY
```

## Not in this release

The following items from the plan are deferred:

- **Orleans, Akka.NET and Spark adapters.** The partition model above is the integration point.
- **TorchSharp training.** Models trained elsewhere run through ONNX instead.
- **HF.Net embeddings and IoTComNet.** These packages are not on NuGet.
- **A native OpenSceneGraph/ThreeNet point-cloud viewer.** Point clouds are exported as glTF instead, for Cesium or any glTF viewer.
