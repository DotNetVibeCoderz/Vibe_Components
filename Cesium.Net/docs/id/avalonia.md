[English](../en/avalonia.md)

# Avalonia

Paket `Cesium.Net.Avalonia`, namespace `CesiumNet.Avalonia`. Paket ini menargetkan Avalonia 12 dan memakai paket resmi `Avalonia.Controls.WebView`.

![Galeri Avalonia](../images/avalonia-gallery.png)

## Cara kerja

```
CesiumView (Decorator)
 └─ NativeWebView ── navigates to ──► http://localhost:{port}/   (CesiumLocalServer.Shared)
        │                                 ├─ /               host page
        │                                 ├─ /cesium-net.js  JS bridge (embedded in Cesium.Net)
        │                                 └─ /{name}/…       folders you map with MapFolder
        ├─ C# → JS: InvokeScript("CesiumNet.dispatchFromHost(viewerId, callId, method, args)")
        └─ JS → C#: postMessage {type: loaded | created | failed | event | result}
```

| Platform | Engine |
|---|---|
| Windows | WebView2 (Edge/Chromium) |
| macOS | WKWebView |
| Linux | WebKitGTK |

Server loopback memberi halaman origin `http://localhost` yang sungguhan. Dengan begitu web worker CesiumJS, `fetch`, dan URL relatif berperilaku sama seperti di halaman web biasa.

## `CesiumView`

| Member | Deskripsi |
|---|---|
| `Controller` | `CesiumController`-nya. Objek ini sudah ada sejak kontrol dibuat, dan pemanggilan diantrekan sampai viewer siap. |
| `Options` | `CesiumViewerOptions`. Atur **sebelum kontrol dipasang** ke window. Opsi diserialisasi saat halaman host melaporkan `loaded`. |
| `IsReady` | `true` setelah CesiumJS selesai dimuat. |
| `WebView` | `NativeWebView` di baliknya, untuk dev tools, cookie, atau pencetakan. |
| `event Ready` | `EventHandler<CesiumReadyEventArgs>`, dipicu sekali. `e.Controller` adalah objek yang sama dengan `Controller`. |
| `event LoadFailed` | `EventHandler<CesiumError>`, dipicu jika CesiumJS gagal dimuat (`Source = "load"`), misalnya saat CDN tidak bisa dijangkau. |

