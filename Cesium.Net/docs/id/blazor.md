[English](../en/blazor.md)

# Blazor

Paket `Cesium.Net.Blazor`, namespace `CesiumNet.Blazor`. Satu komponen, `CesiumViewer`, bisa dipakai di Blazor Server, Blazor WebAssembly, dan Blazor Web App (interaktivitas Server, WebAssembly, atau Auto).

## Parameter `CesiumViewer`

| Parameter | Tipe | Catatan |
|---|---|---|
| `Options` | `CesiumViewerOptions?` | Opsi pembuatan viewer. **Dibaca sekali saja, pada render pertama.** Perubahan berikutnya diabaikan. Untuk membuat ulang viewer, ganti `@key`-nya. |
| `Class` | `string?` | Kelas CSS tambahan pada `div` host. |
| `Style` | `string?` | Style inline pada `div` host, misalnya `height:600px`. |
| `ChildContent` | `RenderFragment?` | Konten overlay yang digambar di atas globe. |
| `LoadingContent` | `RenderFragment?` | Ditampilkan selama CesiumJS dimuat. |
| `ErrorContent` | `RenderFragment<string>?` | Ditampilkan jika CesiumJS gagal dimuat; context-nya berisi pesan error. |
| *(atribut lain)* | ditangkap | Atribut yang tidak cocok dengan parameter, seperti `id` atau `aria-label`, diteruskan ke `div` host. |

| Member | Catatan |
|---|---|
| `Controller` | `CesiumController`-nya. Objek ini sudah ada sejak komponen dibuat, dan pemanggilan diantrekan sampai viewer siap. Akses lewat `@ref`. |
| `IsReady` | `true` setelah CesiumJS selesai dimuat. |

## Event callback

| Callback | Payload | Dipicu saat | Membutuhkan (`Options.Events`) |
|---|---|---|---|
| `OnReady` | `CesiumController` | Viewer selesai dibuat | selalu |
| `OnLeftClick` | `PickResult` | Klik kiri (tidak saat menggambar atau mengedit) | `Click` (default aktif) |
| `OnRightClick` | `PickResult` | Klik kanan | `RightClick` |
| `OnDoubleClick` | `PickResult` | Klik ganda | `DoubleClick` |
| `OnMouseMove` | `PickResult` | Mouse bergerak, di-throttle | `MouseMove` |
| `OnCameraChanged` | `CameraState` | Kamera berhenti bergerak | `CameraChanged` (default aktif) |
| `OnSelectedEntityChanged` | `string?` | Pilihan berubah (null = tidak ada) | `SelectedEntityChanged` (default aktif) |
| `OnClockTick` | `ClockState` | Clock berdetak, di-throttle | `ClockTick` |
| `OnDrawCompleted` | `DrawResult` | Gambar interaktif selesai | selalu |
| `OnEntityEdited` | `EntityEditedResult` | Seretan vertex selesai | selalu |
| `OnError` | `CesiumError` | Gagal memuat atau error render | selalu |

`DrawCancelled` dan `LoadingProgress` tidak punya parameter callback. Subscribe langsung lewat `Controller`:

```razor
<CesiumViewer @ref="_viewer" Options="_options" OnReady="OnReady" />

@code {
    private CesiumViewer? _viewer;
    private readonly CesiumViewerOptions _options = new() { Events = new() { LoadingProgress = true } };
    private int _pendingTiles;

    private Task OnReady(CesiumController cesium)
    {
        cesium.LoadingProgress += (_, n) => { _pendingTiles = n; InvokeAsync(StateHasChanged); };
        cesium.DrawCancelled += (_, _) => Console.WriteLine("Drawing cancelled");
        return Task.CompletedTask;
    }
}
```

Callback berupa `EventCallback`, sehingga komponen induk di-render ulang setiap kali callback dipanggil. Jika `MouseMove` atau `ClockTick` dipicu dengan frekuensi tinggi, buat handler-nya seringan mungkin.

## `AddCesiumNet`

```csharp
builder.Services.AddCesiumNet(o =>
{
    o.IonAccessToken = builder.Configuration["Cesium:IonToken"]; // dipakai jika viewer tidak mengatur token sendiri
    o.CesiumBaseUrl  = "/cesium/";                               // CesiumJS yang di-host sendiri (opsional)
});
```

`CesiumNetBlazorOptions` menyimpan nilai default. `Options.IonAccessToken` atau `Options.CesiumBaseUrl` milik viewer sendiri lebih diutamakan. Registrasi bersifat opsional; tanpa registrasi, viewer memakai CDN jsDelivr dan tanpa token.

## Render mode

