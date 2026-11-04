# Getting started

## Requirements

- .NET 10 SDK.
- For desktop rendering: a GPU with OpenGL 3.3, or Direct3D 11 through ANGLE (Avalonia's default on Windows).
- For web: a WebGL2 browser (current Chrome, Edge, Firefox and Safari).
- For the native package: Windows x64. The package ships prebuilt OpenSceneGraph 3.6.5 binaries.

## Packages

| Package | Use it for |
|---|---|
| `OpenSceneGraph.Net` | The scene graph and everything managed (always required) |
| `OpenSceneGraph.Net.Rendering.OpenGL` | The OpenGL renderer (pulled in by the Avalonia package) |
| `OpenSceneGraph.Net.Avalonia` | `OsgView` control for desktop apps |
| `OpenSceneGraph.Net.Blazor` | `OsgCanvas` component (WebGL2) for Blazor WebAssembly / Server |
| `OpenSceneGraph.Net.Native` | Native OSG: all OSG file formats, osgViewer windows, offscreen rendering |
| `OpenSceneGraph.Net.Templates` | `dotnet new osgnet-console / osgnet-avalonia / osgnet-blazor` |
| `OpenSceneGraph.Net.Cli` | The `osgnet` global tool |

## Your first scene

The coordinate system follows OSG: **Z is up**, units are metres, and models face **+Y**.

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
box.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 45));   // degrees per second
root.AddChild(box);
```

### Show it in Avalonia

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

### Show it in Blazor

```razor
@using OpenSceneGraph.Net.Blazor
<div style="height:600px"><OsgCanvas Scene="@root" /></div>
```

The canvas fills its parent, so **the parent needs a height**. No script tag or service registration is required.

### Show it in a native osgViewer window

```csharp
using OpenSceneGraph.Net.Native;
OsgNative.Initialize();
using var viewer = NativeViewer.CreateWindow(1280, 720, "My scene");
viewer.SetScene(root);       // converts the managed graph to osg::Node
viewer.Run();                // OSG's own event handlers: s = stats, w = wireframe, Esc = quit
```

### Load models

```csharp
OsgNative.Initialize();                                     // optional: enables all OSG plugins
var cessna = OpenSceneGraph.Net.IO.Registry.ReadNodeFile("cessna.osgt");
var part = OpenSceneGraph.Net.IO.Registry.ReadNodeFile("bracket.stl");   // managed reader
root.AddChild(cessna);
```

## Interaction

Every host forwards mouse and keyboard as `InputEvent`s to `viewer.EventQueue`. The default `TrackballManipulator` works like this:

| Input | Action |
|---|---|
| Left drag | Orbit (with inertia) |
| Shift+left drag / middle drag | Pan |
| Right drag / wheel | Zoom |
| Space / h | Home (frame the scene) |

Change it with `viewer.CameraManipulator = new FlightManipulator()`. The other options are `DriveManipulator`, `TerrainManipulator`, `FirstPersonManipulator`, `KeySwitchManipulator` and `AnimationPathManipulator`. Add your own `IEventHandler`s to `viewer.EventHandlers`.

## Build from source

```powershell
dotnet build OpenSceneGraph.Net.slnx
dotnet test tests/OpenSceneGraph.Net.Tests
dotnet run --project src/OpenSceneGraph.Net.Gallery.Avalonia
dotnet run --project src/OpenSceneGraph.Net.Gallery.Blazor
```

The native binaries live in `src/OpenSceneGraph.Net.Native/runtimes/win-x64/native`. To rebuild them (needs Visual Studio with the C++ workload and Rust):

```powershell
powershell -File native/build-osg.ps1      # clone + build OpenSceneGraph 3.6.5 into native/third_party/osg-install
powershell -File native/build-osgnet.ps1   # build the osgnet C shim and stage DLLs + plugins
cargo build --release --manifest-path native/osgnet-fast/Cargo.toml
```
