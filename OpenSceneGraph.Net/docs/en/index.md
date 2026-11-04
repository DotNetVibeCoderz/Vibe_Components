# OpenSceneGraph.Net documentation

*Created by Gravicode Studios, led by Kang Fadhil.* · [Bahasa Indonesia](../id/index.md)

| Guide | Contents |
|---|---|
| [Getting started](getting-started.md) | Install, first scene, Avalonia / Blazor / native hosts, building from source |
| [Architecture](architecture.md) | Packages, the frame loop, the RenderList contract, threading |
| [Scene graph](scene-graph.md) | Nodes, transforms, Switch/LOD/PagedLOD/Proxy, StateSet inheritance, visitors, culling, picking |
| [Rendering](rendering.md) | Renderers, lighting, shadows, fog, particles, HUD and RTT cameras, post effects, custom shaders |
| [Native runtime](native-runtime.md) | OpenSceneGraph 3.6.5 shim, formats, osgViewer, offscreen rendering, building OSG |
| [Animation & effects](animation.md) | Animation paths, keyframe clips, skeletons and skinning, particle systems |
| [GIS, terrain & simulation](gis-simulation.md) | Heightfields, DEM/GeoTIFF, LOD terrain, WGS84/ECEF/UTM, map tiles, DIS, vehicles, orbits |
| [Scientific visualisation](scivis.md) | Colormaps, isosurfaces, slices, vector fields, streamlines |
| [Tools](tools.md) | `osgnet` CLI, `dotnet new` templates, Gallery, Simulator, Blender/Rodin asset pipeline |

## Feature coverage at a glance

Status of each area in [features.md](../../features.md). For the per-feature tracker see [Progress.md](../../Progress.md).

| Area | Managed core | Native OSG |
|---|---|---|
| Scene graph (Node, Group, Geode, MatrixTransform, PAT, Switch, LOD, ProxyNode, PagedLOD) | ✅ | ✅ |
| Rendering (GL 3.3 / GLES 3 / WebGL2, VBO/VAO/FBO, GLSL, multi-pass, RTT) | ✅ | ✅ |
| Cameras & manipulators (Trackball, Flight, Drive, Terrain, FirstPerson, KeySwitch, AnimationPath) | ✅ | ✅ (OSG's own) |
| Model loading | OBJ/MTL, STL, PLY, OSGN | 53 OSG plugins (osg/osgt/osgb/ive/3ds/flt/dxf/ac/lwo/x/shp…) |
| Terrain (heightmap, DEM, GeoTIFF, LOD tiles) | ✅ | via conversion |
| Database pager | ✅ (prioritised, async, expiry) | ✅ |
| Culling (frustum, small feature, occlusion, cluster) | ✅ | ✅ |
| Animation (keyframe, path, skeletal) | ✅ | ✅ |
| Particles (smoke, fire, explosion, rain, snow, dust, sparks) | ✅ | — |
| Effects (lighting, shadow maps, reflection approximation, HDR, bloom) | ✅ | lighting |
| GIS (WGS84, ECEF, UTM, ENU, tiles: OSM / WMS / WMTS / ArcGIS) | ✅ | — |
| Simulation (aircraft, UAV, ground, ship, DIS IEEE 1278.1) | ✅ | — |
| VR/AR, HLA, DDS/ROS 2 | roadmap ([PLAN.md](../../PLAN.md)) | |
