# Remote sensing

This page covers the raster calculator (`BandMath`), spectral indices, the Rust/SIMD kernels and grid-aligned raster creation (namespace `GDAL.Net.Processing`).

```csharp
using GDAL.Net;
using GDAL.Net.Processing;
```

## Band math

`BandMath` compiles an expression once and evaluates it vectorized (`TensorPrimitives`) over row blocks, so memory stays constant for any raster size.

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

### Expression grammar

| Kind | Syntax |
|---|---|
| Variables | Band names (case-insensitive): `B1`…`Bn`, aliases such as `NIR`, `RED` |
| Numbers | `12`, `0.5`, `1e-4`, constants `pi`, `e` |
| Arithmetic | `+ - * /`, power `^` (right associative), unary `-` and `+` |
| Comparison | `< <= > >= == !=` → 1 or 0 |
| Logic | `&&`, `\|\|`, `!` |
| Conditional | `condition ? a : b`, or `where(condition, a, b)` |
| Functions | `abs sqrt exp log log10 sin cos tan floor ceil round` (1 argument), `min max pow` (2), `clamp(x, lo, hi)` (3) |

Pixels where any input is NaN or equals its band's nodata value become `outputNoData` (NaN by default). Infinite results, such as division by zero, become nodata too. Constant sub-expressions are folded at compile time. Syntax errors throw `FormatException` with the position.

## Spectral indices

```csharp
using var ndviDs = SpectralIndices.Ndvi(nir: scene.GetBand(4), red: scene.GetBand(3));
using var ndwi = SpectralIndices.Ndwi(green: scene.GetBand(2), nir: scene.GetBand(4));
using var savi = SpectralIndices.Savi(scene.GetBand(4), scene.GetBand(3), l: 0.5f);
using var eviDs = SpectralIndices.Evi(scene.GetBand(4), scene.GetBand(3), scene.GetBand(1));   // reflectance in [0,1]
```

| Method | Formula | Use |
|---|---|---|
| `Ndvi(nir, red)` | (NIR − Red) / (NIR + Red) | Vegetation vigor |
| `Ndwi(green, nir)` | (Green − NIR) / (Green + NIR) | Open water |
| `Mndwi(green, swir1)` | (Green − SWIR1) / (Green + SWIR1) | Water in built-up areas |
| `Ndbi(swir1, nir)` | (SWIR1 − NIR) / (SWIR1 + NIR) | Built-up areas |
| `Nbr(nir, swir2)` | (NIR − SWIR2) / (NIR + SWIR2) | Burn severity |
| `Savi(nir, red, l)` | (1 + L)(NIR − Red) / (NIR + Red + L) | Sparse vegetation |
| `Evi(nir, red, blue)` | 2.5 (NIR − Red) / (NIR + 6 Red − 7.5 Blue + 1) | Dense vegetation |
| `NormalizedDifference(a, b)` | (a − b) / (a + b) | Any other ND index |

Outputs are Float32 with NaN as nodata and keep the input georeferencing. Inputs with nodata values are masked automatically.

## Kernels

`Kernels` holds the hot loops used by the indices and the renderer. It runs the Rust library `gdalnet_kernels` (rayon data parallelism, LTO, shipped in the NuGet package for win/linux/osx x64 and arm64) when present, and otherwise falls back to managed SIMD with identical results.

```csharp
Console.WriteLine(Kernels.Backend);              // "Rust (rayon, 16 threads)" or "Managed SIMD (Vector256)"
float[] pixels = scene.GetBand(4).ReadAll<float>();
ArrayStatistics s = Kernels.Statistics(pixels, noData: 0);
ulong[] hist = Kernels.Histogram(pixels, s.Minimum, s.Maximum, buckets: 128, noData: 0);
var nd = new float[pixels.Length];
Kernels.NormalizedDifference(pixels, scene.GetBand(3).ReadAll<float>(), nd);

Kernels.ForceManaged = true;                     // benchmark / diagnose the managed path
```

Also available:
- `Colorize`: float → RGBA through a 256-entry LUT.
- `CompositeRgb`: three bands → RGBA with a per-channel stretch.
- `BlendShade`: multiplies RGBA by a hillshade.

## Grid-aligned rasters

```csharp
using var like = RasterFactory.CreateLike(scene, bandCount: 1, DataType.Float32);              // MEM, same grid and CRS
using var onDisk = RasterFactory.CreateLike(scene, 1, DataType.Byte, driver: "GTiff", path: "mask.tif");
using var fromArray = RasterFactory.FromArray<float>(new float[100 * 100], 100, 100,
    GeoTransform.FromOrigin(780_000, 9_240_000, 30, 30), noData: -9999);
```

**Next:** [Rendering](rendering.md) · [Performance](performance.md)
