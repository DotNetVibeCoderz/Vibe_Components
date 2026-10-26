# Konsep inti

Halaman ini menjelaskan cara GDAL.Net mengelola objek native, thread, error, konfigurasi, progres, dan urutan sumbu.

## Kepemilikan dan disposal

Setiap objek native dibungkus oleh kelas turunan `GdalObject` (yang mengimplementasikan `IDisposable`).

| Objek | Dimiliki oleh | Perlu di-dispose? |
|---|---|---|
| `Dataset` | Anda | **Ya**. Dispose menutup file dan menulis data yang tertunda. Pakai `Close()` jika ingin error saat flush dilempar sebagai exception. |
| `RasterBand`, `Layer`, `FeatureDefinition`, `FieldDefinition` dari layer | dataset | Opsional (no-op) |
| `Feature` dari layer (`GetNextFeature`, `GetFeature`, enumerasi) | Anda | **Ya** (atau pakai `layer.ForEach`, yang men-dispose otomatis) |
| `Geometry` dari `feature.Geometry` / `geometry.GetGeometry(i)` | fitur / geometri induk | Tidak. Gunakan `Clone()` bila ingin menyimpannya lebih lama. |
| `Geometry` buatan Anda atau hasil operasi (`Buffer`, `Union`, …) | Anda | **Ya** |
| `SqlResultLayer` dari `ExecuteSql` | Anda | **Ya**. Ini melepaskan result set. |
| `SpatialReference`, `CoordinateTransformation`, `ColorTable`, `MdGroup`, `MdArray` | Anda | **Ya** |

Objek anak menjaga pemiliknya tetap hidup, jadi band tidak akan pernah melebihi umur dataset yang sudah dikumpulkan GC. Jika pemilik di-dispose secara eksplisit, memakai objek anak akan melempar `ObjectDisposedException`, bukan membuat proses crash:

```csharp
var ds = Gdal.OpenRaster("dem.tif");
var band = ds.GetBand(1);
ds.Dispose();
// band.Width  → ObjectDisposedException (bukan access violation)
```

`Feature.SetGeometryDirectly(geometry)` memindahkan kepemilikan geometri ke fitur tanpa menyalin. Setelah itu, objek geometri tidak bisa dipakai lagi.

## Threading

Dataset GDAL **tidak** thread-safe. Gunakan satu `Dataset` (dibuka terpisah) per thread. Fungsi global seperti `Gdal.Open`, pembuatan `SpatialReference` dan `CoordinateTransformation`, serta `GdalUtils` pada dataset yang berbeda boleh berjalan paralel.

```csharp
Parallel.ForEach(files, file =>
{
    using var ds = Gdal.OpenRaster(file);          // satu dataset per thread
    var s = ds.GetBand(1).ComputeStatistics();
});
```

## Error

Pemanggilan yang gagal melempar `GdalException` berisi pesan asli GDAL dan nomor error (`ErrorNumber`). Warning dan pesan debug tidak pernah melempar exception. Untuk melihatnya, subscribe ke `Gdal.Log`:

```csharp
Gdal.Log += m => Console.WriteLine($"[{m.Level}] {m.Message}");
try
{
    using var ds = Gdal.Open("missing.tif");
}
catch (GdalException e)
{
    Console.WriteLine(e.Message);   // "Unable to open 'missing.tif': ..."
}
```

`Gdal.Log` bisa dipicu dari thread mana pun.

## Opsi konfigurasi

```csharp
Gdal.SetConfigOption("GDAL_NUM_THREADS", "ALL_CPUS");
Gdal.SetConfigOption("GDAL_CACHEMAX", "512");           // MB
Gdal.CacheMax = 512L * 1024 * 1024;                      // sama, dalam byte
string? v = Gdal.GetConfigOption("GDAL_NUM_THREADS");

// Opsi per thread dan per path
Gdal.SetThreadLocalConfigOption("GTIFF_DIRECT_IO", "YES");
Gdal.SetPathSpecificOption("/vsis3/my-bucket", "AWS_REGION", "ap-southeast-3");

// Opsi sementara, dikembalikan saat dispose
using (Gdal.ConfigScope(new Dictionary<string, string?> { ["GDAL_HTTP_TIMEOUT"] = "10" }))
{
    // ...
}
```

## Progres dan pembatalan

Operasi yang lama menerima `IProgress<GdalProgress>` dan `CancellationToken`. Saat token dibatalkan, GDAL berhenti pada callback progres berikutnya dan method melempar `OperationCanceledException`.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var progress = new Progress<GdalProgress>(p => Console.Write($"\r{p.Percent:0}% {p.Message}"));
using var warped = GdalUtils.Warp(dem, "out.tif", new WarpOptions { TargetSrs = "EPSG:4326" }, progress, cts.Token);
```

`Progress<T>` mengirim laporan ke synchronization context yang tertangkap (atau thread pool). Jika butuh callback sinkron, buat implementasi `IProgress<T>` sendiri.

## Urutan sumbu

GDAL 3 mengikuti urutan sumbu dari otoritas CRS, misalnya lintang lalu bujur untuk EPSG:4326. GDAL.Net secara default memakai **urutan GIS tradisional**: x = bujur/easting dan y = lintang/northing, untuk setiap `SpatialReference` yang dibuatnya dan untuk seluruh proses. Untuk kembali ke urutan otoritas pada objek tertentu:

```csharp
using var srs = SpatialReference.FromEpsg(4326);
srs.AxisMappingStrategy = AxisMappingStrategy.AuthorityCompliant;
```

**Berikutnya:** [Raster](raster.md) · [Vektor](vector.md) · [Performa](performance.md)
