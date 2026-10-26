# Performance

This page explains the design choices that keep GDAL.Net fast and memory-frugal, and how to get the most out of it.

## Design

| Technique | Where | Benefit |
|---|---|---|
| Source-generated interop (`[LibraryImport]`) | `Native/GdalNative.*.cs` | No runtime marshalling stubs, blittable signatures, trimming/Native AOT friendly (`IsAotCompatible`) |
| One `SafeHandle` type with a parent chain | `GdalSafeHandle` | Owners can't be finalized while children are in use; the marshaller keeps the chain alive during each native call; explicit disposal turns use-after-close into `ObjectDisposedException` |
| Borrowed handles skip finalization | bands, layers, feature geometries | No finalizer queue pressure when iterating millions of features |
| Zero-copy pixel I/O | `RasterBand.Read<T>(…, Span<T>)` | GDAL writes directly into your buffer and converts the data type during the read |
| Resampling inside RasterIO | `ReadResampled`, `RasterRenderer` | GDAL picks overviews and reads only what the output needs |
| Pooled row-block streaming | `ProcessRows`, `BandMath`, `SpectralIndices` | Constant memory regardless of raster size (`ArrayPool<T>`) |
| Vectorized math | `BandMath` (TensorPrimitives), managed kernels | SIMD on every platform |
| Rust kernels | `native/gdalnet-kernels` | rayon data parallelism, fat LTO, `codegen-units=1`, `panic=abort`; statistics, histograms, ND indices, colorize, composites, shading |
| Bulk coordinate APIs | `TransformInPlace`, `Geometry.CopyPoints` | One native call for thousands of points |
| Static unmanaged callbacks | progress and error handlers | No delegate allocation, no GC-rooted thunks |
| Lazy, cached bootstrap | `GdalRuntime` | Driver registration and data-path discovery happen once |

## Tips

- **Read by blocks.** `band.BlockSize` is the natural I/O unit. `ProcessRows` already aligns chunks to it.
- **Build overviews** (`BuildOverviews` or COG `OVERVIEWS=AUTO`) for anything you will display or downsample.
- **Raise the block cache** for heavy random access: `Gdal.CacheMax = 1L << 30`.
- **Use multiple threads** for compression and warping: `Gdal.SetConfigOption("GDAL_NUM_THREADS", "ALL_CPUS")`. `WarpOptions.Multithread` is on by default.
- **Batch writes in transactions** for GeoPackage, SQLite and PostGIS.
- **Skip unused fields** with `layer.SetIgnoredFields([...])`, and pass `includeGeometry: false` to `ReadRecords` for attribute-only work.
- **Prefer MEM/VRT intermediates** (`Format = "MEM"`, `-of VRT`) over temporary files.
- **Parallelize per dataset**: open one `Dataset` per thread.
- **For remote data**, use COG/FlatGeobuf with `GDAL_DISABLE_READDIR_ON_OPEN=EMPTY_DIR` and `VSI_CACHE=TRUE`.

## Benchmarking

Compare the Rust and managed kernels on your own data:

```csharp
using System.Diagnostics;
using GDAL.Net.Processing;

float[] data = band.ReadAll<float>();
var sw = Stopwatch.StartNew();
var native = Kernels.Statistics(data, noData: -9999);
var tNative = sw.Elapsed;

Kernels.ForceManaged = true;
sw.Restart();
var managed = Kernels.Statistics(data, noData: -9999);
var tManaged = sw.Elapsed;
Kernels.ForceManaged = false;

Console.WriteLine($"{Kernels.Backend}: {tNative.TotalMilliseconds:0.0} ms vs managed {tManaged.TotalMilliseconds:0.0} ms");
```

For rigorous numbers use BenchmarkDotNet in Release mode. Warm up first: the first call pays for native library loading and driver registration.

**Next:** [Feature matrix](feature-matrix.md) · [Troubleshooting](troubleshooting.md)
