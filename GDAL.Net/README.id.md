<p align="center"><img src="build/icon.png" width="112" alt="Logo GDAL.Net"></p>

<h1 align="center">GDAL.Net</h1>

<p align="center"><b>GDAL untuk .NET modern</b>: baca, tulis, konversi, reproyeksi, dan analisis lebih dari 200 format raster dan vektor langsung dari C#.<br>
Sudah termasuk GDAL 3.13 native, kernel yang dipercepat Rust, serta komponen siap pakai untuk <b>Blazor</b> (web) dan <b>Avalonia UI</b> (desktop).</p>

<p align="center"><a href="README.md">English</a> · <a href="docs/id/index.md">Dokumentasi</a> · <a href="docs/id/gallery.md">Galeri</a> · <a href="PLAN.md">Roadmap</a> · <a href="Progress.md">Progres</a></p>

![GDAL.Net Gallery (Avalonia) — analisis viewshed](docs/images/avalonia-viewshed.png)

## Mengapa GDAL.Net

- **Seluruh GDAL, dengan C# yang idiomatis.** Cakupannya:
  - I/O raster dan vektor
  - reproyeksi beserta transformasi datum
  - warping dan mosaik (VRT)
  - analisis DEM: hillshade, kemiringan, kontur, viewshed
  - polygonize dan rasterize
  - OGR SQL dan dialek SQLite
  - operasi geometri GEOS
  - object storage cloud
  - kubus data netCDF/HDF5
  - semua utilitas command line, dipanggil di dalam proses: `gdalinfo`, `gdal_translate`, `gdalwarp`, `ogr2ogr`, `gdalbuildvrt`, `gdaldem`, `gdal_rasterize`, `gdal_grid`, dan CLI `gdal` yang baru
- **Cepat dan hemat memori.**
  - Interop memakai `LibraryImport` hasil source generator langsung ke C API GDAL, tanpa SWIG.
  - Pembacaan zero-copy ke `Span<T>`.
  - Raster besar diproses per blok baris dengan buffer pool.
  - Rendering hanya membaca area yang terlihat dan memakai overview.
  - Loop piksel berat (statistik, histogram, NDVI, pewarnaan) berjalan di **Rust** dengan rayon. Ada fallback SIMD managed yang hasilnya identik.
- **Aman sejak desain.** Setiap objek native disimpan dalam satu tipe `SafeHandle` yang menyimpan rantai ke induknya. Memakai band setelah dataset-nya di-dispose akan melempar `ObjectDisposedException`, bukan membuat proses crash.
- **Semua sudah disertakan.** GDAL 3.13.3, PROJ 9.8.1, dan GEOS tersedia untuk Windows, Linux, dan macOS (x64 dan arm64), lengkap dengan `gdal-data`, `proj.db`, dan CA bundle untuk HTTPS. Tidak perlu instalasi apa pun.
- **Komponen UI** untuk Blazor dan Avalonia:
  - peta pan/zoom untuk raster dan vektor, dengan hit testing
  - tabel atribut
  - histogram
  - legenda warna
  - inspektor dataset
  - penjelajah CRS

| Paket | Kegunaan |
|---|---|
| [`Gravicode.GDAL.Net`](src/GDAL.Net) | Library inti, binary native, pemrosesan, rendering |
| [`Gravicode.GDAL.Net.Blazor`](src/GDAL.Net.Blazor) | `<GdalMap>`, `<GdalAttributeTable>`, `<GdalHistogram>`, `<GdalSrsExplorer>`, … (Interactive Server) |
| [`Gravicode.GDAL.Net.Avalonia`](src/GDAL.Net.Avalonia) | `GdalMapView`, `GdalAttributeGrid`, `GdalHistogramChart`, `GdalSrsExplorer`, … (Windows, macOS, Linux) |

## Mulai cepat

```bash
dotnet add package Gravicode.GDAL.Net
```

