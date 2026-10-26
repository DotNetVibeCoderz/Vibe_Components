# GDAL.Net — Development Plan / Rencana Pengembangan

> Created by Gravicode Studios, led by Kang Fadhil · *Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil*

GDAL.Net is a modern .NET 10 wrapper of GDAL/OGR/OSR with Blazor and Avalonia UI components and a gallery of use cases.
Tracking of what is done lives in [Progress.md](Progress.md).

*GDAL.Net adalah wrapper .NET 10 modern untuk GDAL/OGR/OSR, dilengkapi komponen Blazor dan Avalonia serta galeri use case. Status pengerjaan ada di [Progress.md](Progress.md).*

## Goals / Tujuan

1. **Complete coverage** of the GDAL feature list in [features.md](features.md): raster & vector I/O, SRS, raster/vector processing, SQL, VRT, cloud VFS, multidimensional, metadata, the command line utilities and advanced GIS.
2. **Performance & memory efficiency**:
   - source-generated interop (`LibraryImport`)
   - zero-copy `Span<T>` I/O
   - pooled row-block streaming
   - overview-aware rendering
   - Rust (rayon) kernels for hot loops, with a managed SIMD fallback
3. **Safety**: a single `SafeHandle` type with parent chains, so using a disposed object throws `ObjectDisposedException` instead of crashing the process.
4. **UI components** for Blazor (web) and Avalonia (desktop), sharing one rendering pipeline.
5. **Gallery** apps (Blazor and Avalonia) running the same catalog of real use cases on generated sample data.
6. **Bilingual documentation** (English / Bahasa Indonesia) with screenshots.
7. **NuGet packages** with an icon: `Gravicode.GDAL.Net`, `Gravicode.GDAL.Net.Blazor` and `Gravicode.GDAL.Net.Avalonia`.

## Architecture / Arsitektur

```
GDAL.Net/
├─ src/GDAL.Net              Core: P/Invoke (Native/), Dataset, RasterBand, Vector, Srs, Utilities,
│                            Processing (BandMath, SpectralIndices, Kernels), Vsi, MultiDimensional,
│                            Rendering (RasterRenderer, ColorRamp, VectorData, TileGenerator)
├─ src/GDAL.Net.Blazor       Razor components (GdalMap, histogram, attribute table, SRS explorer, ...)
├─ src/GDAL.Net.Avalonia     Avalonia controls (GdalMapView, histogram, attribute grid, SRS explorer, ...)
├─ native/gdalnet-kernels    Rust cdylib: statistics, histogram, normalized difference, colorize, composites
├─ samples/GDAL.Net.Gallery.Shared   Sample data generator + bilingual use-case catalog (30 samples)
├─ samples/GDAL.Net.Gallery.Blazor   Gallery web app (Interactive Server)
├─ samples/GDAL.Net.Gallery.Avalonia Gallery desktop app
├─ tests/GDAL.Net.Tests      xUnit v3 (core + every gallery sample end-to-end)
├─ docs/en, docs/id          Documentation
└─ build/                    icon, PACKAGE.md, build-native.ps1, pack.ps1, publish.ps1
```

**Native binaries:**
- GDAL 3.13.3, PROJ 9.8.1 and GEOS come from the `MaxRev.Gdal.*Runtime.Minimal` packages (win-x64, linux-x64/arm64, osx-x64/arm64).
- GDAL.Net binds directly to the GDAL C API. It does not use the SWIG bindings.
- `gdal-data` is shipped inside the core package.

## Milestones / Tahapan

| # | Milestone | Scope |
|---|---|---|
| M1 | Native foundation | Library loader, SafeHandle model, errors/logging, config, drivers, progress & cancellation |
| M2 | Raster core | Dataset/RasterBand I/O, georeferencing, nodata, statistics, histograms, overviews, color tables, RAT, GCPs, metadata |
| M3 | Vector core | Layers, fields, features, geometry (GEOS ops), filters, transactions, SQL, layer overlay |
| M4 | SRS | EPSG/WKT1/WKT2/PROJJSON/ESRI/PROJ, transformations incl. datum options, CRS database search |
| M5 | Utilities | gdalinfo, gdal_translate, gdalwarp, ogr2ogr, ogrinfo, gdalbuildvrt, gdaldem, gdal_rasterize, gdal_grid, gdalmdiminfo, unified `gdal` CLI |
| M6 | Analysis | Contours, polygonize, viewshed, fill nodata, sieve, GCP/TPS/RPC rectification, clipping, mosaics |
| M7 | Processing & Rust | BandMath engine, spectral indices, Rust kernels + managed fallback |
| M8 | Cloud & multidim | VSI (mem/zip/tar/curl/s3/az/gs), VsiStream, multidimensional API (netCDF/HDF5/Zarr) |
| M9 | Rendering | RasterRenderer, ColorRamp, RgbaImage encoders, VectorData, ScreenTransform, XYZ TileGenerator |
| M10 | Blazor components | GdalMap, GdalHistogram, GdalAttributeTable, GdalColorLegend, GdalDatasetInfo, GdalSrsExplorer, GdalProgressBar |
| M11 | Avalonia controls | GdalMapView, GdalHistogramChart, GdalAttributeGrid, GdalColorLegend, GdalDatasetInfoPanel, GdalSrsExplorer |
| M12 | Gallery | Shared catalog (30 use cases) + Blazor and Avalonia apps, EN/ID |
| M13 | Docs & release | Bilingual docs, screenshots, icon, NuGet packaging, monorepo move, publish |

## Roadmap after 1.0 / Peta jalan setelah 1.0

- **1.1**:
  - more Rust kernels (focal statistics, zonal statistics, reclassify)
- **1.2**:
  - async-friendly helpers (`Task`-based wrappers for utilities with progress)
  - `IAsyncEnumerable` feature streaming
  - Arrow stream interface (`OGR_L_GetArrowStream`) for columnar vector reads
- **1.3**:
  - GNM (network) and raster pipeline builder (`gdal pipeline`) typed API
  - typed options for every `gdal` CLI algorithm generated from `GetUsageJson`
- **1.4**:
  - map components: basemap tiles (OSM/XYZ) under GDAL layers
  - measuring and drawing tools
  - vector editing in the components
- **Integration** with the Vibe libraries where useful:
  - Visual.Net charts for histograms and profiles
  - GravicodeScience for ML on rasters
  - ThreeNet for 3D terrain
