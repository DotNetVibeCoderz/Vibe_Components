[English](../en/core-concepts.md)

# Konsep inti

## Siklus hidup controller

```
host dibuat ──► Controller ada (panggilan diantrekan) ──► CesiumJS dimuat + viewer dibuat ──► Ready
                                                       └─► gagal dimuat ──► WhenReady faulted, Error dipicu
host di-dispose ──► viewer JS dihancurkan ──► panggilan berikutnya gagal
```

| Member | Arti |
|---|---|
| `IsReady` | `true` setelah viewer dibuat. |
| `WhenReady` | `Task` yang selesai saat viewer siap, atau berstatus faulted jika pemuatan gagal. |
| `event Ready` | Dipicu sekali, saat siap. Blazor juga menyediakan `OnReady`; Avalonia juga menyediakan `CesiumView.Ready`. |

Di Blazor, controller didapat dari `CesiumViewer.Controller` / `OnReady`. Di Avalonia, dari `CesiumView.Controller` / `Ready`. Kode C# Anda tidak bergantung pada host. Tulis logika peta terhadap `CesiumController` lalu pakai bersama untuk web dan desktop, seperti yang dilakukan galeri.

**Panggilan sebelum viewer siap akan diantrekan.** Setiap panggilan menunggu `WhenReady` lalu dijalankan. Jika pemuatan gagal, semua panggilan yang menunggu melempar exception pemuatan tersebut. Overload yang menerima `CancellationToken` memungkinkan Anda berhenti menunggu.

## Model async

- Semua operasi viewer bersifat asinkron dan mengembalikan `ValueTask` atau `ValueTask<T>`. Await setiap panggilan tepat satu kali, dan jangan simpan `ValueTask` untuk di-await belakangan. Pakai `.AsTask()` jika butuh `Task`.
- Panggilan selesai saat JavaScript selesai menjalankan operasinya. Ada dua yang menunggu lebih lama: `Camera.FlyToAsync`, `FlyToRectangleAsync`, dan `FlyToObjectAsync` selesai saat **penerbangan berakhir atau dibatalkan**, sedangkan `Drawing.DrawAsync` selesai saat **pengguna menyelesaikan atau membatalkan** gambar.
- Await panggilan secara berurutan jika urutan penting. Panggilan yang tidak di-await bisa saling menyela di sisi JavaScript.
- Untuk mengirim banyak perubahan, pakai API batch (`AddRangeAsync`, `UpdatePositionsAsync`), jangan banyak panggilan kecil. Lihat [Performa](performance.md).
- `ExecuteScriptAsync(script)` adalah jalan keluar untuk fitur CesiumJS yang belum dibungkus. Skrip berjalan dengan `viewer`, `Cesium`, dan `scene` dalam scope, lalu mengembalikan hasil berformat JSON, atau `null`:

```csharp
string? version = await cesium.ExecuteScriptAsync("Cesium.VERSION");                 // "\"1.146\""
await cesium.ExecuteScriptAsync("viewer.scene.globe.showGroundAtmosphere = false");
string? json = await cesium.ExecuteScriptAsync("viewer.scene.primitives.length");    // "7"
await cesium.RequestRenderAsync(); // diperlukan setelah skrip jika RequestRenderMode aktif
```

Skrip pertama-tama dievaluasi sebagai ekspresi. Jika gagal di-parse, skrip dievaluasi sebagai badan fungsi (gunakan `return`). Promise akan di-await. Hasil berupa string dikembalikan dalam bentuk JSON lengkap dengan tanda kutipnya, jadi deserialisasi dengan `JsonSerializer.Deserialize<string>(json!)`.

## Null berarti "tidak berubah"

Kelas opsi memakai properti nullable. **Properti `null` tidak diserialisasi**, karena JSON hasil source generator memakai `WhenWritingNull`. Akibatnya ada dua:

1. **Saat pembuatan**, properti yang tidak diisi memakai default CesiumJS, atau default Cesium.Net jika disebutkan (misalnya font label atau ukuran titik).
2. **Saat pembaruan**, properti yang tidak diisi membiarkan nilai sekarang apa adanya. API pembaruannya:

| API | Tipe pembaruan parsial |
|---|---|
| `Scene.ConfigureAsync` | `SceneSettings` |
| `Clock.ConfigureAsync` | `ClockOptions` |
| `Imagery.UpdateAsync` | `ImageryLayerUpdate` |
| `Tilesets.UpdateAsync` | `TilesetOptions` (hanya `Show`, `MaximumScreenSpaceError`, `DebugShowBoundingVolume`, `Style`, `PointCloudShading`, `HeightOffset`, `ClippingPolygons`/`ClipInverse`, dan `CustomShader` yang diterapkan) |
| `Camera.ConfigureControllerAsync` | `CameraControllerOptions` |
| `Entities.UpdateAsync` | `EntityOptions` |

