[English](../en/imagery-terrain.md)

# Citra & terrain

![Contoh terrain](../images/gallery-terrain.png)

## Lapisan dasar saat startup

```csharp
var options = new CesiumViewerOptions
{
    BaseLayer = ImageryLayerOptions.Satellite(),   // default: OpenStreetMap
    Terrain = TerrainOptions.ArcGis(),             // default: elipsoid mulus
};
```

`NoBaseLayer = true` menghasilkan globe polos yang diwarnai oleh `GlobeBaseColor`. `BaseLayerPicker = true` menampilkan pemilih bawaan CesiumJS. Lapisannya sebagian besar dari ion/Bing dan butuh token, dan `BaseLayer` diabaikan selama pemilih itu aktif.

## Provider bawaan (tanpa key)

| Factory / jenis | Sumber | Catatan |
|---|---|---|
| `ImageryLayerOptions.OpenStreetMap()` | tile.openstreetmap.org | Default. Patuhi [kebijakan penggunaan tile OSM](https://operations.osmfoundation.org/policies/tiles/) dan gunakan tile server sendiri di produksi (`Url`). |
| `Satellite()` | Esri World Imagery | Hingga level 19 |
| `Streets()` | Esri World Street Map | |
| `Topographic()` | Esri World Topo Map | |
| `Dark()`, `Light()` | Basemap CARTO | Cocok untuk dashboard |
| `ImageryProviderKind.TileCoordinates` / `Grid` | dibuat lokal | Untuk debugging |

## Mengganti dan menumpuk lapisan

```csharp
await cesium.Imagery.SetBaseLayerAsync(ImageryLayerOptions.Dark());          // mengembalikan "base"

string labels = await cesium.Imagery.AddAsync(ImageryLayerOptions.FromTemplate(
    "https://{s}.basemaps.cartocdn.com/light_only_labels/{z}/{x}/{y}.png", "© OpenStreetMap contributors © CARTO"));

await cesium.Imagery.UpdateAsync(labels, new ImageryLayerUpdate { Alpha = 0.8, Brightness = 1.2, Saturation = 0 });
await cesium.Imagery.RaiseToTopAsync(labels);
string[] order = await cesium.Imagery.GetIdsAsync();   // bawah → atas
await cesium.Imagery.RemoveAsync(labels);
```

## Layanan OGC dan tile

```csharp
// WMS
await cesium.Imagery.AddAsync(ImageryLayerOptions.Wms(
    "https://ows.terrestris.de/osm/service", layers: "OSM-WMS"));

// WMTS (default Web Mercator). Untuk tiling scheme geografis, tambahkan Parameters["tilingScheme"] = "geographic".
await cesium.Imagery.AddAsync(ImageryLayerOptions.Wmts(
    url: "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/MODIS_Terra_CorrectedReflectance_TrueColor/default/2024-06-01/GoogleMapsCompatible_Level9/{TileMatrix}/{TileRow}/{TileCol}.jpg",
    layer: "MODIS_Terra_CorrectedReflectance_TrueColor", tileMatrixSetId: "GoogleMapsCompatible_Level9",
    format: "image/jpeg", maximumLevel: 9));

// TMS (keluaran gdal2tiles / MapTiler)
await cesium.Imagery.AddAsync(new ImageryLayerOptions { Provider = ImageryProviderKind.Tms, Url = "https://example.com/tiles/ortho" });

// Template XYZ dengan subdomain
await cesium.Imagery.AddAsync(new ImageryLayerOptions
{
    Provider = ImageryProviderKind.UrlTemplate,
    Url = "https://{s}.tile.example.com/{z}/{x}/{y}.png",
    Subdomains = ["a", "b", "c"], MaximumLevel = 18, Credit = "© Example",
});

// ArcGIS MapServer
await cesium.Imagery.AddAsync(new ImageryLayerOptions
{
    Provider = ImageryProviderKind.ArcGisMapServer,
    Url = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Shaded_Relief/MapServer",
});

// Satu gambar bergeoreferensi
await cesium.Imagery.AddAsync(new ImageryLayerOptions
{
    Provider = ImageryProviderKind.SingleTile, Url = "https://example.com/flood-2024.png",
    Rectangle = new GeoRectangle(106.6, -6.4, 107.0, -6.0), Alpha = 0.7,
});

// Bing (key) dan citra ion (token)
await cesium.Imagery.AddAsync(new ImageryLayerOptions { Provider = ImageryProviderKind.Bing, Key = "<bing key>", MapStyle = "AerialWithLabels" });
await cesium.Imagery.AddAsync(new ImageryLayerOptions { Provider = ImageryProviderKind.Ion, AssetId = 3954 }); // Sentinel-2
```

Overlay cuaca hanyalah lapisan WMS, WMTS, atau XYZ biasa dari penyedia data cuaca. Tambahkan dengan salah satu factory di atas, lalu atur `Alpha`.

Tile server harus mengizinkan **CORS** untuk origin halaman. Lihat [Pemecahan masalah](troubleshooting.md#cors-untuk-data-kustom).

## Blending dan perbandingan swipe

```csharp
string before = await cesium.Imagery.AddAsync(new ImageryLayerOptions { Provider = ImageryProviderKind.Ion, AssetId = 3954, SplitDirection = SplitDirection.Left });
await cesium.Imagery.SetSplitPositionAsync(0.5);      // 0..1 selebar layar
// kemudian, dari slider:
await cesium.Imagery.SetSplitPositionAsync(sliderValue);
```

Setiap lapisan mendukung `Alpha`, `Brightness`, `Contrast`, `Hue`, `Saturation`, `Gamma`, dan `Show`. Nilainya bisa diatur saat dibuat atau diubah belakangan lewat `UpdateAsync`.

## Provider terrain

| Factory / jenis | Kebutuhan | Catatan |
|---|---|---|
| `TerrainOptions.Ellipsoid()` | tidak ada | Default, tanpa jaringan. Semua ketinggian bernilai 0. |
| `TerrainOptions.ArcGis()` | tidak ada | Esri World Elevation 3D. Isi `Url` untuk memakai ArcGIS ImageServer milik sendiri. |
| `TerrainOptions.WorldTerrain(lighting, water)` | token ion | Cesium World Terrain dengan vertex normal dan water mask |
| `TerrainProviderKind.Ion` + `AssetId` | token ion | Terrain ion milik sendiri |
| `TerrainProviderKind.CesiumTerrainServer` + `Url` | — | Server quantized-mesh, misalnya Cesium Terrain Server yang di-host sendiri |

```csharp
await cesium.Terrain.SetProviderAsync(TerrainOptions.ArcGis());
await cesium.Scene.ConfigureAsync(new SceneSettings { DepthTestAgainstTerrain = true }); // sembunyikan entitas di balik gunung
await cesium.Terrain.SetExaggerationAsync(2.5);
```

## Ketinggian, sampling, dan profil

```csharp
double? h = await cesium.Terrain.GetHeightAsync(new GeoPoint(110.4457, -7.5407)); // Merapi
GeoPoint[] sampled = await cesium.Terrain.SampleHeightsAsync([new(110.40, -7.54), new(110.45, -7.54)]);

ProfileSample[] profile = await cesium.Terrain.GetProfileAsync(
    new GeoPoint(110.38, -7.56), new GeoPoint(110.50, -7.52), samples: 200);
foreach (var s in profile)
    Console.WriteLine($"{s.Distance,8:F0} m  {s.Position.Height,7:F1} m");

// Gambar garis profil di permukaan tanah:
await cesium.Entities.AddAsync(new EntityOptions
{
    Polyline = new PolylineGraphics { Positions = profile.Select(s => s.Position).ToArray(), ClampToGround = true, Width = 4, Material = CesiumColor.Yellow },
});
```

- Sampling memakai `sampleTerrainMostDetailed` dari CesiumJS, sehingga semua titik dikirim dalam satu batch permintaan.
- Dengan provider elipsoid, semua ketinggian bernilai `0`. Kalau sampling suatu titik gagal, CesiumJS juga melaporkan `0`. Karena itu, dalam praktiknya `GetHeightAsync` hampir tidak pernah mengembalikan `null`.
- Ketinggian diukur relatif terhadap elipsoid WGS84, sama seperti di seluruh Cesium.Net.

## Menempelkan ke tanah (ground clamping)

| Grafik | Caranya |
|---|---|
| Point, billboard, label, model, box, cylinder, ellipsoid | `HeightReference = HeightReference.ClampToGround` (atau `RelativeToGround` + ketinggian) |
| Polyline | `ClampToGround = true` |
| Polygon | Tanpa ketinggian + `ClassifyGround = true` (juga menempel di 3D Tiles), atau `HeightReference` |
| GeoJSON / KML / GPX | `DataSourceOptions.ClampToGround = true` |

## Memotong (clipping) globe

Terrain di dalam polygon bisa disembunyikan, misalnya untuk menampilkan model utilitas bawah tanah atau terowongan:

```csharp
var pit = Geodesy.Circle(new GeoPoint(106.8272, -6.1754), 300, 48);
await cesium.Terrain.SetClippingPolygonsAsync([pit]);           // inverse: true hanya menyisakan bagian dalam
await cesium.Scene.ConfigureAsync(new SceneSettings { GlobeTranslucency = true, GlobeFrontAlpha = 0.4 }); // alternatif: globe tembus pandang
await cesium.Terrain.SetClippingPolygonsAsync(null);            // hapus
```

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
