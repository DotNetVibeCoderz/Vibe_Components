# Memulai

## Persyaratan

- .NET 10 SDK.
- Untuk rendering desktop: GPU dengan OpenGL 3.3, atau Direct3D 11 melalui ANGLE (default Avalonia di Windows).
- Untuk web: browser yang mendukung WebGL2 (Chrome, Edge, Firefox, dan Safari versi terkini).
- Untuk paket native: Windows x64. Paket ini sudah menyertakan binary OpenSceneGraph 3.6.5 yang siap pakai (prebuilt).

## Paket

| Paket | Kegunaan |
|---|---|
| `OpenSceneGraph.Net` | Scene graph dan seluruh bagian managed (selalu diperlukan) |
| `OpenSceneGraph.Net.Rendering.OpenGL` | Renderer OpenGL (otomatis ikut terpasang bersama paket Avalonia) |
| `OpenSceneGraph.Net.Avalonia` | Kontrol `OsgView` untuk aplikasi desktop |
| `OpenSceneGraph.Net.Blazor` | Komponen `OsgCanvas` (WebGL2) untuk Blazor WebAssembly / Server |
| `OpenSceneGraph.Net.Native` | OSG native: semua format file OSG, jendela osgViewer, rendering offscreen |
| `OpenSceneGraph.Net.Templates` | `dotnet new osgnet-console / osgnet-avalonia / osgnet-blazor` |
| `OpenSceneGraph.Net.Cli` | Global tool `osgnet` |

## Scene pertama Anda

Sistem koordinat mengikuti OSG: **Z mengarah ke atas**, satuan dalam meter, dan model menghadap ke **+Y**.

```csharp
using System.Numerics;
using OpenSceneGraph.Net.Animation;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

var root = new Group { Name = "root" };
root.AddChild(Shapes.Plane(40, 40, 1, 1, Color.FromHex("#2b2d42")));
root.AddChild(new LightSource(Light.Sun(new Vector3(-0.4f, 0.5f, -0.8f))));

var box = new PositionAttitudeTransform { Position = new Vector3(0, 0, 1) };
box.AddChild(Shapes.Cube(2, Color.FromHex("#ef476f")));
box.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 45));   // derajat per detik
root.AddChild(box);
```

### Menampilkan di Avalonia

```xml
<Window xmlns:osg="clr-namespace:OpenSceneGraph.Net.Avalonia;assembly=OpenSceneGraph.Net.Avalonia">
  <osg:OsgView x:Name="View" />
</Window>
```

```csharp
View.Scene = root;
View.Viewer.Environment.Shadows.Enabled = true;
View.FrameRendered += (_, stats) => Title = stats.ToString();
```

### Menampilkan di Blazor

```razor
@using OpenSceneGraph.Net.Blazor
<div style="height:600px"><OsgCanvas Scene="@root" /></div>
```

Canvas mengisi elemen induknya, jadi **elemen induk harus memiliki tinggi**. Tidak perlu tag script atau registrasi service.

### Menampilkan di jendela osgViewer native

```csharp
using OpenSceneGraph.Net.Native;
OsgNative.Initialize();
using var viewer = NativeViewer.CreateWindow(1280, 720, "My scene");
viewer.SetScene(root);       // mengonversi graph managed menjadi osg::Node
viewer.Run();                // event handler bawaan OSG: s = statistik, w = wireframe, Esc = keluar
```

### Memuat model

```csharp
OsgNative.Initialize();                                     // opsional: mengaktifkan semua plugin OSG
var cessna = OpenSceneGraph.Net.IO.Registry.ReadNodeFile("cessna.osgt");
var part = OpenSceneGraph.Net.IO.Registry.ReadNodeFile("bracket.stl");   // reader managed
root.AddChild(cessna);
```

## Interaksi

Setiap host meneruskan input mouse dan keyboard sebagai `InputEvent` ke `viewer.EventQueue`. `TrackballManipulator` default bekerja seperti berikut:

| Input | Aksi |
|---|---|
| Seret kiri | Orbit (dengan inersia) |
| Shift+seret kiri / seret tengah | Pan |
| Seret kanan / roda mouse | Zoom |
| Space / h | Home (membingkai seluruh scene) |

Ganti dengan `viewer.CameraManipulator = new FlightManipulator()`. Pilihan lainnya adalah `DriveManipulator`, `TerrainManipulator`, `FirstPersonManipulator`, `KeySwitchManipulator`, dan `AnimationPathManipulator`. Tambahkan `IEventHandler` buatan Anda sendiri ke `viewer.EventHandlers`.

## Build dari source

```powershell
dotnet build OpenSceneGraph.Net.slnx
dotnet test tests/OpenSceneGraph.Net.Tests
dotnet run --project src/OpenSceneGraph.Net.Gallery.Avalonia
dotnet run --project src/OpenSceneGraph.Net.Gallery.Blazor
```

Binary native berada di `src/OpenSceneGraph.Net.Native/runtimes/win-x64/native`. Untuk mem-build ulang (memerlukan Visual Studio dengan workload C++ serta Rust):

```powershell
powershell -File native/build-osg.ps1      # clone + build OpenSceneGraph 3.6.5 ke native/third_party/osg-install
powershell -File native/build-osgnet.ps1   # build shim C osgnet lalu salin DLL + plugin
cargo build --release --manifest-path native/osgnet-fast/Cargo.toml
```
