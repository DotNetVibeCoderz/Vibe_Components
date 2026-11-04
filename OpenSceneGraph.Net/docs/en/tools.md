# Tools

## osgnet CLI

```bash
dotnet tool install -g OpenSceneGraph.Net.Cli      # or: dotnet run --project src/OpenSceneGraph.Net.Cli -- <command>
```

| Command | Example |
|---|---|
| `info` | Shows versions, native OSG, Rust fast path, and managed and native plugins |
| `inspect <file> [--tree] [--native]` | `osgnet inspect cow.osgt --tree` |
| `convert <in> <out> [--native]` | `osgnet convert model.3ds model.osgb` · `osgnet convert part.stl part.obj` |
| `optimize <in> <out>` | `osgnet optimize city.osgt city_opt.osgb` |
| `view <file\|sample:id>` | `osgnet view sample:night-city` (native osgViewer window) |
| `render <file\|sample:id> <out.png>` | `osgnet render sample:molecule molecule.png --width 1920 --height 1080` |
| `terrain <out> [--dem f.tif] [--size --spacing --height --seed --tiles --levels]` | `osgnet terrain java.osgn --dem java.tif --tiles 8 --levels 4` |
| `samples` / `sample <id> <out>` | `osgnet sample ct-scan ct.osgt` |
| `dis listen` / `dis send` | `osgnet dis send --target 239.1.2.3 --count 100` |
| `geo <lat> <lon> [h]` | `osgnet geo -6.2088 106.8456` |

## Project templates

```bash
dotnet new install OpenSceneGraph.Net.Templates
dotnet new osgnet-console  -o Hello3D        # native osgViewer window, or `dotnet run -- --render`
dotnet new osgnet-avalonia -o Desktop3D      # Avalonia window + OsgView
dotnet new osgnet-blazor   -o Web3D          # Blazor WebAssembly + OsgCanvas
```

## Gallery

22 samples in 9 categories. The scene code is in `src/OpenSceneGraph.Net.Gallery.Core/SampleCatalog*.cs` and is shared by:

- **Web:** `dotnet run --project src/OpenSceneGraph.Net.Gallery.Blazor`, then open the printed URL. Deep links have the form `/sample/<id>`.
- **Desktop:** `dotnet run --project src/OpenSceneGraph.Net.Gallery.Avalonia`. The `--screenshot all docs/images/gallery` option writes PNGs for every sample.

Each sample shows a description, the OSG features it uses, live telemetry, the key bindings, and a copyable code snippet. The language can be switched between English and Bahasa Indonesia.

## Simulator

`dotnet run --project src/OpenSceneGraph.Net.Simulator`: an aircraft, UAV, ground vehicle or ship over generated terrain, with weather (fog, rain, snow), time of day, a HUD, a minimap, mission waypoints, chase and cockpit cameras, and optional DIS broadcasting.

## 3D asset pipeline (Rodin + Blender)

The detailed tank and jet models in the gallery were generated with **Rodin** (Hyper3D) from text prompts and then processed by **Blender** running headless:

```bash
blender -b --python tools/blender/glb_to_obj.py -- in.glb out.obj --size 7 --faces 6000 --yaw 180
```

The script joins the meshes, orients the model Z-up with the nose along +Y, scales it to real-world size, rests it on z = 0, decimates it to the triangle budget, and bakes the base-colour texture to PNG for OpenSceneGraph.Net's managed OBJ/MTL reader. Source GLBs (20 MB) are not committed; the converted OBJ + PNG are in `src/OpenSceneGraph.Net.Gallery.Core/Assets/`. The NuGet icon (`assets/icon.png`) was also generated with Rodin.
