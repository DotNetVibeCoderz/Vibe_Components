[Bahasa Indonesia](../id/avalonia.md)

# Avalonia

Package `Cesium.Net.Avalonia`, namespace `CesiumNet.Avalonia`. It targets Avalonia 12 and uses the official `Avalonia.Controls.WebView` package.

![Avalonia gallery](../images/avalonia-gallery.png)

## How it works

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

The loopback server gives the page a real `http://localhost` origin, so CesiumJS web workers, `fetch` and relative URLs behave as they do on a normal web page.

## `CesiumView`

| Member | Description |
|---|---|
| `Controller` | The `CesiumController`. It exists from construction, and calls are queued until the viewer is ready. |
| `Options` | `CesiumViewerOptions`. Set it **before the control is attached** to a window. It is serialized when the host page reports `loaded`. |
| `IsReady` | `true` once CesiumJS has loaded. |
| `WebView` | The underlying `NativeWebView`, for dev tools, cookies or printing. |
| `event Ready` | `EventHandler<CesiumReadyEventArgs>`, raised once. `e.Controller` is the same object as `Controller`. |
| `event LoadFailed` | `EventHandler<CesiumError>`, raised if CesiumJS could not load (`Source = "load"`), for example when the CDN is unreachable. |

All other events (`LeftClick`, `CameraChanged`, `DrawCompleted`, `Error`, …) live on `Controller`. See [Core concepts](core-concepts.md#events).

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

`Ready` only fires if the WebView itself starts. If the platform WebView is missing, for example when the WebView2 runtime is not installed, neither `Ready` nor `LoadFailed` fires. Guard with a timeout:

```csharp
try { await Globe.Controller.WhenReady.WaitAsync(TimeSpan.FromSeconds(30)); }
catch (TimeoutException) { Status.Text = "The map could not start. Is the WebView runtime installed?"; }
```

## `CesiumLocalServer` and `MapFolder`

`CesiumLocalServer.Shared` is a process-wide, loopback-only `HttpListener` on a free port. It is created on first use and shared by every `CesiumView`.

```csharp
// Serve local files to the WebView:
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

- `name` must be a **single path segment** (`"models"`, not `"assets/models"`). Requests are routed by their first segment.
- The method throws `DirectoryNotFoundException` if the folder doesn't exist.
- Path traversal outside the mapped folder returns 404.
- MIME types are set for HTML/JS/CSS, JSON/GeoJSON/TopoJSON/CZML, glTF/GLB, 3D Tiles payloads (`.b3dm`, `.i3dm`, `.pnts`, `.cmpt`, `.subtree`), `.terrain`, KML/KMZ, GPX, PNG/JPEG/WebP/SVG/KTX2 and WASM. Everything else is `application/octet-stream`.
- No `Content-Encoding` header is ever sent. Serve uncompressed files; pre-gzipped quantized-mesh tiles will not decode.
- Mapped files are sent with `Cache-Control: max-age=3600`.

### Offline CesiumJS

Ship the `Build/Cesium` folder of the `cesium` npm package (version 1.146.0) with your app and point `CesiumBaseUrl` at it:

```csharp
var cesiumUrl = CesiumLocalServer.Shared.MapFolder("cesium", Path.Combine(AppContext.BaseDirectory, "cesium"));
Globe.Options = new CesiumViewerOptions
{
    CesiumBaseUrl = cesiumUrl,
    BaseLayer = new ImageryLayerOptions { Provider = ImageryProviderKind.Tms, Url = cesiumUrl + "Assets/Textures/NaturalEarthII" },
};
```

```xml
<!-- .csproj: copy the CesiumJS build next to the executable -->
<ItemGroup>
  <None Include="cesium\**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

More in [Offline & Cesium ion](offline-and-ion.md).

## Platform notes

### Windows

- **An application manifest that declares Windows 10/11 support is required.** `NativeWebView` hosts a child HWND, and Avalonia allows that only when the app declares a supported OS. Without the manifest the app crashes with `InvalidOperationException: Unable to create child window for native control host. Application manifest with supported OS list might be required.` Add `<ApplicationManifest>app.manifest</ApplicationManifest>` to the `.csproj` and include:

  ```xml
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /> <!-- Windows 10 and 11 -->
    </application>
  </compatibility>
  ```

  The complete file is at `samples/Cesium.Net.Gallery.Avalonia/app.manifest`; see also [Getting started](getting-started.md#windows-application-manifest-required).
- The control needs the **WebView2 Runtime**. It is preinstalled on Windows 11 and on most up-to-date Windows 10 machines. For older machines, ship the Evergreen bootstrapper or the Fixed Version runtime with your installer.

### Linux (WebKitGTK)

- Install the WebKitGTK runtime that `Avalonia.Controls.WebView` expects. On Debian/Ubuntu that is typically `libwebkit2gtk-4.1-0`; check the Avalonia WebView documentation for your version.
- WebGL in WebKitGTK depends on the GPU driver. On VMs, or with software rendering, CesiumJS may run slowly or report that WebGL is unavailable; see [Troubleshooting](troubleshooting.md#webgl-unavailable).
- Wayland and X11 both work as far as Avalonia's WebView supports them. If the view stays blank under Wayland, try `GDK_BACKEND=x11`.

### macOS (WKWebView)

- WKWebView is part of the OS, so nothing needs to be installed.
- The page is served over plain `http://localhost`. App Transport Security normally permits loopback. If your `Info.plist` tightens ATS, add `NSAppTransportSecurity` → `NSAllowsLocalNetworking = true`.
- Sandboxed apps, such as Mac App Store builds, need `com.apple.security.network.client` to reach the CDN and `com.apple.security.network.server` for the loopback listener.

## Airspace: overlays go beside the view

The native WebView is a separate OS surface composited **above** Avalonia's visual tree. Avalonia content that overlaps the `CesiumView` is hidden behind the map, whether it comes from a `Panel` z-index, an adorner or a semi-transparent HUD.

Do this instead:

- Put toolbars, legends and readouts **next to** the view (`DockPanel`, `Grid` columns or rows).
- Build HUDs inside the map with Cesium itself, using labels, billboards and screen-space positions via `Scene.ToScreenAsync`.
- Use windows that the OS draws separately. On desktop, Avalonia popups and flyouts use their own native windows by default, so they can appear above the view.

```xml
<Grid ColumnDefinitions="260,*" RowDefinitions="*,28">
  <StackPanel Grid.Column="0" Grid.RowSpan="2"> <!-- tools --> </StackPanel>
  <cesium:CesiumView Grid.Column="1" Grid.Row="0" x:Name="Globe" />
  <TextBlock Grid.Column="1" Grid.Row="1" x:Name="Coords" />   <!-- status bar under, not over -->
</Grid>
```

## Threading

- `Ready`, `LoadFailed` and all `Controller` events are raised on the **Avalonia UI thread**, so handlers can touch controls directly.
- `Controller` methods can be called from **any thread**. The transport marshals `InvokeScript` to the UI thread and completes the returned task when the posted result arrives.
- `RealtimeEntityFeed` flushes from a thread-pool timer, which is safe. Its `FlushFailed` event is raised on that thread pool thread, so marshal with `Dispatcher.UIThread.Post` before touching UI.
- Avoid `.Result` or `.Wait()` on controller calls from the UI thread. The result is delivered through a UI-thread message, so blocking the UI thread deadlocks.

## Lifecycle notes

- Declare `CesiumView` in XAML, or create it in code **after the window is loaded** (in the `Loaded` event). Creating the native control host before the window exists fails.
- Each `CesiumView` hosts its own WebView, page and CesiumJS viewer. Several views can coexist.
- If the page reloads, for example from dev tools, the viewer state is lost and `Ready` is **not** raised again. Recreate the control instead.
- Dev tools are enabled only when `Cesium.Net.Avalonia` itself is compiled in Debug. To enable them in your own build, subscribe before the view is attached: `Globe.WebView.EnvironmentRequested += (_, e) => e.EnableDevTools = true;`.

---
Made by Gravicode Studios, led by Kang Fadhil.
