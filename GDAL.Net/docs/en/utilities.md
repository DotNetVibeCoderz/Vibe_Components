# Utilities

This page shows how to run the GDAL command line programs as in-process library calls with `GdalUtils`, and the unified `gdal` CLI with `GdalCli`.

Every `GdalUtils` method accepts either a **typed options record** or the **exact command line switches** documented on gdal.org. Methods that create a dataset return it, and you own it (dispose it). Pass `""` as the destination with `Format = "MEM"` (or `-of MEM`) to keep results in memory.

| Program | Method | Typed options |
|---|---|---|
| gdalinfo | `GdalUtils.Info(ds, params string[] args)` | – |
| ogrinfo | `GdalUtils.VectorInfo(ds, args)` | – |
| gdalmdiminfo | `GdalUtils.MultiDimInfo(ds, args)` | – |
| gdal_translate | `GdalUtils.Translate(src, dest, ...)` | `TranslateOptions` |
| gdalwarp | `GdalUtils.Warp(src or sources, dest, ...)`, `WarpInto(sources, existing, args)` | `WarpOptions` |
| ogr2ogr | `GdalUtils.VectorTranslate(src or sources, dest, ...)` | `VectorTranslateOptions` |
| gdalbuildvrt | `GdalUtils.BuildVrt(dest, datasets or files, ...)` | `BuildVrtOptions` |
| gdaldem | `GdalUtils.DemProcessing(src, dest, mode, ..., colorFile)` | `DemOptions` |
| gdal_rasterize | `GdalUtils.Rasterize(vector, dest, ...)`, `RasterizeInto(vector, raster, args)` | `RasterizeOptions` |
| gdal_grid | `GdalUtils.Grid(points, dest, args)` | – |
| gdal (3.11+) | `GdalCli.Run(args)` | – |

All creating methods also accept `IProgress<GdalProgress>` and a `CancellationToken`.

## gdalinfo / ogrinfo

```csharp
string text = GdalUtils.Info(dem);
string json = GdalUtils.Info(dem, "-json", "-stats");
string summary = GdalUtils.VectorInfo(cities, "-so", "-al");
```

## gdal_translate

```csharp
// Typed
using var png = GdalUtils.Translate(dem, "preview.png", new TranslateOptions
{
    Format = "PNG",
    OutputType = DataType.Byte,
    AutoScale = true,                // -scale
    OutputSize = (800, 0),           // keep aspect ratio
});

// Raw switches
using var subset = GdalUtils.Translate(dem, "", ["-of", "MEM", "-srcwin", "0", "0", "100", "100"]);
```

`TranslateOptions` also has `Bands`, `SourceWindow`, `ProjectionWindow`, `ScaleRange`, `NoData`, `AssignSrs`, `AssignBounds`, `Resampling`, `Expand`, `Strict`, `CreationOptions` and `ExtraArgs`.

## gdalwarp

```csharp
using var wgs84 = GdalUtils.Warp(dem, "dem_4326.tif", new WarpOptions
{
    TargetSrs = "EPSG:4326",
    Resampling = ResampleAlgorithm.Bilinear,
    Resolution = null,               // or (0.0003, 0.0003)
    DestinationNoData = -9999,
    Multithread = true,              // default: -multi -wo NUM_THREADS=ALL_CPUS
});

// Mosaic several inputs into one output
using var mosaic = GdalUtils.Warp([tileA, tileB], "", new WarpOptions { Format = "MEM" });
```

`WarpOptions` also covers `SourceSrs`, `OutputBounds`, `OutputSize`, `SourceNoData`, cutlines (`CutlineFile`, `CutlineWkt`, `CutlineSrs`, `CropToCutline`), GCP/RPC rectification (`ThinPlateSpline`, `PolynomialOrder`, `Rpc`, `RpcDem`), `OutputType`, `AddAlpha`, `WarpMemoryMb` and `Overwrite`.

## ogr2ogr

```csharp
using var fgb = GdalUtils.VectorTranslate(cities, "cities.fgb", new VectorTranslateOptions
{
    Format = "FlatGeobuf",
    TargetSrs = "EPSG:3857",
    Where = "population > 1000000",
});

using var utm = GdalUtils.VectorTranslate(cities, "big.gpkg", new VectorTranslateOptions
{
    Format = "GPKG",
    TargetSrs = "EPSG:32749",
    NewLayerName = "big_cities",
    LayerCreationOptions = new Dictionary<string, string> { ["SPATIAL_INDEX"] = "YES" },
});

using var csv = GdalUtils.VectorTranslate(cities, "cities.csv", ["-f", "CSV", "-lco", "GEOMETRY=AS_WKT"]);
```

Some writers (GeoJSON, KML, Shapefile) finish writing when the returned dataset is disposed. Dispose it before reading the file.

## gdalbuildvrt

```csharp
using var mosaicVrt = GdalUtils.BuildVrt("mosaic.vrt", Directory.GetFiles("tiles", "*.tif"));
using var stack = GdalUtils.BuildVrt("stack.vrt", [red, nir], new BuildVrtOptions { Separate = true });
```

## gdaldem

```csharp
using GDAL.Net.Rendering;   // ColorRamp

using var hs = GdalUtils.DemProcessing(dem, "hillshade.tif", DemProcessingMode.Hillshade,
    new DemOptions { ZFactor = 2, HillshadeVariant = "multidirectional" });

File.WriteAllText("ramp.txt", ColorRamp.Terrain.ToGdalColorFile(650, 2200));
using var relief = GdalUtils.DemProcessing(dem, "relief.tif", DemProcessingMode.ColorRelief,
    new DemOptions { Alpha = true }, colorFile: "ramp.txt");
```

Modes: `Hillshade`, `Slope`, `Aspect`, `ColorRelief`, `TerrainRuggednessIndex`, `TopographicPositionIndex`, `Roughness`.

## gdal_rasterize and gdal_grid

```csharp
using var classes = GdalUtils.Rasterize(landCover, "classes.tif", new RasterizeOptions
{
    Attribute = "class", Resolution = (60, 60), OutputType = DataType.Byte, AllTouched = true,
});

GdalUtils.RasterizeInto(roads, existingRaster, ["-burn", "255"]);

using var surface = GdalUtils.Grid(points, "", ["-of", "MEM", "-a", "invdist:power=2.0",
    "-outsize", "256", "256", "-txe", "0", "100", "-tye", "0", "100"]);
```

## The unified `gdal` command line (GDAL ≥ 3.11)

`GdalCli` runs any algorithm of the new `gdal` program in-process. Pass the arguments without the leading `gdal`.

```csharp
GdalCliResult info = GdalCli.Run("raster", "info", "--format", "json", "dem.tif");
Console.WriteLine(info.Output);                     // text/JSON output

GdalCli.Run("raster", "reproject", "--output-crs", "EPSG:4326", "dem.tif", "dem_4326.tif");
GdalCli.Run("raster", "tile", "--min-zoom", "8", "--max-zoom", "12", "relief.tif", "tiles/");   // PNG tiles need Byte/UInt16 input

IReadOnlyList<string> top = GdalCli.AlgorithmNames;          // convert, info, raster, vector, mdim, vsi, …
string usage = GdalCli.GetUsageJson("raster", "reproject");  // arguments as JSON
```

When the algorithm writes to an in-memory output (`--output-format MEM`), `GdalCliResult.Dataset` holds it, and you own it.

**Next:** [Analysis](analysis.md) · [Cloud & virtual files](cloud-and-vsi.md)
