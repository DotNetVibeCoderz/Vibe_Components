<p align="center"><img src="build/icon.png" width="96" alt="Logo Cesium.Net"></p>

<h1 align="center">Cesium.Net</h1>

<p align="center"><b>CesiumJS untuk .NET</b>: globe 3D, peta, terrain, 3D Tiles, data dinamis waktu, dan analisis geospasial, semuanya dikendalikan dari C#.<br>
Satu API untuk <b>Blazor</b> (web) dan <b>Avalonia UI</b> (desktop).</p>

<p align="center"><a href="README.md">English</a> · <a href="docs/id/index.md">Dokumentasi</a> · <a href="docs/id/gallery.md">Galeri</a> · <a href="PLAN.md">Rencana</a> · <a href="Progress.md">Progres</a></p>

![Cesium.Net Gallery — beranda](docs/images/gallery-home.png)

## Mengapa Cesium.Net

- **C# yang sama di mana saja.** `CesiumController` tidak bergantung pada host. Komponen Blazor dan kontrol Avalonia hanyalah host tipis, sehingga contoh yang ditulis sekali berjalan di web *dan* desktop. Galeri membuktikannya: kedua front-end memakai satu katalog contoh yang sama.
- **Cakupan CesiumJS yang luas.** Mencakup entitas, citra, terrain, 3D Tiles, data vektor, kamera, jam, menggambar dan mengukur, picking, efek, serta cuaca. Untuk hal lain tersedia jalan pintas `ExecuteScriptAsync`.
- **Matematika geospasial di .NET.** Geodesi Vincenty, konversi WGS84/ECEF/ENU/Web Mercator, luas dan uji titik-dalam-poligon, serta orbit Kepler dengan J2. Semuanya teruji unit dan bisa dipakai di sisi server.
- **Dirancang untuk throughput.** Memakai JSON hasil source generator (aman untuk trimming/AOT), posisi ringkas `[lon,lat,h]`, dan pembuatan entitas secara batch. `RealtimeEntityFeed` tanpa lock menggabungkan lonjakan data SignalR/MQTT/WebSocket, dan koleksi titik GPU sanggup menangani 100rb+ titik.
- **Tanpa akun.** Langsung memakai citra OpenStreetMap dan Esri serta terrain Esri. Tambahkan token Cesium ion untuk World Terrain, OSM Buildings, dan aset ion.

| Paket | Kegunaan |
|---|---|
| [`Cesium.Net`](src/Cesium.Net) | API inti, matematika, orbit, batching real-time, klien REST ion |
| [`Cesium.Net.Blazor`](src/Cesium.Net.Blazor) | `<CesiumViewer>` untuk Blazor Server, WebAssembly, dan Web App |
| [`Cesium.Net.Avalonia`](src/Cesium.Net.Avalonia) | `CesiumView` untuk Windows, macOS, dan Linux (WebView2 / WKWebView / WebKitGTK native) |

## Mulai cepat

### Blazor

```bash
dotnet add package Cesium.Net.Blazor
```

```razor
@using CesiumNet
@using CesiumNet.Blazor

<CesiumViewer Style="height:600px" OnReady="OnReady" OnLeftClick="OnClick" />

@code {
    async Task OnReady(CesiumController cesium)
    {
        await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Monas",
            Position = new GeoPoint(106.827153, -6.175392),
            Billboard = new BillboardGraphics { PinColor = "#1F4FE0", PinIcon = "monument" },
            Label = new LabelGraphics { Text = "Monas", PixelOffset = new Cartesian2(0, -58) },
        });
        await cesium.Camera.FlyToAsync(106.8272, -6.1880, 2_500, pitch: -40);
    }

    void OnClick(PickResult pick) => Console.WriteLine($"{pick.Kind} di {pick.Position}");
}
```

Komponen ini membutuhkan render mode interaktif (Server, WebAssembly, atau Auto). Bila perlu, atur nilai bawaan untuk seluruh aplikasi dengan `builder.Services.AddCesiumNet(o => o.IonAccessToken = "...")`.

### Avalonia

```bash
dotnet add package Cesium.Net.Avalonia
```

```xml
<Window xmlns:cesium="using:CesiumNet.Avalonia" ...>
  <cesium:CesiumView Ready="OnGlobeReady" />
</Window>
```

```csharp
private async void OnGlobeReady(object? sender, CesiumReadyEventArgs e)
{
    var cesium = e.Controller;
    await cesium.Camera.FlyToAsync(106.8272, -6.1880, 2_500, pitch: -40);
}
```