Event lainnya (`LeftClick`, `CameraChanged`, `DrawCompleted`, `Error`, …) ada di `Controller`. Lihat [Konsep inti](core-concepts.md#event).

```csharp
public MainWindow()
{
    InitializeComponent();
    Globe.Options = new CesiumViewerOptions
    {
        IonAccessToken = Environment.GetEnvironmentVariable("CESIUM_ION_TOKEN"),
        BaseLayer = ImageryLayerOptions.Dark(),
        RequestRenderMode = true,
        Events = new CesiumEventOptions { Click = true, MouseMove = true, MouseMoveThrottleMs = 150 },
    };
    Globe.Controller.MouseMove += (_, p) => Coords.Text = p.Position?.ToString() ?? "—";
    Globe.Ready += async (_, e) => await e.Controller.Camera.FlyToAsync(106.8272, -6.1754, 25_000);
    Globe.LoadFailed += (_, err) => Coords.Text = "Load failed: " + err.Message;
}
```

`Ready` hanya dipicu jika WebView-nya sendiri berhasil berjalan. Jika WebView platform tidak tersedia, misalnya WebView2 runtime belum terpasang, `Ready` maupun `LoadFailed` tidak akan dipicu. Lindungi dengan timeout:

```csharp
try { await Globe.Controller.WhenReady.WaitAsync(TimeSpan.FromSeconds(30)); }
catch (TimeoutException) { Status.Text = "The map could not start. Is the WebView runtime installed?"; }
```

## `CesiumLocalServer` dan `MapFolder`

`CesiumLocalServer.Shared` adalah `HttpListener` yang hanya mendengarkan loopback pada port bebas, satu untuk seluruh proses. Server ini dibuat saat pertama kali dipakai dan digunakan bersama oleh semua `CesiumView`.

```csharp
// Sajikan file lokal ke WebView:
string models = CesiumLocalServer.Shared.MapFolder("models", Path.Combine(AppContext.BaseDirectory, "models"));
// models == "http://localhost:51234/models/"

await cesium.Entities.AddAsync(new EntityOptions
{
    Position = new GeoPoint(106.8272, -6.1754),
    Model = new ModelGraphics { Uri = models + "truck.glb", MinimumPixelSize = 48 },
});

string data = CesiumLocalServer.Shared.MapFolder("data", @"C:\gis\exports");
await cesium.DataSources.LoadAsync(DataSourceOptions.GeoJsonUrl(data + "districts.geojson"));
```

- `name` harus berupa **satu segmen path** (`"models"`, bukan `"assets/models"`). Request dirutekan berdasarkan segmen pertamanya.
- Method ini melempar `DirectoryNotFoundException` jika folder tidak ada.
- Path traversal ke luar folder yang dipetakan menghasilkan 404.
- Tipe MIME diatur untuk HTML/JS/CSS, JSON/GeoJSON/TopoJSON/CZML, glTF/GLB, payload 3D Tiles (`.b3dm`, `.i3dm`, `.pnts`, `.cmpt`, `.subtree`), `.terrain`, KML/KMZ, GPX, PNG/JPEG/WebP/SVG/KTX2, dan WASM. Selain itu dikirim sebagai `application/octet-stream`.
- Header `Content-Encoding` tidak pernah dikirim. Sajikan file yang tidak dikompresi; tile quantized-mesh yang sudah di-gzip tidak akan bisa didekode.
- File yang dipetakan dikirim dengan `Cache-Control: max-age=3600`.

### CesiumJS offline

Sertakan folder `Build/Cesium` dari paket npm `cesium` (versi 1.146.0) bersama aplikasi Anda, lalu arahkan `CesiumBaseUrl` ke folder itu:

```csharp
var cesiumUrl = CesiumLocalServer.Shared.MapFolder("cesium", Path.Combine(AppContext.BaseDirectory, "cesium"));
Globe.Options = new CesiumViewerOptions
{
    CesiumBaseUrl = cesiumUrl,
    BaseLayer = new ImageryLayerOptions { Provider = ImageryProviderKind.Tms, Url = cesiumUrl + "Assets/Textures/NaturalEarthII" },
};
```

```xml
<!-- .csproj: salin build CesiumJS ke samping executable -->
<ItemGroup>
  <None Include="cesium\**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Selengkapnya di [Offline & Cesium ion](offline-and-ion.md).

## Catatan platform

### Windows

- **Application manifest yang mendeklarasikan dukungan Windows 10/11 wajib ada.** `NativeWebView` meng-host child HWND, dan Avalonia hanya mengizinkannya jika aplikasi mendeklarasikan OS yang didukung. Tanpa manifest, aplikasi crash dengan `InvalidOperationException: Unable to create child window for native control host. Application manifest with supported OS list might be required.` Tambahkan `<ApplicationManifest>app.manifest</ApplicationManifest>` ke `.csproj` dan sertakan:

  ```xml
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /> <!-- Windows 10 dan 11 -->
    </application>
  </compatibility>
  ```

  File lengkapnya ada di `samples/Cesium.Net.Gallery.Avalonia/app.manifest`; lihat juga [Memulai](getting-started.md#windows-application-manifest-wajib).
- Kontrol ini membutuhkan **WebView2 Runtime**. Runtime ini sudah terpasang di Windows 11 dan di sebagian besar Windows 10 yang mutakhir. Untuk mesin lama, sertakan Evergreen bootstrapper atau runtime Fixed Version di installer Anda.

### Linux (WebKitGTK)

- Pasang runtime WebKitGTK yang dibutuhkan `Avalonia.Controls.WebView`. Di Debian/Ubuntu biasanya `libwebkit2gtk-4.1-0`; periksa dokumentasi Avalonia WebView untuk versi Anda.
- WebGL di WebKitGTK bergantung pada driver GPU. Di VM atau dengan software rendering, CesiumJS bisa lambat atau melaporkan WebGL tidak tersedia; lihat [Pemecahan masalah](troubleshooting.md#webgl-tidak-tersedia).
- Wayland dan X11 sama-sama berfungsi sejauh didukung WebView Avalonia. Jika view tetap kosong di Wayland, coba `GDK_BACKEND=x11`.

### macOS (WKWebView)

- WKWebView sudah menjadi bagian dari OS, jadi tidak ada yang perlu dipasang.
- Halaman disajikan lewat `http://localhost` biasa. App Transport Security umumnya mengizinkan loopback. Jika `Info.plist` Anda memperketat ATS, tambahkan `NSAppTransportSecurity` → `NSAllowsLocalNetworking = true`.
- Aplikasi sandbox, seperti build Mac App Store, membutuhkan `com.apple.security.network.client` untuk menjangkau CDN dan `com.apple.security.network.server` untuk listener loopback.

## Airspace: overlay diletakkan di samping view

Native WebView adalah permukaan OS terpisah yang dikomposisikan **di atas** visual tree Avalonia. Konten Avalonia yang tumpang tindih dengan `CesiumView` akan tersembunyi di balik peta, entah berasal dari z-index `Panel`, adorner, atau HUD semi-transparan.

Sebagai gantinya:

- Letakkan toolbar, legenda, dan tampilan nilai **di samping** view (`DockPanel`, kolom atau baris `Grid`).
- Bangun HUD di dalam peta dengan Cesium sendiri, memakai label, billboard, dan posisi layar lewat `Scene.ToScreenAsync`.
- Gunakan jendela yang digambar terpisah oleh OS. Di desktop, popup dan flyout Avalonia secara default memakai jendela native sendiri, sehingga bisa tampil di atas view.

```xml
<Grid ColumnDefinitions="260,*" RowDefinitions="*,28">
  <StackPanel Grid.Column="0" Grid.RowSpan="2"> <!-- alat --> </StackPanel>
  <cesium:CesiumView Grid.Column="1" Grid.Row="0" x:Name="Globe" />
  <TextBlock Grid.Column="1" Grid.Row="1" x:Name="Coords" />   <!-- status bar di bawah, bukan di atas -->
</Grid>
```

## Threading

- `Ready`, `LoadFailed`, dan semua event `Controller` dipicu di **thread UI Avalonia**, jadi handler boleh langsung mengakses kontrol.
- Method `Controller` boleh dipanggil dari **thread mana pun**. Transport me-marshal `InvokeScript` ke thread UI dan menyelesaikan task yang dikembalikan saat hasil yang di-post tiba.
- `RealtimeEntityFeed` melakukan flush dari timer di thread pool, dan itu aman. Event `FlushFailed`-nya dipicu di thread pool tersebut, jadi marshal dengan `Dispatcher.UIThread.Post` sebelum menyentuh UI.
- Hindari `.Result` atau `.Wait()` pada pemanggilan controller dari thread UI. Hasilnya dikirim lewat pesan di thread UI, jadi memblokir thread UI akan menyebabkan deadlock.

## Catatan siklus hidup

- Deklarasikan `CesiumView` di XAML, atau buat lewat kode **setelah window dimuat** (di event `Loaded`). Membuat native control host sebelum window ada akan gagal.
- Setiap `CesiumView` meng-host WebView, halaman, dan viewer CesiumJS-nya sendiri. Beberapa view bisa berjalan bersamaan.
- Jika halaman dimuat ulang, misalnya dari dev tools, state viewer hilang dan `Ready` **tidak** dipicu lagi. Buat ulang kontrolnya.
- Dev tools hanya aktif jika `Cesium.Net.Avalonia` sendiri dikompilasi dalam mode Debug. Untuk mengaktifkannya di build Anda, subscribe sebelum view dipasang: `Globe.WebView.EnvironmentRequested += (_, e) => e.EnableDevTools = true;`.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
