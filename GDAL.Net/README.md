<p align="center"><img src="build/icon.png" width="112" alt="GDAL.Net logo"></p>

<h1 align="center">GDAL.Net</h1>

<p align="center"><b>GDAL for modern .NET</b>: read, write, convert, reproject and analyse 200+ raster and vector formats from C#.<br>
Native GDAL 3.13 included, with Rust-accelerated kernels and ready-made components for <b>Blazor</b> (web) and <b>Avalonia UI</b> (desktop).</p>

<p align="center"><a href="README.id.md">Bahasa Indonesia</a> · <a href="docs/en/index.md">Documentation</a> · <a href="docs/en/gallery.md">Gallery</a> · <a href="PLAN.md">Roadmap</a> · <a href="Progress.md">Progress</a></p>

![GDAL.Net Gallery (Blazor) — color ramps & hillshading](docs/images/blazor-raster-styling.png)

## Why GDAL.Net

- **The whole GDAL, idiomatic C#.** GDAL.Net covers:
  - raster and vector I/O
  - reprojection with datum transformations
  - warping and mosaics (VRT)
  - DEM analysis: hillshade, slope, contours and viewshed
  - polygonize and rasterize
  - OGR SQL and the SQLite dialect
  - GEOS geometry operations
  - cloud object storage
  - netCDF/HDF5 data cubes
  - every command line utility, called in-process (`gdalinfo`, `gdal_translate`, `gdalwarp`, `ogr2ogr`, `gdalbuildvrt`, `gdaldem`, `gdal_rasterize`, `gdal_grid`, and the new `gdal` CLI)
- **Fast and lean.**
  - Interop is source-generated `LibraryImport` against the GDAL C API, without SWIG.
  - Reads are zero-copy into `Span<T>`.
  - Large rasters are streamed in pooled row blocks.
  - Rendering reads only the visible window and uses overviews.
  - The heavy pixel loops (statistics, histograms, NDVI, colorizing) run in **Rust** with rayon. A bit-identical managed SIMD fallback runs where the Rust binary is not available.
- **Safe by construction.** Every native object lives in one `SafeHandle` type that keeps a link to its parent. Using a band after its dataset was disposed throws `ObjectDisposedException` instead of crashing the process.
- **Batteries included.** GDAL 3.13.3, PROJ 9.8.1 and GEOS ship for Windows, Linux and macOS (x64 and arm64), together with `gdal-data`, `proj.db` and a CA bundle for HTTPS. There is nothing to install.
- **UI components.**
  - A pan/zoom map for rasters and vectors with hit testing
  - An attribute table, histogram, color legend and dataset inspector
  - A CRS explorer
  
  Each one is available for Blazor and for Avalonia.

| Package | Use it for |
|---|---|
| [`Gravicode.GDAL.Net`](src/GDAL.Net) | Core library, native binaries, processing, rendering |
| [`Gravicode.GDAL.Net.Blazor`](src/GDAL.Net.Blazor) | `<GdalMap>`, `<GdalAttributeTable>`, `<GdalHistogram>`, `<GdalSrsExplorer>`, … (Interactive Server) |
| [`Gravicode.GDAL.Net.Avalonia`](src/GDAL.Net.Avalonia) | `GdalMapView`, `GdalAttributeGrid`, `GdalHistogramChart`, `GdalSrsExplorer`, … (Windows, macOS, Linux) |

## Quick start

```bash
dotnet add package Gravicode.GDAL.Net
```

```csharp
using GDAL.Net;
using GDAL.Net.Processing;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

// Raster: open a GeoTIFF, compute statistics, reproject to WGS 84 and export a Cloud Optimized GeoTIFF
using var dem = Gdal.OpenRaster("dem.tif");
var stats = dem.GetBand(1).ComputeStatistics();
Console.WriteLine($"{dem.RasterWidth}x{dem.RasterHeight} {dem.SpatialReference}  {stats.Minimum:0}–{stats.Maximum:0} m");

using var wgs84 = GdalUtils.Warp(dem, "dem_wgs84.tif", new WarpOptions { TargetSrs = "EPSG:4326", Resampling = ResampleAlgorithm.Bilinear });
using var cog = GdalUtils.Translate(dem, "dem_cog.tif", new TranslateOptions
{
    Format = "COG",
    CreationOptions = new Dictionary<string, string> { ["COMPRESS"] = "ZSTD", ["PREDICTOR"] = "YES" },
});

// Remote sensing: NDVI from a multispectral scene (Rust kernels)
using var scene = Gdal.OpenRaster("sentinel2.tif");
using var ndvi = SpectralIndices.Ndvi(nir: scene.GetBand(4), red: scene.GetBand(3));

// Vector: SQL over a GeoPackage, GEOS geometry operations
using var cities = Gdal.OpenVector("cities.gpkg");
using var big = cities.ExecuteSql("SELECT name, geom FROM cities WHERE population > 1000000", dialect: "SQLITE")!;
foreach (var feature in big)
{
    using (feature)
    {
        using var buffer = feature.Geometry!.Buffer(0.25);
        Console.WriteLine($"{feature.GetString("name")}: {buffer.Area:0.00} deg²");
    }
}

// Coordinates: WGS 84 → UTM 48S
using var ct = CoordinateTransformation.Create(4326, 32748);
var (x, y) = ct.Transform(107.6191, -6.9175);
```

### Blazor

```bash
dotnet add package Gravicode.GDAL.Net.Blazor
```

