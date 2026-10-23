[Bahasa Indonesia](../id/gallery.md)

# Gallery

The Cesium.Net Gallery is a set of runnable use cases. Each sample is **one C# class** in `samples/Cesium.Net.Gallery.Shared`. The web front-end (Blazor) and the desktop front-end (Avalonia) both run the same classes, so every sample doubles as proof that the code is host-agnostic.

![Gallery home](../images/gallery-home.png)

## Run it

```bash
# Web (Blazor Server, http://localhost:5180)
dotnet run --project samples/Cesium.Net.Gallery.Blazor

# Desktop (Avalonia; Windows needs the WebView2 runtime, Linux needs WebKitGTK)
dotnet run --project samples/Cesium.Net.Gallery.Avalonia
```

![Avalonia gallery](../images/avalonia-gallery.png)

Each sample page shows the live viewer, a summary, **Try it** action buttons, the sample's source code (only the `// <snippet>` region) and an event log of clicks, drawings and errors. The UI can switch between English and Bahasa Indonesia.

### Cesium ion token (optional)

Samples tagged **"Better with Cesium ion"** (`UsesIon`) unlock World Terrain, OSM Buildings and ion assets when a token is set. Without one, they fall back to keyless data.

| Front-end | Where to set it |
|---|---|
| Blazor | The Settings dialog (stored in browser storage), or server configuration `Cesium:IonToken` (`appsettings.json`, user secrets, or the environment variable `Cesium__IonToken`) |
| Avalonia | The Settings panel (stored in `%AppData%/Cesium.Net.Gallery/settings.json`, or the equivalent per-user application-data folder) |

## Project layout

| Project | Role |
|---|---|
| `Cesium.Net.Gallery.Shared` | `GallerySample`, `SampleContext`, `SampleCatalog`, `Text`, `GalleryDefaults`, and all samples under `Samples/<Category>/`. Sample `.cs` files are embedded as resources so the UI can show the exact code. |
| `Cesium.Net.Gallery.Blazor` | Blazor Server host: index, sample page with `CesiumViewer`, code view, settings |
| `Cesium.Net.Gallery.Avalonia` | Avalonia host: the same catalog in a desktop shell with `CesiumView` (MVVM, CommunityToolkit) |

## Add a sample

1. Create `samples/Cesium.Net.Gallery.Shared/Samples/<Category>/MyThingSample.cs`. The file name must equal the class name, because source lookup uses `/{ClassName}.cs`.
2. Derive from `GallerySample`, then fill in the metadata and `RunAsync`:

```csharp
using CesiumNet.Realtime;

namespace CesiumNet.Gallery.Samples;

[SampleOrder(3)]                                         // position within the category (default 100)
public sealed class HarbourTrafficSample : GallerySample
{
    public override string Id => "harbour-traffic";      // URL: /sample/harbour-traffic (unique)
    public override string CategoryId => "time";         // one of SampleCatalog.Categories
    public override Text Title => ("Harbour traffic", "Lalu lintas pelabuhan");
    public override Text Summary => (
        "Ships moving around Tanjung Priok with live position updates.",
        "Kapal bergerak di sekitar Tanjung Priok dengan pembaruan posisi langsung.");
    public override GeoPoint? Location => new(106.8833, -6.1000);
    public override bool UsesIon => false;

    public override CesiumViewerOptions CreateOptions(string? ionToken)
        => GalleryDefaults.Options(ionToken, o => o.BaseLayer = ImageryLayerOptions.Dark());

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("follow", ("Follow ship 1", "Ikuti kapal 1"), ctx => ctx.Cesium.Camera.TrackEntityAsync("ship-1").AsTask()),
        new("stop", ("Stop following", "Berhenti mengikuti"), ctx => ctx.Cesium.Camera.TrackEntityAsync(null).AsTask()),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        var feed = new RealtimeEntityFeed(cesium);
        ctx.Resources.Add(feed);                          // disposed when the sample stops

        await cesium.Entities.AddAsync(new EntityOptions
        {
            Id = "ship-1", Position = new GeoPoint(106.88, -6.09),
            Billboard = new BillboardGraphics { PinColor = CesiumColor.Teal, PinText = "1" },
        });
        await cesium.Camera.FlyToAsync(106.8833, -6.1000, 8_000, pitch: -50);

        _ = Task.Run(async () =>
        {
            var lon = 106.88;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(ctx.Token))
                feed.Push("ship-1", new GeoPoint(lon += 0.0002, -6.09), heading: 90);
        }, ctx.Token);
        ctx.Log("Ship 1 under way.");
        // </snippet>
    }
}
```

That's all. `SampleCatalog` discovers non-abstract `GallerySample` types by reflection, sorts them by category order and then `[SampleOrder]`, and both front-ends list them automatically.

### Conventions

