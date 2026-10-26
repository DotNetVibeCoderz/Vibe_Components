# Rendering

Halaman ini membahas cara mengubah raster dan vektor menjadi piksel: raster ke RGBA, color ramp, encoding citra, snapshot vektor dan transformasi layar, serta tile web XYZ (namespace `GDAL.Net.Rendering`). Komponen Blazor dan Avalonia dibangun di atas kelas-kelas ini.

```csharp
using GDAL.Net;
using GDAL.Net.Rendering;
using GDAL.Net.Srs;
```

## Raster → RGBA

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

| `RenderOptions` | Default | Catatan |
|---|---|---|
| `Mode` | `Auto` | `SingleBand`, `Rgb`, `Palette`. Auto memilih RGB untuk citra 3+ band dan Palette untuk band bertabel warna. |
| `Bands` | `null` | Satu band (single/palet) atau tiga (RGB), dihitung mulai 1 |
| `Ramp` | `Viridis` | `ColorRamp` apa pun |
| `Stretch` | `Percentile` | `None` (data Byte apa adanya), `MinMax`, `StdDev` (`StdDevFactor`), `Percentile` (`PercentileLow/High`), `Custom` (`Min`/`Max`, atau `Mins`/`Maxs` per kanal RGB) |
| `Resampling` | `Bilinear` | Dipakai saat membaca tampilan |
| `Hillshade` | `false` | Hillshade Horn pada resolusi tampilan (`HillshadeAzimuth`, `HillshadeAltitude`, `ZFactor`, `HillshadeStrength`) |
| `Background` | transparan | Warna area di luar raster |
| `Opacity` | 1 | Alpha global |

Area di luar raster dan piksel nodata ditampilkan transparan, dan band alpha dihormati. Rentang stretch dihitung sekali per dataset dari sampel ≤1024 px lalu di-cache. Panggil `RasterRenderer.InvalidateStretch(ds)` setelah mengubah piksel, atau `ResolveStretch` untuk membaca rentangnya (misalnya untuk legenda). Raster yang terotasi harus di-warp terlebih dulu.

## Color ramp

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

## Citra

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

## Vektor

`VectorData` adalah snapshot immutable dari sebuah layer (bisa sekaligus direproyeksi dan disederhanakan). UI dapat menggambar dan melakukan hit test terhadapnya tanpa menyentuh handle native.

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

`VectorData.FromGeometries(name, geometries)` membungkus hasil operasi geoprocessing.

## Tile XYZ

```csharp
using var tiles = new TileGenerator(dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, tileSize: 256);
Console.WriteLine($"native zoom {tiles.NativeZoom}, extent {tiles.Extent}");
int written = tiles.Generate("tiles/", minZoom: 9, maxZoom: 13);       // + tiles/leaflet.html preview
byte[]? png = tiles.GetTilePng(new TileId(11, 1634, 1061));             // on-demand (null outside the data)

TileId t = Mercator.TileAt(lon: 107.6, lat: -6.9, zoom: 12);
Envelope bounds = TileGenerator.TileBounds(t);
var (mx, my) = Mercator.FromLonLat(107.6, -6.9);
```

Menyajikan tile dari ASP.NET Core:

```csharp
app.MapGet("/tiles/{z}/{x}/{y}.png", (int z, int x, int y) =>
    tiles.GetTilePng(new TileId(z, x, y)) is { } png ? Results.File(png, "image/png") : Results.NotFound());
```

`TileGenerator` menserialisasi akses ke VRT hasil warp-nya, sehingga satu instance aman melayani request bersamaan.

**Berikutnya:** [Komponen Blazor](blazor.md) · [Kontrol Avalonia](avalonia.md) · [Performa](performance.md)