`Entities.UpdateAsync` bekerja per **properti**. Objek grafis yang tidak null (misalnya `Point`, `Label`, atau `Polygon`) **menggantikan seluruh** grafis sejenis sebelumnya pada entitas itu, jadi sebutkan ulang semua sub-properti yang ingin dipertahankan. Grafis yang null tidak disentuh.

```csharp
// Ubah warna titik saja; Label, Position, dll. tetap seperti semula.
await cesium.Entities.UpdateAsync("truck-7", new EntityOptions
{
    Point = new PointGraphics { PixelSize = 12, Color = CesiumColor.Red, OutlineColor = CesiumColor.White, OutlineWidth = 2 },
});
```

## Event

Setiap event adalah `event` .NET pada `CesiumController`. Sisi JavaScript hanya meneruskan event yang diaktifkan di `CesiumViewerOptions.Events` (sebuah `CesiumEventOptions`). Anda bisa mengubahnya nanti dengan `Scene.ConfigureEventsAsync`.

| Event | Argumen | Flag `CesiumEventOptions` (default) | Catatan |
|---|---|---|---|
| `Ready` | — | selalu | Dipicu sekali. |
| `LeftClick` | `PickResult` | `Click` (**aktif**) | Tidak dipicu selama menggambar atau mengedit. |
| `RightClick` | `PickResult` | `RightClick` (nonaktif) | |
| `DoubleClick` | `PickResult` | `DoubleClick` (nonaktif) | |
| `MouseMove` | `PickResult` | `MouseMove` (nonaktif) | Di-throttle sesuai `MouseMoveThrottleMs` (100). |
| `CameraChanged` | `CameraState` | `CameraChanged` (**aktif**) | Saat kamera berhenti bergerak. |
| `SelectedEntityChanged` | `string?` | `SelectedEntityChanged` (**aktif**) | Id entitas atau `null`. |
| `ClockTick` | `ClockState` | `ClockTick` (nonaktif) | Di-throttle sesuai `ClockTickThrottleMs` (500). |
| `DrawCompleted` | `DrawResult` | selalu | Lihat [Menggambar](drawing-and-measuring.md). |
| `DrawCancelled` | — | selalu | |
| `EntityEdited` | `EntityEditedResult` | selalu | Setelah setiap seret verteks. |
| `LoadingProgress` | `int` | `LoadingProgress` (nonaktif) | Jumlah tile **globe** yang tertunda (0 = idle). 3D Tiles tidak dihitung. |
| `Error` | `CesiumError` | selalu | Kegagalan muat, error render, dan lainnya; lihat di bawah. |

```csharp
await cesium.Scene.ConfigureEventsAsync(new CesiumEventOptions
{
    Click = true, MouseMove = true, MouseMoveThrottleMs = 50, CameraChanged = false,
});
```

`ConfigureEventsAsync` mengirim **seluruh** objek `CesiumEventOptions`, karena propertinya non-nullable dan punya default. Isi setiap flag yang Anda inginkan.

**Threading.** Event dipicu di konteks UI/render milik host: synchronization context renderer Blazor, atau UI thread Avalonia. Di Blazor, utamakan callback `On…` milik komponen. Callback itu berupa `EventCallback` dan memicu re-render. Event mentah dari `Controller` tidak memicu re-render, jadi panggil `InvokeAsync(StateHasChanged)` sendiri.

### `PickResult`

| Properti | Arti |
|---|---|
| `Kind` | `None`, `Globe`, `Entity`, `TileFeature`, `Primitive` |
| `Screen` | Posisi layar dalam piksel CSS |
| `Position` | Posisi dunia di bawah kursor (3D Tiles, terrain, atau elipsoid), atau `null` jika mengarah ke angkasa |
| `EntityId`, `EntityName` | Untuk `Entity` |
| `Properties` | Properti entitas, properti fitur 3D Tiles, atau `{ "index": "…" }` untuk point primitive |
| `OwnerId` | Id tileset, data source, atau point collection pemilik objek yang di-pick |

## Id

| Objek | Id |
|---|---|
| Entitas | `EntityOptions.Id` milik Anda, atau GUID buatan CesiumJS. Entitas di dalam data source memakai id dari berkasnya. |
| Lapisan imagery dasar | `"base"` |
| Lapisan imagery lain | `layer-N` |
| Tileset | `tileset-N` |
| Data source | `ds-N` |
| Point collection | `points-N` |
| Bentuk hasil gambar | `draw-N` |

`Camera.FlyToObjectAsync(id)` menerima id entitas, data source, tileset, atau point collection apa pun. Metode `Entities` (`UpdateAsync`, `SetVisibleAsync`, `SelectAsync`, `GetPositionAsync`, `UpdatePositionsAsync`) juga menemukan entitas di dalam data source yang dimuat. `RemoveAsync` juga bisa menghapus entitas data source; hanya `GetIdsAsync` yang terbatas pada koleksi entitas milik viewer.

## Koordinat dan satuan

