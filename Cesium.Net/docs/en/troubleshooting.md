[Bahasa Indonesia](../id/troubleshooting.md)

# Troubleshooting

Start with the `Error` event (`OnError` in Blazor) and the browser or WebView console. Most problems show up there.

```csharp
cesium.Error += (_, e) => Console.WriteLine($"[cesium] {e.Source}: {e.Message}\n{e.Stack}");
```

Useful probes:

```csharp
await cesium.ExecuteScriptAsync("Cesium.VERSION");                       // loaded CesiumJS version
await cesium.ExecuteScriptAsync("viewer.scene.context.webgl2");          // true = WebGL 2
await cesium.ExecuteScriptAsync("viewer.scene.requestRenderMode");
```

## CDN blocked / CesiumJS fails to load

**Symptoms:** Blazor shows `ErrorContent` with `Failed to load CesiumJS from https://cdn.jsdelivr.net/…`. Avalonia raises `LoadFailed`. `WhenReady` is faulted, and every call throws the same error.

**Causes:** no internet, a corporate proxy or firewall, a Content Security Policy that blocks `cdn.jsdelivr.net`, or an air-gapped network.

**Fixes:**
- Self-host CesiumJS and set `CesiumBaseUrl`; see [Offline & Cesium ion](offline-and-ion.md).
- If you use a CSP, allow `script-src`, `style-src`, `worker-src` and `connect-src` for the CesiumJS origin. CesiumJS creates workers from `Workers/` and needs `blob:` for `worker-src`.
- In Blazor, a failure to load `_content/Cesium.Net.Blazor/cesium-net-blazor.js` means static web assets are not served. Make sure `app.MapStaticAssets()` or `app.UseStaticFiles()` is present.

## WebGL unavailable

**Symptoms:** the `Error` event or `ErrorContent` reports something like "WebGL initialization failed", or the area stays dark.

**Fixes:**
- Enable hardware acceleration in the browser (Chrome: Settings → System → "Use graphics acceleration").
- Check `chrome://gpu` or `edge://gpu`. Blocklisted drivers fall back to software or no WebGL, so update the GPU driver.
- In remote desktops and VMs, enable GPU passthrough or accept software rendering (SwiftShader), which is slow.
- Linux/WebKitGTK: install the Mesa or vendor GL drivers. Some distributions disable WebGL in WebKitGTK under specific compositors; test with `WEBKIT_DISABLE_COMPOSITING_MODE=1` to narrow it down.

## Avalonia: "Unable to create child window for native control host"

**Symptom (Windows):** the app crashes on start or when the `CesiumView` is created, with `InvalidOperationException: Unable to create child window for native control host. Application manifest with supported OS list might be required.`

**Cause:** `NativeWebView` hosts a child HWND, and Avalonia requires the app to declare Windows 10/11 support in an application manifest.

**Fix:** add `<ApplicationManifest>app.manifest</ApplicationManifest>` to the `.csproj` with a manifest containing:

```xml
<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
  <application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /> <!-- Windows 10 and 11 -->
  </application>
</compatibility>
```

The full file is at `samples/Cesium.Net.Gallery.Avalonia/app.manifest`. Also declare `CesiumView` in XAML, or create it only after the window's `Loaded` event.

## WebView2 runtime missing (Avalonia on Windows)

**Symptoms:** the `CesiumView` area stays empty; neither `Ready` nor `LoadFailed` fires, so queued calls wait forever.

**Fixes:**
- Install the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (Evergreen), or bundle the Evergreen bootstrapper or Fixed Version runtime with your installer.
- Guard startup with a timeout so the app can report the problem:

```csharp
try { await Globe.Controller.WhenReady.WaitAsync(TimeSpan.FromSeconds(30)); }
catch (TimeoutException) { ShowMessage("Map engine unavailable. Please install the Microsoft Edge WebView2 Runtime."); }
```

On Linux the equivalent is a missing WebKitGTK package. macOS ships WKWebView.

