[English](../en/drawing-and-measuring.md)

# Menggambar & mengukur

`cesium.Drawing` menyediakan sketsa interaktif di atas globe, terrain, dan 3D Tiles, label pengukuran langsung, serta pengeditan vertex.

## Menggambar bentuk

```csharp
DrawResult? r = await cesium.Drawing.DrawAsync(new DrawOptions
{
    Mode = DrawMode.Polygon,
    Color = CesiumColor.Yellow,
    FillColor = CesiumColor.Yellow.WithAlpha(0.25f),
    Width = 3,
});

if (r is null) return;                         // pengguna menekan Esc, atau panggilan dibatalkan
Console.WriteLine($"{r.Positions.Count} vertices, perimeter {Geodesy.FormatDistance(Geodesy.Perimeter(r.Positions))}, area {Geodesy.FormatArea(r.Area)}");
Console.WriteLine($"Kept as entity {r.EntityId}");
```

| Input | Aksi |
|---|---|
| Klik kiri | Menambah vertex (mode Point langsung selesai; Rectangle dan Circle selesai pada klik ke-2) |
| Gerakan mouse | Pratinjau rubber-band |
| Klik ganda, klik kanan, atau Enter | Selesai |
| Esc | Batal → `DrawAsync` mengembalikan `null`, `DrawCancelled` dimunculkan |

| Mode | Klik | Hasil |
|---|---|---|
| `Point` | 1 | `Positions[0]` |
| `Polyline` | ≥ 2 | `Positions`, `Length` |
| `Polygon` | ≥ 3 | `Positions`, `Area` |
| `Rectangle` | 2 (sudut) | `Positions` = 2 sudut |
| `Circle` | 2 (pusat, tepi) | `Positions`, `Radius` (m), `Area` |
| `MeasureDistance` | ≥ 2 | Seperti Polyline, dengan label jarak langsung |
| `MeasureArea` | ≥ 3 | Seperti Polygon, dengan label luas langsung |

Default `DrawOptions`: `ClampToGround = true`, `KeepEntity = true`, dan `ShowMeasurementLabel = true` untuk mode measure, polyline, dan polygon. Dengan `KeepEntity = false`, bentuk dihapus setelah selesai dan `EntityId` bernilai `null`.

Selama menggambar atau mengedit, event `LeftClick`, `RightClick`, `DoubleClick`, dan `MouseMove` tidak dimunculkan. Memulai gambar baru membatalkan gambar sebelumnya tanpa event.

### Hasil pengukuran

`DrawResult.Length` adalah `Geodesy.PathLength(Positions)`, yaitu jarak Vincenty dalam meter. `DrawResult.Area` dihitung sebagai berikut:

- Polygon dan MeasureArea: `Geodesy.Area(Positions)`.
- Circle: π·r².
- Rectangle: dua sudut yang dikembalikan diperluas menjadi persegi utuh, lalu diukur dengan `Geodesy.Area`.

Label di peta memakai rumus yang sama di JavaScript: geodesic ellipsoid untuk panjang dan metode bola autalik untuk luas.

### Membatalkan dari kode

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var r = await cesium.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.MeasureDistance }, cts.Token); // null saat timeout
await cesium.Drawing.CancelAsync();   // atau batalkan secara eksplisit (mis. dari tombol toolbar)
await cesium.Drawing.ClearAsync();    // hapus semua bentuk hasil gambar beserta labelnya
```

`DrawAsync` berlangganan `DrawCompleted` dan `DrawCancelled` selama berjalan. Jalankan hanya satu `DrawAsync` dalam satu waktu.

### Gaya berbasis event

Jika lebih suka event daripada `await`, gunakan event `DrawCompleted` atau callback `OnDrawCompleted` di Blazor:

```razor
<CesiumViewer OnReady="c => _c = c" OnDrawCompleted="OnDrawn" />
<button @onclick="() => _c!.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.MeasureArea }).AsTask()">Measure area</button>

@code {
    CesiumController? _c;
    string? _result;
    void OnDrawn(DrawResult r) => _result = $"{r.Mode}: {Geodesy.FormatArea(r.Area)}";
}
```

## Mengedit vertex

```csharp
await cesium.Drawing.BeginEditAsync(r.EntityId!);   // handle kuning muncul di setiap vertex
cesium.EntityEdited += (_, e) => Console.WriteLine($"{e.EntityId}: {e.Positions.Count} vertices, area {Geodesy.FormatArea(Geodesy.Area(e.Positions))}");
// … pengguna menyeret handle …
await cesium.Drawing.EndEditAsync();               // mengembalikan geometri ke nilai statis
```

- Entitas yang bisa diedit: **polygon** (termasuk polyline outline yang dibuat saat menggambar), **polyline**, dan apa pun yang memiliki **position** (point, billboard, label, model).
- `EntityEdited` dimunculkan setiap kali seretan selesai.
- Selama menyeret, input kamera dinonaktifkan.
- Pengeditan juga berlaku untuk entitas Anda sendiri, tidak hanya hasil gambar. Memulai edit membatalkan gambar yang sedang aktif.

## Pembaruan saat runtime

Bentuk hasil gambar adalah entitas biasa dengan id `draw-N`, jadi Anda bisa mengubah gayanya:

```csharp
await cesium.Entities.UpdateAsync(r.EntityId!, new EntityOptions
{
    Polygon = new PolygonGraphics { Positions = r.Positions, Material = CesiumColor.Red.WithAlpha(0.3f) },
    Name = "Exclusion zone",
});
```

## Geofencing dengan bentuk hasil gambar

```csharp
var zone = (await cesium.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.Polygon }))!.Positions;
bool inside = Geodesy.Contains(zone, new GeoPoint(106.83, -6.18));
```

`Geodesy.Contains` adalah uji planar dalam ruang longitude/latitude. Cocok untuk poligon skala regional; hindari poligon yang melintasi antimeridian atau mencakup kutub.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
