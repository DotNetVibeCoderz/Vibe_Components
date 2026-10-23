[English](../en/aerospace-and-defense.md)

# Dirgantara & pertahanan

![Contoh satelit](../images/gallery-satellites.png)

## Orbit dengan `KeplerianOrbit`

`CesiumNet.Aerospace.KeplerianOrbit` adalah propagator dua benda dengan drift sekuler J2 pada node dan perigee. Kelas ini menghasilkan posisi Earth-fixed untuk visualisasi. Akurasinya cukup untuk demo, tata letak konstelasi, dan studi cakupan. **Untuk pelacakan operasional, gunakan SGP4 dengan TLE terbaru** lalu masukkan hasilnya sebagai `SampledPositions`.

```csharp
using CesiumNet.Aerospace;

var epoch = DateTimeOffset.UtcNow;
var orbit = KeplerianOrbit.Circular(altitude: 550_000, inclination: 53, rightAscension: 40, phase: 0, epoch);
// atau elemen lengkap:
var molniya = new KeplerianOrbit
{
    SemiMajorAxis = 26_600_000, Eccentricity = 0.74, Inclination = 63.4,
    RightAscension = 80, ArgumentOfPerigee = 270, MeanAnomaly = 0, Epoch = epoch,
};

TimeSpan period = orbit.Period;                      // ~95,5 menit
GeoPoint now = orbit.PositionAt(DateTimeOffset.UtcNow);
```

### Satelit dengan lintasan

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
    SampledPosition = orbit.ToSampledPositions(start, stop, TimeSpan.FromSeconds(60)),   // Lagrange, derajat 5
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

### Ground track dan footprint

```csharp
// Ground track statis untuk satu putaran:
await cesium.Entities.AddAsync(new EntityOptions
{
    Polyline = new PolylineGraphics
    {
        Positions = orbit.GroundTrack(start, start + orbit.Period, TimeSpan.FromSeconds(30)),
        Width = 2, Material = CesiumMaterial.Dash(CesiumColor.Yellow),
    },
});

// Footprint cakupan bergerak (terlihat di atas elevasi 10°), mengikuti titik sub-satelit:
double radius = KeplerianOrbit.FootprintRadius(550_000, minElevation: 10);   // meter di permukaan
var subPoints = orbit.Sample(start, stop, TimeSpan.FromSeconds(60)).Select(s => (s.Time, s.Position.WithHeight(0)));
await cesium.Entities.AddAsync(new EntityOptions
{
    Id = "sat-1-footprint",
    SampledPosition = SampledPositions.From(subPoints, InterpolationKind.Lagrange, 5),
    Ellipse = new EllipseGraphics { SemiMajorAxis = radius, SemiMinorAxis = radius, Material = CesiumColor.Cyan.WithAlpha(0.15f), Outline = true, OutlineColor = CesiumColor.Cyan },
});
```

`Sample` mensyaratkan `step > 0`. Gunakan step 30 sampai 60 detik untuk LEO dan lebih panjang untuk GEO.

### Konstelasi

Kumpulkan semua satelit dalam sebuah list lalu kirim dengan `AddRangeAsync`. Satu panggilan jauh lebih cepat daripada ratusan:

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

## Jalur penerbangan dan lalu lintas udara

