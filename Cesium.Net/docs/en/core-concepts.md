[Bahasa Indonesia](../id/core-concepts.md)

# Core concepts

## Controller lifecycle

```
host created ──► Controller exists (calls queue) ──► CesiumJS loaded + viewer created ──► Ready
                                                   └─► load failed ──► WhenReady faulted, Error raised
host disposed ──► JS viewer destroyed ──► further calls fail
```

| Member | Meaning |
|---|---|
| `IsReady` | `true` after the viewer is created. |
| `WhenReady` | A `Task` that completes when the viewer is ready, or faults if loading fails. |
| `event Ready` | Raised once, when ready. Blazor also offers `OnReady`; Avalonia also offers `CesiumView.Ready`. |

You get the controller from `CesiumViewer.Controller` / `OnReady` in Blazor, or from `CesiumView.Controller` / `Ready` in Avalonia. Your C# code does not depend on the host. Write map logic against `CesiumController` and share it between web and desktop, as the gallery does.

**Calls made before the viewer is ready are queued.** Each call awaits `WhenReady` and then runs. If loading fails, every waiting call throws the load exception. Overloads that take a `CancellationToken` let you stop waiting.

## The async model

- Every viewer operation is asynchronous and returns `ValueTask` or `ValueTask<T>`. Await each call exactly once, and don't store a `ValueTask` to await it later. Call `.AsTask()` if you need a `Task`.
- A call completes when JavaScript has finished the operation. Two things go further: `Camera.FlyToAsync`, `FlyToRectangleAsync` and `FlyToObjectAsync` complete when the **flight ends or is cancelled**, and `Drawing.DrawAsync` completes when the **user finishes or cancels**.
- Await calls in order when order matters. Calls issued without awaiting may interleave on the JavaScript side.
- To send many changes, use the batch APIs (`AddRangeAsync`, `UpdatePositionsAsync`) instead of many small calls. See [Performance](performance.md).
- `ExecuteScriptAsync(script)` is the escape hatch for CesiumJS features that are not wrapped. It runs with `viewer`, `Cesium` and `scene` in scope and returns the JSON-serialized result, or `null`:

```csharp
string? version = await cesium.ExecuteScriptAsync("Cesium.VERSION");                 // "\"1.146\""
await cesium.ExecuteScriptAsync("viewer.scene.globe.showGroundAtmosphere = false");
string? json = await cesium.ExecuteScriptAsync("viewer.scene.primitives.length");    // "7"
await cesium.RequestRenderAsync(); // needed after scripts when RequestRenderMode is on
```

The script is evaluated as an expression first and, if that fails to parse, as a function body (use `return`). Promises are awaited. A string result comes back JSON-encoded, with its quotes, so deserialize it with `JsonSerializer.Deserialize<string>(json!)`.

## Null means "unchanged"

Option classes use nullable properties. **`null` properties are not serialized**: the source-generated JSON uses `WhenWritingNull`. This has two consequences.

1. **On creation**, an unset property takes the CesiumJS default, or a Cesium.Net default where noted, such as label font or point size.
2. **On update**, an unset property leaves the current value alone. The update APIs are:

| API | Partial update type |
|---|---|
| `Scene.ConfigureAsync` | `SceneSettings` |
| `Clock.ConfigureAsync` | `ClockOptions` |
| `Imagery.UpdateAsync` | `ImageryLayerUpdate` |
| `Tilesets.UpdateAsync` | `TilesetOptions` (only `Show`, `MaximumScreenSpaceError`, `DebugShowBoundingVolume`, `Style`, `PointCloudShading`, `HeightOffset`, `ClippingPolygons`/`ClipInverse` and `CustomShader` are applied) |
| `Camera.ConfigureControllerAsync` | `CameraControllerOptions` |
| `Entities.UpdateAsync` | `EntityOptions` |

`Entities.UpdateAsync` works per **property**. A non-null graphics object such as `Point`, `Label` or `Polygon` **replaces** the entity's previous graphics of that kind completely, so restate every sub-property you want to keep. Null graphics are left untouched.

```csharp
// Recolor only the point; Label, Position etc. remain as they are.
await cesium.Entities.UpdateAsync("truck-7", new EntityOptions
{
    Point = new PointGraphics { PixelSize = 12, Color = CesiumColor.Red, OutlineColor = CesiumColor.White, OutlineWidth = 2 },
});
```

