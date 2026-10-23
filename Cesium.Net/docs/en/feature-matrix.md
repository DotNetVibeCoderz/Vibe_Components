[Bahasa Indonesia](../id/feature-matrix.md)

# Feature matrix

This page maps every item of the CesiumJS feature list (`features.md`) to how Cesium.Net covers it.

| Mark | Meaning |
|---|---|
| ✅ API | A typed C# API exists |
| 🟡 Partial | Partly wrapped; the rest via `ExecuteScriptAsync` or your own code |
| 🔧 Script | Not wrapped; reachable through `ExecuteScriptAsync` (raw CesiumJS) |
| ⚙️ Built-in | Done automatically by CesiumJS; nothing to call |
| ➖ N/A | Doesn't apply to a .NET wrapper, or is a service-side feature |
| ❌ Not covered | Not available; use other tools |

The Sample column lists gallery sample ids (`/sample/{id}` in the Blazor gallery). See [Gallery](gallery.md).

## 1. Rendering engine

| Feature | Status | How |
|---|---|---|
| 3D globe visualization | ✅ API | `CesiumViewer` / `CesiumView`, `SceneMode.Scene3D` (sample `hello-globe`) |
| Ellipsoidal Earth model | ✅ API | WGS84 everywhere; `Ellipsoid.Wgs84`, `TerrainOptions.Ellipsoid()` |
| Atmospheric scattering | ✅ API | `SceneSettings.ShowSkyAtmosphere`, `ShowGroundAtmosphere`, `Atmosphere*Shift`, `AtmosphereLightIntensity` |
| Skybox and horizon rendering | 🟡 Partial | `ShowSkyBox`, `BackgroundColor`; custom skybox textures via script |
| 2D mode and Columbus view | ✅ API | `Scene.SetModeAsync`, `CesiumViewerOptions.SceneMode` (sample `scene-modes`) |
| GPU-accelerated rendering | ⚙️ Built-in | WebGL in the browser or WebView |
| Frustum culling | ⚙️ Built-in | |
| Occlusion culling | ⚙️ Built-in | Horizon culling by CesiumJS |
| Level of detail | ✅ API | 3D Tiles `MaximumScreenSpaceError`, `SkipLevelOfDetail`, `DynamicScreenSpaceError`; `DistanceDisplayCondition` (sample `discrete-lod`) |
| Instanced rendering | 🟡 Partial | Instanced 3D Tiles (`i3dm`) render natively; `Primitives.AddPointsAsync` batches points; generic `GeometryInstance` primitives via script |

## 2. Terrain & elevation

| Feature | Status | How |
|---|---|---|
| Cesium World Terrain | ✅ API | `TerrainOptions.WorldTerrain()` (ion token) |
| Custom terrain provider | ✅ API | `CesiumTerrainServer` (quantized-mesh URL), `Ion` asset, `ArcGisElevation` (custom ImageServer); custom JS providers via script |
| Terrain exaggeration | ✅ API | `Terrain.SetExaggerationAsync` (sample `terrain-exaggeration`) |
| Terrain clipping | ✅ API | `Terrain.SetClippingPolygonsAsync`, `GlobeTranslucency` (sample `globe-clipping`) |
| Terrain shadows | ✅ API | `SceneSettings.TerrainShadows` |
| Elevation query | ✅ API | `Terrain.GetHeightAsync` |
| Height sampling | ✅ API | `Terrain.SampleHeightsAsync` (batched, most detailed) |
| Terrain profiling | ✅ API | `Terrain.GetProfileAsync` (sample `elevation-profile`) |
| Ground clamping | ✅ API | `HeightReference`, `ClampToGround`, `ClassifyGround`, `DataSourceOptions.ClampToGround` |

## 3. Imagery & map layers