- **Penerbangan terencana atau rekaman**: `SampledPosition` + `OrientToVelocity` + `Path`; lihat [Kamera & waktu](camera-and-time.md#track-time-dynamic).
- **Lalu lintas langsung (ADS-B)**: [`RealtimeEntityFeed`](realtime-tracking.md) dengan billboard atau model yang mengikuti heading.
- **Rute dan jalur udara**: `Polyline` dengan `Geodesic = true` (great circle), atau `Corridor` dengan lebar dalam meter.
- **Alat bantu perencanaan misi**: `Geodesy.Destination`, `Bearing`, `Distance`, dan `Interpolate` menghitung waypoint, leg, dan ETA. `Terrain.GetProfileAsync` memeriksa jarak bebas terhadap terrain di sepanjang leg.

```csharp
var from = new GeoPoint(106.6559, -6.1256);   // CGK
var to   = new GeoPoint(115.1671, -8.7482);   // DPS
var inv = Geodesy.Inverse(from, to);
Console.WriteLine($"{Geodesy.FormatDistance(inv.Distance)}, initial course {inv.InitialBearing:F0}°");
GeoPoint[] leg = Geodesy.Interpolate(from.WithHeight(10_000), to.WithHeight(10_000), 64);
```

## Volume sensor

`EllipsoidGraphics` membentuk cangkang parsial dengan sudut clock dan cone. Sudut clock diukur dalam derajat dari **timur, berlawanan arah jarum jam**; sudut cone dalam derajat dari **tegak ke atas** (0) ke cakrawala (90) dan ke bawah (180).

```csharp
var radar = new GeoPoint(106.65, -6.12);

// Kubah cakupan radar 360°, 40 km
await cesium.Entities.AddAsync(new EntityOptions { Position = radar, Ellipsoid = EllipsoidGraphics.Dome(40_000, outline: CesiumColor.Cyan) });

// Irisan sensor EO/IR untuk bearing kompas 30°..90°, hingga elevasi 45°, 15 km
static double Clock(double compassBearing) => 90 - compassBearing;   // kompas (cw dari U) → clock (ccw dari T)
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = radar,
    Ellipsoid = EllipsoidGraphics.Sector(15_000, minAzimuth: Clock(90), maxAzimuth: Clock(30), maxElevation: 45),
});

// Sonar: belahan bawah di bawah kapal
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = new GeoPoint(107.0, -5.9),
    Ellipsoid = new EllipsoidGraphics
    {
        Radii = new Cartesian3(3_000, 3_000, 3_000), MinimumCone = 90, MaximumCone = 180,
        Material = CesiumColor.Teal.WithAlpha(0.2f), Outline = true, OutlineColor = CesiumColor.Teal,
    },
});

// Kerucut sensor sempit mengarah ke bawah (mis. footprint kamera drone): cylinder dengan TopRadius 0
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = new GeoPoint(106.83, -6.18, 300),          // pusat kerucut: setengah panjangnya di atas tanah
    Cylinder = new CylinderGraphics { Length = 600, TopRadius = 0, BottomRadius = 250, Material = CesiumColor.Orange.WithAlpha(0.25f) },
});
```

Volume sensor hanya bersifat visual. Cesium.Net tidak menghitung line-of-sight atau penutupan oleh terrain. Ambil sampel terrain dengan `Terrain.GetProfileAsync` jika Anda butuh pemeriksaan visibilitas sederhana.

## Common operational picture (COP)

![Contoh COP](../images/gallery-cop.png)

Sebuah COP menumpuk peta dasar gelap, track kawan dan lawan, cincin jarak, zona deteksi, dan peringatan:

```csharp
await cesium.Imagery.SetBaseLayerAsync(ImageryLayerOptions.Dark());
await cesium.Scene.ConfigureAsync(new SceneSettings { ShowSkyAtmosphere = false, ShowGroundAtmosphere = false });

var basePos = new GeoPoint(106.65, -6.12);

// Cincin jarak setiap 10 km
var rings = new List<EntityOptions>();
foreach (var km in new[] { 10, 20, 30, 40 })
    rings.Add(new EntityOptions
    {
        Polyline = new PolylineGraphics { Positions = [.. Geodesy.Circle(basePos, km * 1_000, 128), Geodesy.Destination(basePos, 0, km * 1_000)], Width = 1, Material = CesiumColor.Cyan.WithAlpha(0.5f), ClampToGround = true },
        Position = Geodesy.Destination(basePos, 0, km * 1_000),
        Label = new LabelGraphics { Text = $"{km} km", Scale = 0.7, FillColor = CesiumColor.Cyan },
    });
await cesium.Entities.AddRangeAsync(rings);

// Zona terlarang
GeoPoint[] zone = [new(106.70, -6.05), new(106.80, -6.05), new(106.80, -6.15), new(106.70, -6.15)];
await cesium.Entities.AddAsync(new EntityOptions
{
    Id = "zone-r1", Name = "Restricted R1",
    Polygon = new PolygonGraphics { Positions = zone, Material = CesiumMaterial.Stripe(CesiumColor.Red.WithAlpha(0.35f), CesiumColor.Transparent, 12), Outline = true, OutlineColor = CesiumColor.Red },
});

// Track (lawan = pin merah, kawan = pin biru) dari RealtimeEntityFeed; pemeriksaan intrusi di C#:
void OnTrack(string id, GeoPoint p, bool hostile)
{
    feed.Push(id, p);
    if (hostile && Geodesy.Contains(zone, p)) alerts.Raise($"{id} entered R1 at {CoordinateConverter.ToDms(p.Latitude, true)} {CoordinateConverter.ToDms(p.Longitude, false)}");
}
```

Simbologi: Cesium.Net tidak menyertakan renderer MIL-STD-2525 / APP-6. Render simbolnya sendiri ke PNG atau SVG, misalnya dengan pustaka simbol di server, lalu gunakan sebagai `BillboardGraphics.Image`. Data URL juga bisa.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