## Cesium ion 401 / 403

**Symptoms:** a `CesiumInteropException` or `Error` mentioning `401`/`403` or "Invalid access token" when you add World Terrain, OSM Buildings, an ion asset or Google tiles. `CesiumIonClient` throws `HttpRequestException` (401/403).

**Fixes:**
- Set `IonAccessToken`, or `AddCesiumNet(o => o.IonAccessToken = …)`. Check that the configuration key is actually populated.
- **401**: the token is wrong, revoked or expired.
- **403**: the token lacks the asset, or the **allowed URLs** restriction excludes the current origin. Remember `http://localhost:<random>` for Avalonia, and your dev port for Blazor.
- `CesiumIonClient.ListAssetsAsync` needs the `assets:list` scope.
- The default ion token that ships with CesiumJS is not used. Without a token, ion-only features fail by design.

## CORS for custom data

**Symptoms:** a tileset, GeoJSON, model or imagery layer fails with "blocked by CORS policy" in the console, imagery tiles stay blank, or `CaptureScreenshotAsync` throws "tainted canvas".

**Cause:** the page origin (your Blazor site or `http://localhost:{port}` in Avalonia) differs from the data host, and the data host doesn't send `Access-Control-Allow-Origin`.

**Fixes:**
- Configure the data server to send `Access-Control-Allow-Origin: *` (or your origin). For S3 or Azure Blob, set a CORS rule.
- Proxy the data through your own backend (Blazor Server or an ASP.NET API) on the same origin.
- In Avalonia, serve local files through `CesiumLocalServer.Shared.MapFolder`, which gives them the same origin as the page.
- WMS servers often need `Parameters` such as `["transparent"] = "true"`. Some only allow CORS for specific formats.

## Blank or frozen globe in request-render mode

With `RequestRenderMode = true`, CesiumJS renders only when asked. Cesium.Net requests a render after every API call and keeps rendering for its own animations. Some cases still need help:

| Symptom | Cause | Fix |
|---|---|---|
| Nothing changes after `ExecuteScriptAsync` | Scripts don't trigger a render | `await cesium.RequestRenderAsync()` |
| Time-dynamic entities, CZML or models don't move | The clock is paused, or `MaximumRenderTimeChange` was set to a large value | Call `await cesium.Clock.PlayAsync()` (or `ConfigureAsync` with `ShouldAnimate = true`); leave `MaximumRenderTimeChange` unset (CesiumJS default 0) |
| Tiles finish loading but the view stays stale | Some custom providers don't request renders | Move the camera or call `RequestRenderAsync()`; set `MaximumRenderTimeChange` |
| Globe appears only after the first mouse move | A layout change wasn't followed by a render | Blazor already calls `resize()` and requests a render on element resize. In custom layouts, call `RequestRenderAsync()` after showing the view. |

## Other quick checks

| Problem | Check |
|---|---|
| Blazor viewer has zero height | The parent element needs a height, or set `Style="height:600px"`. Make sure the `{App}.styles.css` link is present. |
| `OnReady` never fires in Blazor | The page isn't interactive. Add `@rendermode InteractiveServer` (or WASM/Auto). |
| Overlay hidden in Avalonia | Native WebView airspace: put it beside the view; see [Avalonia](avalonia.md#airspace-overlays-go-beside-the-view). |
| Entity is under the terrain | Heights are above the ellipsoid. Use `HeightReference.ClampToGround`/`RelativeToGround`, or `ClampToGround` for lines. |
| Entity is in the wrong place (the sea near Africa) | Longitude and latitude are swapped. `GeoPoint(lon, lat)` takes longitude first. |
| `UpdatePositionsAsync` does nothing | The id doesn't exist yet; create the entity first. |
| Clicks don't arrive | `Events.Click` is off, or a drawing or edit is active (clicks are suppressed then). |
| Model points the wrong way | Cesium.Net assumes models face +X. Add a heading offset in your data. |

---
Made by Gravicode Studios, led by Kang Fadhil.
