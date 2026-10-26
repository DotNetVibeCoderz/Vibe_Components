# Rendering

This page covers turning rasters and vectors into pixels: raster to RGBA, color ramps, image encoding, vector snapshots and screen transforms, and XYZ web tiles (namespace `GDAL.Net.Rendering`). The Blazor and Avalonia components are built on these classes.

```csharp
using GDAL.Net;
using GDAL.Net.Rendering;
using GDAL.Net.Srs;
```

## Rasters → RGBA

```csharp
using var dem = Gdal.OpenRaster("dem_bandung.tif");

// Whole raster
RgbaImage img = RasterRenderer.Render(dem, new RenderOptions
{
    Ramp = ColorRamp.Terrain,
    Stretch = StretchMode.Percentile,      // 2–98 %
    Hillshade = true,
    HillshadeStrength = 0.6,
}, width: 1024, height: 768);
img.SavePng("dem.png");

// Any view in world coordinates: only that window is read, at output resolution (overviews used automatically)
var full = RasterRenderer.FullView(dem);
var view = new Envelope(full.MinX, full.MinY, full.CenterX, full.CenterY);
RgbaImage zoomed = RasterRenderer.Render(dem, new RenderOptions(), view, 800, 600);

// Multispectral composites
using var scene = Gdal.OpenRaster("scene_bandung.tif");
RgbaImage falseColor = RasterRenderer.Render(scene, new RenderOptions { Bands = [4, 3, 2] }, 800, 600);
```

| `RenderOptions` | Default | Notes |
|---|---|---|
| `Mode` | `Auto` | `SingleBand`, `Rgb`, `Palette`; Auto chooses RGB for 3+ band imagery and Palette for color tables |
| `Bands` | `null` | One band (single/palette) or three (RGB), 1-based |
| `Ramp` | `Viridis` | Any `ColorRamp` |
| `Stretch` | `Percentile` | `None` (Byte data as-is), `MinMax`, `StdDev` (`StdDevFactor`), `Percentile` (`PercentileLow/High`), `Custom` (`Min`/`Max`, or `Mins`/`Maxs` per RGB channel) |
| `Resampling` | `Bilinear` | Used when reading the view |
| `Hillshade` | `false` | Horn hillshade at display resolution (`HillshadeAzimuth`, `HillshadeAltitude`, `ZFactor`, `HillshadeStrength`) |
| `Background` | transparent | Fill outside the raster |
| `Opacity` | 1 | Global alpha |

Areas outside the raster are transparent, nodata pixels are transparent, and an alpha band is honoured. Stretch ranges are computed once per dataset from a ≤1024 px sample and cached. Call `RasterRenderer.InvalidateStretch(ds)` after editing pixels, or use `ResolveStretch` to read the range (e.g. for a legend). Rotated rasters must be warped first.

## Color ramps

```csharp
ColorRamp ramp = ColorRamp.Ndvi;            // Viridis, Terrain, Ndvi, Magma, Inferno, Turbo, Spectral, Water, Ocean, Heat, Grayscale
ColorRamp custom = ColorRamp.FromHex("Lava", "#000000", "#7f0000", "#ff6a00", "#ffd000");
ColorRamp stops = new("Bathymetry", (0.0, Rgba.FromHex("#03045e")), (0.7, Rgba.FromHex("#48cae4")), (1.0, Rgba.FromHex("#ffffff")));
Rgba mid = ramp.Evaluate(0.5);
ReadOnlySpan<byte> lut = ramp.Lut;          // 256 RGBA entries for Kernels.Colorize
string css = ramp.ToCssGradient();           // "linear-gradient(to right, ...)" for HTML legends
File.WriteAllText("ramp.txt", ColorRamp.Terrain.ToGdalColorFile(min: 650, max: 2200));   // gdaldem color-relief
ColorRamp reversed = ramp.Reversed();
```

## Images

```csharp
byte[] png = img.EncodePng();               // via GDAL's PNG driver, entirely in memory
byte[] jpeg = img.EncodeJpeg(quality: 85);
byte[] webp = img.EncodeWebp(quality: 80);
string dataUrl = img.ToPngDataUrl();        // <img src="data:image/png;base64,...">
Rgba pixel = img[10, 20];

// Desktop bitmaps (Avalonia/Skia/WPF use premultiplied BGRA)
var bgra = new byte[img.Width * img.Height * 4];
img.CopyToBgraPremultiplied(bgra, img.Width * 4);

using Dataset asRaster = img.ToDataset();   // 4-band Byte MEM dataset
RgbaImage loaded = RgbaImage.Load("photo.jpg");
```

## Vectors

`VectorData` is an immutable snapshot of a layer (optionally reprojected and simplified) that UIs can draw and hit-test without touching native handles.

```csharp
using var ds = Gdal.OpenVector("indonesia_cities.gpkg");
using var merc = SpatialReference.WebMercator;
VectorData cities = VectorData.Load(ds.GetLayer("cities"), merc, maxFeatures: 100_000, simplifyTolerance: 0);

foreach (VectorFeature f in cities.Features)
    Console.WriteLine($"{f["name"]}: {f.Shapes.Count} shape(s), {f.Envelope}");

// Screen mapping (y down, uniform scale)
var screen = ScreenTransform.Fit(cities.Extent, width: 800, height: 600, padding: 20);
var (sx, sy) = screen.ToScreen(cities.Features[0].Envelope.CenterX, cities.Features[0].Envelope.CenterY);
var (wx, wy) = screen.ToWorld(sx, sy);
screen = screen.ZoomAt(sx, sy, 2).Pan(-50, 0).Resize(1024, 768);
string svgPath = screen.ToSvgPath(cities.Features[0].Shapes[0]);
Envelope visible = screen.WorldExtent;

// Hit testing in world units
var hits = cities.HitTest(wx, wy, tolerance: 5 / screen.Scale).ToList();

// Styling shared by the UI components
var style = new VectorStyle
{
    LabelField = "name",
    FillSelector = f => ColorRamp.Inferno.Evaluate(Convert.ToDouble(f["population"]) / 1e7),
    RadiusSelector = f => 3 + 10 * Math.Sqrt(Convert.ToDouble(f["population"]) / 1e7),
};
var layer = new StyledVector(cities, style, "Cities");
```

`VectorData.FromGeometries(name, geometries)` wraps the result of a geoprocessing operation.

## XYZ tiles

```csharp
using var tiles = new TileGenerator(dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, tileSize: 256);
Console.WriteLine($"native zoom {tiles.NativeZoom}, extent {tiles.Extent}");
int written = tiles.Generate("tiles/", minZoom: 9, maxZoom: 13);       // + tiles/leaflet.html preview
byte[]? png = tiles.GetTilePng(new TileId(11, 1634, 1061));             // on-demand (null outside the data)

TileId t = Mercator.TileAt(lon: 107.6, lat: -6.9, zoom: 12);
Envelope bounds = TileGenerator.TileBounds(t);
var (mx, my) = Mercator.FromLonLat(107.6, -6.9);
```

Serve tiles from ASP.NET Core:

```csharp
app.MapGet("/tiles/{z}/{x}/{y}.png", (int z, int x, int y) =>
    tiles.GetTilePng(new TileId(z, x, y)) is { } png ? Results.File(png, "image/png") : Results.NotFound());
```

`TileGenerator` serializes access to its warped VRT, so one instance can safely serve concurrent requests.

**Next:** [Blazor components](blazor.md) · [Avalonia controls](avalonia.md) · [Performance](performance.md)
