[English](../en/camera-and-time.md)

# Kamera & waktu

Satuan sudut adalah derajat. Heading diukur searah jarum jam dari utara, dan pitch −90 berarti menghadap lurus ke bawah. Ketinggian dalam meter di atas elipsoid.

## Terbang, lompat, membingkai

```csharp
await cesium.Camera.FlyToAsync(106.8272, -6.1880, 2_500, heading: 0, pitch: -40, duration: 4); // menunggu penerbangan selesai
await cesium.Camera.SetViewAsync(CameraView.At(110.4457, -7.62, 9_000, heading: 10, pitch: -25)); // seketika
await cesium.Camera.FlyToRectangleAsync(new GeoRectangle(95, -11, 141, 6), duration: 3);           // seluruh Indonesia
await cesium.Camera.FlyToObjectAsync("truck", new HeadingPitchRange(45, -30, 800));                // entitas / tileset / data source / titik
await cesium.Camera.FlyHomeAsync();
```

`FlyToAsync`, `FlyToRectangleAsync`, dan `FlyToObjectAsync` selesai ketika penerbangan berakhir, atau ketika perintah kamera lain atau pengguna menyelanya. Karena itu, `await` berurutan bisa langsung dipakai untuk membuat tur:

```csharp
foreach (var (lon, lat, h, pitch) in new[] { (106.8272, -6.19, 2_500.0, -35.0), (110.2038, -7.6079, 1_500.0, -25.0), (115.1889, -8.4095, 40_000.0, -50.0) })
{
    await cesium.Camera.FlyToAsync(lon, lat, h, pitch: pitch, duration: 5, cancellationToken: ct);
    await Task.Delay(1_500, ct);
}
```

Membatalkan `CancellationToken` hanya menghentikan penantian; penerbangan di JavaScript tetap berjalan. Untuk menghentikannya, kirim perintah kamera lain seperti `SetViewAsync`.

## Look-at, orbit, spin, track

```csharp
var monas = new GeoPoint(106.827153, -6.175392, 60);
await cesium.Camera.LookAtAsync(monas, new HeadingPitchRange(0, -30, 600));  // mengunci kamera pada target
await cesium.Camera.ReleaseLookAtAsync();

await cesium.Camera.StartOrbitAsync(monas, range: 800, pitch: -25, degreesPerSecond: 8);
await cesium.Camera.StartSpinAsync(3);           // mode presentasi
await cesium.Camera.StopAnimationAsync();        // menghentikan orbit/spin

await cesium.Camera.TrackEntityAsync("truck");   // mengikuti entitas (yang bergerak)
await cesium.Camera.TrackEntityAsync(null);
```

## Kontrol bertahap (zoom, pan, rotate, tilt)

| Aksi | Panggilan |
|---|---|
| Zoom in / out | `ZoomAsync()` / `ZoomAsync(zoomIn: false)` (default 20 % / 25 % dari ketinggian) atau `ZoomAsync(500)` |
| Pan | `MoveAsync(CameraDirection.Left)` (default 10 % dari ketinggian) |
| Orbit mengelilingi pusat globe | `RotateAsync(CameraDirection.Right, 10)` |
| Menoleh di tempat / tilt | `LookAsync(CameraDirection.Left, 15)`, `LookAsync(CameraDirection.Up, 10)` |
| Roll | `TwistAsync(5)` |

Metode-metode ini cocok untuk tombol di layar atau shortcut keyboard di UI Anda sendiri.

## Konfigurasi input

```csharp
await cesium.Camera.ConfigureControllerAsync(new CameraControllerOptions
{
    EnableTilt = false, EnableLook = false,
    MinimumZoomDistance = 200, MaximumZoomDistance = 2_000_000,
    EnableCollisionDetection = true,             // jaga kamera tetap di atas terrain
});
```

## Orang pertama dan terbang bebas

```csharp
await cesium.Camera.EnableFlightModeAsync(new FlightModeOptions { Speed = 60, WalkHeight = 1.7 }); // berjalan di atas terrain
await cesium.Camera.EnableFlightModeAsync(new FlightModeOptions { Speed = 300 });                 // terbang bebas, kecepatan menyesuaikan ketinggian
await cesium.Camera.DisableFlightModeAsync();
```

