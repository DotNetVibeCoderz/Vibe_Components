# GDAL.Net — Progress / Kemajuan

> Plan: [PLAN.md](PLAN.md) · Created by Gravicode Studios, led by Kang Fadhil

Legend: ✅ done · 🟡 in progress · ⏳ planned

## Milestones

| # | Milestone | Status | Notes |
|---|---|---|---|
| M1 | Native foundation | ✅ | `NativeLibraryLoader` (all RIDs, versioned sonames), `GdalSafeHandle` parent chains, `Gdal.Log`, config scopes, progress/cancellation bridge (`UnmanagedCallersOnly`) |
| M2 | Raster core | ✅ | Span-based `Read`/`Write`, resampled reads, `ProcessRows` streaming, interleaved dataset IO, statistics, histograms, overviews, color tables, RAT, GCPs, metadata domains |
| M3 | Vector core | ✅ | Layers/fields/features, geometry factories & exports, GEOS predicates/operations, filters, transactions, OGR SQL + SQLite dialect, 7 layer overlays, dissolve |
| M4 | SRS | ✅ | EPSG/WKT1/WKT2/PROJJSON/ESRI/PROJ, transformation options (AOI, pipelines, ballpark), bounds transform, CRS database search |
| M5 | Utilities | ✅ | gdalinfo, ogrinfo, gdalmdiminfo, gdal_translate, gdalwarp (+WarpInto), ogr2ogr, gdalbuildvrt, gdaldem, gdal_rasterize (+Into), gdal_grid, unified `gdal` CLI (`GdalCli`) with typed options |
| M6 | Analysis | ✅ | Contours (lines/polygons), polygonize, viewshed, fill nodata, sieve, GCP polynomial/TPS, RPC options, cutline clipping, VRT mosaics |
| M7 | Processing & Rust | ✅ | `BandMath` vectorized expression engine, NDVI/NDWI/MNDWI/NDBI/NBR/SAVI/EVI, Rust kernels (stats, histogram, NDI, colorize, RGB composite, shade blend) + identical managed fallback |
| M8 | Cloud & multidim | ✅ | `/vsimem/` buffers, zip/tar/gzip, curl, S3/Azure/GCS helpers, `VsiStream`, `MdGroup`/`MdArray` read/write, `AsClassicDataset`, netCDF creation |
| M9 | Rendering | ✅ | `RasterRenderer` (auto/single/RGB/palette, stretches, hillshade, floating windows), `ColorRamp` ×11, PNG/JPEG/WebP encoders, `VectorData` + hit testing, `ScreenTransform`, `TileGenerator` (XYZ + Leaflet preview) |
| M10 | Blazor components | ✅ | GdalMap (server-side raster PNG + SVG vectors, client-side pan/zoom, hit testing, scale bar), GdalAttributeTable, GdalHistogram, GdalColorLegend, GdalDatasetInfo, GdalSrsExplorer, GdalProgressBar |
| M11 | Avalonia controls | ✅ | GdalMapView (background rendering, native vectors, keyboard), GdalAttributeGrid, GdalHistogramChart, GdalColorLegend, GdalDatasetInfoPanel, GdalSrsExplorer, theme |
| M12 | Gallery | ✅ | 32 bilingual use cases; Blazor + Avalonia apps with contour signature header, EN/ID, dark mode; 24 screenshots |
| M13 | Docs & release | 🟡 | Icon ✅, bilingual docs (18 pages × 2) ✅, README EN/ID ✅, packages packed & verified in a clean consumer ✅, moved to the Vibe_Components monorepo ✅, GitHub Actions CI (Rust kernels for win-x64, linux-x64/arm64, osx-x64/arm64) and NuGet publishing on `gdal-net-v*` tags ✅ |

## Test status

| Suite | Result |
|---|---|
| Core (runtime, raster, vector, SRS, utilities, VSI, multidim, processing, rendering) | ✅ 70 / 70 |
| Gallery samples end-to-end (defaults + every choice option) | ✅ 63 / 63 |
| Package smoke test (fresh consumer via local feed: natives, gdal-data, proj.db, Rust kernels, HTTPS) | ✅ |
| Rust kernels (`cargo test`) | ✅ 3 / 3 |

## Log

- **2026-10-27**
  - Blazor and Avalonia component libraries and gallery apps finished, with screenshots.
  - Fixes: vsicurl HTTPS (bundled CA certificates), `MdArray.NoDataValue` error handling.
  - NuGet packages built.
- **2026-10-26**
  - Solution scaffolded; core library, Rust kernels and the test suite are complete.
  - Gallery catalog finished: 30 use cases with synthetic Bandung DEM/scene data, Indonesian cities, land cover and a climate cube.
  - Icon generated from real GDAL.Net contours.
