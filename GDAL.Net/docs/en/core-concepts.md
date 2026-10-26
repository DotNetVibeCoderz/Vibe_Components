# Core concepts

This page explains how GDAL.Net manages native objects, threads, errors, configuration, progress and axis order.

## Ownership and disposal

Every native object is wrapped by a class deriving from `GdalObject` (which is `IDisposable`).

| Object | Owned by | Dispose it? |
|---|---|---|
| `Dataset` | you | **Yes**. Disposing closes the file and flushes writes. Use `Close()` if you want flush errors to throw. |
| `RasterBand`, `Layer`, `FeatureDefinition`, `FieldDefinition` from a layer | the dataset | Optional (no-op) |
| `Feature` returned by a layer (`GetNextFeature`, `GetFeature`, enumeration) | you | **Yes** (or use `layer.ForEach`, which disposes for you) |
| `Geometry` from `feature.Geometry` / `geometry.GetGeometry(i)` | the feature / parent geometry | No. `Clone()` it to keep it longer. |
| `Geometry` created by you or returned by an operation (`Buffer`, `Union`, …) | you | **Yes** |
| `SqlResultLayer` from `ExecuteSql` | you | **Yes**. This releases the result set. |
| `SpatialReference`, `CoordinateTransformation`, `ColorTable`, `MdGroup`, `MdArray` | you | **Yes** |

Child objects keep their owner alive, so a band can never outlive a dataset that was garbage-collected. When you dispose the owner explicitly, using a child throws `ObjectDisposedException` instead of crashing the process:

```csharp
var ds = Gdal.OpenRaster("dem.tif");
var band = ds.GetBand(1);
ds.Dispose();
// band.Width  → ObjectDisposedException (no access violation)
```

`Feature.SetGeometryDirectly(geometry)` transfers ownership to the feature without a copy. The geometry object becomes unusable afterwards.

## Threading

GDAL datasets are **not** thread-safe. Use one `Dataset` (opened separately) per thread. Global functions (`Gdal.Open`, `SpatialReference`, `CoordinateTransformation` creation, `GdalUtils` on different datasets) can run in parallel.

```csharp
Parallel.ForEach(files, file =>
{
    using var ds = Gdal.OpenRaster(file);          // one dataset per thread
    var s = ds.GetBand(1).ComputeStatistics();
});
```

## Errors

Failing calls throw `GdalException` with GDAL's own message and error number (`ErrorNumber`). Warnings and debug messages never throw; subscribe to `Gdal.Log` to see them:

```csharp
Gdal.Log += m => Console.WriteLine($"[{m.Level}] {m.Message}");
try
{
    using var ds = Gdal.Open("missing.tif");
}
catch (GdalException e)
{
    Console.WriteLine(e.Message);   // "Unable to open 'missing.tif': ..."
}
```

`Gdal.Log` may be raised from any thread.

## Configuration options

```csharp
Gdal.SetConfigOption("GDAL_NUM_THREADS", "ALL_CPUS");
Gdal.SetConfigOption("GDAL_CACHEMAX", "512");           // MB
Gdal.CacheMax = 512L * 1024 * 1024;                      // same, in bytes
string? v = Gdal.GetConfigOption("GDAL_NUM_THREADS");

// Thread-local and path-specific options
Gdal.SetThreadLocalConfigOption("GTIFF_DIRECT_IO", "YES");
Gdal.SetPathSpecificOption("/vsis3/my-bucket", "AWS_REGION", "ap-southeast-3");

// Temporary options, restored on dispose
using (Gdal.ConfigScope(new Dictionary<string, string?> { ["GDAL_HTTP_TIMEOUT"] = "10" }))
{
    // ...
}
```

## Progress and cancellation

Long-running operations accept `IProgress<GdalProgress>` and a `CancellationToken`. When the token is cancelled, GDAL stops at the next progress callback and the method throws `OperationCanceledException`.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var progress = new Progress<GdalProgress>(p => Console.Write($"\r{p.Percent:0}% {p.Message}"));
using var warped = GdalUtils.Warp(dem, "out.tif", new WarpOptions { TargetSrs = "EPSG:4326" }, progress, cts.Token);
```

`Progress<T>` posts to the captured synchronization context (or the thread pool). Use your own `IProgress<T>` implementation if you need synchronous callbacks.

## Axis order

GDAL 3 follows the axis order of the CRS authority (for example latitude, longitude for EPSG:4326). GDAL.Net defaults to the **traditional GIS order**, x = longitude/easting and y = latitude/northing, for every `SpatialReference` it creates and process-wide. Opt back in per object:

```csharp
using var srs = SpatialReference.FromEpsg(4326);
srs.AxisMappingStrategy = AxisMappingStrategy.AuthorityCompliant;
```

**Next:** [Raster](raster.md) · [Vector](vector.md) · [Performance](performance.md)
