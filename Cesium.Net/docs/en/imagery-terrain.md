[Bahasa Indonesia](../id/imagery-terrain.md)

# Imagery & terrain

![Terrain sample](../images/gallery-terrain.png)

## Base layer at startup

```csharp
var options = new CesiumViewerOptions
{
    BaseLayer = ImageryLayerOptions.Satellite(),   // default is OpenStreetMap
    Terrain = TerrainOptions.ArcGis(),             // default is the smooth ellipsoid
};
```

Set `NoBaseLayer = true` for a bare globe colored by `GlobeBaseColor`. `BaseLayerPicker = true` shows CesiumJS's picker, which mostly offers ion and Bing layers that need a token. With the picker on, `BaseLayer` is ignored.

## Built-in providers (no key)

| Factory / kind | Source | Notes |
|---|---|---|
| `ImageryLayerOptions.OpenStreetMap()` | tile.openstreetmap.org | Default. Respect the [OSM tile usage policy](https://operations.osmfoundation.org/policies/tiles/); use your own tile server in production (`Url`). |
| `Satellite()` | Esri World Imagery | Up to level 19 |
| `Streets()` | Esri World Street Map | |
| `Topographic()` | Esri World Topo Map | |
| `Dark()`, `Light()` | CARTO basemaps | Good for dashboards |
| `ImageryProviderKind.TileCoordinates` / `Grid` | generated | Debugging |

## Switching and stacking layers

```csharp
await cesium.Imagery.SetBaseLayerAsync(ImageryLayerOptions.Dark());          // returns "base"

string labels = await cesium.Imagery.AddAsync(ImageryLayerOptions.FromTemplate(
    "https://{s}.basemaps.cartocdn.com/light_only_labels/{z}/{x}/{y}.png", "© OpenStreetMap contributors © CARTO"));

await cesium.Imagery.UpdateAsync(labels, new ImageryLayerUpdate { Alpha = 0.8, Brightness = 1.2, Saturation = 0 });
await cesium.Imagery.RaiseToTopAsync(labels);
string[] order = await cesium.Imagery.GetIdsAsync();   // bottom → top
await cesium.Imagery.RemoveAsync(labels);
```

## OGC and tile services

```csharp
// WMS
await cesium.Imagery.AddAsync(ImageryLayerOptions.Wms(
    "https://ows.terrestris.de/osm/service", layers: "OSM-WMS"));

// WMTS (Web Mercator by default). For a geographic tiling scheme, add Parameters["tilingScheme"] = "geographic".
await cesium.Imagery.AddAsync(ImageryLayerOptions.Wmts(
    url: "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/MODIS_Terra_CorrectedReflectance_TrueColor/default/2024-06-01/GoogleMapsCompatible_Level9/{TileMatrix}/{TileRow}/{TileCol}.jpg",
    layer: "MODIS_Terra_CorrectedReflectance_TrueColor", tileMatrixSetId: "GoogleMapsCompatible_Level9",
    format: "image/jpeg", maximumLevel: 9));

// TMS (gdal2tiles / MapTiler output)
await cesium.Imagery.AddAsync(new ImageryLayerOptions { Provider = ImageryProviderKind.Tms, Url = "https://example.com/tiles/ortho" });

// XYZ template with subdomains
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

// One georeferenced image
await cesium.Imagery.AddAsync(new ImageryLayerOptions
{
    Provider = ImageryProviderKind.SingleTile, Url = "https://example.com/flood-2024.png",
    Rectangle = new GeoRectangle(106.6, -6.4, 107.0, -6.0), Alpha = 0.7,
});

// Bing (key) and ion imagery (token)
await cesium.Imagery.AddAsync(new ImageryLayerOptions { Provider = ImageryProviderKind.Bing, Key = "<bing key>", MapStyle = "AerialWithLabels" });
await cesium.Imagery.AddAsync(new ImageryLayerOptions { Provider = ImageryProviderKind.Ion, AssetId = 3954 }); // Sentinel-2
```

Weather overlays are ordinary WMS, WMTS or XYZ layers from a weather provider. Add them with one of the factories above and set `Alpha`.

The tile servers must allow **CORS** for the page's origin; see [Troubleshooting](troubleshooting.md#cors-for-custom-data).

## Blending and swipe comparison

```csharp
string before = await cesium.Imagery.AddAsync(new ImageryLayerOptions { Provider = ImageryProviderKind.Ion, AssetId = 3954, SplitDirection = SplitDirection.Left });
await cesium.Imagery.SetSplitPositionAsync(0.5);      // 0..1 across the screen
// later, from a slider:
await cesium.Imagery.SetSplitPositionAsync(sliderValue);
```

Each layer supports `Alpha`, `Brightness`, `Contrast`, `Hue`, `Saturation`, `Gamma` and `Show`. You can set them at creation or update them later with `UpdateAsync`.

## Terrain providers

| Factory / kind | Needs | Notes |
|---|---|---|
| `TerrainOptions.Ellipsoid()` | nothing | Default; no network. Heights are all 0. |
| `TerrainOptions.ArcGis()` | nothing | Esri World Elevation 3D. Set `Url` for your own ArcGIS ImageServer. |
| `TerrainOptions.WorldTerrain(lighting, water)` | ion token | Cesium World Terrain, with vertex normals and a water mask |
| `TerrainProviderKind.Ion` + `AssetId` | ion token | Your own ion terrain |
| `TerrainProviderKind.CesiumTerrainServer` + `Url` | — | Quantized-mesh server, e.g. a self-hosted Cesium Terrain Server |

```csharp
await cesium.Terrain.SetProviderAsync(TerrainOptions.ArcGis());
await cesium.Scene.ConfigureAsync(new SceneSettings { DepthTestAgainstTerrain = true }); // hide entities behind mountains
await cesium.Terrain.SetExaggerationAsync(2.5);
```

## Heights, sampling and profiles

```csharp
double? h = await cesium.Terrain.GetHeightAsync(new GeoPoint(110.4457, -7.5407)); // Merapi
GeoPoint[] sampled = await cesium.Terrain.SampleHeightsAsync([new(110.40, -7.54), new(110.45, -7.54)]);

ProfileSample[] profile = await cesium.Terrain.GetProfileAsync(
    new GeoPoint(110.38, -7.56), new GeoPoint(110.50, -7.52), samples: 200);
foreach (var s in profile)
    Console.WriteLine($"{s.Distance,8:F0} m  {s.Position.Height,7:F1} m");

// Draw the profile line on the ground:
await cesium.Entities.AddAsync(new EntityOptions
{
    Polyline = new PolylineGraphics { Positions = profile.Select(s => s.Position).ToArray(), ClampToGround = true, Width = 4, Material = CesiumColor.Yellow },
});
```

- Sampling uses CesiumJS `sampleTerrainMostDetailed`, which is one request batch for all points.
- With the ellipsoid provider every height is `0`. When sampling fails for a point, CesiumJS also reports `0`, so `GetHeightAsync` effectively never returns `null`.
- Heights are relative to the WGS84 ellipsoid, as everywhere in Cesium.Net.

## Ground clamping

| Graphic | How |
|---|---|
| Points, billboards, labels, models, boxes, cylinders, ellipsoids | `HeightReference = HeightReference.ClampToGround` (or `RelativeToGround` + height) |
| Polylines | `ClampToGround = true` |
| Polygons | No height + `ClassifyGround = true` (also drapes on 3D Tiles), or `HeightReference` |
| GeoJSON / KML / GPX | `DataSourceOptions.ClampToGround = true` |

## Clipping the globe

Hide terrain inside polygons, for example to reveal an underground utility model or a tunnel:

```csharp
var pit = Geodesy.Circle(new GeoPoint(106.8272, -6.1754), 300, 48);
await cesium.Terrain.SetClippingPolygonsAsync([pit]);           // inverse: true keeps only the inside
await cesium.Scene.ConfigureAsync(new SceneSettings { GlobeTranslucency = true, GlobeFrontAlpha = 0.4 }); // alternative: see-through globe
await cesium.Terrain.SetClippingPolygonsAsync(null);            // clear
```

---
Made by Gravicode Studios, led by Kang Fadhil.
