[Bahasa Indonesia](../id/getting-started.md)

# Getting started

You need the .NET 10 SDK. The viewer loads CesiumJS 1.146.0 from `cdn.jsdelivr.net` by default, so the machine running the browser or WebView needs internet access. To run without it, see [Offline & Cesium ion](offline-and-ion.md).

![Hello, globe sample](../images/gallery-hello-globe.png)

## 1. Install

| Host | Command |
|---|---|
| Blazor (Server, WebAssembly, Web App) | `dotnet add package Cesium.Net.Blazor` |
| Avalonia desktop | `dotnet add package Cesium.Net.Avalonia` |
| Shared library code only (no UI) | `dotnet add package Cesium.Net` |

Both host packages reference `Cesium.Net`, so you don't need to add it separately.

## 2a. Blazor

### Program.cs

Registration is optional. It lets you set app-wide defaults such as the ion token or a self-hosted CesiumJS URL.

```csharp
using CesiumNet.Blazor;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCesiumNet(); // optional: o => o.IonAccessToken = builder.Configuration["Cesium:IonToken"]

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();             // serves _content/Cesium.Net.Blazor/*.js
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
```

For **standalone WebAssembly**, call `builder.Services.AddCesiumNet()` on the `WebAssemblyHostBuilder` in the client's `Program.cs`.

### _Imports.razor

```razor
@using CesiumNet
@using CesiumNet.Blazor
```

### App.razor

The component's layout comes from its scoped CSS. Keep the standard bundle link that the templates include:

```html
<link rel="stylesheet" href="@Assets["MyApp.styles.css"]" />
```

### A page

`CesiumViewer` uses JS interop, so it must render **interactively** (Server, WebAssembly or Auto). It fills its parent, so give the parent or the component a height.

```razor
@page "/map"
@rendermode InteractiveServer

<div style="height: 80vh">
    <CesiumViewer OnReady="OnReady" OnLeftClick="OnClick" />
</div>

<p>@_status</p>

@code {
    private string _status = "Loading…";

    private async Task OnReady(CesiumController cesium)
    {
        // First entity: a pin with a label.
        await cesium.Entities.AddAsync(new EntityOptions
        {
            Id = "monas",
            Name = "Monumen Nasional",
            Position = new GeoPoint(106.827153, -6.175392),
            Billboard = new BillboardGraphics { PinColor = "#1F4FE0", PinIcon = "monument", HeightReference = HeightReference.ClampToGround },
            Label = new LabelGraphics { Text = "Monas", PixelOffset = new Cartesian2(0, -58), ShowBackground = true },
        });

        // First fly-to: longitude, latitude, height (m), pitch in degrees.
        await cesium.Camera.FlyToAsync(106.8272, -6.1880, 2_500, pitch: -40, duration: 4);
        _status = "Ready";
    }

    private void OnClick(PickResult pick)
        => _status = pick.Position is { } p ? $"Clicked {pick.Kind} at {p}" : "Clicked space";
}
```

Render mode notes:

- **Blazor Web App, Interactive Server**: use the example above.
- **Blazor Web App, Interactive WebAssembly / Auto**: put the page in the `.Client` project and reference `Cesium.Net.Blazor` from that project.
- **Standalone WebAssembly**: every component is interactive, so you can drop `@rendermode`.

See [Blazor](blazor.md) for all parameters.

## 2b. Avalonia

Cesium.Net.Avalonia targets Avalonia 12 and uses `Avalonia.Controls.WebView`. A typical app references `Avalonia.Desktop` and `Avalonia.Themes.Fluent` as well.

### Windows: application manifest (required)

On Windows, an app that uses `CesiumView` **must** ship an application manifest that declares Windows 10/11 support. Without it the app crashes with `InvalidOperationException: Unable to create child window for native control host. Application manifest with supported OS list might be required.`

```xml
<!-- MyApp.csproj -->
<PropertyGroup>
  <ApplicationManifest>app.manifest</ApplicationManifest>
</PropertyGroup>
```

```xml
<!-- app.manifest -->
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="MyApp.app" />
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 and Windows 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

The full file is at `samples/Cesium.Net.Gallery.Avalonia/app.manifest`.

### Program.cs

```csharp
using Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
```

### MainWindow.axaml

Place toolbars **beside** the globe, not on top of it. The native WebView is drawn above Avalonia content; see [Avalonia: airspace](avalonia.md#airspace-overlays-go-beside-the-view).

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:cesium="using:CesiumNet.Avalonia"
        x:Class="MyApp.MainWindow"
        Title="My globe" Width="1200" Height="800">
  <DockPanel>
    <StackPanel DockPanel.Dock="Left" Width="220" Margin="12" Spacing="8">
      <Button Content="Fly to Monas" Click="OnFlyClick" />
      <TextBlock x:Name="Status" Text="Loading…" TextWrapping="Wrap" />
    </StackPanel>
    <cesium:CesiumView x:Name="Globe" Ready="OnGlobeReady" LoadFailed="OnGlobeLoadFailed" />
  </DockPanel>
</Window>
```

### MainWindow.axaml.cs

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using CesiumNet;
using CesiumNet.Avalonia;

namespace MyApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Options are read when the page loads, so set them before the window is shown.
        Globe.Options = new CesiumViewerOptions
        {
            BaseLayer = ImageryLayerOptions.Satellite(),
            InitialView = CameraView.At(117.5, -2.5, 9_500_000),
        };
        Globe.Controller.LeftClick += (_, pick) => Status.Text = $"{pick.Kind} at {pick.Position}";
    }

    private async void OnGlobeReady(object? sender, CesiumReadyEventArgs e)
    {
        var cesium = e.Controller;
        await cesium.Entities.AddAsync(new EntityOptions
        {
            Id = "monas",
            Name = "Monumen Nasional",
            Position = new GeoPoint(106.827153, -6.175392),
            Billboard = new BillboardGraphics { PinColor = CesiumColor.Blue, PinText = "M" },
        });
        await cesium.Camera.FlyToAsync(106.8272, -6.1880, 2_500, pitch: -40);
        Status.Text = "Ready";
    }

    private void OnGlobeLoadFailed(object? sender, CesiumError error)
        => Status.Text = "CesiumJS failed to load: " + error.Message;

    private async void OnFlyClick(object? sender, RoutedEventArgs e)
        => await Globe.Controller.Camera.FlyToAsync(106.8272, -6.1820, 1_800, pitch: -35);
}
```

Declare `CesiumView` in XAML as shown. If you create it in code, do so after the window has loaded (in the `Loaded` event), not in the constructor. You can call `Globe.Controller` before the viewer is ready because calls are queued. On Windows the WebView2 runtime must be installed. On Linux, install WebKitGTK. See [Avalonia](avalonia.md).

## 3. Next steps

- [Core concepts](core-concepts.md): ids, units, events, errors.
- [Entities](entities.md) and [Camera & time](camera-and-time.md).
- Run the [gallery](gallery.md): `dotnet run --project samples/Cesium.Net.Gallery.Blazor`.

---
Made by Gravicode Studios, led by Kang Fadhil.
