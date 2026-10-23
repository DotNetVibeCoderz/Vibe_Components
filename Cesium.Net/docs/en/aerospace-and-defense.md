[Bahasa Indonesia](../id/aerospace-and-defense.md)

# Aerospace & defense

![Satellites sample](../images/gallery-satellites.png)

## Orbits with `KeplerianOrbit`

`CesiumNet.Aerospace.KeplerianOrbit` is a two-body propagator with J2 secular drift of node and perigee. It produces Earth-fixed positions for visualization. Its accuracy is fine for demos, constellation layouts and coverage studies. **For operational tracking, use SGP4 with current TLEs** and feed the results in as `SampledPositions`.

```csharp
using CesiumNet.Aerospace;

var epoch = DateTimeOffset.UtcNow;
var orbit = KeplerianOrbit.Circular(altitude: 550_000, inclination: 53, rightAscension: 40, phase: 0, epoch);
// or full elements:
var molniya = new KeplerianOrbit
{
    SemiMajorAxis = 26_600_000, Eccentricity = 0.74, Inclination = 63.4,
    RightAscension = 80, ArgumentOfPerigee = 270, MeanAnomaly = 0, Epoch = epoch,
};

TimeSpan period = orbit.Period;                      // ~95.5 min
GeoPoint now = orbit.PositionAt(DateTimeOffset.UtcNow);
```

### Satellite with a path

```csharp
var start = epoch;
var stop = epoch + orbit.Period * 3;

await cesium.Clock.ConfigureAsync(new ClockOptions
{
    StartTime = start, StopTime = stop, CurrentTime = start,
    Multiplier = 60, ClockRange = ClockRange.LoopStop, ShouldAnimate = true,
});

await cesium.Entities.AddAsync(new EntityOptions
{
    Id = "sat-1", Name = "SAT-1",
    SampledPosition = orbit.ToSampledPositions(start, stop, TimeSpan.FromSeconds(60)),   // Lagrange, degree 5
    Point = new PointGraphics { PixelSize = 8, Color = CesiumColor.Cyan, OutlineColor = CesiumColor.White, OutlineWidth = 1 },
    Label = new LabelGraphics { Text = "SAT-1", PixelOffset = new Cartesian2(12, 0), HorizontalOrigin = HorizontalOrigin.Left, Scale = 0.8 },
    Path = new PathGraphics
    {
        LeadTime = orbit.Period.TotalSeconds / 2, TrailTime = orbit.Period.TotalSeconds / 2,
        Width = 1.5, Material = CesiumMaterial.Glow(CesiumColor.Cyan, 0.2),
    },
});
await cesium.Camera.FlyToAsync(110, 0, 25_000_000);
```

### Ground track and footprint

```csharp
// Static ground track for one revolution:
await cesium.Entities.AddAsync(new EntityOptions
{
    Polyline = new PolylineGraphics
    {
        Positions = orbit.GroundTrack(start, start + orbit.Period, TimeSpan.FromSeconds(30)),
        Width = 2, Material = CesiumMaterial.Dash(CesiumColor.Yellow),
    },
});

// Moving coverage footprint (visible above 10° elevation), following the sub-satellite point:
double radius = KeplerianOrbit.FootprintRadius(550_000, minElevation: 10);   // meters on the ground
var subPoints = orbit.Sample(start, stop, TimeSpan.FromSeconds(60)).Select(s => (s.Time, s.Position.WithHeight(0)));
await cesium.Entities.AddAsync(new EntityOptions
{
    Id = "sat-1-footprint",
    SampledPosition = SampledPositions.From(subPoints, InterpolationKind.Lagrange, 5),
    Ellipse = new EllipseGraphics { SemiMajorAxis = radius, SemiMinorAxis = radius, Material = CesiumColor.Cyan.WithAlpha(0.15f), Outline = true, OutlineColor = CesiumColor.Cyan },
});
```

`Sample` requires `step > 0`. Use steps of 30 to 60 s for LEO and longer ones for GEO.

### Constellations

Build all satellites in a list and send them with `AddRangeAsync`. One call is much faster than hundreds:

```csharp
var sats = new List<EntityOptions>();
for (int plane = 0; plane < 6; plane++)
for (int slot = 0; slot < 10; slot++)
{
    var o = KeplerianOrbit.Circular(550_000, 53, plane * 60, slot * 36 + plane * 6, epoch);
    sats.Add(new EntityOptions
    {
        Id = $"p{plane}-s{slot}",
        SampledPosition = o.ToSampledPositions(start, stop, TimeSpan.FromSeconds(90)),
        Point = new PointGraphics { PixelSize = 5, Color = CesiumColor.FromHsl(plane / 6.0, 0.8, 0.6) },
    });
}
await cesium.Entities.AddRangeAsync(sats);
```

## Flight paths and air traffic

