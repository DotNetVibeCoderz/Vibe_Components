# Memulai

Halaman ini membahas instalasi GDAL.Net, program raster dan vektor pertama, serta cara library native ditemukan saat runtime.

## Instalasi

```bash
dotnet add package Gravicode.GDAL.Net
```

Kebutuhan: .NET 10. Paket ini otomatis membawa build native GDAL 3.13.3 (dengan PROJ 9.8.1, GEOS, SQLite/SpatiaLite, curl, netCDF, HDF4/5, OpenJPEG, …) melalui paket `MaxRev.Gdal.WindowsRuntime.Minimal`, `MaxRev.Gdal.LinuxRuntime.Minimal`, dan `MaxRev.Gdal.MacosRuntime.Minimal`. Anda tidak perlu menginstal GDAL terpisah.

## Program pertama

```csharp
using GDAL.Net;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

Console.WriteLine($"GDAL {Gdal.Version}, PROJ {Gdal.ProjVersion}");

// --- Raster: buat GeoTIFF kecil, tulis piksel, lalu baca statistiknya
using (var ds = Gdal.GetDriver("GTiff").Create("hello.tif", 100, 50, 1, DataType.Float32,
           new Dictionary<string, string> { ["COMPRESS"] = "DEFLATE" }))
{
    ds.GeoTransform = GeoTransform.FromOrigin(106.8, -6.1, 0.01, 0.01);
    using var wgs84 = SpatialReference.Wgs84;
    ds.SpatialReference = wgs84;
    var pixels = Enumerable.Range(0, 100 * 50).Select(i => (float)(i % 100)).ToArray();
    ds.GetBand(1).WriteAll<float>(pixels);
}

using (var ds = Gdal.OpenRaster("hello.tif"))
{
    var stats = ds.GetBand(1).ComputeStatistics();
    Console.WriteLine($"{ds.RasterWidth}x{ds.RasterHeight}, mean {stats.Mean:0.0}, extent {ds.Extent}");
}

// --- Vektor: buat GeoPackage dengan satu layer titik
using (var gpkg = Gdal.GetDriver("GPKG").CreateVector("cities.gpkg"))
{
    using var wgs84 = SpatialReference.Wgs84;
    var layer = gpkg.CreateLayer("cities", wgs84, GeometryType.Point);
    layer.CreateField("name", FieldType.String, 50);
    layer.CreateField("population", FieldType.Integer64);

    using var bandung = Geometry.Point(107.6191, -6.9175);
    layer.AddFeature(bandung, new Dictionary<string, object?> { ["name"] = "Bandung", ["population"] = 2_444_160L });
}

using (var gpkg = Gdal.OpenVector("cities.gpkg"))
{
    gpkg.GetLayer("cities").ForEach(f => Console.WriteLine($"{f.GetString("name")} {f.Geometry}"));
}
```

## Platform

| Platform | GDAL native | Kernel Rust (`gdalnet_kernels`) |
|---|---|---|
| Windows x64 | ✔ | ✔ sudah termasuk di paket |
| Linux x64 / arm64 | ✔ | ✔ di dalam paket (di-build oleh CI) |
| macOS x64 / arm64 | ✔ | ✔ di dalam paket (di-build oleh CI) |

Kernel Rust mempercepat statistik, histogram, indeks normalized difference, dan pewarnaan. Jika kernel ini tidak tersedia, fungsi yang sama berjalan sebagai kode managed tervektorisasi (`System.Numerics.Tensors`) dengan hasil identik. `Kernels.Backend` menunjukkan backend yang sedang aktif.

## Cara library native ditemukan

Saat assembly dimuat, GDAL.Net mendaftarkan resolver `DllImport` yang mencari secara berurutan:

1. Path di environment variable `GDALNET_LIBRARY_PATH` (file `gdal.dll` / `libgdal.so.*` / `libgdal.*.dylib` tertentu).
2. `runtimes/<rid>/native/` dan folder aplikasi. Ini berlaku untuk build portable, khusus RID, self-contained, maupun single-file.
3. Loader bawaan OS (`PATH`, `LD_LIBRARY_PATH`, atau instalasi GDAL sistem).

Pada pemakaian pertama, GDAL.Net juga:

- mengatur `GDAL_DATA` ke folder `gdal-data` yang dibawa paket (atau memakai env var `GDAL_DATA` milik Anda);
- mengarahkan PROJ ke `proj.db` (dibawa paket runtime, atau dari `PROJ_DATA`/`PROJ_LIB`);
- mengatur `OSR_DEFAULT_AXIS_MAPPING_STRATEGY=TRADITIONAL_GIS_ORDER` (bujur/easting lebih dulu);
- mendaftarkan semua driver dan memasang error handler senyap yang meneruskan pesan ke `Gdal.Log`.

Hasilnya bisa diperiksa:

```csharp
Console.WriteLine(Gdal.NativeLibraryPath);  // library gdal yang dimuat
Console.WriteLine(Gdal.DataPath);           // GDAL_DATA
Console.WriteLine(Gdal.ProjDataPath);       // folder berisi proj.db
Gdal.Initialize();                          // opsional: paksa inisialisasi lebih awal
```

**Berikutnya:** [Konsep inti](core-concepts.md) · [Raster](raster.md) · [Vektor](vector.md)
