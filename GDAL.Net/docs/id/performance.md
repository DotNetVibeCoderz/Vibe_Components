# Performa

Halaman ini menjelaskan pilihan desain yang membuat GDAL.Net cepat dan hemat memori, serta cara memaksimalkannya.

## Desain

| Teknik | Lokasi | Manfaat |
|---|---|---|
| Interop yang di-generate (`[LibraryImport]`) | `Native/GdalNative.*.cs` | Tanpa stub marshalling runtime, signature blittable, ramah trimming/Native AOT (`IsAotCompatible`) |
| Satu tipe `SafeHandle` dengan rantai induk | `GdalSafeHandle` | Pemilik tidak bisa di-finalize selama anaknya dipakai. Marshaller menjaga rantai tetap hidup selama setiap panggilan native. Dispose eksplisit mengubah use-after-close menjadi `ObjectDisposedException`. |
| Handle pinjaman tanpa finalisasi | band, layer, geometri fitur | Tidak membebani antrian finalizer saat mengiterasi jutaan fitur |
| I/O piksel zero-copy | `RasterBand.Read<T>(…, Span<T>)` | GDAL menulis langsung ke buffer Anda dan sekaligus mengonversi tipe data |
| Resampling di dalam RasterIO | `ReadResampled`, `RasterRenderer` | GDAL memilih overview dan hanya membaca yang dibutuhkan keluaran |
| Streaming per blok baris dengan pool | `ProcessRows`, `BandMath`, `SpectralIndices` | Memori konstan berapa pun ukuran raster (`ArrayPool<T>`) |
| Matematika tervektorisasi | `BandMath` (TensorPrimitives), kernel managed | SIMD di semua platform |
| Kernel Rust | `native/gdalnet-kernels` | Paralelisme data rayon, fat LTO, `codegen-units=1`, `panic=abort`. Dipakai untuk statistik, histogram, indeks ND, colorize, komposit, dan shading. |
| API koordinat massal | `TransformInPlace`, `Geometry.CopyPoints` | Satu panggilan native untuk ribuan titik |
| Callback unmanaged statis | handler progres dan error | Tanpa alokasi delegate dan tanpa thunk yang di-root GC |
| Bootstrap malas yang di-cache | `GdalRuntime` | Registrasi driver dan pencarian path data hanya terjadi sekali |

## Tips

- **Baca per blok.** `band.BlockSize` adalah unit I/O alami. `ProcessRows` sudah menyelaraskan chunk dengannya.
- **Bangun overview** (`BuildOverviews` atau COG `OVERVIEWS=AUTO`) untuk data yang akan ditampilkan atau diperkecil.
- **Perbesar block cache** untuk akses acak yang berat: `Gdal.CacheMax = 1L << 30`.
- **Gunakan banyak thread** untuk kompresi dan warping: `Gdal.SetConfigOption("GDAL_NUM_THREADS", "ALL_CPUS")`. `WarpOptions.Multithread` sudah aktif secara default.
- **Kelompokkan penulisan dalam transaksi** untuk GeoPackage, SQLite, dan PostGIS.
- **Lewati field yang tidak dipakai** dengan `layer.SetIgnoredFields([...])`, dan pakai `includeGeometry: false` pada `ReadRecords` jika hanya butuh atribut.
- **Pakai MEM/VRT untuk hasil antara** (`Format = "MEM"`, `-of VRT`), bukan file sementara.
- **Paralelkan per dataset**: satu `Dataset` per thread.
- **Untuk data jarak jauh**, pakai COG/FlatGeobuf dengan `GDAL_DISABLE_READDIR_ON_OPEN=EMPTY_DIR` dan `VSI_CACHE=TRUE`.

## Benchmark

Bandingkan kernel Rust dan managed pada data Anda sendiri:

```csharp
using System.Diagnostics;
using GDAL.Net.Processing;

float[] data = band.ReadAll<float>();
var sw = Stopwatch.StartNew();
var native = Kernels.Statistics(data, noData: -9999);
var tNative = sw.Elapsed;

Kernels.ForceManaged = true;
sw.Restart();
var managed = Kernels.Statistics(data, noData: -9999);
var tManaged = sw.Elapsed;
Kernels.ForceManaged = false;

Console.WriteLine($"{Kernels.Backend}: {tNative.TotalMilliseconds:0.0} ms vs managed {tManaged.TotalMilliseconds:0.0} ms");
```

Untuk angka yang akurat, gunakan BenchmarkDotNet dalam mode Release. Lakukan pemanasan terlebih dulu, karena panggilan pertama ikut menanggung biaya pemuatan library native dan registrasi driver.

**Berikutnya:** [Matriks fitur](feature-matrix.md) · [Pemecahan masalah](troubleshooting.md)
