# GDAL.Net documentation

GDAL.Net brings GDAL 3.13 (raster, vector, coordinate systems, utilities, multidimensional data and cloud file systems) to .NET 10 through a modern, safe and fast API.

*Bahasa Indonesia: [docs/id/index.md](../id/index.md)*

## Packages

| Package | Namespace | Use it for |
|---|---|---|
| `Gravicode.GDAL.Net` | `GDAL.Net` (+ `.Vector`, `.Srs`, `.Processing`, `.Rendering`, `.Vsi`, `.MultiDimensional`) | Core API. Ships the native GDAL 3.13.3 / PROJ 9.8.1 / GEOS binaries (via the MaxRev runtime packages) for Windows, Linux and macOS, plus the Rust kernels for win, linux and osx (x64 & arm64; built by CI). |
| `Gravicode.GDAL.Net.Blazor` | `GDAL.Net.Blazor` | Map, attribute table, histogram, legend and SRS explorer components for Blazor Server / Web Apps. See [blazor.md](blazor.md). |
| `Gravicode.GDAL.Net.Avalonia` | `GDAL.Net.Avalonia` | The same components as Avalonia controls for desktop apps. See [avalonia.md](avalonia.md). |

## What's inside

```csharp
using GDAL.Net;

using var dem = Gdal.OpenRaster("dem.tif");
var stats = dem.GetBand(1).ComputeStatistics();
using var wgs84 = GdalUtils.Warp(dem, "dem_4326.tif", new WarpOptions { TargetSrs = "EPSG:4326" });
Console.WriteLine($"{stats.Minimum:0} – {stats.Maximum:0} m, GDAL {Gdal.Version}");
```

## Guide

| Page | Topics |
|---|---|
| [Getting started](getting-started.md) | Installation, first program, platforms, native discovery |
| [Core concepts](core-concepts.md) | Ownership and disposal, threading, errors, config options, progress and cancellation, axis order |
| [Raster](raster.md) | Open/create, pixel I/O, georeferencing, statistics, overviews, palettes, RAT, metadata, GCPs, COG |
| [Vector](vector.md) | Layers, fields, features, geometry, GEOS operations, filters, transactions, SQL, overlay |
| [Coordinate systems](srs.md) | `SpatialReference`, WKT1/WKT2/PROJJSON/ESRI/PROJ, transformations, datum shifts, EPSG search |
| [Utilities](utilities.md) | gdalinfo, gdal_translate, gdalwarp, ogr2ogr, gdalbuildvrt, gdaldem, gdal_rasterize, gdal_grid, the `gdal` CLI |
| [Analysis](analysis.md) | DEM products, contours, polygonize, viewshed, fill nodata, sieve, georeferencing, clipping, mosaics |
| [Remote sensing](remote-sensing.md) | Band math, NDVI/NDWI/SAVI/EVI, Rust/SIMD kernels |
| [Cloud & virtual files](cloud-and-vsi.md) | /vsimem/, /vsizip/, /vsicurl/, /vsis3/, /vsiaz/, /vsigs/, `VsiStream` |
| [Multidimensional](multidimensional.md) | netCDF/HDF5/Zarr groups, arrays, dimensions, attributes, slicing |
| [Rendering](rendering.md) | Raster → RGBA, color ramps, PNG encoding, vector snapshots, XYZ tiles |
| [Performance](performance.md) | How GDAL.Net stays fast and memory-frugal |
| [Feature matrix](feature-matrix.md) | Every GDAL capability mapped to its GDAL.Net API |
| [Troubleshooting](troubleshooting.md) | Common problems and fixes |
| [Blazor components](blazor.md) · [Avalonia controls](avalonia.md) · [Gallery](gallery.md) | UI components and the sample gallery |

---

Created by Gravicode Studios, led by Kang Fadhil.

**Next:** [Getting started](getting-started.md)
