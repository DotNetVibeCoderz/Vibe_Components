# Penginderaan jauh

Halaman ini membahas kalkulator raster (`BandMath`), indeks spektral, kernel Rust/SIMD, dan pembuatan raster yang sejajar grid (namespace `GDAL.Net.Processing`).

```csharp
using GDAL.Net;
using GDAL.Net.Processing;
```

## Band math

`BandMath` mengompilasi ekspresi sekali saja, lalu mengevaluasinya secara tervektorisasi (`TensorPrimitives`) per blok baris. Pemakaian memori tetap konstan berapa pun ukuran rasternya.

```csharp
using var scene = Gdal.OpenRaster("scene_bandung.tif");      // B2, B3, B4, B8
var aliases = new Dictionary<string, int> { ["BLUE"] = 1, ["GREEN"] = 2, ["RED"] = 3, ["NIR"] = 4 };

// One-liner → single-band Float32 MEM dataset with the source georeferencing
using var evi = BandMath.Calculate(scene, "2.5 * (NIR - RED) / (NIR + 6 * RED - 7.5 * BLUE + 10000)", aliases);
using var mask = BandMath.Calculate(scene, "NIR > 2500 && RED < 1000 ? 1 : 0", aliases);
using var b4 = BandMath.Calculate(scene, "B4 * 0.0001");     // bands are always available as B1..Bn

// Reusable compiled expression over any bands of the same size
var ndvi = BandMath.Compile("(NIR - RED) / (NIR + RED)");
Console.WriteLine(string.Join(", ", ndvi.Variables));       // NIR, RED
using var output = RasterFactory.CreateLike(scene, 1, DataType.Float32);
ndvi.Evaluate(new Dictionary<string, RasterBand> { ["NIR"] = scene.GetBand(4), ["RED"] = scene.GetBand(3) },
    output.GetBand(1), outputNoData: -9999);

// Or over plain arrays
var result = new float[3];
BandMath.Compile("max(A, B) * 2").Evaluate(new Dictionary<string, float[]> { ["A"] = [1, 2, 3], ["B"] = [3, 2, 1] }, result);
```

### Tata bahasa ekspresi

| Jenis | Sintaks |
|---|---|
| Variabel | Nama band (tidak peka huruf besar/kecil): `B1`…`Bn`, alias seperti `NIR`, `RED` |
| Angka | `12`, `0.5`, `1e-4`, konstanta `pi`, `e` |
| Aritmetika | `+ - * /`, pangkat `^` (asosiatif kanan), unary `-` dan `+` |
| Perbandingan | `< <= > >= == !=` → 1 atau 0 |
| Logika | `&&`, `\|\|`, `!` |
| Kondisi | `kondisi ? a : b`, atau `where(kondisi, a, b)` |
| Fungsi | `abs sqrt exp log log10 sin cos tan floor ceil round` (1 argumen), `min max pow` (2), `clamp(x, lo, hi)` (3) |

Piksel menjadi `outputNoData` (default NaN) jika salah satu input bernilai NaN atau sama dengan nilai nodata band-nya. Hasil tak hingga, misalnya pembagian dengan nol, juga menjadi nodata. Sub-ekspresi konstan dihitung saat kompilasi. Kesalahan sintaks melempar `FormatException` lengkap dengan posisinya.

## Indeks spektral

```csharp
using var ndviDs = SpectralIndices.Ndvi(nir: scene.GetBand(4), red: scene.GetBand(3));
using var ndwi = SpectralIndices.Ndwi(green: scene.GetBand(2), nir: scene.GetBand(4));
using var savi = SpectralIndices.Savi(scene.GetBand(4), scene.GetBand(3), l: 0.5f);
using var eviDs = SpectralIndices.Evi(scene.GetBand(4), scene.GetBand(3), scene.GetBand(1));   // reflectance in [0,1]
```

| Method | Rumus | Kegunaan |
|---|---|---|
| `Ndvi(nir, red)` | (NIR − Red) / (NIR + Red) | Kerapatan vegetasi |
| `Ndwi(green, nir)` | (Green − NIR) / (Green + NIR) | Badan air terbuka |
| `Mndwi(green, swir1)` | (Green − SWIR1) / (Green + SWIR1) | Air di kawasan terbangun |
| `Ndbi(swir1, nir)` | (SWIR1 − NIR) / (SWIR1 + NIR) | Kawasan terbangun |
| `Nbr(nir, swir2)` | (NIR − SWIR2) / (NIR + SWIR2) | Tingkat keparahan kebakaran |
| `Savi(nir, red, l)` | (1 + L)(NIR − Red) / (NIR + Red + L) | Vegetasi jarang |
| `Evi(nir, red, blue)` | 2.5 (NIR − Red) / (NIR + 6 Red − 7.5 Blue + 1) | Vegetasi rapat |
| `NormalizedDifference(a, b)` | (a − b) / (a + b) | Indeks ND lainnya |

Keluarannya bertipe Float32 dengan NaN sebagai nodata dan mempertahankan georeferensi input. Nilai nodata pada input otomatis dimask.

## Kernel

`Kernels` berisi loop-loop panas yang dipakai indeks dan renderer. Jika library Rust `gdalnet_kernels` tersedia (rayon untuk paralelisme data, LTO, disertakan di paket NuGet untuk win/linux/osx x64 dan arm64), kernel memakainya. Jika tidak, kernel memakai SIMD managed dengan hasil identik.

```csharp
Console.WriteLine(Kernels.Backend);              // "Rust (rayon, 16 threads)" or "Managed SIMD (Vector256)"
float[] pixels = scene.GetBand(4).ReadAll<float>();
ArrayStatistics s = Kernels.Statistics(pixels, noData: 0);
ulong[] hist = Kernels.Histogram(pixels, s.Minimum, s.Maximum, buckets: 128, noData: 0);
var nd = new float[pixels.Length];
Kernels.NormalizedDifference(pixels, scene.GetBand(3).ReadAll<float>(), nd);

Kernels.ForceManaged = true;                     // benchmark / diagnose the managed path
```

Tersedia juga:
- `Colorize`: float → RGBA lewat LUT 256 entri.
- `CompositeRgb`: tiga band → RGBA dengan stretch per kanal.
- `BlendShade`: mengalikan RGBA dengan hillshade.

## Raster sejajar grid

```csharp
using var like = RasterFactory.CreateLike(scene, bandCount: 1, DataType.Float32);              // MEM, same grid and CRS
using var onDisk = RasterFactory.CreateLike(scene, 1, DataType.Byte, driver: "GTiff", path: "mask.tif");
using var fromArray = RasterFactory.FromArray<float>(new float[100 * 100], 100, 100,
    GeoTransform.FromOrigin(780_000, 9_240_000, 30, 30), noData: -9999);
```

**Berikutnya:** [Rendering](rendering.md) · [Performa](performance.md)
