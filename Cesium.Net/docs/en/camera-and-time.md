[Bahasa Indonesia](../id/camera-and-time.md)

# Camera & time

Angles are in degrees. Heading is clockwise from north, and pitch −90 looks straight down. Heights are in meters above the ellipsoid.

## Fly, jump, frame

```csharp
await cesium.Camera.FlyToAsync(106.8272, -6.1880, 2_500, heading: 0, pitch: -40, duration: 4); // awaits the flight
await cesium.Camera.SetViewAsync(CameraView.At(110.4457, -7.62, 9_000, heading: 10, pitch: -25)); // instant
await cesium.Camera.FlyToRectangleAsync(new GeoRectangle(95, -11, 141, 6), duration: 3);           // whole Indonesia
await cesium.Camera.FlyToObjectAsync("truck", new HeadingPitchRange(45, -30, 800));                // entity / tileset / data source / points
await cesium.Camera.FlyHomeAsync();
```

`FlyToAsync`, `FlyToRectangleAsync` and `FlyToObjectAsync` complete when the flight ends, or when another camera command or the user interrupts it. Sequential `await`s therefore make tours:

```csharp
foreach (var (lon, lat, h, pitch) in new[] { (106.8272, -6.19, 2_500.0, -35.0), (110.2038, -7.6079, 1_500.0, -25.0), (115.1889, -8.4095, 40_000.0, -50.0) })
{
    await cesium.Camera.FlyToAsync(lon, lat, h, pitch: pitch, duration: 5, cancellationToken: ct);
    await Task.Delay(1_500, ct);
}
```

A cancelled `CancellationToken` stops the wait only; the flight itself continues in JavaScript. To stop the flight, issue another camera command such as `SetViewAsync`.

## Look-at, orbit, spin, track

```csharp
var monas = new GeoPoint(106.827153, -6.175392, 60);
await cesium.Camera.LookAtAsync(monas, new HeadingPitchRange(0, -30, 600));  // locks the camera to the target
await cesium.Camera.ReleaseLookAtAsync();

await cesium.Camera.StartOrbitAsync(monas, range: 800, pitch: -25, degreesPerSecond: 8);
await cesium.Camera.StartSpinAsync(3);           // presentation mode
await cesium.Camera.StopAnimationAsync();        // stops orbit/spin

await cesium.Camera.TrackEntityAsync("truck");   // follow a (moving) entity
await cesium.Camera.TrackEntityAsync(null);
```

## Step controls (zoom, pan, rotate, tilt)

| Action | Call |
|---|---|
| Zoom in / out | `ZoomAsync()` / `ZoomAsync(zoomIn: false)` (default 20 % / 25 % of height) or `ZoomAsync(500)` |
| Pan | `MoveAsync(CameraDirection.Left)` (default 10 % of height) |
| Orbit around globe center | `RotateAsync(CameraDirection.Right, 10)` |
| Turn in place / tilt | `LookAsync(CameraDirection.Left, 15)`, `LookAsync(CameraDirection.Up, 10)` |
| Roll | `TwistAsync(5)` |

These are handy for on-screen buttons and keyboard shortcuts in your own UI.

## Input configuration

```csharp
await cesium.Camera.ConfigureControllerAsync(new CameraControllerOptions
{
    EnableTilt = false, EnableLook = false,
    MinimumZoomDistance = 200, MaximumZoomDistance = 2_000_000,
    EnableCollisionDetection = true,             // keep the camera above terrain
});
```

## First-person and free flight

```csharp
await cesium.Camera.EnableFlightModeAsync(new FlightModeOptions { Speed = 60, WalkHeight = 1.7 }); // walk on terrain
await cesium.Camera.EnableFlightModeAsync(new FlightModeOptions { Speed = 300 });                 // free flight, speed scales with altitude
await cesium.Camera.DisableFlightModeAsync();
```

| Key / input | Action |
|---|---|
| W / S | forward / backward |
| A / D | left / right |
| Q / E | down / up |
| Left-drag | look around |
| Shift | 4× speed |