## Events

Every event is a .NET `event` on `CesiumController`. The JavaScript side forwards only the events enabled in `CesiumViewerOptions.Events`, a `CesiumEventOptions`. You can change them later with `Scene.ConfigureEventsAsync`.

| Event | Args | `CesiumEventOptions` flag (default) | Notes |
|---|---|---|---|
| `Ready` | — | always | Raised once. |
| `LeftClick` | `PickResult` | `Click` (**on**) | Suppressed while drawing or editing. |
| `RightClick` | `PickResult` | `RightClick` (off) | |
| `DoubleClick` | `PickResult` | `DoubleClick` (off) | |
| `MouseMove` | `PickResult` | `MouseMove` (off) | Throttled by `MouseMoveThrottleMs` (100). |
| `CameraChanged` | `CameraState` | `CameraChanged` (**on**) | On camera move end. |
| `SelectedEntityChanged` | `string?` | `SelectedEntityChanged` (**on**) | Entity id or `null`. |
| `ClockTick` | `ClockState` | `ClockTick` (off) | Throttled by `ClockTickThrottleMs` (500). |
| `DrawCompleted` | `DrawResult` | always | See [Drawing](drawing-and-measuring.md). |
| `DrawCancelled` | — | always | |
| `EntityEdited` | `EntityEditedResult` | always | After each vertex drag. |
| `LoadingProgress` | `int` | `LoadingProgress` (off) | Pending **globe** tiles (0 = idle). 3D Tiles are not counted. |
| `Error` | `CesiumError` | always | Load failures, render errors and more; see below. |

```csharp
await cesium.Scene.ConfigureEventsAsync(new CesiumEventOptions
{
    Click = true, MouseMove = true, MouseMoveThrottleMs = 50, CameraChanged = false,
});
```

`ConfigureEventsAsync` sends the **whole** `CesiumEventOptions` object, because its properties are non-nullable with defaults. Set every flag you want.

**Threading.** Events are raised on the host's UI or render context: the Blazor renderer's synchronization context, or the Avalonia UI thread. In Blazor, prefer the component's `On…` callbacks. They are `EventCallback`s and trigger a re-render. Raw `Controller` events don't trigger one, so call `InvokeAsync(StateHasChanged)` yourself.

### `PickResult`

| Property | Meaning |
|---|---|
| `Kind` | `None`, `Globe`, `Entity`, `TileFeature`, `Primitive` |
| `Screen` | Screen position in CSS pixels |
| `Position` | World position under the cursor (3D Tiles, terrain or ellipsoid), or `null` in space |
| `EntityId`, `EntityName` | For `Entity` |
| `Properties` | Entity properties, 3D Tiles feature properties, or `{ "index": "…" }` for a point primitive |
| `OwnerId` | Id of the tileset, data source or point collection that owns the picked object |

## Ids

| Object | Id |
|---|---|
| Entity | Your `EntityOptions.Id`, or a GUID generated by CesiumJS. Entities inside data sources keep the ids from the file. |
| Base imagery layer | `"base"` |
| Other imagery layers | `layer-N` |
| Tileset | `tileset-N` |
| Data source | `ds-N` |
| Point collection | `points-N` |
| Drawn shape | `draw-N` |

`Camera.FlyToObjectAsync(id)` accepts any entity, data source, tileset or point collection id. The `Entities` methods (`UpdateAsync`, `SetVisibleAsync`, `SelectAsync`, `GetPositionAsync`, `UpdatePositionsAsync`) also find entities inside loaded data sources. `RemoveAsync` also removes data-source entities; only `GetIdsAsync` is limited to the viewer's own entity collection.

## Coordinates and units

| Quantity | Unit / convention |
|---|---|
| Longitude, latitude | **Degrees**, WGS84. **Longitude comes first**: `new GeoPoint(lon, lat, height)`. |
| Height | **Meters above the WGS84 ellipsoid**, not above mean sea level. Use `HeightReference.ClampToGround` / `RelativeToGround` to work relative to terrain. |
| Distances, radii, widths | Meters (polyline `Width` and point `PixelSize` are pixels) |
| Heading | Degrees, **clockwise from north** (0 = N, 90 = E) |
| Pitch | Degrees, up from the horizon; −90 looks straight down |
| Roll | Degrees |
| Billboard `Rotation`, position-update `Heading` | Degrees clockwise |
| `EllipseGraphics.Rotation` | Degrees **clockwise** from north (same as billboard rotation) |
| Ellipsoid clock angles | Degrees from **east, counter-clockwise** (CesiumJS convention) |
| Screen positions | CSS pixels; `x` to the right, `y` down |
| Times | `DateTimeOffset` (sent as ISO 8601) |
| Area | m² |

