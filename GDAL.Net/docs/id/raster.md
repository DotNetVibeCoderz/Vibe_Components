# Raster

Halaman ini membahas membuka, membuat, dan menyalin raster, membaca/menulis piksel, georeferensi, statistik, overview, palet, tabel atribut, metadata, GCP, serta Cloud Optimized GeoTIFF.

## Membuka, membuat, menyalin

```csharp
using GDAL.Net;

using var dem = Gdal.OpenRaster("dem.tif");                    // read-only
using var edit = Gdal.OpenRaster("dem.tif", update: true);      // update mode
using var any = Gdal.Open("file.nc", OpenFlags.Raster, allowedDrivers: ["netCDF"],
    openOptions: new Dictionary<string, string> { ["HONOUR_VALID_RANGE"] = "NO" });
using var maybe = Gdal.TryOpen("maybe.tif");                    // null instead of throwing

// Create (driver must support DCAP_CREATE)
using var tif = Gdal.GetDriver("GTiff").Create("out.tif", 1024, 768, 3, DataType.Byte,
    new Dictionary<string, string> { ["COMPRESS"] = "ZSTD", ["TILED"] = "YES" });

// In-memory scratch raster
using var mem = Gdal.CreateMemoryRaster(512, 512, bandCount: 1, DataType.Float32);

// Copy into another format (DCAP_CREATECOPY drivers such as PNG, JPEG, COG)
using var png = Gdal.GetDriver("PNG").CreateCopy("preview.png", tif);
```

Jelajahi driver dengan `Gdal.Drivers`, `Gdal.GetDriver(name)`, `Gdal.TryGetDriver(name)`, dan `Gdal.IdentifyDriver(path)`. Setiap `Driver` menyediakan `ShortName`, `LongName`, `SupportsRaster`, `SupportsVector`, `SupportsCreate`, `SupportsCreateCopy`, `Extensions`, dan `CreationOptionList`.

## Properti dataset dan band

```csharp
Console.WriteLine($"{dem.RasterWidth} x {dem.RasterHeight}, {dem.RasterCount} band(s), driver {dem.Driver.ShortName}");
var band = dem.GetBand(1);                // 1-based, like GDAL
Console.WriteLine($"{band.DataType}, block {band.BlockSize}, nodata {band.NoDataValue}");
Console.WriteLine($"{band.ColorInterpretation}, unit '{band.UnitType}', scale {band.Scale}, offset {band.Offset}");
foreach (var b in dem.Bands) Console.WriteLine(b);
```

## Membaca dan menulis piksel

