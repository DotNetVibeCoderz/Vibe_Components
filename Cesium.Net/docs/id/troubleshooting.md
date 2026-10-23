[English](../en/troubleshooting.md)

# Pemecahan masalah

Mulailah dari event `Error` (`OnError` di Blazor) dan console browser atau WebView. Sebagian besar masalah terlihat di sana.

```csharp
cesium.Error += (_, e) => Console.WriteLine($"[cesium] {e.Source}: {e.Message}\n{e.Stack}");
```

Pemeriksaan yang berguna:

```csharp
await cesium.ExecuteScriptAsync("Cesium.VERSION");                       // versi CesiumJS yang dimuat
await cesium.ExecuteScriptAsync("viewer.scene.context.webgl2");          // true = WebGL 2
await cesium.ExecuteScriptAsync("viewer.scene.requestRenderMode");
```

## CDN diblokir / CesiumJS gagal dimuat

**Gejala:** Blazor menampilkan `ErrorContent` dengan `Failed to load CesiumJS from https://cdn.jsdelivr.net/…`. Avalonia memicu `LoadFailed`. `WhenReady` berstatus faulted, dan setiap panggilan melempar error yang sama.

**Penyebab:** tidak ada internet, proxy atau firewall kantor, Content Security Policy yang memblokir `cdn.jsdelivr.net`, atau jaringan air-gapped.

**Solusi:**
- Host CesiumJS sendiri dan atur `CesiumBaseUrl`; lihat [Offline & Cesium ion](offline-and-ion.md).
- Jika memakai CSP, izinkan `script-src`, `style-src`, `worker-src`, dan `connect-src` untuk origin CesiumJS. CesiumJS membuat worker dari `Workers/` dan membutuhkan `blob:` untuk `worker-src`.
- Di Blazor, jika `_content/Cesium.Net.Blazor/cesium-net-blazor.js` gagal dimuat, berarti static web assets tidak dilayani. Pastikan ada `app.MapStaticAssets()` atau `app.UseStaticFiles()`.

## WebGL tidak tersedia

**Gejala:** event `Error` atau `ErrorContent` melaporkan pesan seperti "WebGL initialization failed", atau area peta tetap gelap.

**Solusi:**
- Aktifkan akselerasi hardware di browser (Chrome: Settings → System → "Use graphics acceleration").
- Periksa `chrome://gpu` atau `edge://gpu`. Driver yang masuk blocklist jatuh ke software rendering atau tanpa WebGL, jadi perbarui driver GPU.
- Di remote desktop dan VM, aktifkan GPU passthrough atau terima software rendering (SwiftShader) yang lambat.
- Linux/WebKitGTK: pasang driver GL Mesa atau vendor. Beberapa distro mematikan WebGL di WebKitGTK pada compositor tertentu; uji dengan `WEBKIT_DISABLE_COMPOSITING_MODE=1` untuk mempersempit masalah.

## Avalonia: "Unable to create child window for native control host"

**Gejala (Windows):** aplikasi crash saat start atau saat `CesiumView` dibuat, dengan `InvalidOperationException: Unable to create child window for native control host. Application manifest with supported OS list might be required.`

**Penyebab:** `NativeWebView` menampung child HWND, dan Avalonia mewajibkan aplikasi mendeklarasikan dukungan Windows 10/11 di application manifest.

**Solusi:** tambahkan `<ApplicationManifest>app.manifest</ApplicationManifest>` ke `.csproj`, dengan manifest berisi:

```xml
<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
  <application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /> <!-- Windows 10 dan 11 -->
  </application>
</compatibility>
```

Berkas lengkapnya ada di `samples/Cesium.Net.Gallery.Avalonia/app.manifest`. Selain itu, deklarasikan `CesiumView` di XAML, atau buat hanya setelah event `Loaded` milik window.

## Runtime WebView2 tidak ada (Avalonia di Windows)

**Gejala:** area `CesiumView` tetap kosong. `Ready` maupun `LoadFailed` tidak dipicu, sehingga panggilan yang diantrekan menunggu selamanya.

