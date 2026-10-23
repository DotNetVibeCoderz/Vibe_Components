[English](../en/entities.md)

# Entitas

Entitas adalah satu objek di globe. Ia punya posisi (statis atau time-sampled) dan kombinasi grafik apa pun. Semua cuplikan kode berasumsi ada `CesiumController cesium` dan `using CesiumNet;`.

![Ragam entitas](../images/gallery-entities.png)

## Menambah, mengubah, menghapus

```csharp
string id = await cesium.Entities.AddAsync(new EntityOptions
{
    Id = "hq",                                   // opsional; dibuat otomatis bila dikosongkan
    Name = "Headquarters",
    Description = "<b>Open</b> 08:00–17:00",     // HTML yang tampil di info box
    Position = new GeoPoint(106.8456, -6.2088),
    Point = new PointGraphics { PixelSize = 12, Color = CesiumColor.Yellow, OutlineColor = CesiumColor.Black, OutlineWidth = 2 },
    Properties = new() { ["floor"] = "12", ["owner"] = "ACME" },   // dikembalikan di PickResult.Properties
});

await cesium.Entities.UpdateAsync(id, new EntityOptions { Position = new GeoPoint(106.85, -6.21) });
await cesium.Entities.SetVisibleAsync(id, false);
await cesium.Entities.SelectAsync(id);          // membuka info box
bool removed = await cesium.Entities.RemoveAsync(id);
await cesium.Entities.ClearAsync();              // semua isi koleksi entitas viewer
```

Untuk menambah banyak entitas, pakai **`AddRangeAsync`**. Metode ini hanya butuh satu panggilan interop, dan event koleksi ditangguhkan selama proses:

```csharp
var batch = Enumerable.Range(0, 500).Select(i => new EntityOptions
{
    Id = $"site-{i}",
    Position = new GeoPoint(95 + Random.Shared.NextDouble() * 46, -10 + Random.Shared.NextDouble() * 15),
    Point = new PointGraphics { PixelSize = 6, Color = CesiumColor.Cyan },
}).ToList();
string[] ids = await cesium.Entities.AddRangeAsync(batch);
```