| Feature | Status | How |
|---|---|---|
| Satellite imagery | ✅ API | `ImageryLayerOptions.Satellite()` (Esri), ion assets, Bing (sample `base-maps`) |
| Street maps | ✅ API | `OpenStreetMap()`, `Streets()`, `Dark()`, `Light()` |
| Topographic maps | ✅ API | `Topographic()` |
| Weather maps | 🟡 Partial | Any weather WMS/WMTS/XYZ service as a layer; no built-in weather provider (sample `weather-radar`) |
| OpenStreetMap | ✅ API | Default base layer |
| Bing Maps | ✅ API | `ImageryProviderKind.Bing` + `Key`, or ion assets 2–4 |
| ArcGIS services | ✅ API | `ArcGisMapServer`, Esri presets, `ArcGisElevation` terrain |
| WMS | ✅ API | `ImageryLayerOptions.Wms(...)` (sample `wms-wmts`) |
| WMTS | ✅ API | `ImageryLayerOptions.Wmts(...)` |
| TMS | ✅ API | `ImageryProviderKind.Tms` |
| Custom imagery providers | 🟡 Partial | `UrlTemplate`, `SingleTile`, `Grid`, `TileCoordinates`; a JS `ImageryProvider` subclass via script |
| Layer opacity & blending | ✅ API | `Alpha`, `Brightness`, `Contrast`, `Hue`, `Saturation`, `Gamma`, `SplitDirection` + `SetSplitPositionAsync`, ordering (sample `layer-blending`) |

## 4. OGC 3D Tiles

| Feature | Status | How |
|---|---|---|
| Massive dataset streaming | ✅ API | `Tilesets.AddAsync` (URL, ion, OSM Buildings, Google Photorealistic) |
| Runtime LOD | ✅ API | `MaximumScreenSpaceError` via `UpdateAsync` (sample `discrete-lod`) |
| View-dependent loading | ⚙️ Built-in | Plus `SkipLevelOfDetail`, `DynamicScreenSpaceError` |
| Smart city visualization | ✅ API | OSM Buildings + `TilesetStyle` conditions (sample `feature-styling`) |
| BIM visualization | 🟡 Partial | Load BIM tilesets (e.g. ion conversions); feature picking with properties; per-feature show/color via style expressions. No BIM tree or element API |
| LiDAR point clouds | ✅ API | `PointCloudShading` (attenuation, eye-dome lighting), `PointSize` style (sample `point-cloud`) |
| Photogrammetry meshes | ✅ API | Tilesets, `HeightOffset`, clipping polygons |
| Digital twin support | 🟡 Partial | Tiles + custom shaders + clipping + realtime entities; you provide twin logic |

## 5. 3D models & formats

| Feature | Status | How |
|---|---|---|
| glTF / GLB | ✅ API | `ModelGraphics` |
| CZML | ✅ API | `DataSourceKind.Czml` |
| GeoJSON | ✅ API | `DataSourceOptions.GeoJsonUrl/GeoJsonData`, styling, extrusion |
| KML / KMZ | ✅ API | `DataSourceOptions.Kml` (KMZ by URL) |
| GPX | ✅ API | `DataSourceOptions.Gpx` |
| TopoJSON | ✅ API | `DataSourceOptions.TopoJson` |
| Animation | 🟡 Partial | `ModelGraphics.RunAnimations` (all clips); selecting individual clips or articulations via script |
| PBR materials | ⚙️ Built-in | glTF PBR is rendered natively; `Color`, `Silhouette*` tints |
| Runtime transformations | ✅ API | `Entities.UpdateAsync` (position, orientation, scale), `UpdatePositionsAsync` with heading |

## 6. Time-dynamic visualization

| Feature | Status | How |
|---|---|---|
| Timeline control | ✅ API | `Animation`/`Timeline` widgets, `ClockApi`, `ClockTick` |
| Historical playback | ✅ API | `ClockOptions` (range, multiplier, `LoopStop`), `SampledPositions` |
| Asset tracking | ✅ API | `RealtimeEntityFeed`, `TrackEntityAsync` |
| Aircraft simulation | ✅ API | `SampledPosition` + `OrientToVelocity` + `Path` |
| Ship tracking | ✅ API | Realtime feed / sampled positions |
| Vehicle tracking | ✅ API | Realtime feed / sampled positions |
| Satellite tracking | ✅ API | `KeplerianOrbit` → `SampledPositions` (SGP4/TLE propagation not included) |
| Time-based simulation | ✅ API | Clock + `Availability` + `AddSamplesAsync` |

## 7. Aerospace & satellite

