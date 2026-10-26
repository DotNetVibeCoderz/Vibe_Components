# Dokumentasi GDAL.Net

GDAL.Net menghadirkan GDAL 3.13 (raster, vektor, sistem koordinat, utilitas, data multidimensi, dan file system cloud) ke .NET 10 lewat API yang modern, aman, dan cepat.

*English: [docs/en/index.md](../en/index.md)*

## Paket

| Paket | Namespace | Kegunaan |
|---|---|---|
| `Gravicode.GDAL.Net` | `GDAL.Net` (+ `.Vector`, `.Srs`, `.Processing`, `.Rendering`, `.Vsi`, `.MultiDimensional`) | API inti. Sudah membawa binary native GDAL 3.13.3 / PROJ 9.8.1 / GEOS (lewat paket runtime MaxRev) untuk Windows, Linux, dan macOS, plus kernel Rust untuk win, linux, dan osx (x64 & arm64; di-build oleh CI). |
| `Gravicode.GDAL.Net.Blazor` | `GDAL.Net.Blazor` | Komponen peta, tabel atribut, histogram, legenda, dan penjelajah SRS untuk Blazor Server / Web App. Lihat [blazor.md](blazor.md). |
| `Gravicode.GDAL.Net.Avalonia` | `GDAL.Net.Avalonia` | Komponen yang sama sebagai kontrol Avalonia untuk aplikasi desktop. Lihat [avalonia.md](avalonia.md). |

## Sekilas

```csharp
using GDAL.Net;

using var dem = Gdal.OpenRaster("dem.tif");
var stats = dem.GetBand(1).ComputeStatistics();
using var wgs84 = GdalUtils.Warp(dem, "dem_4326.tif", new WarpOptions { TargetSrs = "EPSG:4326" });
Console.WriteLine($"{stats.Minimum:0} – {stats.Maximum:0} m, GDAL {Gdal.Version}");
```

## Panduan

| Halaman | Topik |
|---|---|
| [Memulai](getting-started.md) | Instalasi, program pertama, platform, pencarian library native |
| [Konsep inti](core-concepts.md) | Kepemilikan & disposal, threading, error, opsi konfigurasi, progres & pembatalan, urutan sumbu |
| [Raster](raster.md) | Buka/buat, I/O piksel, georeferensi, statistik, overview, palet, RAT, metadata, GCP, COG |
| [Vektor](vector.md) | Layer, field, fitur, geometri, operasi GEOS, filter, transaksi, SQL, overlay |
| [Sistem koordinat](srs.md) | `SpatialReference`, WKT1/WKT2/PROJJSON/ESRI/PROJ, transformasi, pergeseran datum, pencarian EPSG |
| [Utilitas](utilities.md) | gdalinfo, gdal_translate, gdalwarp, ogr2ogr, gdalbuildvrt, gdaldem, gdal_rasterize, gdal_grid, CLI `gdal` |
| [Analisis](analysis.md) | Produk DEM, kontur, poligonisasi, viewshed, isi nodata, sieve, georeferensi, pemotongan, mosaik |
| [Penginderaan jauh](remote-sensing.md) | Band math, NDVI/NDWI/SAVI/EVI, kernel Rust/SIMD |
| [Cloud & file virtual](cloud-and-vsi.md) | /vsimem/, /vsizip/, /vsicurl/, /vsis3/, /vsiaz/, /vsigs/, `VsiStream` |
| [Multidimensi](multidimensional.md) | Grup, array, dimensi, atribut netCDF/HDF5/Zarr, pemotongan (slicing) |
| [Rendering](rendering.md) | Raster → RGBA, color ramp, encoding PNG, snapshot vektor, tile XYZ |
| [Performa](performance.md) | Cara GDAL.Net tetap cepat dan hemat memori |
| [Matriks fitur](feature-matrix.md) | Setiap kemampuan GDAL dipetakan ke API GDAL.Net |
| [Pemecahan masalah](troubleshooting.md) | Masalah umum dan solusinya |
| [Komponen Blazor](blazor.md) · [Kontrol Avalonia](avalonia.md) · [Galeri](gallery.md) | Komponen UI dan galeri contoh |

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

**Berikutnya:** [Memulai](getting-started.md)