Untuk ribuan titik polos, gunakan `Primitives.AddPointsAsync`. Lihat [3D Tiles & data](3d-tiles-and-data.md#100rb-titik-dengan-primitive).

## Titik, pin, ikon, label

```csharp
// Pin peta yang dibuat oleh PinBuilder CesiumJS (warna + teks atau ikon Maki), otomatis berjangkar di bawah.
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = new GeoPoint(106.8272, -6.1754),
    Billboard = new BillboardGraphics { PinColor = CesiumColor.Red, PinIcon = "hospital", PinSize = 48, HeightReference = HeightReference.ClampToGround },
});

// Ikon dari URL gambar atau data URL.
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = new GeoPoint(106.80, -6.20),
    Billboard = new BillboardGraphics { Image = "https://example.com/icons/drone.png", Scale = 0.5, Rotation = 45 }, // derajat searah jarum jam
});

// Label berlatar, hanya tampil dalam radius 50 km dan selalu digambar paling atas.
await cesium.Entities.AddAsync(new EntityOptions
{
    Position = new GeoPoint(106.8456, -6.2088, 200),
    Label = new LabelGraphics
    {
        Text = "Jakarta Selatan",
        Font = "600 14px \"IBM Plex Sans\", sans-serif",
        ShowBackground = true,
        BackgroundColor = "#15212ACC",
        PixelOffset = new Cartesian2(0, -24),               // y negatif = ke atas
        DistanceDisplayCondition = new DistanceDisplayCondition(0, 50_000),
        ScaleByDistance = new NearFarScalar(1_000, 1.0, 50_000, 0.5),
        AlwaysOnTop = true,
    },
});
```

`AlwaysOnTop` menonaktifkan depth test, sehingga terrain dan 3D Tiles tidak pernah menutupi grafik tersebut. Pin di-cache per kombinasi warna, teks, ikon, dan ukuran, jadi ribuan pin yang sama hanya memerlukan satu gambar.

## Garis dan permukaan

```csharp
var a = new GeoPoint(106.70, -6.10);
var b = new GeoPoint(106.95, -6.30);

await cesium.Entities.AddRangeAsync(
[
    new() { Polyline = new PolylineGraphics { Positions = [a, b], Width = 8, Material = CesiumMaterial.Glow(CesiumColor.Cyan), ClampToGround = true } },
    new() { Polyline = new PolylineGraphics { Positions = [a, b], Width = 4, Material = CesiumMaterial.Dash(CesiumColor.Orange), Geodesic = false } },
    new()
    {
        Polygon = new PolygonGraphics
        {
            Positions = [new(106.80, -6.15), new(106.86, -6.15), new(106.86, -6.20), new(106.80, -6.20)],
            Holes = [[new(106.82, -6.17), new(106.84, -6.17), new(106.84, -6.18)]],
            Material = CesiumColor.Blue.WithAlpha(0.4f),
            Outline = true, OutlineColor = CesiumColor.White,
        },
    },
    new() { Rectangle = new RectangleGraphics { Coordinates = new GeoRectangle(106.9, -6.3, 107.0, -6.2), Material = CesiumMaterial.Grid(CesiumColor.Yellow) } },
    new() { Position = new GeoPoint(106.75, -6.25), Ellipse = new EllipseGraphics { SemiMajorAxis = 3_000, SemiMinorAxis = 1_500, Rotation = 30, Material = CesiumMaterial.Stripe(CesiumColor.Green, CesiumColor.White) } },
    new() { Corridor = new CorridorGraphics { Positions = [a, new(106.8, -6.3), b], Width = 400, Material = CesiumColor.Teal.WithAlpha(0.6f) } },
    new() { Wall = new WallGraphics { Positions = [new(106.7, -6.3, 800), new(106.8, -6.35, 800)], Material = CesiumColor.Magenta.WithAlpha(0.5f) } },
]);
```

- Polygon tanpa ketinggian dengan `ClassifyGround = true` akan menempel di permukaan terrain **dan** 3D Tiles.
- `EllipseGraphics.Rotation` dalam derajat searah jarum jam dari utara, sama seperti rotasi billboard.
- `Holes` pada polygon adalah ring bagian dalam.

## Volume

```csharp
var p = new GeoPoint(106.8456, -6.2088);
await cesium.Entities.AddRangeAsync(
[
    // Footprint terekstrusi (mis. model massa bangunan).
    new() { Polygon = new PolygonGraphics { Positions = Geodesy.Circle(p, 120, 6), ExtrudedHeight = 160, Material = CesiumColor.Indigo, Shadows = ShadowMode.Enabled } },
    new() { Position = p.WithHeight(250), Box = new BoxGraphics { Dimensions = new Cartesian3(200, 200, 500), Material = CesiumColor.Red.WithAlpha(0.7f), Outline = true } },
    new() { Position = p.WithHeight(400), Cylinder = new CylinderGraphics { Length = 800, TopRadius = 0, BottomRadius = 300, Material = CesiumColor.Teal } }, // kerucut
    new() { Position = p, Ellipsoid = EllipsoidGraphics.Dome(5_000, outline: CesiumColor.Cyan) },                                                   // setengah bola
]);
```

Box, cylinder, dan ellipsoid berpusat di `Position`. Agar tegak di atas tanah, naikkan posisinya setengah dari tingginya. Untuk irisan sensor, lihat [Dirgantara & pertahanan](aerospace-and-defense.md).

## Model 3D (glTF / GLB)

```csharp
await cesium.Entities.AddAsync(new EntityOptions
{
    Id = "truck",
    Position = new GeoPoint(106.8272, -6.1754),
    Orientation = new HeadingPitchRoll(Heading: 135),       // heading kompas, derajat
    Model = new ModelGraphics
    {
        Uri = "https://example.com/models/truck.glb",
        MinimumPixelSize = 48, MaximumScale = 200,
        RunAnimations = true,
        SilhouetteColor = CesiumColor.Yellow, SilhouetteSize = 2,
        HeightReference = HeightReference.ClampToGround,
        Shadows = ShadowMode.Enabled,
    },
});
```

Model diharapkan menghadap **+X** dalam kerangka lokalnya, sesuai konvensi glTF yang dipakai contoh-contoh CesiumJS. Heading kompas dikonversi secara otomatis. Di Avalonia, sajikan model lokal lewat [`CesiumLocalServer.MapFolder`](avalonia.md#cesiumlocalserver-dan-mapfolder).

## Entitas dinamis

| Kebutuhan | API |
|---|---|
| Mengubah properti apa pun | `UpdateAsync(id, changes)` |
| Memindahkan banyak entitas sesering mungkin (feed live) | `UpdatePositionsAsync([...])` atau [`RealtimeEntityFeed`](realtime-tracking.md) |
| Memutar model/billboard sambil bergerak | `EntityPositionUpdate.Heading` |
| Jejak terekam atau simulasi | `SampledPosition` + `Path` + clock, lihat [Kamera & waktu](camera-and-time.md#track-time-dynamic) |
| Menambah sampel ke jejak | `AddSamplesAsync(id, samples)` |
| Tampil hanya dalam jendela waktu tertentu | `Availability = new TimeInterval(start, stop)` |
| Tampil/sembunyi bersama | `ParentId` |

```csharp
await cesium.Entities.UpdatePositionsAsync(
[
    new EntityPositionUpdate("truck", new GeoPoint(106.8300, -6.1760), Heading: 140),
    new EntityPositionUpdate("bus-12", new GeoPoint(106.8100, -6.2000)),
]);
GeoPoint? where = await cesium.Entities.GetPositionAsync("truck");
```

`UpdatePositionsAsync` mengganti posisi sampled menjadi posisi konstan dan melewati id yang belum ada, jadi buat entitasnya terlebih dahulu.

## Picking

```csharp
cesium.LeftClick += async (_, pick) =>
{
    if (pick.Kind == PickKind.Entity && pick.Properties?.TryGetValue("floor", out var floor) == true)
        Console.WriteLine($"{pick.EntityName} on floor {floor}");
    else if (pick.Kind == PickKind.Globe && pick.Position is { } pos)
        await cesium.Entities.AddAsync(new EntityOptions { Position = pos, Point = new PointGraphics { Color = CesiumColor.Lime } });
};
cesium.SelectedEntityChanged += (_, id) => Console.WriteLine(id is null ? "Deselected" : $"Selected {id}");
```

Untuk hit test layar sesuai kebutuhan, panggil `await cesium.Scene.PickAsync(x, y)`.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