| Model hosting | Yang perlu dilakukan |
|---|---|
| Web App, Interactive Server | Tambahkan `@rendermode InteractiveServer` di halaman, atau aktifkan secara global pada `<Routes>`. |
| Web App, Interactive WebAssembly / Auto | Letakkan halaman atau komponen di proyek `.Client`, yang mereferensikan `Cesium.Net.Blazor`. |
| WebAssembly standalone | Tidak perlu apa-apa; semuanya sudah interaktif. |
| Static SSR | Tidak didukung. Viewer butuh JS interop, jadi yang ter-render hanya host kosong dan `LoadingContent`. |

Sebaiknya render mode diatur di **halaman atau komponen induk**, bukan langsung di `<CesiumViewer @rendermode=…>`. Parameter yang melintasi batas render mode harus bisa diserialisasi, sedangkan `RenderFragment` dan `EventCallback` tidak bisa.

Prerendering tidak bermasalah. Komponen baru memulai CesiumJS di `OnAfterRenderAsync(firstRender)`, jadi saat prerendering yang dihasilkan hanya `div` host dan `LoadingContent`.

Di **Blazor Server**, setiap pemanggilan adalah satu round-trip SignalR ke browser. Kelompokkan pemanggilan secara batch; lihat [Performa](performance.md).

## Overlay lewat `ChildContent`

`ChildContent` ditempatkan pada lapisan berposisi absolut di atas canvas. Lapisan ini memakai `pointer-events: none`, sedangkan anak langsungnya memakai `pointer-events: auto`. Hasilnya, globe tetap bisa diseret di mana saja kecuali di atas kontrol Anda. Atur posisi elemen anak sendiri:

```razor
<CesiumViewer Style="height:600px" OnReady="OnReady" OnCameraChanged="s => _camera = s">
    <div style="position:absolute; top:12px; left:12px; display:flex; gap:8px">
        <button @onclick="() => _cesium!.Camera.FlyHomeAsync().AsTask()">Home</button>
        <button @onclick="() => _cesium!.Scene.SetModeAsync(SceneMode.Scene2D).AsTask()">2D</button>
    </div>
    <div style="position:absolute; bottom:12px; right:12px; background:#0009; color:#fff; padding:4px 8px">
        @_camera
    </div>
</CesiumViewer>

@code {
    private CesiumController? _cesium;
    private CameraState? _camera;
    private Task OnReady(CesiumController c) { _cesium = c; return Task.CompletedTask; }
}
```

## Template loading dan error

```razor
<CesiumViewer Options="_options" OnReady="OnReady" OnError="e => _lastError = e.Message">
    <LoadingContent>
        <span class="spinner"></span> Loading CesiumJS…
    </LoadingContent>
    <ErrorContent Context="message">
        <div class="alert">CesiumJS could not load. <code>@message</code></div>
    </ErrorContent>
</CesiumViewer>
```

- `LoadingContent` tampil sampai viewer siap atau gagal.
- `ErrorContent` muncul jika impor modul JS atau pembuatan viewer melempar exception. Penyebab yang umum adalah CDN yang diblokir.
- Setelah gagal memuat, `Controller.WhenReady` berstatus faulted. Semua pemanggilan yang mengantre maupun yang dilakukan sesudahnya akan melempar ulang exception tersebut.

## Disposal

- `CesiumViewer` mengimplementasikan `IAsyncDisposable`. Saat Blazor menghapusnya, viewer CesiumJS dihancurkan, resize observer diputus, dan modul JS dilepas.
- Dispose resource milik Anda lebih dulu, misalnya `RealtimeEntityFeed`, timer, atau koneksi hub. Setelah viewer hilang, pemanggilan akan gagal dengan `JSDisconnectedException` (Server) atau `ObjectDisposedException`/`InvalidOperationException`.

```razor
@implements IAsyncDisposable
...
@code {
    private RealtimeEntityFeed? _feed;
    private CancellationTokenSource _cts = new();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_feed is not null) await _feed.DisposeAsync();
    }
}
```

- Untuk **memulai ulang** viewer dengan `Options` baru, ganti `@key`-nya. Blazor akan men-dispose instance lama dan membuat yang baru:

```razor
<CesiumViewer @key="_run" Options="_options" OnReady="OnReady" />
@code {
    private int _run;
    private CesiumViewerOptions _options = new();
    private void SwitchTo2D() { _options = new() { SceneMode = SceneMode.Scene2D }; _run++; }
}
```

## Tata letak

Scoped CSS membuat host bergaya `position:relative; width:100%; height:100%; min-height:240px`. Karena itu, elemen induk harus punya tinggi, atau berikan `Style="height:…"`. Link bundle `<link href="{App}.styles.css">` harus ada; link ini sudah tersedia di template default.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
