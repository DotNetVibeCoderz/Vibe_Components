[Bahasa Indonesia](../id/offline-and-ion.md)

# Offline & Cesium ion

## What is loaded from where

| Resource | Default source | Offline alternative |
|---|---|---|
| CesiumJS (`Cesium.js`, `Widgets/`, `Workers/`, `Assets/`, `ThirdParty/`) | `https://cdn.jsdelivr.net/npm/cesium@1.146.0/Build/Cesium/` | Self-host the build; set `CesiumBaseUrl` |
| JS bridge (`cesium-net.js`) | Bundled: Blazor static web asset / Avalonia embedded resource | Already local |
| Base imagery | OpenStreetMap tiles | Local TMS/XYZ tiles, `SingleTile`, or CesiumJS's bundled Natural Earth II |
| Terrain | Ellipsoid (no network) | Ellipsoid, or a local quantized-mesh server |
| Pins with Maki icons | `{CesiumBaseUrl}/Assets/Textures/maki/` | Comes with the self-hosted build |
| Your data (glTF, GeoJSON, 3D Tiles…) | wherever you point | Blazor `wwwroot`, Avalonia `MapFolder` |

## Get the CesiumJS build

```bash
npm pack cesium@1.146.0          # downloads cesium-1.146.0.tgz
tar -xzf cesium-1.146.0.tgz      # → package/Build/Cesium/
```

Use the matching version, because the bridge is tested against `CesiumViewerOptions.DefaultCesiumVersion` (`1.146.0`). To use a different CDN version instead of self-hosting, set `CesiumBaseUrl = CesiumScripts.DefaultCesiumBaseUrl("1.xxx.0")` (namespace `CesiumNet.Interop`).

## Blazor: `wwwroot` + `CesiumBaseUrl`

1. Copy `package/Build/Cesium/` to `wwwroot/cesium/` of the **host** project (for WebAssembly, the client project).
2. Point Cesium.Net at it, either globally or per viewer:

```csharp
builder.Services.AddCesiumNet(o => o.CesiumBaseUrl = "/cesium/");
// or
var options = new CesiumViewerOptions { CesiumBaseUrl = "/cesium/" };
```

3. Use an offline base layer:

```csharp
var options = new CesiumViewerOptions
{
    CesiumBaseUrl = "/cesium/",
    BaseLayer = new ImageryLayerOptions { Provider = ImageryProviderKind.Tms, Url = "/cesium/Assets/Textures/NaturalEarthII" },
    Terrain = TerrainOptions.Ellipsoid(),
};
```

The build is roughly 30 to 40 MB in about a thousand files. If you use `MapStaticAssets`, they are fingerprinted and compressed at build time, which adds build time once. If that is a concern, serve the folder with `app.UseStaticFiles()` from a separate path.

## Avalonia: `MapFolder`

```csharp
var cesiumUrl = CesiumLocalServer.Shared.MapFolder("cesium", Path.Combine(AppContext.BaseDirectory, "cesium"));
var tilesUrl  = CesiumLocalServer.Shared.MapFolder("tiles",  @"D:\maps\jakarta-xyz");

Globe.Options = new CesiumViewerOptions
{
    CesiumBaseUrl = cesiumUrl,
    BaseLayer = ImageryLayerOptions.FromTemplate(tilesUrl + "{z}/{x}/{y}.png", "© Local survey", maximumLevel: 18),
};
```

```xml
<ItemGroup>
  <None Include="cesium\**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Avalonia has no `AddCesiumNet` equivalent, so set `CesiumBaseUrl` on every `CesiumView`'s `Options`. See [Avalonia](avalonia.md#cesiumlocalserver-and-mapfolder) for MIME types and limits (single-segment names, no `Content-Encoding`).

## Cesium ion

[Cesium ion](https://cesium.com/ion) hosts terrain, imagery, 3D Tiles (including photogrammetry and BIM conversions) and glTF. A **token** is needed only for ion content:

| Feature | Needs ion token |
|---|---|
| `TerrainOptions.WorldTerrain()`, `TerrainProviderKind.Ion` | yes |
| `TilesetOptions.OsmBuildings()`, `FromIon(id)` | yes |
| `TilesetOptions.GooglePhotorealistic()` without `key` | yes |
| `ImageryProviderKind.Ion`, Bing via ion | yes |
| `BaseLayerPicker`, `Geocoder` widgets (default ion/Bing content) | yes |
| OSM, Esri, CARTO, WMS/WMTS/TMS/XYZ, ArcGIS terrain, your own 3D Tiles | no |

### Providing the token

```csharp
// Blazor: app-wide default (appsettings / user secrets / environment)
builder.Services.AddCesiumNet(o => o.IonAccessToken = builder.Configuration["Cesium:IonToken"]);