| Feature | Status | How |
|---|---|---|
| Orbit visualization | ✅ API | `KeplerianOrbit.ToSampledPositions` + `PathGraphics` |
| Ground tracks | ✅ API | `KeplerianOrbit.GroundTrack` |
| Mission planning | 🟡 Partial | Building blocks: `Geodesy` (waypoints, bearings), terrain profiles, corridors. No planner UI or optimizer |
| Flight paths | ✅ API | Sampled positions, geodesic polylines, corridors |
| Air traffic visualization | ✅ API | `RealtimeEntityFeed`, heading-aware billboards and models |
| Coverage analysis | 🟡 Partial | `KeplerianOrbit.FootprintRadius`, domes and sectors; no access windows or visibility computation |
| Sensor volumes | ✅ API | `EllipsoidGraphics.Dome`/`Sector`, clock/cone angles, cones via `CylinderGraphics` |

## 8. GIS & spatial analysis

| Feature | Status | How |
|---|---|---|
| Point, polyline, polygon | ✅ API | Entity graphics (sample `entity-showcase`) |
| Rectangle & circle | ✅ API | `RectangleGraphics`, `EllipseGraphics`, `Geodesy.Circle` |
| Corridor & walls | ✅ API | `CorridorGraphics`, `WallGraphics` |
| Distance calculation | ✅ API | `Geodesy.Distance` (Vincenty), `DrawMode.MeasureDistance` |
| Area calculation | ✅ API | `Geodesy.Area`, `DrawMode.MeasureArea` |
| Coordinate conversion | ✅ API | `CoordinateConverter` |
| Spatial queries | 🟡 Partial | `Geodesy.Contains`, `GeoRectangle.Contains`, view rectangle, radius checks. No spatial index or topology operations (union, buffer of polygons) |
| Ray casting | 🟡 Partial | Screen-ray picking via `Scene.PickAsync` / click events (tiles, terrain, ellipsoid); arbitrary-ray intersection (`scene.pickFromRay`) via script |

## 9. Entity system

| Feature | Status | How |
|---|---|---|
| Billboard | ✅ API | `BillboardGraphics` |
| Marker | ✅ API | `PinColor`/`PinText` (PinBuilder) |
| Icon | ✅ API | `Image` URL/data URL, `PinIcon` (Maki) |
| Polyline | ✅ API | `PolylineGraphics` + materials |
| Polygon | ✅ API | `PolygonGraphics` (holes, extrusion, classification) |
| Volume objects | ✅ API | Box, cylinder, ellipsoid, extruded polygons, walls |
| Dynamic entities | ✅ API | `UpdateAsync`, `UpdatePositionsAsync`, sampled positions |

## 10. Camera system

| Feature | Status | How |
|---|---|---|
| Zoom | ✅ API | `ZoomAsync`, mouse wheel |
| Pan | ✅ API | `MoveAsync` |
| Rotate | ✅ API | `RotateAsync`, `StartSpinAsync` |
| Tilt | ✅ API | `LookAsync(Up/Down)`, `EnableTilt` |
| FlyTo | ✅ API | `FlyToAsync`, `FlyToRectangleAsync`, `FlyToObjectAsync`, `FlyHomeAsync` |
| LookAt | ✅ API | `LookAtAsync`, `StartOrbitAsync`, `TrackEntityAsync` |
| First-person camera | ✅ API | `EnableFlightModeAsync(WalkHeight = …)` |
| Free-flight camera | ✅ API | `EnableFlightModeAsync()` |

## 11. Interactivity

| Feature | Status | How |
|---|---|---|
| Mouse events | ✅ API | `LeftClick`, `RightClick`, `DoubleClick`, `MouseMove` |
| Touch gestures | 🟡 Partial | CesiumJS camera gestures (pinch, rotate, tilt) work natively; taps arrive as `LeftClick`. No .NET events for raw gestures |
| Object picking | ✅ API | `PickResult` (`Entity`, `Primitive`), `SelectedEntityChanged` |
| Terrain picking | ✅ API | `PickResult.Position` with `Kind = Globe` |
| Tile picking | ✅ API | `PickKind.TileFeature` with feature properties and `OwnerId` |

## 12. Drawing & editing