- **Planned or recorded flights**: `SampledPosition` + `OrientToVelocity` + `Path`; see [Camera & time](camera-and-time.md#time-dynamic-tracks).
- **Live traffic (ADS-B)**: [`RealtimeEntityFeed`](realtime-tracking.md) with heading-aware billboards or models.
- **Routes and airways**: `Polyline` with `Geodesic = true` (great circles), or `Corridor` with a width in meters.
- **Mission planning helpers**: `Geodesy.Destination`, `Bearing`, `Distance` and `Interpolate` compute waypoints, legs and ETAs. `Terrain.GetProfileAsync` checks terrain clearance along a leg.

```csharp
var from = new GeoPoint(106.6559, -6.1256);   // CGK
var to   = new GeoPoint(115.1671, -8.7482);   // DPS
var inv = Geodesy.Inverse(from, to);
Console.WriteLine($"{Geodesy.FormatDistance(inv.Distance)}, initial course {inv.InitialBearing:F0}°");
GeoPoint[] leg = Geodesy.Interpolate(from.WithHeight(10_000), to.WithHeight(10_000), 64);
```

## Sensor volumes

`EllipsoidGraphics` carves partial shells with clock and cone angles. Clock angles are measured in degrees from **east, counter-clockwise**; cone angles in degrees from **straight up** (0) to the horizon (90) and down (180).

```csharp
var radar = new GeoPoint(106.65, -6.12);

// 360° radar coverage dome, 40 km
await cesium.Entities.AddAsync(new EntityOptions { Position = radar, Ellipsoid = EllipsoidGraphics.Dome(40_000, outline: CesiumColor.Cyan) });

// EO/IR sensor wedge covering compass bearings 30°..90°, up to 45° elevation, 15 km
static double Clock(double compassBearing) => 90 - compassBearing;   // compass (cw from N) → clock (ccw from E)
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = radar,
    Ellipsoid = EllipsoidGraphics.Sector(15_000, minAzimuth: Clock(90), maxAzimuth: Clock(30), maxElevation: 45),
});

// Sonar: lower hemisphere below a ship
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = new GeoPoint(107.0, -5.9),
    Ellipsoid = new EllipsoidGraphics
    {
        Radii = new Cartesian3(3_000, 3_000, 3_000), MinimumCone = 90, MaximumCone = 180,
        Material = CesiumColor.Teal.WithAlpha(0.2f), Outline = true, OutlineColor = CesiumColor.Teal,
    },
});

// Narrow sensor cone pointing down (e.g. a drone camera footprint): a cylinder with TopRadius 0
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = new GeoPoint(106.83, -6.18, 300),          // cone center: half the length above the ground
    Cylinder = new CylinderGraphics { Length = 600, TopRadius = 0, BottomRadius = 250, Material = CesiumColor.Orange.WithAlpha(0.25f) },
});
```

Sensor volumes are visual. Cesium.Net does not compute line-of-sight or terrain masking. Sample terrain with `Terrain.GetProfileAsync` if you need a simple visibility check.

## Common operational picture (COP)

![COP sample](../images/gallery-cop.png)

A COP layers a dark base map, friendly and hostile tracks, range rings, detection zones and alerts:

```csharp
await cesium.Imagery.SetBaseLayerAsync(ImageryLayerOptions.Dark());
await cesium.Scene.ConfigureAsync(new SceneSettings { ShowSkyAtmosphere = false, ShowGroundAtmosphere = false });

var basePos = new GeoPoint(106.65, -6.12);

// Range rings every 10 km
var rings = new List<EntityOptions>();
foreach (var km in new[] { 10, 20, 30, 40 })
    rings.Add(new EntityOptions
    {
        Polyline = new PolylineGraphics { Positions = [.. Geodesy.Circle(basePos, km * 1_000, 128), Geodesy.Destination(basePos, 0, km * 1_000)], Width = 1, Material = CesiumColor.Cyan.WithAlpha(0.5f), ClampToGround = true },
        Position = Geodesy.Destination(basePos, 0, km * 1_000),
        Label = new LabelGraphics { Text = $"{km} km", Scale = 0.7, FillColor = CesiumColor.Cyan },
    });
await cesium.Entities.AddRangeAsync(rings);

// Restricted zone
GeoPoint[] zone = [new(106.70, -6.05), new(106.80, -6.05), new(106.80, -6.15), new(106.70, -6.15)];
await cesium.Entities.AddAsync(new EntityOptions
{
    Id = "zone-r1", Name = "Restricted R1",
    Polygon = new PolygonGraphics { Positions = zone, Material = CesiumMaterial.Stripe(CesiumColor.Red.WithAlpha(0.35f), CesiumColor.Transparent, 12), Outline = true, OutlineColor = CesiumColor.Red },
});

// Tracks (hostile = red pin, friendly = blue pin) fed by a RealtimeEntityFeed; intrusion check in C#:
void OnTrack(string id, GeoPoint p, bool hostile)
{
    feed.Push(id, p);
    if (hostile && Geodesy.Contains(zone, p)) alerts.Raise($"{id} entered R1 at {CoordinateConverter.ToDms(p.Latitude, true)} {CoordinateConverter.ToDms(p.Longitude, false)}");
}
```

Symbology: Cesium.Net does not include a MIL-STD-2525 / APP-6 renderer. Render the symbols to PNG or SVG yourself, for example with a symbol library on the server, and use them as `BillboardGraphics.Image`. Data URLs work.

---
Made by Gravicode Studios, led by Kang Fadhil.