`GeoPoint` is serialized as a compact `[lon, lat, h]` array. `Cartographic` holds radians, as CesiumJS does. ECEF and ENU values are `Cartesian3` in meters. See [Geodesy & coordinates](geodesy-and-coordinates.md).

## Colors and materials

`CesiumColor` is an RGBA struct with components from 0 to 1. It is sent to JavaScript as `#RRGGBBAA`.

```csharp
CesiumColor a = "#1F4FE0";                    // implicit from hex (#RGB, #RGBA, #RRGGBB, #RRGGBBAA)
CesiumColor b = CesiumColor.FromBytes(255, 149, 0);
CesiumColor c = CesiumColor.FromHsl(0.55, 0.8, 0.5);
CesiumColor d = CesiumColor.Cyan.WithAlpha(0.3f);
CesiumColor e = CesiumColor.Lerp(CesiumColor.Green, CesiumColor.Red, 0.5f);
```

The named colors are a curated palette, **not** CSS named colors. For example `Red` is `#FF3B30` and `Blue` is `#1F4FE0`. The full set is `Transparent`, `White`, `Black`, `Red`, `Orange`, `Yellow`, `Lime`, `Green`, `Teal`, `Cyan`, `Blue`, `Indigo`, `Purple`, `Magenta` and `Gray`.

`CesiumMaterial` describes surfaces and lines. A `CesiumColor` converts implicitly to a solid material. A hex string does not chain into a material, so write `CesiumColor.Red`, not `"#FF0000"`.

| Factory | Use on |
|---|---|
| `Solid(color)` | anything |
| `Glow(color, glowPower = 0.25, taperPower = 1)` | polylines, paths |
| `Dash(color, gapColor = null, dashLength = 16)` | polylines |
| `Arrow(color)` | polylines |
| `Outline(color, outlineColor, outlineWidth = 2)` | polylines |
| `Stripe(even, odd, repeat = 8, horizontal = true)` | surfaces |
| `Grid(color, cellAlpha = 0.1, lineCount = 8, lineThickness = 1)` | surfaces |
| `Checkerboard(even, odd, repeat = 4)` | surfaces |
| `FromImage(url, repeat = 1, transparent = true)` | surfaces |

## Error handling

| Situation | What you get |
|---|---|
| A JavaScript call throws (bad URL, unknown id, invalid shader…) | The awaited call throws **`CesiumInteropException`**. `Method` is the bridge method, `JsMessage` the JavaScript message. |
| An id-returning call gets no id | `CesiumInteropException("…", "No id returned.")` |
| CesiumJS fails to load | `WhenReady` faults. Queued and later calls rethrow. `Error` is raised with `Source = "host"`. Blazor shows `ErrorContent`; Avalonia raises `LoadFailed`. |
| Render error in CesiumJS | `Error` event, `Source = "render"`. CesiumJS stops rendering after a render error. |
| Uncaught page error (Avalonia host page) | `Error` event, `Source = "window"` |

Host differences:

- **Blazor**: `CesiumInteropException.Method` is the bridge method name, for example `entities.update`. Failed calls do **not** raise `Error`.
- **Avalonia**: `CesiumInteropException.Method` holds the failing method name. The `Error` event (`CesiumError.Source` = method) is **also** raised for each failed call.

```csharp
cesium.Error += (_, e) => logger.LogWarning("Cesium {Source}: {Message}", e.Source, e.Message);

try
{
    await cesium.Tilesets.AddAsync(TilesetOptions.FromUrl("https://example.com/missing/tileset.json"));
}
catch (CesiumInteropException ex)
{
    logger.LogError("Tileset failed in {Method}: {Js}", ex.Method, ex.JsMessage);
}
```

---
Made by Gravicode Studios, led by Kang Fadhil.