// Any host: per viewer
var options = new CesiumViewerOptions { IonAccessToken = token };
```

The token is sent to the browser or WebView, because CesiumJS calls ion directly. It is not secret, so treat it as public:

- Create a dedicated token with only the **`assets:read`** scope and only the assets you need.
- Restrict it to your **allowed URLs** in the ion dashboard. In Avalonia the origin is `http://localhost:{random port}`.
- Never ship a token that has `assets:list`, `assets:write` or other account scopes to a client.

All viewers on one page share `Cesium.Ion.defaultAccessToken`. If two Blazor viewers on the same page set different tokens, the last one wins.

### `CesiumIonClient`

A minimal server-side REST client for listing and inspecting your assets, for example to build an asset picker:

```csharp
using CesiumNet.Ion;

var ion = new CesiumIonClient(httpClient, serverSideToken);        // token with assets:list / assets:read
IReadOnlyList<IonAsset> assets = await ion.ListAssetsAsync();
foreach (var a in assets.Where(a => a.Status == "COMPLETE"))
    Console.WriteLine($"{a.Id,8} {a.Type,-8} {a.Name}");

IonAsset? one = await ion.GetAssetAsync(CesiumIonClient.WellKnownAssets.CesiumOsmBuildings);

// Hand the id to the viewer (which uses its own, read-only token):
var asset = assets.First(a => a.Type == "3DTILES");
await cesium.Tilesets.AddAsync(TilesetOptions.FromIon(asset.Id, flyTo: true));
```

- `ListAssetsAsync` needs the `assets:list` scope; `GetAssetAsync` needs `assets:read` and returns `null` on 404. Other HTTP errors throw `HttpRequestException`. A **401** means the token is missing or invalid; **403** means a scope is missing.
- Asset `Type`: `3DTILES`, `GLTF`, `IMAGERY`, `TERRAIN`, `KML`, `CZML`, `GEOJSON`. `Status`: `COMPLETE`, `IN_PROGRESS`, `AWAITING_FILES`, `NOT_STARTED`, `DATA_ERROR`, `ERROR`.
- Uploading, tiling, BIM conversion and asset versioning are done in the ion web app or REST API. They are not wrapped by `CesiumIonClient`.
- `WellKnownAssets`: `CesiumWorldTerrain` (1), `BingAerial` (2), `BingAerialWithLabels` (3), `BingRoad` (4), `CesiumOsmBuildings` (96188), `GooglePhotorealistic3DTiles` (2275207), `Sentinel2` (3954), `EarthAtNight` (3812).

## Attribution

Most map data licenses require visible attribution.

- Keep `CesiumViewerOptions.ShowCredits = true` (the default). It shows the CesiumJS credit bar, including the Cesium ion logo when ion content is used, and each provider's credits.
- Set `ImageryLayerOptions.Credit` for custom layers. Built-in presets already include credits: Esri, "© OpenStreetMap contributors © CARTO", and so on.
- **OpenStreetMap**: "© OpenStreetMap contributors" is required. The public `tile.openstreetmap.org` servers are not for heavy production traffic, so use a commercial or self-hosted tile server.
- **Esri** basemaps: keep the Esri attribution and follow Esri's terms for production use.
- **Google Photorealistic 3D Tiles**: Google's logo and data attributions must stay visible.
- **Bing Maps**: requires your own key and Microsoft's attribution.
- If you hide the credit bar (`ShowCredits = false`), you must show equivalent attribution in your own UI.

---
Made by Gravicode Studios, led by Kang Fadhil.