```razor
@using GDAL.Net
@using GDAL.Net.Blazor
@using GDAL.Net.Rendering
@rendermode InteractiveServer

<GdalMap Rasters="rasters" Vectors="vectors" CrsLabel="EPSG:32748" Style="height:560px"
         OnFeatureClick="c => selected = c.Feature" />

@code {
    Dataset dem = Gdal.OpenRaster("dem.tif");
    IReadOnlyList<MapRaster> rasters = [];
    List<StyledVector> vectors = [];
    VectorFeature? selected;

    protected override void OnInitialized()
    {
        rasters = [new MapRaster(dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "DEM")];
        using var contours = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval: 50);
        vectors.Add(new StyledVector(VectorData.Load(contours.GetLayer(0)), VectorStyle.Lines(new Rgba(200, 85, 61))));
    }
}
```

GDAL runs natively on the server, so the components need **Interactive Server** rendering. More in [docs/en/blazor.md](docs/en/blazor.md).

### Avalonia

```bash
dotnet add package Gravicode.GDAL.Net.Avalonia
```

```xml
<!-- App.axaml -->
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://GDAL.Net.Avalonia/Themes/GdalNetTheme.axaml" />
</Application.Styles>

<!-- MainWindow.axaml, xmlns:gdal="using:GDAL.Net.Avalonia" -->
<gdal:GdalMapView x:Name="Map" CrsLabel="EPSG:32748" />
```

```csharp
var dem = Gdal.OpenRaster("dem.tif");
Map.Rasters = [new MapRasterLayer(dem, new GDAL.Net.Rendering.RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "DEM")];
Map.FeatureClicked += (_, e) => Console.WriteLine(e.Feature["name"]);
```

More in [docs/en/avalonia.md](docs/en/avalonia.md).

## GDAL.Net Gallery

Two gallery apps, one for the web and one for the desktop, run the **same 32 interactive use cases**. All of them use real GDAL.Net code against data generated on first launch: a DEM and a Sentinel-2-like scene of the Bandung basin, Indonesian cities, land cover polygons and a netCDF climate cube. Every sample shows its parameters, results (map, metrics, table, histogram, text output) and the C# code that ran. Both apps switch between English and Bahasa Indonesia and have light and dark modes.

```bash
dotnet run --project samples/GDAL.Net.Gallery.Blazor      # http://localhost:5280
dotnet run --project samples/GDAL.Net.Gallery.Avalonia
```

| Blazor (web) | Avalonia (desktop) |
|---|---|
| ![](docs/images/blazor-home.png) | ![](docs/images/avalonia-home.png) |
| ![](docs/images/blazor-contours.png) | ![](docs/images/avalonia-viewshed.png) |
| ![](docs/images/blazor-ndvi.png) | ![](docs/images/avalonia-composites.png) |
| ![](docs/images/blazor-cities.png) | ![](docs/images/avalonia-sql.png) |
| ![](docs/images/blazor-climate-cube.png) | ![](docs/images/avalonia-climate-cube.png) |
| ![](docs/images/blazor-dark.png) | ![](docs/images/avalonia-dark.png) |

The full list of samples is in [docs/en/gallery.md](docs/en/gallery.md).

## Feature coverage

| Area | GDAL.Net API |
|---|---|
| Raster I/O, metadata, overviews, color tables, RAT, GCPs | `Gdal.Open*`, `Dataset`, `RasterBand`, `ColorTable`, `RasterAttributeTable` |
| Vector I/O, fields, geometry, filters, transactions | `Layer`, `Feature`, `Geometry`, `FieldDefinition`, `Transaction` |
| SQL engine (OGR SQL, SQLite/SpatiaLite, native DB SQL) | `Dataset.ExecuteSql` |
| SRS: EPSG, WKT1/WKT2, PROJJSON, ESRI, PROJ; datum transformations | `SpatialReference`, `CoordinateTransformation`, `CrsDatabase` |
| Command line utilities | `GdalUtils.Info/Translate/Warp/VectorTranslate/BuildVrt/DemProcessing/Rasterize/Grid`, `GdalCli.Run` |
| Terrain and raster analysis | `RasterAlgorithms.GenerateContours/Polygonize/Viewshed/FillNoData/Sieve` |
| Band math, NDVI/NDWI/SAVI/EVI | `BandMath`, `SpectralIndices`, `Kernels` (Rust) |
| Cloud and virtual file systems | `VirtualFileSystem` (`/vsimem/`, `/vsizip/`, `/vsitar/`, `/vsicurl/`, `/vsis3/`, `/vsiaz/`, `/vsigs/`), `VsiStream` |
| Multidimensional (netCDF, HDF5, Zarr) | `Gdal.OpenMultidimensional`, `MdGroup`, `MdArray` |
| Rendering and tiles | `RasterRenderer`, `ColorRamp`, `RgbaImage`, `VectorData`, `TileGenerator` |

See the [feature matrix](docs/en/feature-matrix.md) for the item-by-item mapping of [features.md](features.md).

## Building from source

```bash
dotnet build GDAL.Net.slnx
dotnet test --project tests/GDAL.Net.Tests          # 133 tests: core + every gallery sample end-to-end
pwsh build/build-native.ps1                         # rebuild the Rust kernels (optional, needs cargo)
pwsh build/pack.ps1 -Version 1.0.0                  # NuGet packages into ./artifacts
```

Requirements: the .NET 10 SDK. A Rust toolchain is optional and only needed to rebuild the kernels.

## License

MIT. GDAL, PROJ and GEOS are distributed under their own permissive licenses (MIT/X, MIT, LGPL), via the MaxRev.Gdal runtime packages.

---

<p align="center">Created by <b>Gravicode Studios</b>, led by <b>Kang Fadhil</b>.</p>