| Tombol / input | Aksi |
|---|---|
| W / S | maju / mundur |
| A / D | kiri / kanan |
| Q / E | turun / naik |
| Drag kiri | melihat sekeliling |
| Shift | kecepatan 4× |

Selama flight mode aktif, kontrol kamera mouse yang biasa dinonaktifkan sampai mode itu dimatikan. Bila `WalkHeight` diisi, kamera mengikuti ketinggian terrain dan penyesuaian kecepatan terhadap ketinggian tidak berlaku. Fokus keyboard harus berada di peta; canvas otomatis mendapat fokus saat flight mode dimulai.

## State kamera dan event

```csharp
CameraState s = await cesium.Camera.GetStateAsync();
Console.WriteLine(s);                           // (lon, lat, h) hdg … pitch …
GeoRectangle? visible = s.ViewRectangle;        // null bila angkasa/horizon terlihat

cesium.CameraChanged += (_, state) => hud.Text = $"{state.Position.Height:N0} m";
```

`CameraChanged` dipicu ketika pergerakan kamera berhenti. Untuk melacak kursor terus-menerus, gunakan `MouseMove` (opt-in).

## Mode scene

```csharp
await cesium.Scene.SetModeAsync(SceneMode.Scene2D);          // atau ColumbusView (2.5D), Scene3D
var options = new CesiumViewerOptions { SceneMode = SceneMode.ColumbusView };  // mode awal
```

## Clock

```csharp
var start = DateTimeOffset.UtcNow;
await cesium.Clock.ConfigureAsync(new ClockOptions
{
    StartTime = start, StopTime = start.AddHours(2), CurrentTime = start,
    Multiplier = 60,                       // 1 detik nyata = 1 menit simulasi
    ClockRange = ClockRange.LoopStop,      // Unbounded | Clamped | LoopStop
    ShouldAnimate = true,
    ZoomTimeline = true,                   // bila widget Timeline aktif
});
await cesium.Clock.PauseAsync();
await cesium.Clock.SetTimeAsync(start.AddMinutes(30));
await cesium.Clock.SetMultiplierAsync(-10); // putar mundur
await cesium.Clock.PlayAsync();
ClockState now = await cesium.Clock.GetStateAsync();
```

Widget CesiumJS nonaktif secara default. Aktifkan dengan `new CesiumViewerOptions { Animation = true, Timeline = true }`, atau buat kontrol sendiri dan gerakkan dengan `ClockTick` (opt-in, di-throttle sesuai `ClockTickThrottleMs`).

## Track time-dynamic

`SampledPosition` mengubah entitas menjadi objek bergerak, dan CesiumJS menginterpolasi posisinya di antara sampel:

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
    OrientToVelocity = true,                                   // moncong mengikuti lintasan
    Availability = new TimeInterval(t0, t0.AddMinutes(12)),
    Model = new ModelGraphics { Uri = "https://example.com/models/aircraft.glb", MinimumPixelSize = 40 },
    Path = new PathGraphics { TrailTime = 600, LeadTime = 0, Width = 3, Material = CesiumMaterial.Glow(CesiumColor.Cyan) },
});
await cesium.Camera.TrackEntityAsync("flight-1");
```

- Sampel live ditambahkan dengan `Entities.AddSamplesAsync(id, new SampledPositions { Times = [...], Positions = [...] })`. Isi `Extrapolate = true` saat membuat entitas agar posisi terakhirnya tetap dipertahankan di antara update.
- `InterpolationKind.Linear` cocok untuk log GPS yang rapat, `Lagrange` menghasilkan jalur terbang yang mulus, dan `Hermite` juga tersedia.
- **Mode request-render:** selama `ShouldAnimate` bernilai `true`, Cesium.Net terus merender frame, tetapi hanya bila clock dijalankan lewat `Clock.ConfigureAsync`, `PlayAsync`, atau panggilan sejenis. Lihat [Pemecahan masalah](troubleshooting.md#globe-kosong-atau-membeku-dalam-mode-request-render).

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
