# Dokumentasi OpenSceneGraph.Net

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/index.md)

| Panduan | Isi |
|---|---|
| [Memulai](getting-started.md) | Instalasi, scene pertama, host Avalonia / Blazor / native, build dari source |
| [Arsitektur](architecture.md) | Paket, frame loop, kontrak RenderList, threading |
| [Scene graph](scene-graph.md) | Node, transform, Switch/LOD/PagedLOD/Proxy, pewarisan StateSet, visitor, culling, picking |
| [Rendering](rendering.md) | Renderer, pencahayaan, bayangan, kabut, partikel, kamera HUD dan RTT, efek pasca-proses, shader kustom |
| [Runtime native](native-runtime.md) | Shim OpenSceneGraph 3.6.5, format, osgViewer, rendering offscreen, build OSG |
| [Animasi & efek](animation.md) | Animation path, klip keyframe, skeleton dan skinning, sistem partikel |
| [GIS, terrain & simulasi](gis-simulation.md) | Heightfield, DEM/GeoTIFF, terrain LOD, WGS84/ECEF/UTM, tile peta, DIS, kendaraan, orbit |
| [Visualisasi ilmiah](scivis.md) | Colormap, isosurface, irisan (slice), medan vektor, streamline |
| [Tools](tools.md) | CLI `osgnet`, template `dotnet new`, Gallery, Simulator, pipeline aset Blender/Rodin |

## Ringkasan cakupan fitur

Status setiap area dalam [features.md](../../features.md). Untuk pelacak per fitur, lihat [Progress.md](../../Progress.md).

| Area | Inti managed | OSG native |
|---|---|---|
| Scene graph (Node, Group, Geode, MatrixTransform, PAT, Switch, LOD, ProxyNode, PagedLOD) | ✅ | ✅ |
| Rendering (GL 3.3 / GLES 3 / WebGL2, VBO/VAO/FBO, GLSL, multi-pass, RTT) | ✅ | ✅ |
| Kamera & manipulator (Trackball, Flight, Drive, Terrain, FirstPerson, KeySwitch, AnimationPath) | ✅ | ✅ (milik OSG sendiri) |
| Pemuatan model | OBJ/MTL, STL, PLY, OSGN | 53 plugin OSG (osg/osgt/osgb/ive/3ds/flt/dxf/ac/lwo/x/shp…) |
| Terrain (heightmap, DEM, GeoTIFF, tile LOD) | ✅ | melalui konversi |
| Database pager | ✅ (berprioritas, async, kedaluwarsa) | ✅ |
| Culling (frustum, small feature, occlusion, cluster) | ✅ | ✅ |
| Animasi (keyframe, path, skeletal) | ✅ | ✅ |
| Partikel (asap, api, ledakan, hujan, salju, debu, percikan api) | ✅ | — |
| Efek (pencahayaan, shadow map, aproksimasi refleksi, HDR, bloom) | ✅ | pencahayaan |
| GIS (WGS84, ECEF, UTM, ENU, tile: OSM / WMS / WMTS / ArcGIS) | ✅ | — |
| Simulasi (pesawat, UAV, kendaraan darat, kapal, DIS IEEE 1278.1) | ✅ | — |
| VR/AR, HLA, DDS/ROS 2 | roadmap ([PLAN.md](../../PLAN.md)) | |
