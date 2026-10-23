[Bahasa Indonesia](../id/drawing-and-measuring.md)

# Drawing & measuring

`cesium.Drawing` provides interactive sketching on the globe, terrain and 3D Tiles, live measurement labels, and vertex editing.

## Draw a shape

```csharp
DrawResult? r = await cesium.Drawing.DrawAsync(new DrawOptions
{
    Mode = DrawMode.Polygon,
    Color = CesiumColor.Yellow,
    FillColor = CesiumColor.Yellow.WithAlpha(0.25f),
    Width = 3,
});

if (r is null) return;                         // user pressed Esc, or the call was cancelled
Console.WriteLine($"{r.Positions.Count} vertices, perimeter {Geodesy.FormatDistance(Geodesy.Perimeter(r.Positions))}, area {Geodesy.FormatArea(r.Area)}");
Console.WriteLine($"Kept as entity {r.EntityId}");
```

| Input | Action |
|---|---|
| Left click | Add a vertex (Point mode finishes immediately; Rectangle and Circle finish on the 2nd click) |
| Mouse move | Rubber-band preview |
| Double-click, right-click or Enter | Finish |
| Esc | Cancel → `DrawAsync` returns `null`, `DrawCancelled` is raised |

| Mode | Clicks | Result |
|---|---|---|
| `Point` | 1 | `Positions[0]` |
| `Polyline` | ≥ 2 | `Positions`, `Length` |
| `Polygon` | ≥ 3 | `Positions`, `Area` |
| `Rectangle` | 2 (corners) | `Positions` = the 2 corners |
| `Circle` | 2 (center, edge) | `Positions`, `Radius` (m), `Area` |
| `MeasureDistance` | ≥ 2 | Like Polyline, with a live distance label |
| `MeasureArea` | ≥ 3 | Like Polygon, with a live area label |

`DrawOptions` defaults: `ClampToGround = true`, `KeepEntity = true`, and `ShowMeasurementLabel = true` for the measure, polyline and polygon modes. With `KeepEntity = false`, the shape is removed after completion and `EntityId` is `null`.

While a drawing or edit is active, `LeftClick`, `RightClick`, `DoubleClick` and `MouseMove` events are not raised. Starting a new drawing cancels the previous one silently.

### Measurement results

`DrawResult.Length` is `Geodesy.PathLength(Positions)`, the Vincenty distance in meters. `DrawResult.Area` is computed as follows:

- Polygon and MeasureArea: `Geodesy.Area(Positions)`.
- Circle: π·r².
- Rectangle: the two returned corners are expanded to the full rectangle, then measured with `Geodesy.Area`.

The on-map label uses the same formulas in JavaScript: an ellipsoid geodesic for length and the authalic-sphere method for area.

### Cancel from code

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var r = await cesium.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.MeasureDistance }, cts.Token); // null on timeout
await cesium.Drawing.CancelAsync();   // or cancel explicitly (e.g. from a toolbar button)
await cesium.Drawing.ClearAsync();    // remove all drawn shapes and their labels
```

`DrawAsync` subscribes to `DrawCompleted` and `DrawCancelled` for its duration. Run only one `DrawAsync` at a time.

### Event-driven style

If you prefer events over awaiting, use the `DrawCompleted` event or Blazor's `OnDrawCompleted` callback:

```razor
<CesiumViewer OnReady="c => _c = c" OnDrawCompleted="OnDrawn" />
<button @onclick="() => _c!.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.MeasureArea }).AsTask()">Measure area</button>

@code {
    CesiumController? _c;
    string? _result;
    void OnDrawn(DrawResult r) => _result = $"{r.Mode}: {Geodesy.FormatArea(r.Area)}";
}
```

## Edit vertices

```csharp
await cesium.Drawing.BeginEditAsync(r.EntityId!);   // yellow handles appear on each vertex
cesium.EntityEdited += (_, e) => Console.WriteLine($"{e.EntityId}: {e.Positions.Count} vertices, area {Geodesy.FormatArea(Geodesy.Area(e.Positions))}");
// … user drags handles …
await cesium.Drawing.EndEditAsync();               // bakes the geometry back to static values
```

- Editable entities: **polygons** (including the outline polyline that drawing creates), **polylines**, and anything with a **position** (points, billboards, labels, models).
- `EntityEdited` fires after each drag ends.
- While dragging, camera input is disabled.
- Editing also works on your own entities, not only on drawn ones. Starting an edit cancels any active drawing.

## Runtime updates

Drawn shapes are ordinary entities with ids `draw-N`, so you can restyle them:

```csharp
await cesium.Entities.UpdateAsync(r.EntityId!, new EntityOptions
{
    Polygon = new PolygonGraphics { Positions = r.Positions, Material = CesiumColor.Red.WithAlpha(0.3f) },
    Name = "Exclusion zone",
});
```

## Geofencing with drawn shapes

```csharp
var zone = (await cesium.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.Polygon }))!.Positions;
bool inside = Geodesy.Contains(zone, new GeoPoint(106.83, -6.18));
```

`Geodesy.Contains` is a planar test in longitude/latitude space. It is fine for regional polygons; avoid polygons that cross the antimeridian or contain a pole.

---
Made by Gravicode Studios, led by Kang Fadhil.