Setiap baca/tulis langsung dikonversi ke tipe CLR yang diminta (`byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `Half`) dan ditulis langsung ke span Anda tanpa salinan perantara.

```csharp
// Whole band
float[] all = band.ReadAll<float>();

// A window into your own buffer
var row = new short[100];
band.Read<short>(xOff: 0, yOff: 3, xSize: 100, ySize: 1, row);

// Resampled on the fly (GDAL uses overviews automatically)
float[] preview = band.ReadResampled<float>(256, 192, ResampleAlgorithm.Average);
var buffer = new float[64 * 64];
band.Read<float>(0, 0, 1024, 1024, buffer, 64, 64, ResampleAlgorithm.Bilinear);

// Write
band.WriteAll<float>(all);
band.Write<float>(10, 10, 5, 5, new float[25]);
band.Fill(0);

// Constant memory for huge rasters: pooled row blocks
double sum = 0;
band.ProcessRows<float>((y, rows, data) =>
{
    foreach (var v in data) sum += v;
});

// Pixel-interleaved multi-band read (RGBRGB…)
var rgb = new byte[256 * 256 * 3];
dem.ReadInterleaved<byte>(0, 0, dem.RasterWidth, dem.RasterHeight, rgb, 256, 256, [1, 1, 1]);
```

## Georeferensi

```csharp
GeoTransform gt = dem.GeoTransform;                 // origin + pixel size (+ rotation)
bool hasGt = dem.HasGeoTransform;
Envelope extent = dem.Extent;                       // MinX, MinY, MaxX, MaxY
var (x, y) = gt.PixelToWorld(10, 5);
var (px, line) = gt.WorldToPixel(x, y);

mem.GeoTransform = GeoTransform.FromOrigin(upperLeftX: 780_000, upperLeftY: 9_240_000, pixelWidth: 30, pixelHeight: 30);
mem.SpatialReference = dem.SpatialReference;        // or Projection = "WKT…"
```

`GeoTransform.FromExtent(envelope, width, height)` membuat transformasi north-up yang menutupi suatu envelope. `Invert()` menghasilkan transformasi dunia → piksel.

## Nodata, statistik, dan histogram

```csharp
band.NoDataValue = -9999;                  // null removes it
BandStatistics stats = band.ComputeStatistics();             // exact
BandStatistics? cached = band.GetStatistics(approximate: true);
var (min, max) = band.ComputeMinMax();
Histogram h = band.GetHistogram(buckets: 64);                 // auto range, max included
Histogram ndvi = band.GetHistogram(60, min: -1, max: 1);
Console.WriteLine($"{h.BucketCount} buckets, total {h.Total}, peak {h.MaxCount}, first center {h.BucketCenter(0)}");
int checksum = band.Checksum();
MaskFlags flags = band.MaskFlags;          // AllValid, NoData, Alpha, PerDataset
RasterBand mask = band.MaskBand;
```

## Overview (piramida)

```csharp
using var ds = Gdal.OpenRaster("big.tif", update: true);
ds.BuildOverviews("AVERAGE", [2, 4, 8, 16]);        // or levels: null for automatic
var ov = ds.GetBand(1).GetOverview(0);
var best = ds.GetBand(1).GetBestOverview(targetWidth: 800, targetHeight: 600);
```

## Tabel warna dan raster attribute table

```csharp
using var ct = new ColorTable();
ct[0] = ColorEntry.FromRgb(0, 0, 255);
ct[1] = ColorEntry.FromRgb(0, 128, 0);
band.ColorTable = ct;                          // the getter returns a managed copy (dispose it)

using var rat = new RasterAttributeTable();
rat.AddColumn("Value", RatFieldType.Integer, RatFieldUsage.MinMax);
rat.AddColumn("Class", RatFieldType.String, RatFieldUsage.Name);
rat.Set(0, 0, 0); rat.Set(0, 1, "Water");
rat.Set(1, 0, 1); rat.Set(1, 1, "Forest");
band.AttributeTable = rat;
var (columns, rows) = band.AttributeTable!.ToTable();
```

## Metadata

```csharp
dem.SetMetadataItem("AUTHOR", "Gravicode Studios");
string? author = dem.GetMetadataItem("AUTHOR");
IReadOnlyList<string> domains = dem.MetadataDomains;     // "", IMAGE_STRUCTURE, RPC, EXIF, xml:XMP, SUBDATASETS…
var structure = dem.GetMetadata("IMAGE_STRUCTURE");      // COMPRESSION, INTERLEAVE, LAYOUT…
var xmp = dem.GetMetadataList("xml:XMP");
```

Band memiliki API metadata yang sama (`band.GetMetadata()`, `band.Description`, …).

## Ground control point (GCP)

```csharp
using var srs = SpatialReference.Wgs84;
mem.SetGcps(
[
    new(Pixel: 0, Line: 0, X: 106.0, Y: -6.0),
    new(Pixel: 100, Line: 0, X: 107.0, Y: -6.0),
    new(Pixel: 0, Line: 100, X: 106.0, Y: -7.0),
    new(Pixel: 100, Line: 100, X: 107.0, Y: -7.0),
], srs);
IReadOnlyList<GroundControlPoint> gcps = mem.Gcps;
```

Citra yang ber-GCP dapat direktifikasi dengan `GdalUtils.Warp` (lihat [Analisis](analysis.md#georeferensi-dan-ortorektifikasi)).

## Cloud Optimized GeoTIFF

```csharp
using var cog = GdalUtils.Translate(dem, "dem_cog.tif", new TranslateOptions
{
    Format = "COG",
    CreationOptions = new Dictionary<string, string>
    {
        ["COMPRESS"] = "ZSTD", ["PREDICTOR"] = "YES", ["OVERVIEWS"] = "AUTO", ["BLOCKSIZE"] = "512",
    },
});
// When reopened, GDAL's GTiff driver reports LAYOUT=COG in IMAGE_STRUCTURE.
```

## Driver raster yang umum

| Driver | Format | Catatan |
|---|---|---|
| `GTiff`, `COG` | GeoTIFF, Cloud Optimized GeoTIFF | Baca/tulis, kompresi DEFLATE/LZW/ZSTD/JPEG/WEBP (3–4 band) |
| `PNG`, `JPEG`, `WEBP`, `BMP`, `GIF` | Citra | CreateCopy |
| `JP2OpenJPEG` | JPEG 2000 | Baca/tulis |
| `netCDF`, `HDF5`, `HDF4`, `GRIB`, `Zarr` | Data ilmiah / multidimensi | Juga lewat API multidimensi |
| `VRT` | Raster virtual | `GdalUtils.BuildVrt`, VRT hasil warp |
| `MEM` | Di memori | Hasil antara yang cepat |
| `AAIGrid`, `EHdr` (BIL), `USGSDEM`, `SRTMHGT`, `SAFE` (Sentinel) | Grid, DEM, produk satelit | Baca |

Gunakan `Gdal.Drivers` (atau sampel *Format & driver* di galeri) untuk melihat daftar lengkap build Anda. ECW dan MrSID memerlukan SDK berlisensi dan tidak termasuk dalam build bawaan.

**Berikutnya:** [Vektor](vector.md) · [Utilitas](utilities.md) · [Rendering](rendering.md)
