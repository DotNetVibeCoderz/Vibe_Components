# GDAL.Net

**GDAL for modern .NET 10**: read, write, convert, reproject and analyse 200+ raster and vector formats from C#. Native GDAL 3.13.3, PROJ 9.8.1 and GEOS are included for Windows, Linux and macOS (x64 and arm64). Hot loops are Rust-accelerated, and there are components for **Blazor** and **Avalonia UI**.

*GDAL untuk .NET modern: baca, tulis, konversi, reproyeksi, dan analisis 200+ format raster dan vektor dari C#. GDAL native sudah termasuk, dengan kernel Rust serta komponen Blazor dan Avalonia.*

| Package | Use it for |
|---|---|
| `Gravicode.GDAL.Net` | Core API: raster/vector I/O, SRS, utilities (gdalinfo, gdal_translate, gdalwarp, ogr2ogr, …), analysis, band math, VSI/cloud, multidimensional, rendering |
| `Gravicode.GDAL.Net.Blazor` | `<GdalMap>`, `<GdalAttributeTable>`, `<GdalHistogram>`, `<GdalColorLegend>`, `<GdalDatasetInfo>`, `<GdalSrsExplorer>` (Interactive Server) |
| `Gravicode.GDAL.Net.Avalonia` | `GdalMapView`, `GdalAttributeGrid`, `GdalHistogramChart`, `GdalColorLegend`, `GdalDatasetInfoPanel`, `GdalSrsExplorer` |

```csharp
using GDAL.Net;
using GDAL.Net.Processing;

using var dem = Gdal.OpenRaster("dem.tif");
var stats = dem.GetBand(1).ComputeStatistics();

using var wgs84 = GdalUtils.Warp(dem, "dem_wgs84.tif", new WarpOptions { TargetSrs = "EPSG:4326" });
using var hillshade = GdalUtils.DemProcessing(dem, "hillshade.tif", DemProcessingMode.Hillshade, new DemOptions { ZFactor = 2 });
using var contours = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval: 50);

using var scene = Gdal.OpenRaster("sentinel2.tif");
using var ndvi = SpectralIndices.Ndvi(scene.GetBand(4), scene.GetBand(3));

using var gpkg = Gdal.OpenVector("cities.gpkg");
using var rows = gpkg.ExecuteSql("SELECT island, SUM(population) FROM cities GROUP BY island", dialect: "SQLITE");
```

Documentation (English and Bahasa Indonesia), the gallery apps and screenshots are on GitHub: https://github.com/DotNetVibeCoderz/Vibe_Components/tree/main/GDAL.Net

Created by Gravicode Studios, led by Kang Fadhil · *Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil*
