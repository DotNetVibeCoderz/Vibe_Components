# Getting started

This page covers installing GDAL.Net, writing a first raster and vector program, and how the native libraries are found at runtime.

## Install

```bash
dotnet add package Gravicode.GDAL.Net
```

Requirements: .NET 10. The package pulls in the native GDAL 3.13.3 build (with PROJ 9.8.1, GEOS, SQLite/SpatiaLite, curl, netCDF, HDF4/5, OpenJPEG, …) through the `MaxRev.Gdal.WindowsRuntime.Minimal`, `MaxRev.Gdal.LinuxRuntime.Minimal` and `MaxRev.Gdal.MacosRuntime.Minimal` packages. You don't need a separate GDAL install.

## First program

```csharp
using GDAL.Net;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

Console.WriteLine($"GDAL {Gdal.Version}, PROJ {Gdal.ProjVersion}");

// --- Raster: create a small GeoTIFF, write pixels, read statistics back
using (var ds = Gdal.GetDriver("GTiff").Create("hello.tif", 100, 50, 1, DataType.Float32,
           new Dictionary<string, string> { ["COMPRESS"] = "DEFLATE" }))
{
    ds.GeoTransform = GeoTransform.FromOrigin(106.8, -6.1, 0.01, 0.01);
    using var wgs84 = SpatialReference.Wgs84;
    ds.SpatialReference = wgs84;
    var pixels = Enumerable.Range(0, 100 * 50).Select(i => (float)(i % 100)).ToArray();
    ds.GetBand(1).WriteAll<float>(pixels);
}

using (var ds = Gdal.OpenRaster("hello.tif"))
{
    var stats = ds.GetBand(1).ComputeStatistics();
    Console.WriteLine($"{ds.RasterWidth}x{ds.RasterHeight}, mean {stats.Mean:0.0}, extent {ds.Extent}");
}

// --- Vector: create a GeoPackage with one point layer
using (var gpkg = Gdal.GetDriver("GPKG").CreateVector("cities.gpkg"))
{
    using var wgs84 = SpatialReference.Wgs84;
    var layer = gpkg.CreateLayer("cities", wgs84, GeometryType.Point);
    layer.CreateField("name", FieldType.String, 50);
    layer.CreateField("population", FieldType.Integer64);

    using var bandung = Geometry.Point(107.6191, -6.9175);
    layer.AddFeature(bandung, new Dictionary<string, object?> { ["name"] = "Bandung", ["population"] = 2_444_160L });
}

using (var gpkg = Gdal.OpenVector("cities.gpkg"))
{
    gpkg.GetLayer("cities").ForEach(f => Console.WriteLine($"{f.GetString("name")} {f.Geometry}"));
}
```

## Platforms

| Platform | Native GDAL | Rust kernels (`gdalnet_kernels`) |
|---|---|---|
| Windows x64 | ✔ | ✔ in the package |
| Linux x64 / arm64 | ✔ | ✔ in the package (built by CI) |
| macOS x64 / arm64 | ✔ | ✔ in the package (built by CI) |

The Rust kernels speed up statistics, histograms, normalized difference indices and colorization. When they are missing, the same functions run as vectorized managed code (`System.Numerics.Tensors`) and return identical results. `Kernels.Backend` tells you which one is active.

## How the native libraries are found

GDAL.Net registers a `DllImport` resolver when the assembly loads. It searches, in order:

1. The path in the `GDALNET_LIBRARY_PATH` environment variable (an explicit `gdal.dll` / `libgdal.so.*` / `libgdal.*.dylib`).
2. `runtimes/<rid>/native/` and the application directory (works for portable, RID-specific, self-contained and single-file builds).
3. The platform loader (`PATH`, `LD_LIBRARY_PATH`, a system-wide GDAL).

On first use GDAL.Net also:

- sets `GDAL_DATA` to the `gdal-data` folder shipped with the package (or keeps your `GDAL_DATA` env var);
- points PROJ at `proj.db` (shipped by the runtime packages, or `PROJ_DATA`/`PROJ_LIB`);
- sets `OSR_DEFAULT_AXIS_MAPPING_STRATEGY=TRADITIONAL_GIS_ORDER` (longitude/easting first);
- registers all drivers and installs a quiet error handler that forwards messages to `Gdal.Log`.

You can inspect the result:

```csharp
Console.WriteLine(Gdal.NativeLibraryPath);  // which gdal library was loaded
Console.WriteLine(Gdal.DataPath);           // GDAL_DATA
Console.WriteLine(Gdal.ProjDataPath);       // folder containing proj.db
Gdal.Initialize();                          // optional: force initialization early
```

**Next:** [Core concepts](core-concepts.md) · [Raster](raster.md) · [Vector](vector.md)
