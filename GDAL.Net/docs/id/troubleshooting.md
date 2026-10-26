# Pemecahan masalah

Halaman ini berisi masalah umum saat memakai GDAL.Net beserta solusinya.

## `DllNotFoundException` / `TypeInitializationException` pada panggilan pertama

Library GDAL native gagal dimuat.

- Pastikan aplikasi mereferensikan `Gravicode.GDAL.Net`, yang membawa paket `MaxRev.Gdal.*Runtime.Minimal`. Periksa apakah `runtimes/<rid>/native/gdal.dll` (Windows), `libgdal.so.*` (Linux), atau `libgdal.*.dylib` (macOS) ada di folder output.
- Hanya proses 64-bit (x64/arm64) yang didukung. Matikan opsi "Prefer 32-bit".
- Di Windows, semua DLL dalam folder runtime harus tetap bersama. Jangan menyalin `gdal.dll` sendirian.
- Untuk memakai GDAL sistem, set `GDALNET_LIBRARY_PATH` ke path lengkap library-nya.
- Container Linux butuh runtime C/C++ standar (`libstdc++`). Alpine/musl tidak didukung oleh build bawaan.
- `Gdal.NativeLibraryPath` menunjukkan library yang dimuat.

## "PROJ: proj_create_from_database: Cannot find proj.db"

Ini berarti `Gdal.ProjDataPath` bernilai `null`. Paket runtime menyertakan `proj.db` di `runtimes/<rid>/native/maxrev.gdal.core.libshared/`. Jika Anda publish dengan tata letak khusus, salin folder itu ke samping aplikasi, atau set `PROJ_DATA` ke folder berisi `proj.db` sebelum panggilan GDAL pertama.

## `gdal-data` tidak ditemukan

Driver seperti DXF, GML, S-57, dan tabel CF netCDF membaca file pendukung dari `GDAL_DATA`. Paket menyalin `gdal-data/` ke folder output (lewat `buildTransitive`). Periksa `Gdal.DataPath`, atau set environment variable `GDAL_DATA`.

## Koordinat tertukar

GDAL.Net secara default memakai x = bujur dan y = lintang. Jika Anda mengatur `AxisMappingStrategy.AuthorityCompliant`, EPSG:4326 mengharapkan lintang lebih dulu. WKT dari tool lain tidak mengubah hal ini, karena GDAL.Net menerapkan urutan GIS ke setiap `SpatialReference` yang dibuatnya.

## `/vsicurl/` dan akses cloud

- Pasang `Gdal.Log += …` untuk melihat error HTTP (403, 404, timeout).
- Set `GDAL_DISABLE_READDIR_ON_OPEN=EMPTY_DIR` agar GDAL tidak lambat me-listing direktori saat membuka file.
- Di balik proxy, set `GDAL_HTTP_PROXY` (dan `GDAL_HTTP_PROXYUSERPWD`).
- Di jaringan kantor yang memeriksa TLS, set `CURL_CA_BUNDLE` ke bundle CA Anda, atau (hanya untuk pengujian) `GDAL_HTTP_UNSAFESSL=YES`.
- Untuk bucket S3 publik, panggil `VirtualFileSystem.ConfigureS3(null, null, region, noSign: true)`.
- Setelah file jarak jauh berubah, panggil `VirtualFileSystem.ClearCurlCache()`.

## "Cannot create TIFF file due to missing codec" / opsi tidak didukung

Build bawaan adalah build *minimal* MaxRev, jadi beberapa codec tidak tersedia, misalnya LERC. Periksa `Gdal.BuildInfo` dan `CreationOptionList` milik driver. Aturan codec lainnya:

- WebP dan JPEG di dalam GeoTIFF/COG membutuhkan data Byte. WebP juga membutuhkan 3 atau 4 band.
- Untuk DEM Float32 satu band, gunakan `DEFLATE`, `ZSTD`, atau `LZW` dengan `PREDICTOR=YES`.

## Gagal menulis GeoTIFF ke dalam ZIP

Penulisan ke `/vsizip/` bersifat berurutan, sedangkan GeoTIFF butuh akses acak. Tulis dulu ke `/vsimem/`, lalu jalankan `VirtualFileSystem.CopyFile(tmp, VirtualFileSystem.Zip(zip, "file.tif"))`.

## File keluaran kosong atau terpotong

Beberapa driver (GeoJSON, KML, Shapefile, PNG) baru selesai menulis saat dataset ditutup. Dispose dataset hasil `Create`, `CreateCopy`, `Translate`, atau `VectorTranslate` sebelum membaca filenya, atau panggil `Close()` agar error saat flush terlihat.

## `ObjectDisposedException`

Anda memakai band, layer, fitur, atau geometri setelah pemiliknya (dataset atau fitur) di-dispose. Jaga pemiliknya tetap hidup, atau `Clone()` geometri yang masih Anda butuhkan.

## Crash atau hasil rusak saat beban paralel

Sebuah `Dataset` (dan semua yang diperoleh darinya) hanya boleh dipakai oleh satu thread pada satu waktu. Buka file sekali per thread.

## Sampel galeri atau `GdalCli` gagal dengan "invalid arguments"

Nama argumen CLI `gdal` mengikuti versi GDAL yang dipakai (3.13 memakai `--output-crs`, `--output-format`, …). Tanyakan langsung ke GDAL dengan `GdalCli.GetUsageJson("raster", "reproject")`.

## Catatan Windows PowerShell 5.1

- `cargo` dan `dotnet` menulis progres ke stderr. Dengan `$ErrorActionPreference = 'Stop'`, itu dianggap error, sehingga `build/build-native.ps1` membungkus perintah native.
- `dotnet test` di .NET 10 memakai Microsoft.Testing.Platform (lihat `global.json`). Jalankan `dotnet test --project tests/GDAL.Net.Tests`.

## Mendapatkan diagnostik lebih lengkap

```csharp
Gdal.SetConfigOption("CPL_DEBUG", "ON");
Gdal.Log += m => Console.WriteLine($"[{m.Level}] {m.Message}");
```

**Kembali ke:** [Indeks](index.md)
