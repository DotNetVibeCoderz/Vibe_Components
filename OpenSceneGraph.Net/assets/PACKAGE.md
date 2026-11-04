# OpenSceneGraph.Net

A .NET 10 scene graph for [OpenSceneGraph](https://github.com/openscenegraph/OpenSceneGraph): a managed core that runs anywhere .NET runs (including the browser), an optional native OSG 3.6.5 runtime, OpenGL and WebGL2 renderers, and Avalonia and Blazor components.

Created by **Gravicode Studios**, led by **Kang Fadhil** · Dibuat oleh **Gravicode Studios** dipimpin oleh **Kang Fadhil**

| Package | Use it for |
|---|---|
| `OpenSceneGraph.Net` | Scene graph, culling, LOD and paging, animation, particles, terrain, GIS, DIS, scientific visualisation |
| `OpenSceneGraph.Net.Rendering.OpenGL` | OpenGL 3.3 / GLES 3 renderer |
| `OpenSceneGraph.Net.Avalonia` | `OsgView` desktop control |
| `OpenSceneGraph.Net.Blazor` | `OsgCanvas` WebGL2 component |
| `OpenSceneGraph.Net.Native` | Real OSG 3.6.5 on Windows x64: 53 file-format plugins, osgViewer, offscreen rendering |
| `OpenSceneGraph.Net.Templates` | `dotnet new osgnet-console`, `osgnet-avalonia`, `osgnet-blazor` |
| `OpenSceneGraph.Net.Cli` | `osgnet` tool: inspect, convert, render, terrain, DIS, geo |

```csharp
using System.Numerics;
using OpenSceneGraph.Net.Animation;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

var root = new Group();
root.AddChild(new LightSource(Light.Sun(new Vector3(-0.4f, 0.5f, -0.8f))));
var spinner = new PositionAttitudeTransform { Position = new Vector3(0, 0, 2) };
spinner.AddChild(Shapes.Torus(2, 0.6f, 64, 24, Color.FromHex("#4cc9f0")));
spinner.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 45));
root.AddChild(spinner);

// Avalonia: View.Scene = root;
// Blazor:   <div style="height:600px"><OsgCanvas Scene="root" /></div>
```

Documentation (English and Bahasa Indonesia), a gallery of 22 use cases and a simulator are in the [repository](https://github.com/DotNetVibeCoderz/Vibe_Components/tree/main/OpenSceneGraph.Net).

MIT licensed. `OpenSceneGraph.Net.Native` redistributes OpenSceneGraph 3.6.5 binaries under the OpenSceneGraph Public License (`OpenSceneGraph-LICENSE.txt` in the package).
