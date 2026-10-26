# Analysis

This page covers terrain products, contours, raster-to-vector conversion, viewshed, nodata filling, sieve filtering, georeferencing and orthorectification, clipping and mosaics.

```csharp
using GDAL.Net;
using GDAL.Net.Srs;
using GDAL.Net.Vector;
```

## Terrain products (gdaldem)

```csharp
using var dem = Gdal.OpenRaster("dem_bandung.tif");
using var hillshade = GdalUtils.DemProcessing(dem, "", DemProcessingMode.Hillshade,
    new DemOptions { Format = "MEM", Azimuth = 315, Altitude = 45, ZFactor = 1.5 });
using var slope = GdalUtils.DemProcessing(dem, "", DemProcessingMode.Slope,
    new DemOptions { Format = "MEM", SlopePercent = false, Algorithm = "Horn" });
using var aspect = GdalUtils.DemProcessing(dem, "", DemProcessingMode.Aspect, new DemOptions { Format = "MEM" });
using var tri = GdalUtils.DemProcessing(dem, "", DemProcessingMode.TerrainRuggednessIndex, new DemOptions { Format = "MEM" });
```

| `DemOptions` | Meaning |
|---|---|
| `ZFactor` | Vertical exaggeration |
| `Scale` | Vertical/horizontal unit ratio (111120 for DEMs in degrees with metre elevations) |
| `Azimuth`, `Altitude` | Sun position (hillshade). Leave `Azimuth` unset for multidirectional and `Altitude` unset for Igor's method. |
| `HillshadeVariant` | `null`, `"combined"`, `"multidirectional"`, `"igor"` |
| `ComputeEdges` | Compute values on the image border (default `true`) |
| `SlopePercent` | Slope in percent instead of degrees |
| `Alpha`, `ColorMatching` | Color relief alpha channel and `"exact"`/`"nearest"` matching |

## Contours

```csharp
// Quick: in-memory dataset with a "contours" layer and an "elev" field
using var contours = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval: 50);
// Elevation bands as polygons ("elev_min"/"elev_max")
using var bands = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval: 250, polygons: true);

// Full control: into your own layer (any vector format)
using var gpkg = Gdal.GetDriver("GPKG").CreateVector("contours.gpkg");
using var srs = dem.SpatialReference;
var layer = gpkg.CreateLayer("contours", srs, GeometryType.LineString);
layer.CreateField("elev", FieldType.Real);
RasterAlgorithms.GenerateContours(dem.GetBand(1), layer, new ContourOptions
{
    FixedLevels = [700, 1000, 1500, 2000],
    ElevationField = "elev",
});
```

## Polygonize (raster → vector)

```csharp
// Quick: connected regions of equal value → polygons with a "value" field (nodata skipped)
using var polys = RasterAlgorithms.Polygonize(classes.GetBand(1), eightConnected: true);

// Into an existing layer, using the field at index 0 for the pixel value
RasterAlgorithms.Polygonize(classes.GetBand(1), landCoverLayer, valueFieldIndex: 0, eightConnected: true);
```

The reverse, vector → raster, is `GdalUtils.Rasterize` (see [Utilities](utilities.md#gdal_rasterize-and-gdal_grid)).

## Sieve and fill nodata

```csharp
var band = classes.GetBand(1);
RasterAlgorithms.Sieve(band, band, threshold: 12, eightConnected: true);   // remove regions < 12 px, in place
RasterAlgorithms.FillNoData(demBand, maxSearchDistance: 20, smoothingIterations: 1);
```

## Viewshed

```csharp
using var visible = RasterAlgorithms.Viewshed(dem.GetBand(1),
    observerX: 787_500, observerY: 9_250_400,      // in the DEM's CRS
    observerHeight: 2, targetHeight: 0, maxDistance: 20_000);
byte[] mask = visible.GetBand(1).ReadAll<byte>(); // 255 = visible, 0 = hidden
```

Optional arguments set the output driver and path, the visible/invisible/out-of-range values, the earth curvature coefficient and the `ViewshedMode`.

## Georeferencing and orthorectification

```csharp
using var scan = Gdal.OpenRaster("scan.png", update: true);
using var utm = SpatialReference.FromEpsg(32748);
scan.SetGcps(
[
    new GroundControlPoint(0, 0, 770_000, 9_256_000),
    new GroundControlPoint(600, 0, 806_000, 9_256_000),
    new GroundControlPoint(0, 450, 770_000, 9_229_000),
    new GroundControlPoint(600, 450, 806_000, 9_229_000),
], utm);

using var poly = GdalUtils.Warp(scan, "rect_poly.tif", new WarpOptions { PolynomialOrder = 1, TargetSrs = "EPSG:32748" });
using var tps = GdalUtils.Warp(scan, "rect_tps.tif", new WarpOptions { ThinPlateSpline = true, TargetSrs = "EPSG:32748" });

// RPC orthorectification of satellite imagery with a DEM
using var ortho = GdalUtils.Warp(level1, "ortho.tif", new WarpOptions { Rpc = true, RpcDem = "dem.tif", TargetSrs = "EPSG:32748" });
```

A second-order polynomial needs at least 6 well-spread GCPs. Thin plate spline honours every GCP exactly.

## Clipping

```csharp
// By a polygon file (optionally filtered with -cwhere)
using var urban = GdalUtils.Warp(dem, "", new WarpOptions
{
    Format = "MEM", CutlineFile = "land_cover.gpkg", CropToCutline = true, DestinationNoData = -9999,
    ExtraArgs = ["-cwhere", "class = 2"],
});

// By a WKT geometry
using var center = Geometry.Point(788_600, 9_235_000);
using var circle = center.Buffer(8_000);
using var clipped = GdalUtils.Warp(dem, "", new WarpOptions
{
    Format = "MEM", CutlineWkt = circle.ToWkt(), CutlineSrs = "EPSG:32748", CropToCutline = true, DestinationNoData = -9999,
});

// By a rectangle, without resampling (gdal_translate -projwin)
using var window = GdalUtils.Translate(dem, "", new TranslateOptions
{
    Format = "MEM", ProjectionWindow = new Envelope(780_000, 9_230_000, 790_000, 9_240_000),
});
```

## Mosaics

```csharp
// Virtual: no pixels are copied
using var vrt = GdalUtils.BuildVrt("mosaic.vrt", Directory.GetFiles("dem_tiles", "*.tif"), ["-srcnodata", "-9999"]);
// Materialized, reprojected on the fly
using var merged = GdalUtils.Warp([tileA, tileB, tileC], "merged.tif", new WarpOptions { TargetSrs = "EPSG:4326" });
// Into a prepared canvas
GdalUtils.WarpInto([tileA, tileB], canvas, ["-r", "bilinear"]);
```

**Next:** [Remote sensing](remote-sensing.md) · [Rendering](rendering.md)
