# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

This project lives in the `OpenSceneGraph.Net/` subfolder of the `Vibe_Components` monorepo; run the commands below from this folder. CI is `.github/workflows/openscenegraph-net.yml` at the repo root (a tag like `osgnet-v0.1.0` publishes to NuGet; the templates pin package versions, so bump them together with `VersionPrefix`). Native binaries under `src/*/runtimes` are git-ignored and rebuilt by `native/build-*.ps1` or CI, so on a fresh clone the native tests skip themselves.

OpenSceneGraph.Net is a .NET 10 port and wrapper of OpenSceneGraph: a managed scene graph (runs anywhere, including Blazor WASM), plus an optional native OSG 3.6.5 runtime behind a C ABI shim. It also ships an OpenGL renderer, Avalonia and Blazor hosts, a gallery (22 samples), a simulator, a CLI and `dotnet new` templates.
`OpenSceneGraph.Net.txt` (Indonesian) is the product brief and `features.md` is the feature list; `PLAN.md` holds the roadmap and `Progress.md` the tracker. Update `Progress.md` when you complete something.

## Commands

```powershell
dotnet build OpenSceneGraph.Net.slnx                          # .slnx (XML) solution
dotnet test tests/OpenSceneGraph.Net.Tests                    # 66 tests incl. every gallery sample + native OSG
dotnet test tests/OpenSceneGraph.Net.Tests --filter "FullyQualifiedName~GeoTests"
dotnet run --project src/OpenSceneGraph.Net.Gallery.Avalonia [-- --sample flight-sim --lang id]
<gallery exe> --screenshot all docs/images/gallery             # regenerate doc screenshots (needs a GPU)
dotnet run --project src/OpenSceneGraph.Net.Gallery.Blazor
dotnet run --project src/OpenSceneGraph.Net.Simulator
dotnet run --project src/OpenSceneGraph.Net.Cli -- info        # or: render sample:<id> out.png, convert, inspect...
dotnet pack src/<Project> -c Release                           # -> artifacts/nuget
cargo build --release --manifest-path native/osgnet-fast/Cargo.toml
powershell -File native/build-osg.ps1       # OSG 3.6.5 from source (~20 min) -> native/third_party/osg-install
powershell -File native/build-osgnet.ps1    # C shim + stage DLLs/plugins into src/OpenSceneGraph.Net.Native/runtimes/win-x64/native
blender -b --python tools/blender/glb_to_obj.py -- in.glb out.obj --size 7 --faces 6000 --yaw 180
```

Environment: Windows, PowerShell 5.1 is primary (no `&&`). Git Bash is available, but big heredocs in Bash calls break; write files with the Write tool. When editing files containing non-ASCII text (°, ₈, Indonesian) from Python, pass `encoding='utf-8'` explicitly. The cp1252 default fails or corrupts.

## Architecture (read several files to see this)

- **Frame loop:** `Viewer.Frame()` advances the FrameStamp, drains `EventQueue` (EventHandlers first, then the CameraManipulator), runs the update traversal (pager merge → `IUpdateCallback`s → `Viewer.Updated` → manipulator → expiry), then `CullVisitor.Run` → `RenderList`. Hosts call `renderer.Render(list)`. Renderers never mutate the scene.
- **RenderList is the backend contract** (`src/OpenSceneGraph.Net/Rendering/RenderList.cs`): ordered passes (PreRender RTT / Main / PostRender HUD), sorted Opaque/Transparent `DrawItem`s (Geometry, World, shared immutable `ResolvedState`), world-space Lights, and the Environment. GPU caches key on `Geometry.Id`/`Version` and `Texture2D.Id`/`Image.Version`. After mutating arrays in place, call `MarkDirty()`.
- **One shader library for all GL backends:** `Rendering/ShaderLibrary.cs` (GLSL ES 3.00 bodies) is used by `GlRenderer` (Silk.NET, GL 3.3 core or GLES 3 via ANGLE, which is what Avalonia uses on Windows) and by the Blazor WebGL2 JS renderer. `GlRenderer` is the reference for uniform names and state semantics. If you change a uniform, change both renderers.
- **Matrices:** `System.Numerics`, row-vector (`world = local * parent`). Upload them to GLSL untransposed. Scene convention: Z-up, metres, models face +Y. `MathUtil.FromHeadingPitchRoll` uses heading clockwise from north.
- **StateSet inheritance:** null = inherit; `Override` uses a bitmask per attribute (`ResolvedState.Merge`). A material alpha below 1 implies blending, the transparent bin and no depth write. Resolved states are cached in the CullVisitor per (parent, StateSet, version).
- **Native layer:** `native/osgnet/osgnet.{h,cpp}` is the C ABI (opaque ref-counted handles; matrices as `osg::Matrixf` memory = `Matrix4x4`), P/Invoked by `src/OpenSceneGraph.Net.Native` (`NativeMethods.cs`). `OsgNative.Initialize()` adds the plugin path and installs `Registry.NativeFallback`, so `Registry.ReadNodeFile` transparently uses OSG plugins. Conversion: managed→native bakes transforms (Switch = active children, Lod = first child); native→managed extracts only triangle meshes with world matrix, material, texture and lighting. The OSG build has no png/jpeg plugins (no libs); PNG is decoded on the managed side.
- **Rust fast path** (`native/osgnet-fast`, `Interop/FastPath.cs`): optional; every call has a managed fallback and size thresholds. `OSGNET_DISABLE_FAST=1` forces managed. Keep `osgnet_fast_abi_version` in sync.
- **Gallery samples** (`src/OpenSceneGraph.Net.Gallery.Core/SampleCatalog*.cs`) build both the scene and the viewer configuration (manipulator, environment, `Updated` handlers, `ctx.Telemetry`, `ctx.Controls`) and are shared by the Blazor and Avalonia galleries. All text is bilingual (`Text(En, Id)`). Assigning `Viewer.CameraManipulator` disables auto-home on `SceneData` changes. Rodin/Blender models are embedded resources loaded by `Assets.Tank()`/`Assets.Jet()`, with procedural fallbacks in `Models`.
- **Threading:** `ThreadingModel.DrawThreadPerContext` culls frame N on a worker while the host draws N−1; update never overlaps cull. The DatabasePager loads on workers and merges in update.

## Conventions

- Branding: docs and apps carry "Created by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil".
- Docs are bilingual: `docs/en/*.md` and `docs/id/*.md` share file names. Keep them in sync and put screenshots in `docs/images/`.
- Package metadata (version, icon `assets/icon.png`, README) comes from `Directory.Build.props`. Projects are `IsPackable=false` unless they set it.
- NuGet publishing goes through CI with the `NUGET_API_KEY` repository secret. Publishing is outward-facing and can't be undone, so don't tag a release unless asked.
- MCP: Rodin (image/3D generation) and Blender (`.mcp.json`) are available for assets. Don't call `read_factory_settings` through the Blender MCP; it kills the addon server. Use headless `blender -b` for batch conversion instead.
- Use at most 2–3 subagents at a time.