Flight mode disables the normal mouse camera until it is turned off. With `WalkHeight` set, the camera follows terrain height; speed scaling is off in that case. Keyboard focus must be on the map: the canvas is focused when flight mode starts.

## Camera state and events

```csharp
CameraState s = await cesium.Camera.GetStateAsync();
Console.WriteLine(s);                           // (lon, lat, h) hdg … pitch …
GeoRectangle? visible = s.ViewRectangle;        // null when space/horizon is visible

cesium.CameraChanged += (_, state) => hud.Text = $"{state.Position.Height:N0} m";
```

`CameraChanged` is raised when movement ends. For continuous tracking of the cursor, use `MouseMove` (opt-in).

## Scene modes

```csharp
await cesium.Scene.SetModeAsync(SceneMode.Scene2D);          // or ColumbusView (2.5D), Scene3D
var options = new CesiumViewerOptions { SceneMode = SceneMode.ColumbusView };  // initial mode
```

## The clock

```csharp
var start = DateTimeOffset.UtcNow;
await cesium.Clock.ConfigureAsync(new ClockOptions
{
    StartTime = start, StopTime = start.AddHours(2), CurrentTime = start,
    Multiplier = 60,                       // 1 real second = 1 simulated minute
    ClockRange = ClockRange.LoopStop,      // Unbounded | Clamped | LoopStop
    ShouldAnimate = true,
    ZoomTimeline = true,                   // when the Timeline widget is on
});
await cesium.Clock.PauseAsync();
await cesium.Clock.SetTimeAsync(start.AddMinutes(30));
await cesium.Clock.SetMultiplierAsync(-10); // rewind
await cesium.Clock.PlayAsync();
ClockState now = await cesium.Clock.GetStateAsync();
```

The CesiumJS widgets are off by default. Enable them with `new CesiumViewerOptions { Animation = true, Timeline = true }`, or build your own controls and drive them with `ClockTick` (opt-in, throttled to `ClockTickThrottleMs`).

## Time-dynamic tracks

`SampledPosition` turns an entity into a moving object that CesiumJS interpolates between samples:

```csharp
var t0 = DateTimeOffset.UtcNow;
var route = new[] { new GeoPoint(106.65, -6.12, 0), new GeoPoint(106.80, -6.15, 1_500), new GeoPoint(107.20, -6.40, 6_000), new GeoPoint(107.60, -6.90, 9_000) };
var samples = SampledPositions.From(route.Select((p, i) => (t0.AddMinutes(i * 4), p)), InterpolationKind.Lagrange, degree: 3);

await cesium.Clock.ConfigureAsync(new ClockOptions { StartTime = t0, StopTime = t0.AddMinutes(12), CurrentTime = t0, Multiplier = 20, ClockRange = ClockRange.LoopStop, ShouldAnimate = true });

await cesium.Entities.AddAsync(new EntityOptions
{
    Id = "flight-1",
    Name = "GA-123",
    SampledPosition = samples,
    OrientToVelocity = true,                                   // nose follows the path
    Availability = new TimeInterval(t0, t0.AddMinutes(12)),
    Model = new ModelGraphics { Uri = "https://example.com/models/aircraft.glb", MinimumPixelSize = 40 },
    Path = new PathGraphics { TrailTime = 600, LeadTime = 0, Width = 3, Material = CesiumMaterial.Glow(CesiumColor.Cyan) },
});
await cesium.Camera.TrackEntityAsync("flight-1");
```

- Append live samples with `Entities.AddSamplesAsync(id, new SampledPositions { Times = [...], Positions = [...] })`. Set `Extrapolate = true` when creating the entity so it holds its last position between updates.
- `InterpolationKind.Linear` suits dense GPS logs; `Lagrange` gives smooth flight paths; `Hermite` is also available.
- **Request-render mode:** while `ShouldAnimate` is `true`, Cesium.Net keeps frames flowing, but only when the clock was started through `Clock.ConfigureAsync`, `PlayAsync` or a similar call. See [Troubleshooting](troubleshooting.md#blank-or-frozen-globe-in-request-render-mode).

---
Made by Gravicode Studios, led by Kang Fadhil.
