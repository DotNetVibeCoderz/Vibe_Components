# Architecture

```
        ┌──────────── your app ────────────┐
        │  scene graph (OpenSceneGraph.Net) │   Group / Transform / Lod / Geometry / StateSet ...
        └──────────────┬───────────────────┘
                       │ Viewer.Frame()
     events → update traversal → pager merge → manipulator → CullVisitor
                       │
                       ▼
                  RenderList            passes (RTT, main, HUD) · sorted draw items · lights · environment
        ┌──────────────┼───────────────────────┬──────────────────────┐
        ▼              ▼                       ▼                      ▼
  GlRenderer      Blazor WebGL2          NativeSceneConverter     your renderer
  (Avalonia)      (OsgCanvas)            → osgViewer (OSG 3.6.5)   (IRenderer)
```

## Projects

| Project | Role |
|---|---|
| `src/OpenSceneGraph.Net` | Core: Math, Scene, State, Meshes, Rendering (cull/render list/shaders), Manipulators, Animation, Particles, IO, Terrain, Geo, Simulation, Science, Interop (Rust fast path) |
| `src/OpenSceneGraph.Net.Rendering.OpenGL` | `GlRenderer` built on Silk.NET.OpenGL |
| `src/OpenSceneGraph.Net.Avalonia` | `OsgView : OpenGlControlBase` |
| `src/OpenSceneGraph.Net.Blazor` | `OsgCanvas` component and a WebGL2 JS module |
| `src/OpenSceneGraph.Net.Native` | P/Invoke into `osgnet.dll`, a C ABI shim over OSG |
| `native/osgnet` | C++ shim (CMake) |
| `native/osgnet-fast` | Rust `cdylib`: normals, heightfield meshes, batch sphere culling, particle integration |
| `src/OpenSceneGraph.Net.Gallery.Core` | 22 samples shared by both gallery apps |
| `src/OpenSceneGraph.Net.Cli`, `src/OpenSceneGraph.Net.Templates` | Tooling |

## The frame

`Viewer.Frame()` does the following, in order:

1. **Advance** the `FrameStamp` (wall clock or `FixedTimeStep`, `TimeScale`, `Paused`).
2. **Event traversal.** Drain `EventQueue`; registered `EventHandlers` get first chance, then the camera manipulator.
3. **Update traversal.** Merge finished `DatabasePager` loads, run every `IUpdateCallback`, raise `Viewer.Updated`, update the manipulator, and expire unused paged tiles.
4. **Cull traversal** (`CullVisitor`). Walk the graph with the accumulated matrix and inherited `ResolvedState`, reject invisible subgraphs, select LOD children, issue paging requests, collect lights, open extra passes for `CameraNode`s, and compute the near/far planes.

The host then calls `renderer.Render(list)`. Renderers never touch the scene graph. They only read the `RenderList` and the `Geometry`/`Texture2D` objects it references, cached on the GPU by `Id`/`Version`.

## RenderList contract

- `Passes` are ordered pre-render (render-to-texture), main, then post-render (HUD).
- Each pass has `Opaque` items sorted by render bin, then state key, then front to back, and `Transparent` items sorted back to front.
- `DrawItem` holds `Geometry`, `World` (row-vector matrix), the shared immutable `ResolvedState`, and the view `Depth`.
- `Lights` are already in world space. The headlight is added automatically when `Environment.HeadLight` is on.
- Matrices are `System.Numerics` (row-vector, `v' = v * M`). Uploading them untransposed to GLSL gives the column-vector equivalent.

## Threading

`Viewer.ThreadingModel`:

- `SingleThreaded`: everything runs on the caller's thread.
- `DrawThreadPerContext`: the cull of frame N runs on a worker while the host draws frame N−1 (one frame of latency). Update never overlaps cull, so mutating the scene from update callbacks is safe.

The database pager always loads on background threads. Results are merged during the update traversal, so the graph is never mutated concurrently.

## Performance notes

- Resolved states are cached per (parent state, StateSet, version), so steady-state culling allocates almost nothing.
- Render passes and draw lists are pooled and reused frame to frame.
- Geometry buffers are uploaded only when `Geometry.Version` changes. Mark dynamic meshes with `BufferUsage.Dynamic`/`Stream`.
- Shared geometry and state sets are instancing: draw the same `Geometry` under many transforms.
- The Rust fast path engages above a size threshold, where P/Invoke overhead is amortised. Set `OSGNET_DISABLE_FAST=1` to benchmark the managed path.
