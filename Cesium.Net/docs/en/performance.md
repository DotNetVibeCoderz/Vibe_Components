[Bahasa Indonesia](../id/performance.md)

# Performance

Cesium.Net has two cost centers: **interop** (C# ↔ JavaScript) and **rendering** (CesiumJS/WebGL). Interop costs a fixed overhead per call. That overhead is about a microsecond in-process in WebAssembly, and a network round-trip per call in Blazor Server. Rendering costs per object and per frame. Each item below states the rule and the reason for it.

## Interop

### 1. Batch: one call instead of N

| Instead of | Use | Why |
|---|---|---|
| `AddAsync` in a loop | `Entities.AddRangeAsync(list)` | One payload and one round-trip. The CesiumJS entity collection events are suspended while adding, so CesiumJS processes the change once. |
| `UpdateAsync(id, new { Position })` per entity | `Entities.UpdatePositionsAsync(updates)` | Position-only payloads (`{id, [lon,lat,h], heading}`), with the collection's events suspended during the batch |
| N entity points | `Primitives.AddPointsAsync` | See §4 |

In Blazor Server, 1,000 individual calls means 1,000 SignalR messages and awaits. One batched call means one.

### 2. Coalesce live feeds with `RealtimeEntityFeed`

High-frequency sources often send more updates than you can see. The feed keeps the **latest position per id** and flushes at a fixed rate (default 10 Hz). Cost is then bounded by *number of moving entities × flush rate*, independent of message rate. Pick the interval from what the eye needs. 100 ms looks smooth for vehicles; 250 to 500 ms is enough for ships. See [Realtime tracking](realtime-tracking.md).

### 3. Small payloads by design

- **Source-generated JSON** (`CesiumJsonContext`) uses no reflection and is trimming/AOT-safe. The core package is marked `IsAotCompatible`.
- **`null` is never sent** (`WhenWritingNull`), so an `EntityOptions` with one graphic serializes only that graphic.
- **Positions are compact arrays**: a `GeoPoint` is written as `[lon,lat,h]`, not as `{"longitude":…}`. That is roughly half the bytes for position-heavy payloads.
- **Colors** are short `#RRGGBBAA` strings.
- **Argument arrays** are written with a pooled `Utf8JsonWriter`/`ArrayBufferWriter` per thread. Buffers above 64 KB are not retained, so a single large batch doesn't pin memory.

### 4. Opt out of events you don't use

Every forwarded event is a JS → .NET message, plus a re-render in Blazor when you use component callbacks.

```csharp
var options = new CesiumViewerOptions
{
    Events = new CesiumEventOptions
    {
        Click = true,
        CameraChanged = false,          // on by default; turn off if unused
        SelectedEntityChanged = false,  // on by default
        MouseMove = false,              // only if you show cursor coordinates (throttle ≥ 100 ms)
        ClockTick = false,              // only for custom timeline UIs (throttle ≥ 250 ms)
        LoadingProgress = false,
    },
};
```

You can change them at runtime with `Scene.ConfigureEventsAsync`, for example to enable `MouseMove` only while a "measure" tool is active.

### 5. Await meaningfully

Don't wrap tight loops of tiny calls in `Task.WhenAll`; batch them instead. Await long operations such as tilesets and data sources once, and do other work while they load.

## Rendering

### 6. 100k+ points: primitives, not entities

`Primitives.AddPointsAsync` builds a single `PointPrimitiveCollection`. CesiumJS draws it in very few draw calls, and there is no per-entity property evaluation each frame. Expect smooth interaction with hundreds of thousands of points. Per-point colors are parsed once per distinct color.

### 7. Request-render mode for mostly static scenes

```csharp
new CesiumViewerOptions { RequestRenderMode = true };
```

CesiumJS then renders only when something changes, which saves a lot of CPU, GPU and battery for dashboards and desktop apps. Cesium.Net calls `requestRender()` after **every** API call and keeps rendering continuously while it is needed:

- camera orbit and spin, and flight mode
- an animating clock started via `Clock.ConfigureAsync`/`PlayAsync`
- rain and snow
- custom shaders with `AnimateTime`

`MaximumRenderTimeChange` keeps the CesiumJS default (0): a frame is rendered whenever simulation time advances, so an animating clock (from any source, including CZML) keeps rendering and a paused one stays idle. After `ExecuteScriptAsync`, call `RequestRenderAsync()` yourself. See [Troubleshooting](troubleshooting.md#blank-or-frozen-globe-in-request-render-mode) for the edge cases.

### 8. Resolution, anti-aliasing and FPS

| Setting | Guidance |
|---|---|
| `ResolutionScale` | `0.75` on HiDPI laptops or weak GPUs renders about 44 % fewer pixels. You can change it at runtime with `SceneSettings.ResolutionScale`, e.g. lower during animations. |
| `MsaaSamples` | `1` disables MSAA, which is cheapest; `4` is a good quality default. Combine `1` with `Fxaa = true` for cheap smoothing. |
| `ShowFps` | Shows a frame-rate overlay while tuning. |

### 9. Reduce what's drawn

- `DistanceDisplayCondition` hides labels, billboards, models and lines beyond a range. Labels are among the most expensive graphics.
- `ScaleByDistance` shrinks distant icons instead of drawing them full size.
- `AlwaysOnTop` costs little, but it hides depth cues; use it only where you need it.
- Prefer one `Polyline` with many vertices over many two-point polylines.
- Turn off `Shadows`, `AmbientOcclusion` and `Bloom` unless they serve a purpose. They are full-screen passes.

### 10. 3D Tiles

| Option | Effect |
|---|---|
| `MaximumScreenSpaceError` | 16 is the default; 24 to 32 is much faster with little visible loss on city models |
| `SkipLevelOfDetail = true` | Fewer intermediate tiles are requested for massive tilesets |
| `DynamicScreenSpaceError = true` | Less detail toward the horizon |
| `PointCloudShading.Attenuation` | Better-looking point clouds at a lower point budget |

### 11. Terrain

`Ellipsoid` terrain costs nothing; ArcGIS or World Terrain stream tiles. `SampleHeightsAsync` with many points at once is one batched request. Call it once with a list rather than calling `GetHeightAsync` in a loop.

## Measuring

- `CesiumEventOptions.LoadingProgress` reports pending globe tiles; 0 means idle.
- `RealtimeEntityFeed.PublishedCount` and `PendingCount` show throughput and backlog.
- `ShowFps = true`, or `SceneSettings.ShowFps`, shows frame rate.
- In Blazor Server, watch the SignalR message rate in the browser dev tools (WS frames).

---
Made by Gravicode Studios, led by Kang Fadhil.