| Item | Rule |
|---|---|
| `// <snippet>` … `// </snippet>` | Marks the region shown in the Code tab. The region is de-indented. Without markers the whole file is shown. Keep the snippet self-contained and copy-pasteable. |
| `Text` | Every user-visible string is bilingual: `("English", "Bahasa Indonesia")`. It converts implicitly from a tuple. |
| `Id` | Lowercase kebab-case, unique, stable (used in URLs). |
| `CategoryId` | `start`, `imagery`, `terrain`, `tiles`, `data`, `gis`, `draw`, `camera`, `time`, `aerospace`, `effects`, `advanced` |
| `[SampleOrder(n)]` | Order within the category; lower comes first; default 100. |
| `Location` | Optional; shown as coordinates in the index. |
| `UsesIon` | `true` shows the ion tag and hint. Check `ctx.HasIonToken` and fall back gracefully. |
| `CreateOptions` | Start from `GalleryDefaults.Options(ionToken, o => …)`: satellite base, Indonesia view, click and camera events. |
| `SampleContext` | `Cesium`, `Token` (cancelled when the sample stops), `Log(...)`, `State` (scratch dictionary), `Resources` (`IAsyncDisposable`s disposed on stop), `Http` (shared `HttpClient`), `IonToken`/`HasIonToken` |
| Actions | `SampleAction(key, label, run)`, with an optional `Group` label. Each runs against the same `SampleContext`. |
| Shared code | Must compile for both hosts: use only `CesiumNet` APIs, no Blazor or Avalonia types. |

## Samples

### Categories

Categories are defined in `SampleCatalog.Categories` and shown in this order:

| Id | Title | Scope |
|---|---|---|
| `start` | Getting started | A globe in a few lines of C# |
| `imagery` | Imagery & maps | Satellite, streets, WMS, WMTS, XYZ, blending |
| `terrain` | Terrain & elevation | Elevation, exaggeration, profiles, clipping |
| `tiles` | 3D Tiles | Massive cities, point clouds, BIM, styling |
| `data` | Models & data | glTF, GeoJSON, TopoJSON, KML, CZML, GPX |
| `gis` | Entities & GIS | Shapes, picking, coordinates, spatial queries |
| `draw` | Drawing & measuring | Sketch, edit, measure distance and area |
| `camera` | Camera | Fly-to tours, orbit, first-person flight |
| `time` | Time & tracking | Timelines, simulations, live fleets |
| `aerospace` | Aerospace & defense | Orbits, sensors, operational pictures |
| `effects` | Visual effects | Lighting, shadows, atmosphere, weather |
| `advanced` | Performance & integration | 100k points, Cesium ion, screenshots, JS |

### Current samples

The gallery currently ships 33 samples across all 12 categories. The running gallery is always the authoritative index.

| Category | Id | Title | Ion |
|---|---|---|---|
| start | `hello-globe` | Hello, globe |  |
| start | `scene-modes` | Scene modes |  |
| imagery | `base-maps` | Base maps |  |
| imagery | `wms-wmts` | WMS & WMTS |  |
| imagery | `layer-blending` | Layer blending & swipe |  |
| imagery | `weather-radar` | Weather radar overlay |  |
| terrain | `terrain-exaggeration` | Terrain & exaggeration | ✓ |
| terrain | `elevation-profile` | Elevation profile | ✓ |
| terrain | `globe-clipping` | Globe clipping |  |
| tiles | `discrete-lod` | Discrete LOD tileset |  |
| tiles | `point-cloud` | Point cloud |  |
| tiles | `feature-styling` | Feature styling & custom shader | ✓ |
| data | `gltf-models` | glTF models |  |
| data | `geojson-topojson` | GeoJSON & TopoJSON |  |
| data | `kml-czml-gpx` | KML, CZML & GPX |  |
| gis | `entity-showcase` | Entity showcase |  |
| gis | `picking` | Picking & properties |  |
| gis | `coordinate-systems` | Coordinate systems |  |
| gis | `spatial-queries` | Spatial queries |  |
| draw | `draw-edit` | Draw & edit |  |
| draw | `measure` | Measure distance & area |  |
| camera | `camera-tour` | Camera tour | ✓ |
| camera | `orbit-first-person` | Orbit & first-person | ✓ |
| time | `flight-simulation` | Flight simulation |  |
| time | `live-fleet` | Live fleet |  |
| aerospace | `satellite-constellation` | Satellite constellation |  |
| aerospace | `sensor-volumes` | Sensor volumes |  |
| aerospace | `operational-picture` | Common operational picture |  |
| effects | `lighting-shadows` | Lighting & shadows |  |
| effects | `atmosphere-weather` | Atmosphere & weather | ✓ |
| advanced | `points-100k` | 100,000 points |  |
| advanced | `cesium-ion` | Cesium ion | ✓ |
| advanced | `screenshot-script` | Screenshot & JavaScript |  |


---
Made by Gravicode Studios, led by Kang Fadhil.
