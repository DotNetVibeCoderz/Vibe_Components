# Cesium.Net

**CesiumJS for .NET** — 3D globes, maps, terrain, 3D Tiles, time-dynamic data and geospatial analysis, driven entirely from C#.
One API for **Blazor** (web) and **Avalonia UI** (desktop).

*CesiumJS untuk .NET — globe 3D, peta, terrain, 3D Tiles, data dinamis waktu, dan analisis geospasial, sepenuhnya dari C#. Satu API untuk Blazor (web) dan Avalonia UI (desktop).*

| Package | Use it for |
|---|---|
| `Cesium.Net` | Core API, geodesy & coordinate math, orbits, realtime batching (referenced by the hosts) |
| `Cesium.Net.Blazor` | `<CesiumViewer>` component for Blazor Server, WebAssembly and Web Apps |
| `Cesium.Net.Avalonia` | `CesiumView` control for Windows, macOS and Linux desktop apps |

## Blazor

```razor
<CesiumViewer Style="height:600px" OnReady="OnReady" OnLeftClick="p => Console.WriteLine(p.Position)" />

@code {
    async Task OnReady(CesiumController cesium)
    {
        await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Monas",
            Position = new GeoPoint(106.827153, -6.175392),
            Billboard = new BillboardGraphics { PinColor = "#1F4FE0" },
        });
        await cesium.Camera.FlyToAsync(106.8272, -6.1880, 2_500, pitch: -40);
    }
}
```

## Avalonia

```xml
<cesium:CesiumView Ready="OnGlobeReady" />   <!-- xmlns:cesium="using:CesiumNet.Avalonia" -->
```

```csharp
private async void OnGlobeReady(object? sender, CesiumReadyEventArgs e)
    => await e.Controller.Camera.FlyToAsync(106.8272, -6.1880, 2_500, pitch: -40);
```

On Windows, add an `app.manifest` declaring Windows 10/11 support (required by the native WebView host).

## Highlights
- Entities (points, pins, labels, polylines, polygons, ellipses, boxes, cylinders, corridors, walls, glTF models, paths), imagery (OSM, Esri, Bing, WMS, WMTS, TMS, XYZ, ion), terrain, 3D Tiles (styling, custom shaders, point clouds, clipping), GeoJSON/TopoJSON/KML/CZML/GPX.
- Camera fly-to, orbit, first-person flight; timeline & sampled positions; drawing, editing and measuring; picking and events.
- Lighting, shadows, atmosphere, fog, bloom, rain/snow/fog weather; screenshots.
- Vincenty geodesy, WGS84/ECEF/ENU/Web Mercator conversions, Keplerian orbits, a lock-free realtime feed for SignalR/MQTT/WebSocket tracking, GPU point collections for 100k+ points.
- Works without a Cesium ion account; add a token to unlock World Terrain, OSM Buildings and ion assets.
- Trim/AOT-safe source-generated JSON; .NET 10.

Made by Gravicode Studios, led by Kang Fadhil. · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
