[English](../en/performance.md)

# Performa

Cesium.Net punya dua pusat biaya: **interop** (C# ↔ JavaScript) dan **rendering** (CesiumJS/WebGL). Interop memiliki overhead tetap per panggilan. Overhead itu sekitar satu mikrodetik in-process di WebAssembly, dan satu round-trip jaringan per panggilan di Blazor Server. Biaya rendering dihitung per objek dan per frame. Setiap poin di bawah menyebutkan aturannya beserta alasannya.

## Interop

### 1. Batch: satu panggilan, bukan N

| Daripada | Gunakan | Alasan |
|---|---|---|
| `AddAsync` dalam loop | `Entities.AddRangeAsync(list)` | Satu payload dan satu round-trip. Event koleksi entitas CesiumJS ditangguhkan selama penambahan, sehingga CesiumJS memproses perubahan sekali saja. |
| `UpdateAsync(id, new { Position })` per entitas | `Entities.UpdatePositionsAsync(updates)` | Payload khusus posisi (`{id, [lon,lat,h], heading}`), dengan event koleksi ditangguhkan selama batch |
| N entitas point | `Primitives.AddPointsAsync` | Lihat §6 |

Di Blazor Server, 1.000 panggilan terpisah berarti 1.000 pesan SignalR dan 1.000 await. Satu panggilan batch berarti satu.

### 2. Gabungkan feed langsung dengan `RealtimeEntityFeed`

Sumber berfrekuensi tinggi sering mengirim lebih banyak pembaruan daripada yang bisa Anda lihat. Feed menyimpan **posisi terbaru per id** dan melakukan flush dengan laju tetap (default 10 Hz). Biaya kemudian dibatasi oleh *jumlah entitas bergerak × laju flush*, tidak bergantung pada laju pesan. Pilih interval sesuai kebutuhan mata. 100 ms terlihat halus untuk kendaraan; 250 sampai 500 ms cukup untuk kapal. Lihat [Pelacakan real-time](realtime-tracking.md).

### 3. Payload kecil sejak desain

- **JSON hasil source generator** (`CesiumJsonContext`) tidak memakai reflection dan aman untuk trimming/AOT. Paket inti ditandai `IsAotCompatible`.
- **`null` tidak pernah dikirim** (`WhenWritingNull`), jadi `EntityOptions` dengan satu graphic hanya menserialisasi graphic itu.
- **Posisi berupa array ringkas**: `GeoPoint` ditulis sebagai `[lon,lat,h]`, bukan `{"longitude":…}`. Hasilnya kira-kira setengah ukuran byte untuk payload yang padat posisi.
- **Warna** berupa string pendek `#RRGGBBAA`.
- **Array argumen** ditulis dengan `Utf8JsonWriter`/`ArrayBufferWriter` yang di-pool per thread. Buffer di atas 64 KB tidak disimpan, jadi satu batch besar tidak menahan memori.

### 4. Matikan event yang tidak dipakai

Setiap event yang diteruskan adalah satu pesan JS → .NET, ditambah render ulang di Blazor jika Anda memakai callback komponen.

```csharp
var options = new CesiumViewerOptions
{
    Events = new CesiumEventOptions
    {
        Click = true,
        CameraChanged = false,          // aktif secara default; matikan jika tidak dipakai
        SelectedEntityChanged = false,  // aktif secara default
        MouseMove = false,              // hanya jika Anda menampilkan koordinat kursor (throttle ≥ 100 ms)
        ClockTick = false,              // hanya untuk UI timeline kustom (throttle ≥ 250 ms)
        LoadingProgress = false,
    },
};
```

Anda bisa mengubahnya saat runtime dengan `Scene.ConfigureEventsAsync`, misalnya mengaktifkan `MouseMove` hanya selama alat "ukur" aktif.

### 5. Gunakan await dengan bijak

Jangan membungkus loop rapat berisi panggilan kecil dalam `Task.WhenAll`; gunakan batch. Await operasi panjang seperti tileset dan data source sekali saja, dan kerjakan hal lain selama pemuatan berlangsung.

## Rendering

### 6. 100rb+ titik: primitive, bukan entitas

`Primitives.AddPointsAsync` membangun satu `PointPrimitiveCollection`. CesiumJS menggambarnya dengan sangat sedikit draw call, dan tidak ada evaluasi properti per entitas di setiap frame. Interaksi tetap mulus dengan ratusan ribu titik. Warna per titik diparse sekali untuk setiap warna yang berbeda.

### 7. Mode request-render untuk scene yang sebagian besar statis

```csharp
new CesiumViewerOptions { RequestRenderMode = true };
```

CesiumJS kemudian hanya merender saat ada perubahan, sehingga menghemat banyak CPU, GPU, dan baterai untuk dashboard dan aplikasi desktop. Cesium.Net memanggil `requestRender()` setelah **setiap** panggilan API dan terus merender selama diperlukan:

- orbit dan putaran kamera, serta flight mode
- clock yang beranimasi dan dijalankan lewat `Clock.ConfigureAsync`/`PlayAsync`
- hujan dan salju
- custom shader dengan `AnimateTime`

`MaximumRenderTimeChange` memakai default CesiumJS (0): frame dirender setiap kali waktu simulasi maju, sehingga jam yang beranimasi (dari sumber mana pun, termasuk CZML) terus dirender dan jam yang dijeda tetap diam. Setelah `ExecuteScriptAsync`, panggil `RequestRenderAsync()` sendiri. Lihat [Pemecahan masalah](troubleshooting.md#globe-kosong-atau-membeku-dalam-mode-request-render) untuk kasus-kasus tepi.

### 8. Resolusi, anti-aliasing, dan FPS

| Pengaturan | Panduan |
|---|---|
| `ResolutionScale` | `0.75` pada laptop HiDPI atau GPU lemah merender sekitar 44 % piksel lebih sedikit. Anda bisa mengubahnya saat runtime dengan `SceneSettings.ResolutionScale`, mis. diturunkan selama animasi. |
| `MsaaSamples` | `1` mematikan MSAA dan paling murah; `4` adalah default kualitas yang baik. Gabungkan `1` dengan `Fxaa = true` untuk penghalusan yang murah. |
| `ShowFps` | Menampilkan overlay frame rate saat tuning. |

### 9. Kurangi yang digambar

- `DistanceDisplayCondition` menyembunyikan label, billboard, model, dan garis di luar jarak tertentu. Label termasuk graphic paling mahal.
- `ScaleByDistance` mengecilkan ikon yang jauh alih-alih menggambarnya dalam ukuran penuh.
- `AlwaysOnTop` murah, tetapi menghilangkan petunjuk kedalaman; gunakan hanya bila perlu.
- Lebih baik satu `Polyline` dengan banyak vertex daripada banyak polyline dua titik.
- Matikan `Shadows`, `AmbientOcclusion`, dan `Bloom` kecuali memang dibutuhkan. Semuanya adalah pass layar penuh.

### 10. 3D Tiles

| Opsi | Efek |
|---|---|
| `MaximumScreenSpaceError` | Default 16; 24 sampai 32 jauh lebih cepat dengan sedikit kehilangan detail pada model kota |
| `SkipLevelOfDetail = true` | Lebih sedikit tile perantara yang diminta untuk tileset masif |
| `DynamicScreenSpaceError = true` | Detail lebih rendah ke arah cakrawala |
| `PointCloudShading.Attenuation` | Point cloud tampak lebih baik dengan anggaran titik lebih rendah |

### 11. Terrain

Terrain `Ellipsoid` tidak memakan biaya; ArcGIS atau World Terrain melakukan streaming tile. `SampleHeightsAsync` dengan banyak titik sekaligus adalah satu permintaan batch. Panggil sekali dengan sebuah list, jangan memanggil `GetHeightAsync` dalam loop.

## Mengukur

- `CesiumEventOptions.LoadingProgress` melaporkan jumlah tile globe yang tertunda; 0 berarti diam.
- `RealtimeEntityFeed.PublishedCount` dan `PendingCount` menunjukkan throughput dan antrean.
- `ShowFps = true`, atau `SceneSettings.ShowFps`, menampilkan frame rate.
- Di Blazor Server, pantau laju pesan SignalR di dev tools browser (frame WS).

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