> **Windows:** sertakan `app.manifest` yang menyatakan dukungan Windows 10/11 (`<supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>`). Host WebView native membutuhkannya. Lihat [`samples/Cesium.Net.Gallery.Avalonia/app.manifest`](samples/Cesium.Net.Gallery.Avalonia/app.manifest).

## Cakupan fitur

| Area | Sorotan |
|---|---|
| Rendering | Tampilan 3D / 2D / Columbus, pencahayaan, bayangan, atmosfer, kabut, bloom, ambient occlusion, HDR, mode request-render |
| Citra | OSM, Esri satelit/jalan/topo, CARTO, Bing, WMS, WMTS, TMS, template XYZ, ion, gambar tunggal; alpha, penyesuaian warna, swipe/split |
| Terrain | Ellipsoid, Cesium World Terrain, Esri World Elevation, quantized-mesh kustom; eksagerasi, sampling ketinggian, profil, clipping |
| 3D Tiles | URL / ion / OSM Buildings / Google Photorealistic; styling deklaratif, shader GLSL kustom, shading point cloud, poligon clipping |
| Data vektor | GeoJSON, TopoJSON, KML/KMZ, CZML, GPX; ekstrusi berdasarkan properti |
| Entitas | Titik, billboard/pin (ikon Maki), label, polyline (glow/dash/arrow/outline), poligon (lubang, ekstrusi), elips, persegi, kotak, silinder, ellipsoid/volume sensor, koridor, dinding, model glTF, jejak |
| Waktu | Jam & timeline, posisi tersampel (Lagrange/Hermite), orientasi kecepatan, pembaruan posisi langsung |
| Interaksi | Picking klik / klik kanan / klik ganda / gerak mouse (entitas, fitur 3D Tiles, primitif, globe), kamera fly-to/look-at/orbit/terbang orang pertama, menggambar, mengedit, mengukur |
| Analisis (C#) | Jarak/arah/tujuan Vincenty, interpolasi geodesik, luas, keliling, centroid, titik-dalam-poligon, konversi ECEF/ENU/Web Mercator/tile XYZ/DMS, orbit Kepler, radius jejak cakupan |
| Efek | Hujan, salju, dan kabut post-processing; cahaya kamera; globe tembus pandang |

Pemetaan lengkap setiap fitur CesiumJS ada di [docs/id/feature-matrix.md](docs/id/feature-matrix.md).

## Galeri

Setiap contoh adalah satu kelas C# di [`samples/Cesium.Net.Gallery.Shared`](samples/Cesium.Net.Gallery.Shared). Kelas yang sama berjalan di kedua front-end, masing-masing lengkap dengan kode langsung, aksi, dan log peristiwa.

```bash
dotnet run --project samples/Cesium.Net.Gallery.Blazor      # http://localhost:5180
dotnet run --project samples/Cesium.Net.Gallery.Avalonia
```

| | |
|---|---|
| ![Halo globe](docs/images/gallery-hello-globe.png) | ![Entitas](docs/images/gallery-entities.png) |
| ![Terrain](docs/images/gallery-terrain.png) | ![3D Tiles](docs/images/gallery-3dtiles.png) |
| ![Satelit](docs/images/gallery-satellites.png) | ![Gambaran operasional](docs/images/gallery-cop.png) |
| ![Armada real-time](docs/images/gallery-fleet.png) | ![Galeri desktop Avalonia](docs/images/avalonia-gallery.png) |

## Cara kerja

```
 C# Anda  ──►  CesiumController (Cesium.Net)
                 │  nama metode + argumen JSON  (ICesiumTransport)
      ┌──────────┴──────────┐
 JS interop Blazor    NativeWebView Avalonia + server loopback
      └──────────┬──────────┘
          bridge cesium-net.js  ──►  CesiumJS (WebGL)
                 │  peristiwa: klik, kamera, gambar, jam, galat
                 └──────────►  event .NET / EventCallback
```

## Build & uji

```bash
dotnet build Cesium.Net.slnx
dotnet test tests/Cesium.Net.Tests
./build/pack.ps1            # paket NuGet → ./artifacts
```

## Dokumentasi

- Bahasa Indonesia: [docs/id](docs/id/index.md). Memulai, Blazor, Avalonia, konsep inti, referensi API, panduan, performa, offline & ion, pemecahan masalah.
- English: [docs/en](docs/en/index.md).

## Lisensi

MIT. Lihat [LICENSE](LICENSE). CesiumJS berlisensi Apache-2.0. Citra, terrain, dan konten 3D mengikuti ketentuan penyedianya, jadi biarkan atribusi di layar tetap terlihat.

---

<p align="center"><b>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.</b><br><i>Made by Gravicode Studios, led by Kang Fadhil.</i></p>