**Solusi:**
- Pasang [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (Evergreen), atau sertakan Evergreen bootstrapper atau runtime Fixed Version di installer Anda.
- Lindungi startup dengan timeout agar aplikasi bisa melaporkan masalahnya:

```csharp
try { await Globe.Controller.WhenReady.WaitAsync(TimeSpan.FromSeconds(30)); }
catch (TimeoutException) { ShowMessage("Map engine unavailable. Please install the Microsoft Edge WebView2 Runtime."); }
```

Di Linux, padanannya adalah paket WebKitGTK yang belum terpasang. macOS sudah menyertakan WKWebView.

## Cesium ion 401 / 403

**Gejala:** `CesiumInteropException` atau `Error` yang menyebut `401`/`403` atau "Invalid access token" saat menambahkan World Terrain, OSM Buildings, aset ion, atau tile Google. `CesiumIonClient` melempar `HttpRequestException` (401/403).

**Solusi:**
- Atur `IonAccessToken`, atau `AddCesiumNet(o => o.IonAccessToken = …)`. Pastikan kunci konfigurasinya benar-benar berisi nilai.
- **401**: token salah, dicabut, atau kedaluwarsa.
- **403**: token tidak memiliki akses ke aset, atau batasan **allowed URLs** tidak mencakup origin saat ini. Ingat `http://localhost:<acak>` untuk Avalonia dan port dev untuk Blazor.
- `CesiumIonClient.ListAssetsAsync` membutuhkan scope `assets:list`.
- Token ion bawaan CesiumJS tidak dipakai. Tanpa token, fitur khusus ion memang akan gagal.

## CORS untuk data kustom

**Gejala:** tileset, GeoJSON, model, atau lapisan imagery gagal dengan "blocked by CORS policy" di console, tile imagery tetap kosong, atau `CaptureScreenshotAsync` melempar "tainted canvas".

**Penyebab:** origin halaman (situs Blazor Anda atau `http://localhost:{port}` di Avalonia) berbeda dari host data, dan host data tidak mengirim `Access-Control-Allow-Origin`.

**Solusi:**
- Konfigurasikan server data agar mengirim `Access-Control-Allow-Origin: *` (atau origin Anda). Untuk S3 atau Azure Blob, buat aturan CORS.
- Lewatkan data melalui backend Anda sendiri (Blazor Server atau ASP.NET API) pada origin yang sama.
- Di Avalonia, layani berkas lokal lewat `CesiumLocalServer.Shared.MapFolder`, sehingga origin-nya sama dengan halaman.
- Server WMS sering membutuhkan `Parameters` seperti `["transparent"] = "true"`. Sebagian hanya mengizinkan CORS untuk format tertentu.

## Globe kosong atau membeku dalam mode request-render

Dengan `RequestRenderMode = true`, CesiumJS hanya me-render bila diminta. Cesium.Net meminta render setelah setiap panggilan API dan terus me-render selama animasinya sendiri berjalan. Beberapa kasus masih perlu bantuan:

| Gejala | Penyebab | Solusi |
|---|---|---|
| Tidak ada perubahan setelah `ExecuteScriptAsync` | Skrip tidak memicu render | `await cesium.RequestRenderAsync()` |
| Entitas time-dynamic, CZML, atau model tidak bergerak | Clock sedang dijeda, atau `MaximumRenderTimeChange` diatur ke nilai besar | Panggil `await cesium.Clock.PlayAsync()` (atau `ConfigureAsync` dengan `ShouldAnimate = true`); biarkan `MaximumRenderTimeChange` tidak diatur (default CesiumJS 0) |
| Tile selesai dimuat tetapi tampilan tidak diperbarui | Sebagian provider kustom tidak meminta render | Gerakkan kamera atau panggil `RequestRenderAsync()`; atur `MaximumRenderTimeChange` |
| Globe baru muncul setelah mouse digerakkan | Perubahan layout tidak diikuti render | Blazor sudah memanggil `resize()` dan meminta render saat ukuran elemen berubah. Pada layout kustom, panggil `RequestRenderAsync()` setelah view ditampilkan. |

## Pemeriksaan cepat lainnya

| Masalah | Periksa |
|---|---|
| Viewer Blazor tingginya nol | Elemen induk harus punya tinggi, atau set `Style="height:600px"`. Pastikan link `{App}.styles.css` ada. |
| `OnReady` tidak pernah dipanggil di Blazor | Halamannya tidak interaktif. Tambahkan `@rendermode InteractiveServer` (atau WASM/Auto). |
| Overlay tersembunyi di Avalonia | Airspace WebView native: letakkan di samping view; lihat [Avalonia](avalonia.md#airspace-overlay-diletakkan-di-samping-view). |
| Entitas berada di bawah terrain | Tinggi diukur di atas elipsoid. Pakai `HeightReference.ClampToGround`/`RelativeToGround`, atau `ClampToGround` untuk garis. |
| Entitas muncul di tempat yang salah (laut dekat Afrika) | Bujur dan lintang tertukar. `GeoPoint(lon, lat)` menerima bujur lebih dulu. |
| `UpdatePositionsAsync` tidak berefek | Id belum ada; buat entitasnya terlebih dahulu. |
| Klik tidak diterima | `Events.Click` nonaktif, atau sedang menggambar atau mengedit (klik ditahan selama itu). |
| Model menghadap arah yang salah | Cesium.Net mengasumsikan model menghadap +X. Tambahkan offset heading di data Anda. |

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