| Feature | Status | How |
|---|---|---|
| Draw points | ✅ API | `DrawMode.Point` |
| Draw polylines | ✅ API | `DrawMode.Polyline`, `MeasureDistance` |
| Draw polygons | ✅ API | `DrawMode.Polygon`, `Rectangle`, `Circle`, `MeasureArea` |
| Dynamic shape editing | ✅ API | `Drawing.BeginEditAsync`, `EntityEdited` (vertex drag; no vertex insert/delete) |
| Runtime updates | ✅ API | `Entities.UpdateAsync` |

## 13. Visual effects

| Feature | Status | How |
|---|---|---|
| Sun lighting | ✅ API | `EnableLighting`, `ShowSun`, `LightIntensity`, `LightColor` |
| Moon lighting | 🟡 Partial | `ShowMoon` renders the moon; CesiumJS has no moonlight. Simulate with `LightColor`/`LightIntensity` or `CameraLight` |
| Ambient lighting | 🟡 Partial | `AmbientOcclusion`, `LightIntensity`; image-based lighting via script |
| Dynamic shadows | ✅ API | `Shadows`, `SoftShadows`, `ShadowDarkness`, `ShadowMapSize`, per-object `Shadows` |
| Atmosphere effects | ✅ API | Sky/ground atmosphere, shifts, `HighDynamicRange`, `Bloom` |
| Fog | ✅ API | `FogEnabled`, `FogDensity`, `WeatherKind.Fog` |
| Ocean rendering | ✅ API | `ShowWaterEffect` with a water-mask terrain (World Terrain) |
| Reflections | 🟡 Partial | PBR reflections of the environment on glTF; no planar or screen-space reflections; IBL via script |
| Custom weather effects | ✅ API | `Scene.SetWeatherAsync` (rain, snow, fog); custom `PostProcessStage` via script |

## 14. Sensor & military visualization

| Feature | Status | How |
|---|---|---|
| Radar coverage | ✅ API | `EllipsoidGraphics.Dome` |
| EO/IR sensors | ✅ API | `EllipsoidGraphics.Sector`, cones (`CylinderGraphics` with `TopRadius = 0`) |
| Sonar visualization | ✅ API | Lower-hemisphere ellipsoid (`MinimumCone = 90`, `MaximumCone = 180`) |
| Threat analysis | 🟡 Partial | Range rings (`Geodesy.Circle`), zones, `Geodesy.Contains`, distances in C#. No line-of-sight or terrain masking |
| Detection zones | ✅ API | Polygons/ellipses + `Geodesy.Contains` |
| Battlefield visualization | 🟡 Partial | Entities, pins, paths and dark base maps; no MIL-STD-2525/APP-6 symbol renderer (bring your own images) |
| Common operational picture (COP) | ✅ API | Combination of the above + `RealtimeEntityFeed`; see [Aerospace & defense](aerospace-and-defense.md) |

## 15. Real-time data streaming

| Feature | Status | How |
|---|---|---|
| WebSocket | ✅ API | `RealtimeEntityFeed` + `ClientWebSocket`; see [Realtime tracking](realtime-tracking.md) |
| SignalR | ✅ API | `RealtimeEntityFeed` + SignalR client |
| MQTT | ✅ API | `RealtimeEntityFeed` + MQTTnet (transport library not included) |
| REST APIs | ✅ API | `RealtimeEntityFeed` + polling, or `SampledPosition` + `AddSamplesAsync` |
| UAV tracking | ✅ API | Feed + heading + `TrackEntityAsync` |
| Vehicle tracking | ✅ API | Feed + `UpdatePositionsAsync` |
| IoT monitoring | ✅ API | Entities with `Properties`, `UpdateAsync` for state colors, feed for moving sensors |

## 16. Coordinate systems

| Feature | Status | How |
|---|---|---|
| WGS84 | ✅ API | `GeoPoint`, `Ellipsoid.Wgs84` |
| Latitude / longitude | ✅ API | `GeoPoint` (degrees), `Cartographic` (radians), `ToDms` |
| Cartesian2 | ✅ API | `Cartesian2` |
| Cartesian3 | ✅ API | `Cartesian3` (vector math) |
| Cartesian4 | ✅ API | `Cartesian4` |
| ECEF | ✅ API | `GeodeticToEcef`/`EcefToGeodetic`, `GeoPoint.ToCartesian()` |
| ENU | ✅ API | `EcefToEnu`, `EnuToEcef`, `GeodeticToEnu`, `EnuToGeodetic` |
| Web Mercator | ✅ API | `GeodeticToWebMercator`, `WebMercatorToGeodetic`, `GeodeticToTile` |

