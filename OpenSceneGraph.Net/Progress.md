# Progress / Kemajuan

*Created by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil*

Legend / Keterangan: ✅ done/selesai · 🟡 partial/sebagian · ⏳ planned/direncanakan (see [PLAN.md](PLAN.md))

Last update / Pembaruan terakhir: 2026-11-04

## Feature tracker (from features.md) / Pelacak fitur

| # | Area | Status | Notes / Catatan |
|---|---|---|---|
| 1 | Scene graph: Node, Group, Geode, MatrixTransform, PAT, Switch, LOD, ProxyNode, PagedLOD, state graph | ✅ | Plus AutoTransform, CameraNode, LightSource, OccluderNode |
| 2 | Rendering: GL 2.x–4.x, VBO/VAO/FBO, GLSL, multi-pass, RTT | ✅ | GL 3.3 core / GLES 3 / WebGL2; PBO not used |
| 3 | Camera: multiple cameras, Trackball/Flight/Drive/Terrain, stereo | ✅ | HUD and RTT cameras, anaglyph / side-by-side / top-bottom |
| 4 | Model loading: OSG/OSGB/OSGT/IVE, OBJ, STL, DAE, FLT, 3DS, DXF, FBX, CAD | 🟡 | Managed: OBJ/MTL, STL, PLY, OSGN. Native: 53 plugins incl. osg/osgt/osgb/ive/3ds/flt/dxf/ac/lwo/x/shp. DAE, FBX and STEP/IGES need third-party SDKs (not built) |
| 5 | Terrain: heightmap, DEM, GeoTIFF, streaming | 🟡 | ASCII grid + uncompressed GeoTIFF + tiled LOD; streaming via PagedLOD; geocentric streaming in v0.3 |
| 6 | Database pager: dynamic/async loading, large datasets | ✅ | Prioritised, de-duplicated, timeout, merge budget, expiry |
| 7 | LOD: LOD node, PagedLOD, distance/pixel size | ✅ | |
| 8 | Culling: frustum, small feature, occlusion, cluster | ✅ | Occlusion is CPU convex occluders (OSG style); GPU queries ⏳ |
| 9 | Animation: skeletal, keyframe, path | ✅ | CPU linear-blend skinning |
| 10 | Particles: smoke, fire, explosion, rain, snow, dust | ✅ | Plus sparks; Rust integrator for large systems |
| 11 | Effects: lighting, shadow maps, reflection, refraction, HDR, bloom | 🟡 | Reflection is Fresnel environment colour; real planar reflection via RTT camera; refraction ⏳ |
| 12 | Shader management: programs, uniforms, runtime loading, generation | ✅ | Custom `ShaderProgram` + uniforms; built-in shader library |
| 13 | GIS: osgEarth integration, WGS84, UTM, ECEF, GeoTIFF, WMS, WMTS, ArcGIS, OSM | 🟡 | Geodesy + tile sources + ENU tile quads ✅; full osgEarth-like globe engine ⏳ |
| 14 | Simulation: flight, maritime, UAV, tank, DIS, HLA | 🟡 | Vehicles + DIS Entity State ✅; HLA ⏳ |
| 15 | VR & AR: OpenXR, Oculus, SteamVR, Vive | ⏳ | Stereo rendering is in place |
| 16 | UI: Qt, ImGui, CEGUI, HUD | 🟡 | Avalonia + Blazor hosts and in-graph HUD cameras; ImGui-style widgets ⏳ |
| 17 | Multi-threading models | 🟡 | SingleThreaded, DrawThreadPerContext (managed); all OSG models in the native viewer |
| 18 | Networking: DDS, ROS 2, ZeroMQ, gRPC, MQTT | 🟡 | DIS over UDP ✅; others ⏳ |
| 19 | Scientific visualisation: CFD, FEM, medical, seismic | ✅ | Isosurfaces, slices, glyphs, streamlines, colormaps |
| 20 | Plugin architecture | ✅ | `IReaderWriter` / `IImageReader` registry + native fallback |
| 21 | Platforms: Windows, Linux, macOS, embedded, Android | 🟡 | Managed: all .NET platforms + browser. Native package: Windows x64 |
| 22 | Ecosystem: osgParticle, osgShadow, osgAnimation, osgManipulator… | 🟡 | Equivalents implemented in managed code; native OSG libraries shipped |

## Deliverables (from the brief) / Hasil kerja

| Item | Status | Where / Lokasi |
|---|---|---|
| .NET 10 libraries | ✅ | `src/OpenSceneGraph.Net*` |
| Rust for performance | ✅ | `native/osgnet-fast` (normals, heightfield, culling, particles) |
| Native OSG wrapper | ✅ | `native/osgnet` + `src/OpenSceneGraph.Net.Native` |
| Blazor component | ✅ | `src/OpenSceneGraph.Net.Blazor` |
| Avalonia component | ✅ | `src/OpenSceneGraph.Net.Avalonia` |
| Gallery (Blazor + Avalonia) | ✅ | `src/OpenSceneGraph.Net.Gallery.*` (22 samples, 9 categories) |
| Simulator | ✅ | `src/OpenSceneGraph.Net.Simulator` |
| CLI tool | ✅ | `src/OpenSceneGraph.Net.Cli` (`osgnet`) |
| Project templates | ✅ | `src/OpenSceneGraph.Net.Templates` |
| NuGet packages + icon | ✅ | `artifacts/nuget`, `assets/icon.png` (Rodin) |
| Bilingual docs + screenshots | ✅ | `docs/en`, `docs/id`, `docs/images` |
| 3D assets via Rodin + Blender | ✅ | `assets/models/source`, `tools/blender/glb_to_obj.py` |
| Tests | ✅ | `tests/OpenSceneGraph.Net.Tests` (66) |
| CI + NuGet publish on tag | ✅ | `.github/workflows/openscenegraph-net.yml` (`osgnet-v*` tags) |

## Known issues / Masalah yang diketahui

- Blazor: custom `ShaderProgram`s and stereo are ignored. Real-GPU frame rates haven't been measured (only SwiftShader); interpreted .NET limits heavy samples (night-city, math-surface spend 150-340 ms per frame in .NET). AOT (`-p:OsgAot=true`) has not been built or verified.
- `MapTileLoader` network download and WebGL context-loss recovery have never been run; side-by-side and top-bottom stereo were not run (anaglyph was).
- Native package is Windows x64 only; lights and environment are not converted to native OSG scenes.
- A `LightSource` under a render-to-texture camera also lights the main pass (lights are collected per frame, not per pass).
- `bar-chart` shadows are hard to see. Terrain shows thin cracks between tiles at different LODs (skirts planned). Rain and snow are soft dots, not streaks.
- Telemetry keys and feature badges in samples are English in both UI languages.

## Log

- **2026-11-04**: Solution scaffolded. OSG 3.6.5 built from source (CMake 4, C++14 `_HAS_STD_BYTE=0`, osga `_FPOSOFF` patch). Core library, OpenGL renderer, Avalonia control, native shim, Rust fast path, gallery samples, CLI, templates, docs. Rodin icon and tank/jet models converted with headless Blender.
- **2026-11-04**: All 22 samples verified on desktop GL (ANGLE) and WebGL2 (SwiftShader). Render-to-texture, custom shaders (desktop) and anaglyph stereo exercised for the first time. Moved into the Vibe_Components monorepo with CI.