```csharp
using GDAL.Net;
using GDAL.Net.Processing;
using GDAL.Net.Srs;

// Raster: buka GeoTIFF, hitung statistik, reproyeksi ke WGS 84, lalu ekspor Cloud Optimized GeoTIFF
using var dem = Gdal.OpenRaster("dem.tif");
var stats = dem.GetBand(1).ComputeStatistics();
Console.WriteLine($"{dem.RasterWidth}x{dem.RasterHeight} {dem.SpatialReference}  {stats.Minimum:0}–{stats.Maximum:0} m");

using var wgs84 = GdalUtils.Warp(dem, "dem_wgs84.tif", new WarpOptions { TargetSrs = "EPSG:4326", Resampling = ResampleAlgorithm.Bilinear });
using var cog = GdalUtils.Translate(dem, "dem_cog.tif", new TranslateOptions
{
    Format = "COG",
    CreationOptions = new Dictionary<string, string> { ["COMPRESS"] = "ZSTD", ["PREDICTOR"] = "YES" },
});

// Penginderaan jauh: NDVI dari citra multispektral (kernel Rust)
using var scene = Gdal.OpenRaster("sentinel2.tif");
using var ndvi = SpectralIndices.Ndvi(nir: scene.GetBand(4), red: scene.GetBand(3));

// Vektor: SQL pada GeoPackage
using var cities = Gdal.OpenVector("cities.gpkg");
using var big = cities.ExecuteSql("SELECT name, population FROM cities ORDER BY population DESC LIMIT 5", dialect: "SQLITE")!;
foreach (var row in big.ReadRecords()) Console.WriteLine($"{row.Attributes["name"]}: {row.Attributes["population"]:N0}");

// Koordinat: WGS 84 → UTM 48S
using var ct = CoordinateTransformation.Create(4326, 32748);
var (x, y) = ct.Transform(107.6191, -6.9175);
```

Contoh Blazor (`<GdalMap>`) dan Avalonia (`GdalMapView`) ada di [README berbahasa Inggris](README.md#blazor), [docs/id/blazor.md](docs/id/blazor.md), dan [docs/id/avalonia.md](docs/id/avalonia.md).

## GDAL.Net Gallery

Ada dua aplikasi galeri, web dan desktop. Keduanya menjalankan **32 use case interaktif yang sama**, semuanya memakai kode GDAL.Net sungguhan. Datanya dibuat otomatis saat aplikasi pertama kali dijalankan:
- DEM dan citra mirip Sentinel-2 untuk cekungan Bandung
- kota-kota Indonesia
- poligon tutupan lahan
- kubus iklim netCDF

Setiap sampel menampilkan:
- parameter
- hasil: peta, metrik, tabel, histogram, dan output teks
- kode C# yang dijalankan

Bahasa bisa diganti antara Inggris dan Bahasa Indonesia, dan tersedia mode terang serta gelap.

```bash
dotnet run --project samples/GDAL.Net.Gallery.Blazor      # http://localhost:5280
dotnet run --project samples/GDAL.Net.Gallery.Avalonia
```

| Blazor (web) | Avalonia (desktop) |
|---|---|
| ![](docs/images/blazor-home.png) | ![](docs/images/avalonia-indonesian.png) |
| ![](docs/images/blazor-raster-styling.png) | ![](docs/images/avalonia-hillshade.png) |
| ![](docs/images/blazor-spatial-join.png) | ![](docs/images/avalonia-land-cover.png) |
| ![](docs/images/blazor-components.png) | ![](docs/images/avalonia-components.png) |

Daftar lengkap sampel ada di [docs/id/gallery.md](docs/id/gallery.md).

## Build dari source

```bash
dotnet build GDAL.Net.slnx
dotnet test --project tests/GDAL.Net.Tests          # 133 test: inti + semua sampel galeri end-to-end
pwsh build/build-native.ps1                         # build ulang kernel Rust (opsional, butuh cargo)
pwsh build/pack.ps1 -Version 1.0.0                  # paket NuGet ke ./artifacts
```

## Lisensi

MIT. GDAL, PROJ, dan GEOS memakai lisensinya masing-masing (MIT/X, MIT, LGPL) dan didistribusikan melalui paket runtime MaxRev.Gdal.

---

<p align="center">Dibuat oleh <b>Gravicode Studios</b> dipimpin oleh <b>Kang Fadhil</b>.</p>
