# Utilitas

Halaman ini menunjukkan cara menjalankan program command line GDAL sebagai pemanggilan library di dalam proses lewat `GdalUtils`, serta CLI terpadu `gdal` lewat `GdalCli`.

Setiap method `GdalUtils` menerima **record opsi bertipe** atau **switch command line yang sama persis** dengan dokumentasi gdal.org. Method yang membuat dataset mengembalikannya, dan dataset itu milik Anda (wajib di-dispose). Untuk menyimpan hasil di memori, isi tujuan dengan `""` dan pakai `Format = "MEM"` (atau `-of MEM`).

| Program | Method | Opsi bertipe |
|---|---|---|
| gdalinfo | `GdalUtils.Info(ds, params string[] args)` | – |
| ogrinfo | `GdalUtils.VectorInfo(ds, args)` | – |
| gdalmdiminfo | `GdalUtils.MultiDimInfo(ds, args)` | – |
| gdal_translate | `GdalUtils.Translate(src, dest, ...)` | `TranslateOptions` |
| gdalwarp | `GdalUtils.Warp(src atau sources, dest, ...)`, `WarpInto(sources, existing, args)` | `WarpOptions` |
| ogr2ogr | `GdalUtils.VectorTranslate(src atau sources, dest, ...)` | `VectorTranslateOptions` |
| gdalbuildvrt | `GdalUtils.BuildVrt(dest, dataset atau file, ...)` | `BuildVrtOptions` |
| gdaldem | `GdalUtils.DemProcessing(src, dest, mode, ..., colorFile)` | `DemOptions` |
| gdal_rasterize | `GdalUtils.Rasterize(vector, dest, ...)`, `RasterizeInto(vector, raster, args)` | `RasterizeOptions` |
| gdal_grid | `GdalUtils.Grid(points, dest, args)` | – |
| gdal (3.11+) | `GdalCli.Run(args)` | – |

Semua method yang membuat dataset juga menerima `IProgress<GdalProgress>` dan `CancellationToken`.

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

`TranslateOptions` juga punya `Bands`, `SourceWindow`, `ProjectionWindow`, `ScaleRange`, `NoData`, `AssignSrs`, `AssignBounds`, `Resampling`, `Expand`, `Strict`, `CreationOptions`, dan `ExtraArgs`.

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

`WarpOptions` juga mencakup `SourceSrs`, `OutputBounds`, `OutputSize`, `SourceNoData`, cutline (`CutlineFile`, `CutlineWkt`, `CutlineSrs`, `CropToCutline`), rektifikasi GCP/RPC (`ThinPlateSpline`, `PolynomialOrder`, `Rpc`, `RpcDem`), `OutputType`, `AddAlpha`, `WarpMemoryMb`, dan `Overwrite`.

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

Beberapa writer (GeoJSON, KML, Shapefile) baru selesai menulis saat dataset hasil di-dispose. Dispose dataset itu dulu sebelum membaca filenya.

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

Mode yang tersedia: `Hillshade`, `Slope`, `Aspect`, `ColorRelief`, `TerrainRuggednessIndex`, `TopographicPositionIndex`, `Roughness`.

## gdal_rasterize dan gdal_grid

```csharp
using var classes = GdalUtils.Rasterize(landCover, "classes.tif", new RasterizeOptions
{
    Attribute = "class", Resolution = (60, 60), OutputType = DataType.Byte, AllTouched = true,
});

GdalUtils.RasterizeInto(roads, existingRaster, ["-burn", "255"]);

using var surface = GdalUtils.Grid(points, "", ["-of", "MEM", "-a", "invdist:power=2.0",
    "-outsize", "256", "256", "-txe", "0", "100", "-tye", "0", "100"]);
```

## Command line terpadu `gdal` (GDAL ≥ 3.11)

`GdalCli` menjalankan algoritma apa pun dari program `gdal` yang baru di dalam proses. Berikan argumennya tanpa awalan `gdal`.

```csharp
GdalCliResult info = GdalCli.Run("raster", "info", "--format", "json", "dem.tif");
Console.WriteLine(info.Output);                     // text/JSON output

GdalCli.Run("raster", "reproject", "--output-crs", "EPSG:4326", "dem.tif", "dem_4326.tif");
GdalCli.Run("raster", "tile", "--min-zoom", "8", "--max-zoom", "12", "relief.tif", "tiles/");   // PNG tiles need Byte/UInt16 input

IReadOnlyList<string> top = GdalCli.AlgorithmNames;          // convert, info, raster, vector, mdim, vsi, …
string usage = GdalCli.GetUsageJson("raster", "reproject");  // arguments as JSON
```

Jika algoritma menulis ke output di memori (`--output-format MEM`), dataset hasilnya ada di `GdalCliResult.Dataset` dan menjadi milik Anda.

**Berikutnya:** [Analisis](analysis.md) · [Cloud & file virtual](cloud-and-vsi.md)