## 17. Developer features

| Feature | Status | How |
|---|---|---|
| TypeScript support | ➖ N/A | Replaced by a strongly typed C# API with nullable annotations and XML docs |
| React integration | ➖ N/A | .NET wrapper |
| Angular integration | ➖ N/A | .NET wrapper |
| Vue integration | ➖ N/A | .NET wrapper |
| Blazor integration | ✅ API | `Cesium.Net.Blazor` (`CesiumViewer`) |
| Svelte integration | ➖ N/A | .NET wrapper |
| Custom shaders | 🟡 Partial | `CustomShaderOptions` on 3D Tiles (with uniforms and `u_time`); models and primitives via script |
| Custom materials | 🟡 Partial | Built-in `CesiumMaterial` kinds; Fabric materials via script |
| *(extra)* Desktop | ✅ API | `Cesium.Net.Avalonia` (Windows, macOS, Linux) |

## 18. Cesium ion integration

| Feature | Status | How |
|---|---|---|
| Asset hosting | ✅ API | Consume ion assets by id (`FromIon`, `TerrainProviderKind.Ion`, `ImageryProviderKind.Ion`); list/inspect with `CesiumIonClient` |
| Terrain hosting | ✅ API | `TerrainProviderKind.Ion` |
| Photogrammetry hosting | ✅ API | `TilesetOptions.FromIon` |
| 3D Tiles generation | ➖ N/A | Done by ion (upload/tiling). `CesiumIonClient` reads status (`Status`, `PercentComplete`) but cannot upload |
| BIM conversion | ➖ N/A | Done by ion; load the result with `FromIon` |
| Asset versioning | ❌ Not covered | Not exposed by `CesiumIonClient`; use the ion REST API directly |

## 19. Enterprise use cases

These are combinations of the features above. The table lists the main building blocks for each.

| Use case | Building blocks |
|---|---|
| Smart city | OSM Buildings / city tilesets, styling, GeoJSON extrusion, realtime IoT entities |
| Digital twin | Photogrammetry/BIM tilesets, clipping, custom shaders, feature picking, realtime feed |
| Aerospace | `KeplerianOrbit`, sampled positions, sensor volumes, clock |
| Defense | COP pattern, sensor volumes, zones and `Geodesy.Contains`, dark base maps |
| Agritech | Field polygons, `Geodesy.Area`, WMS/WMTS NDVI layers, `MeasureArea` drawing |
| Mining | Terrain profiles, exaggeration, globe clipping/translucency, point clouds |
| Maritime | AIS via `RealtimeEntityFeed`, water effect, bathymetry imagery layers |
| Energy | Corridors and walls for lines and pipelines, terrain profiles, asset picking |

## Key strengths of CesiumJS, as exposed by Cesium.Net

| Strength | Coverage |
|---|---|
| Planet-scale 3D globe | ✅ |
| OGC 3D Tiles streaming | ✅ |
| Digital twin visualization | 🟡 (building blocks) |
| Satellite & orbit simulation | ✅ (Keplerian; no SGP4) |
| Time-dynamic geospatial data | ✅ |
| LiDAR point cloud rendering | ✅ |
| Massive photogrammetry | ✅ |
| Global terrain rendering | ✅ |
| Military situational awareness | 🟡 (no symbology, no line-of-sight) |
| GIS + 3D engine in one platform | ✅ (C# geodesy + CesiumJS) |

## Known gaps

- No SGP4/TLE propagator (Keplerian + J2 only).
- No line-of-sight, viewshed or terrain-masking analysis.
- No polygon boolean operations, buffering or spatial index.
- No MIL-STD-2525 symbology.
- Vertex editing moves existing vertices only; there is no insert or delete.
- Custom shaders are limited to 3D Tiles; Fabric materials and IBL need scripts.
- `CesiumIonClient` is read-only: no upload, tiling or versioning.

For anything else that CesiumJS can do, `ExecuteScriptAsync` gives full access to `viewer`, `scene` and `Cesium`.

---
Made by Gravicode Studios, led by Kang Fadhil.
