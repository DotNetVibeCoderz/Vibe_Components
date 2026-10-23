[Bahasa Indonesia](../id/blazor.md)

# Blazor

Package `Cesium.Net.Blazor`, namespace `CesiumNet.Blazor`. One component, `CesiumViewer`, works in Blazor Server, Blazor WebAssembly and Blazor Web App (Server, WebAssembly or Auto interactivity).

## `CesiumViewer` parameters

| Parameter | Type | Notes |
|---|---|---|
| `Options` | `CesiumViewerOptions?` | Viewer creation options. **Read once, on first render.** Later changes are ignored. To recreate the viewer, change its `@key`. |
| `Class` | `string?` | Extra CSS classes on the host `div`. |
| `Style` | `string?` | Inline style on the host `div`, e.g. `height:600px`. |
| `ChildContent` | `RenderFragment?` | Overlay content drawn above the globe. |
| `LoadingContent` | `RenderFragment?` | Shown while CesiumJS loads. |
| `ErrorContent` | `RenderFragment<string>?` | Shown if CesiumJS fails to load; the context is the error message. |
| *(any other attribute)* | captured | Unmatched attributes such as `id` or `aria-label` are splatted onto the host `div`. |

| Member | Notes |
|---|---|
| `Controller` | The `CesiumController`. It exists from construction, and calls are queued until the viewer is ready. Use it with `@ref`. |
| `IsReady` | `true` once CesiumJS has loaded. |

## Event callbacks

| Callback | Payload | Raised when | Requires (`Options.Events`) |
|---|---|---|---|
| `OnReady` | `CesiumController` | Viewer created | always |
| `OnLeftClick` | `PickResult` | Left click (not while drawing or editing) | `Click` (default on) |
| `OnRightClick` | `PickResult` | Right click | `RightClick` |
| `OnDoubleClick` | `PickResult` | Double click | `DoubleClick` |
| `OnMouseMove` | `PickResult` | Mouse move, throttled | `MouseMove` |
| `OnCameraChanged` | `CameraState` | Camera stops moving | `CameraChanged` (default on) |
| `OnSelectedEntityChanged` | `string?` | Selection changes (null = none) | `SelectedEntityChanged` (default on) |
| `OnClockTick` | `ClockState` | Clock ticks, throttled | `ClockTick` |
| `OnDrawCompleted` | `DrawResult` | Interactive drawing finished | always |
| `OnEntityEdited` | `EntityEditedResult` | A vertex drag ended | always |
| `OnError` | `CesiumError` | Load failure or render error | always |

`DrawCancelled` and `LoadingProgress` have no callback parameter. Subscribe to them on `Controller`:

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

Callbacks are `EventCallback`s, so the parent component re-renders after each one. If you raise `MouseMove` or `ClockTick` at a high rate, keep the handlers cheap.

## `AddCesiumNet`

```csharp
builder.Services.AddCesiumNet(o =>
{
    o.IonAccessToken = builder.Configuration["Cesium:IonToken"]; // used when a viewer doesn't set one
    o.CesiumBaseUrl  = "/cesium/";                               // self-hosted CesiumJS (optional)
});
```

`CesiumNetBlazorOptions` holds defaults. A viewer's own `Options.IonAccessToken` or `Options.CesiumBaseUrl` wins over them. Registration is optional: without it, the viewer uses the jsDelivr CDN and no token.

## Render modes

| Hosting model | What to do |
|---|---|
| Web App, Interactive Server | Put `@rendermode InteractiveServer` on the page, or enable it globally on `<Routes>`. |
| Web App, Interactive WebAssembly / Auto | Put the page or component in the `.Client` project, which references `Cesium.Net.Blazor`. |
| Standalone WebAssembly | Nothing extra; everything is interactive. |
| Static SSR | Not supported. The viewer needs JS interop, so it renders only the empty host and `LoadingContent`. |

Prefer setting the render mode on the **page or a parent component** rather than on `<CesiumViewer @rendermode=…>` directly. Parameters that cross a render-mode boundary must be serializable, and `RenderFragment`s and `EventCallback`s are not.

Prerendering is harmless. The component starts CesiumJS in `OnAfterRenderAsync(firstRender)`, so during prerendering only the host `div` and `LoadingContent` are produced.

In **Blazor Server**, every call is a SignalR round-trip to the browser. Batch your calls; see [Performance](performance.md).

## Overlays via `ChildContent`

`ChildContent` is placed in an absolutely positioned layer over the canvas. That layer has `pointer-events: none` and its direct children have `pointer-events: auto`, so the globe stays draggable everywhere except on your controls. Position the children yourself:

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

## Loading and error templates

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

- `LoadingContent` is visible until the viewer is ready or fails.
- `ErrorContent` appears when importing the JS module or creating the viewer throws. A typical cause is a blocked CDN.
- After a load failure, `Controller.WhenReady` is faulted. Any queued or later call rethrows that exception.

## Disposal

- `CesiumViewer` implements `IAsyncDisposable`. When Blazor removes it, it destroys the CesiumJS viewer, disconnects the resize observer and releases the JS module.
- Dispose your own resources first, such as a `RealtimeEntityFeed`, timers or hub connections. After the viewer is gone, calls fail with `JSDisconnectedException` (Server) or `ObjectDisposedException`/`InvalidOperationException`.

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

- To **restart** a viewer with new `Options`, change its `@key`. Blazor disposes the old instance and creates a new one:

```razor
<CesiumViewer @key="_run" Options="_options" OnReady="OnReady" />
@code {
    private int _run;
    private CesiumViewerOptions _options = new();
    private void SwitchTo2D() { _options = new() { SceneMode = SceneMode.Scene2D }; _run++; }
}
```

## Layout

The scoped CSS makes the host `position:relative; width:100%; height:100%; min-height:240px`. The parent must therefore have a height, or you pass `Style="height:…"`. The bundle `<link href="{App}.styles.css">` must be present; it is in the default templates.

---
Made by Gravicode Studios, led by Kang Fadhil.