| Besaran | Satuan / konvensi |
|---|---|
| Bujur, lintang | **Derajat**, WGS84. **Bujur di depan**: `new GeoPoint(lon, lat, height)`. |
| Tinggi | **Meter di atas elipsoid WGS84**, bukan di atas permukaan laut rata-rata. Pakai `HeightReference.ClampToGround` / `RelativeToGround` untuk bekerja relatif terhadap terrain. |
| Jarak, radius, lebar | Meter (`Width` polyline dan `PixelSize` titik dalam piksel) |
| Heading | Derajat, **searah jarum jam dari utara** (0 = U, 90 = T) |
| Pitch | Derajat, naik dari horizon; −90 menghadap lurus ke bawah |
| Roll | Derajat |
| `Rotation` billboard, `Heading` pada update posisi | Derajat searah jarum jam |
| `EllipseGraphics.Rotation` | Derajat **searah jarum jam** dari utara (sama seperti rotasi billboard) |
| Sudut clock elipsoid | Derajat dari **timur, berlawanan arah jarum jam** (konvensi CesiumJS) |
| Posisi layar | Piksel CSS; `x` ke kanan, `y` ke bawah |
| Waktu | `DateTimeOffset` (dikirim sebagai ISO 8601) |
| Luas | m² |

`GeoPoint` diserialisasi sebagai array ringkas `[lon, lat, h]`. `Cartographic` menyimpan radian, seperti di CesiumJS. Nilai ECEF dan ENU berupa `Cartesian3` dalam meter. Lihat [Geodesi & koordinat](geodesy-and-coordinates.md).

## Warna dan material

`CesiumColor` adalah struct RGBA dengan komponen 0 sampai 1. Nilainya dikirim ke JavaScript sebagai `#RRGGBBAA`.

```csharp
CesiumColor a = "#1F4FE0";                    // implicit dari hex (#RGB, #RGBA, #RRGGBB, #RRGGBBAA)
CesiumColor b = CesiumColor.FromBytes(255, 149, 0);
CesiumColor c = CesiumColor.FromHsl(0.55, 0.8, 0.5);
CesiumColor d = CesiumColor.Cyan.WithAlpha(0.3f);
CesiumColor e = CesiumColor.Lerp(CesiumColor.Green, CesiumColor.Red, 0.5f);
```

Warna bernama adalah palet pilihan, **bukan** nama warna CSS. Contohnya `Red` adalah `#FF3B30` dan `Blue` adalah `#1F4FE0`. Daftar lengkapnya `Transparent`, `White`, `Black`, `Red`, `Orange`, `Yellow`, `Lime`, `Green`, `Teal`, `Cyan`, `Blue`, `Indigo`, `Purple`, `Magenta`, dan `Gray`.

`CesiumMaterial` mendeskripsikan permukaan dan garis. `CesiumColor` dikonversi implisit menjadi material warna solid. String hex tidak bisa langsung berantai menjadi material, jadi tulis `CesiumColor.Red`, bukan `"#FF0000"`.

| Factory | Dipakai pada |
|---|---|
| `Solid(color)` | apa saja |
| `Glow(color, glowPower = 0.25, taperPower = 1)` | polyline, path |
| `Dash(color, gapColor = null, dashLength = 16)` | polyline |
| `Arrow(color)` | polyline |
| `Outline(color, outlineColor, outlineWidth = 2)` | polyline |
| `Stripe(even, odd, repeat = 8, horizontal = true)` | permukaan |
| `Grid(color, cellAlpha = 0.1, lineCount = 8, lineThickness = 1)` | permukaan |
| `Checkerboard(even, odd, repeat = 4)` | permukaan |
| `FromImage(url, repeat = 1, transparent = true)` | permukaan |

## Penanganan error

| Situasi | Yang Anda dapatkan |
|---|---|
| Panggilan JavaScript melempar error (URL salah, id tidak dikenal, shader tidak valid…) | Panggilan yang di-await melempar **`CesiumInteropException`**. `Method` berisi nama metode bridge, `JsMessage` berisi pesan JavaScript. |
| Panggilan yang seharusnya mengembalikan id tidak menerima id | `CesiumInteropException("…", "No id returned.")` |
| CesiumJS gagal dimuat | `WhenReady` faulted. Panggilan yang diantrekan maupun berikutnya melempar ulang error itu. `Error` dipicu dengan `Source = "host"`. Blazor menampilkan `ErrorContent`; Avalonia memicu `LoadFailed`. |
| Error render di CesiumJS | Event `Error`, `Source = "render"`. CesiumJS berhenti me-render setelah error render. |
| Error halaman yang tidak tertangkap (halaman host Avalonia) | Event `Error`, `Source = "window"` |

Perbedaan antar-host:

- **Blazor**: `CesiumInteropException.Method` berisi nama metode bridge, misalnya `entities.update`. Panggilan yang gagal **tidak** memicu `Error`.
- **Avalonia**: `CesiumInteropException.Method` berisi nama metode yang gagal. Event `Error` (`CesiumError.Source` = metode) **juga** dipicu untuk setiap panggilan gagal.

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
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
