# Raster

This page covers opening, creating and copying rasters, reading and writing pixels, georeferencing, statistics, overviews, palettes, attribute tables, metadata, GCPs and Cloud Optimized GeoTIFF.

## Open, create, copy

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

Explore drivers with `Gdal.Drivers`, `Gdal.GetDriver(name)`, `Gdal.TryGetDriver(name)` and `Gdal.IdentifyDriver(path)`. Each `Driver` exposes `ShortName`, `LongName`, `SupportsRaster`, `SupportsVector`, `SupportsCreate`, `SupportsCreateCopy`, `Extensions` and `CreationOptionList`.

## Dataset and band properties

```csharp
Console.WriteLine($"{dem.RasterWidth} x {dem.RasterHeight}, {dem.RasterCount} band(s), driver {dem.Driver.ShortName}");
var band = dem.GetBand(1);                // 1-based, like GDAL
Console.WriteLine($"{band.DataType}, block {band.BlockSize}, nodata {band.NoDataValue}");
Console.WriteLine($"{band.ColorInterpretation}, unit '{band.UnitType}', scale {band.Scale}, offset {band.Offset}");
foreach (var b in dem.Bands) Console.WriteLine(b);
```

## Reading and writing pixels

Every read/write converts to the requested CLR type (`byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `Half`) and goes straight into your span, with no intermediate copy.

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

## Georeferencing

```csharp
GeoTransform gt = dem.GeoTransform;                 // origin + pixel size (+ rotation)
bool hasGt = dem.HasGeoTransform;
Envelope extent = dem.Extent;                       // MinX, MinY, MaxX, MaxY
var (x, y) = gt.PixelToWorld(10, 5);
var (px, line) = gt.WorldToPixel(x, y);

mem.GeoTransform = GeoTransform.FromOrigin(upperLeftX: 780_000, upperLeftY: 9_240_000, pixelWidth: 30, pixelHeight: 30);
mem.SpatialReference = dem.SpatialReference;        // or Projection = "WKT…"
```

`GeoTransform.FromExtent(envelope, width, height)` builds a north-up transform covering an envelope. `Invert()` returns the world-to-pixel transform.

## Nodata, statistics and histograms

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

## Overviews (pyramids)

```csharp
using var ds = Gdal.OpenRaster("big.tif", update: true);
ds.BuildOverviews("AVERAGE", [2, 4, 8, 16]);        // or levels: null for automatic
var ov = ds.GetBand(1).GetOverview(0);
var best = ds.GetBand(1).GetBestOverview(targetWidth: 800, targetHeight: 600);
```

## Color tables and raster attribute tables

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

Bands have the same metadata API (`band.GetMetadata()`, `band.Description`, …).

## Ground control points

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

Rectify GCP-referenced images with `GdalUtils.Warp` (see [Analysis](analysis.md#georeferencing-and-orthorectification)).

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

## Common raster drivers

| Driver | Formats | Notes |
|---|---|---|
| `GTiff`, `COG` | GeoTIFF, Cloud Optimized GeoTIFF | Read/write, compression DEFLATE/LZW/ZSTD/JPEG/WEBP (3–4 bands) |
| `PNG`, `JPEG`, `WEBP`, `BMP`, `GIF` | Images | CreateCopy |
| `JP2OpenJPEG` | JPEG 2000 | Read/write |
| `netCDF`, `HDF5`, `HDF4`, `GRIB`, `Zarr` | Scientific / multidimensional | Also via the multidimensional API |
| `VRT` | Virtual rasters | `GdalUtils.BuildVrt`, warped VRTs |
| `MEM` | In-memory | Fast intermediate results |
| `AAIGrid`, `EHdr` (BIL), `USGSDEM`, `SRTMHGT`, `SAFE` (Sentinel) | Grids, DEMs, satellite products | Read |

Run `Gdal.Drivers` (or the gallery's *Formats & drivers* sample) for the exact list of your build. ECW and MrSID need proprietary SDKs and are not in the bundled build.

**Next:** [Vector](vector.md) · [Utilities](utilities.md) · [Rendering](rendering.md)
