# OpenSceneGraph.Net Roadmap / Peta Jalan

*Created by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil*

Status of the work so far: [Progress.md](Progress.md). Feature reference: [features.md](features.md).

## Guiding principles / Prinsip

1. **Two layers, one model.** The managed scene graph runs everywhere, including the browser. The native OSG 3.6.5 runtime adds the full plugin ecosystem and osgViewer where native code is allowed.
   *Dua lapisan, satu model:* scene graph managed berjalan di mana saja (termasuk browser); runtime OSG native menambah semua plugin dan osgViewer.
2. **Renderers read, never write.** Every backend consumes the same `RenderList`, and the GLSL is shared between OpenGL and WebGL.
3. **Performance by design.** Pooled render lists, cached resolved state, GPU upload only on `Version` change, and optional Rust hot paths.
4. **Bilingual docs, real screenshots, tested samples.**

## Milestones / Tahapan

### v0.1: Foundation (done) / Fondasi (selesai)
- Core scene graph, StateSet inheritance with OVERRIDE, visitors, culling (frustum, small feature, cluster, occlusion), LOD/PagedLOD/Proxy and the database pager
- Manipulators, animation (paths, clips, skeletal), particles, terrain and DEM/GeoTIFF, WGS84/ECEF/UTM/ENU, map tile sources, DIS, vehicles, orbits, scientific visualisation
- OpenGL renderer (shadows, fog, RTT, HUD, stereo, HDR/bloom), Avalonia `OsgView`, Blazor `OsgCanvas` (WebGL2)
- Native OSG 3.6.5 C shim, 53 plugins, osgViewer, offscreen capture, scene conversion
- Rust `osgnet_fast` acceleration
- Gallery (Blazor and Avalonia, 22 samples), Simulator, `osgnet` CLI, `dotnet new` templates, NuGet packages with icon, Rodin + Blender asset pipeline

### v0.2: Formats and fidelity / Format dan kualitas
- glTF 2.0 / GLB reader and writer in managed code (PBR materials, skins, animations)
- JPEG decoder (managed) and OSG rebuilt with libpng/libjpeg/zlib for native image plugins
- Native → managed conversion of LOD/Switch/PagedLOD structure (today only triangle meshes are converted)
- Custom `ShaderProgram`s and stereo in the Blazor WebGL2 renderer
- Tangent-space normal maps and PBR (metal/roughness) shading path in both renderers
- Cascaded shadow maps and soft spot/point light shadows
- Terrain skirts to hide cracks between tiles at different LODs

### v0.3: Large worlds / Dunia skala besar
- Geocentric tiled terrain (quadtree of PagedLODs on the WGS84 ellipsoid) streamed from DEM and imagery tile sources (OSM, WMS, WMTS, ArcGIS), with disk cache
- Double-precision camera-relative rendering throughout (`Vector3d` world matrices)
- GPU instancing for vegetation and crowds; hardware occlusion queries
- Multi-view `CompositeViewer` and per-camera parallel cull

### v0.4: Simulation and interoperability / Simulasi dan interoperabilitas
- HLA (IEEE 1516) RPR-FOM adapter
- DDS and ROS 2 bridges (topics to transforms)
- MQTT / IoTComNet integration for digital twins
- Fire, detonation and transmitter DIS PDUs; DIS dead-reckoning smoothing filter
- Reinforcement learning hooks (RLNet) for autonomous agents in the Simulator

### v0.5: Immersive and platforms / Imersif dan platform
- OpenXR stereo rendering (SteamVR, Quest Link, Vive), head tracking
- Linux x64/arm64 and macOS native runtime packages (CI-built)
- Android (GLES 3) host via Avalonia mobile
- Dear ImGui-style immediate-mode HUD widgets and text rendering (SDF fonts)

### v1.0: Stable API / API stabil
- API review and freeze, XML docs at 100 %, benchmark suite, sample performance budgets
- Visual regression tests from offscreen renders in CI

## Integrations considered / Integrasi yang dipertimbangkan

| Library | Use |
|---|---|
| Visual.Net | 2D charts overlaid on 3D dashboards |
| ThreeNet | Interop and comparison renderer |
| GravicodeScience | NumPy/SciPy-style arrays to `ScalarField3D` |
| HF.Net / MediaPipeNet / OpenCvSharp | Vision-driven interaction (pose → skeleton), AR overlays |
| TorchSharp / ML.NET / RLNet | Learned controllers and anomaly detection in simulations |
| IoTComNet | Live sensor feeds for digital twins |
