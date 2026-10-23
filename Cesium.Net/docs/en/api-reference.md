[Bahasa Indonesia](../id/api-reference.md)

# API reference

This page is generated from the public surface of `src/`. Unless stated otherwise, everything lives in namespace `CesiumNet`. `ct` stands for `CancellationToken cancellationToken = default`.

- [CesiumController](#cesiumcontroller)
- [CameraApi](#cameraapi) · [EntitiesApi](#entitiesapi) · [ImageryApi](#imageryapi) · [TerrainApi](#terrainapi) · [TilesetsApi](#tilesetsapi) · [DataSourcesApi](#datasourcesapi) · [PrimitivesApi](#primitivesapi) · [ClockApi](#clockapi) · [SceneApi](#sceneapi) · [DrawingApi](#drawingapi)
- [Options & results](#options-and-results) · [Enums](#enums) · [Math](#math) · [Geodesy](#geodesy) · [CoordinateConverter](#coordinateconverter)
- [RealtimeEntityFeed](#realtimeentityfeed) · [KeplerianOrbit](#keplerianorbit) · [CesiumIonClient](#cesiumionclient) · [Interop](#interop)
- [Blazor](#blazor-cesiumnetblazor) · [Avalonia](#avalonia-cesiumnetavalonia)

## CesiumController

`public sealed class CesiumController` is the API for one viewer. Hosts create it. `new CesiumController(ICesiumTransport)` is public for custom hosts.

| Member | Signature | Description |
|---|---|---|
| Camera | `CameraApi Camera { get; }` | Camera control |
| Entities | `EntitiesApi Entities { get; }` | Entities |
| Imagery | `ImageryApi Imagery { get; }` | Imagery layers |
| Terrain | `TerrainApi Terrain { get; }` | Terrain and elevation |
| Tilesets | `TilesetsApi Tilesets { get; }` | 3D Tiles |
| DataSources | `DataSourcesApi DataSources { get; }` | GeoJSON, TopoJSON, KML/KMZ, CZML, GPX |
| Primitives | `PrimitivesApi Primitives { get; }` | High-volume point collections |
| Clock | `ClockApi Clock { get; }` | Simulation clock |
| Scene | `SceneApi Scene { get; }` | Scene mode, effects, picking, screenshots, events |
| Drawing | `DrawingApi Drawing { get; }` | Drawing, measuring, editing |
| IsReady | `bool IsReady { get; }` | Viewer created |
| WhenReady | `Task WhenReady { get; }` | Completes when ready; faults on load failure |
| ExecuteScriptAsync | `ValueTask<string?> ExecuteScriptAsync(string script, ct)` | Evaluates JS with `viewer`, `Cesium`, `scene` in scope; returns JSON |
| RequestRenderAsync | `ValueTask RequestRenderAsync()` | Requests a frame (request-render mode, after scripts) |
| SerializeOptions | `static string SerializeOptions(CesiumViewerOptions options)` | Serializes options for a host's create call (advanced) |
| HandleHostEvent | `void HandleHostEvent(string name, string? json)` | Host → controller event entry point (hidden; for custom hosts) |
| HandleHostFailure | `void HandleHostFailure(Exception exception)` | Host reports load failure (hidden; for custom hosts) |

Events:

| Event | Type |
|---|---|
| `Ready` | `EventHandler` |
| `LeftClick`, `RightClick`, `DoubleClick`, `MouseMove` | `EventHandler<PickResult>` |
| `CameraChanged` | `EventHandler<CameraState>` |
| `SelectedEntityChanged` | `EventHandler<string?>` |
| `ClockTick` | `EventHandler<ClockState>` |
| `DrawCompleted` | `EventHandler<DrawResult>` |
| `DrawCancelled` | `EventHandler` |
| `EntityEdited` | `EventHandler<EntityEditedResult>` |
| `LoadingProgress` | `EventHandler<int>` |
| `Error` | `EventHandler<CesiumError>` |

## CameraApi

| Method | Signature | Description |
|---|---|---|
| FlyToAsync | `ValueTask FlyToAsync(CameraView view, ct)` | Smooth flight; completes when the flight ends or is cancelled |
| FlyToAsync | `ValueTask FlyToAsync(double longitude, double latitude, double height, double heading = 0, double pitch = -90, double? duration = null, ct)` | Flight to lon/lat/height (degrees, meters) |
| SetViewAsync | `ValueTask SetViewAsync(CameraView view)` | Instantly sets the view |
| FlyToRectangleAsync | `ValueTask FlyToRectangleAsync(GeoRectangle rectangle, double? duration = null)` | Flies so the rectangle fills the view |
| FlyHomeAsync | `ValueTask FlyHomeAsync(double? duration = null)` | Flies to the default home view (returns when started) |
| FlyToObjectAsync | `ValueTask FlyToObjectAsync(string id, HeadingPitchRange? offset = null, double? duration = null)` | Flies to an entity, data source, tileset or point collection |
| LookAtAsync | `ValueTask LookAtAsync(GeoPoint target, HeadingPitchRange offset)` | Locks the camera to orbit a target |
| ReleaseLookAtAsync | `ValueTask ReleaseLookAtAsync()` | Releases a look-at lock |
| TrackEntityAsync | `ValueTask TrackEntityAsync(string? entityId)` | Follows an entity; `null` stops |
| StartOrbitAsync | `ValueTask StartOrbitAsync(GeoPoint target, double range, double pitch = -30, double degreesPerSecond = 6)` | Continuous orbit around a point |
| StartSpinAsync | `ValueTask StartSpinAsync(double degreesPerSecond = 2)` | Spins the globe |
| StopAnimationAsync | `ValueTask StopAnimationAsync()` | Stops orbit/spin |
| ZoomAsync | `ValueTask ZoomAsync(double? meters = null, bool zoomIn = true)` | Zoom in/out; default 20 % (in) / 25 % (out) of height |
| MoveAsync | `ValueTask MoveAsync(CameraDirection direction, double? meters = null)` | Pans; default 10 % of height |
| RotateAsync | `ValueTask RotateAsync(CameraDirection direction, double degrees = 5)` | Rotates around the globe center |
| LookAsync | `ValueTask LookAsync(CameraDirection direction, double degrees = 5)` | Turns in place: Left/Right yaw, Up/Down tilt |
| TwistAsync | `ValueTask TwistAsync(double degrees)` | Rolls the camera |
| GetStateAsync | `ValueTask<CameraState> GetStateAsync(ct)` | Current position, heading, pitch, roll, view rectangle |
| ConfigureControllerAsync | `ValueTask ConfigureControllerAsync(CameraControllerOptions options)` | Enables/disables inputs, zoom limits, collision |
| EnableFlightModeAsync | `ValueTask EnableFlightModeAsync(FlightModeOptions? options = null)` | WASD/QE + mouse-look first-person/free flight |
| DisableFlightModeAsync | `ValueTask DisableFlightModeAsync()` | Leaves flight mode |

## EntitiesApi

| Method | Signature | Description |
|---|---|---|
| AddAsync | `ValueTask<string> AddAsync(EntityOptions entity, ct)` | Adds an entity; returns its id |
| AddRangeAsync | `ValueTask<string[]> AddRangeAsync(IReadOnlyList<EntityOptions> entities, ct)` | Adds many in one call with events suspended |
| UpdateAsync | `ValueTask UpdateAsync(string id, EntityOptions changes)` | Applies non-null properties; graphics objects replace previous ones |
| UpdatePositionsAsync | `ValueTask UpdatePositionsAsync(IReadOnlyList<EntityPositionUpdate> updates)` | Moves many entities in one call (unknown ids are skipped) |
| AddSamplesAsync | `ValueTask AddSamplesAsync(string id, SampledPositions samples)` | Appends time samples to a sampled-position entity |
| SetVisibleAsync | `ValueTask SetVisibleAsync(string id, bool visible)` | Shows/hides |
| RemoveAsync | `ValueTask<bool> RemoveAsync(string id)` | Removes; `false` if not found |
| ClearAsync | `ValueTask ClearAsync()` | Removes all viewer entities, including drawn shapes |
| SelectAsync | `ValueTask SelectAsync(string? id)` | Selects (info box + indicator); `null` clears |
| GetPositionAsync | `ValueTask<GeoPoint?> GetPositionAsync(string id)` | Position at the current clock time |
| GetIdsAsync | `ValueTask<string[]> GetIdsAsync()` | Ids in the viewer's entity collection |

## ImageryApi

| Method | Signature | Description |
|---|---|---|
| AddAsync | `ValueTask<string> AddAsync(ImageryLayerOptions layer, ct)` | Adds a layer on top; returns its id |
| SetBaseLayerAsync | `ValueTask<string> SetBaseLayerAsync(ImageryLayerOptions layer, ct)` | Replaces the bottom layer; returns `"base"` |
| UpdateAsync | `ValueTask UpdateAsync(string id, ImageryLayerUpdate update)` | Opacity, color adjustments, visibility, split side |
| SetAlphaAsync | `ValueTask SetAlphaAsync(string id, double alpha)` | Opacity 0..1 |
| RemoveAsync | `ValueTask RemoveAsync(string id)` | Removes a layer |
| RaiseAsync / LowerAsync | `ValueTask RaiseAsync(string id)` / `ValueTask LowerAsync(string id)` | Moves one step |
| RaiseToTopAsync / LowerToBottomAsync | `ValueTask RaiseToTopAsync(string id)` / `ValueTask LowerToBottomAsync(string id)` | Moves to the top/bottom |
| SetSplitPositionAsync | `ValueTask SetSplitPositionAsync(double position)` | Swipe position 0..1 |
| GetIdsAsync | `ValueTask<string[]> GetIdsAsync()` | Known layer ids, bottom to top |

## TerrainApi

| Method | Signature | Description |
|---|---|---|
| SetProviderAsync | `ValueTask SetProviderAsync(TerrainOptions terrain, ct)` | Switches the terrain provider |
| SetExaggerationAsync | `ValueTask SetExaggerationAsync(double factor, double relativeHeight = 0)` | Vertical exaggeration |
| SampleHeightsAsync | `ValueTask<GeoPoint[]> SampleHeightsAsync(IReadOnlyList<GeoPoint> points, ct)` | Most-detailed terrain heights (in `Height`) |
| GetHeightAsync | `ValueTask<double?> GetHeightAsync(GeoPoint point, ct)` | Height at one point |
| GetProfileAsync | `ValueTask<ProfileSample[]> GetProfileAsync(GeoPoint start, GeoPoint end, int samples = 100, ct)` | Elevation profile along the geodesic (`samples` ≥ 2) |
| SetClippingPolygonsAsync | `ValueTask SetClippingPolygonsAsync(IReadOnlyList<IReadOnlyList<GeoPoint>>? polygons, bool inverse = false)` | Hides the globe inside (or outside) polygons; `null` clears |

## TilesetsApi

| Method | Signature | Description |
|---|---|---|
| AddAsync | `ValueTask<string> AddAsync(TilesetOptions tileset, ct)` | Loads a tileset; returns its id |
| SetStyleAsync | `ValueTask SetStyleAsync(string id, TilesetStyle? style)` | Declarative style; `null` resets |
| UpdateAsync | `ValueTask UpdateAsync(string id, TilesetOptions changes)` | Show, SSE, style, point cloud shading, height offset, clipping, shader |
| SetShaderUniformAsync | `ValueTask SetShaderUniformAsync(string id, string name, double value)` | Sets a float uniform of the custom shader |
| RemoveAsync | `ValueTask RemoveAsync(string id)` | Removes the tileset |

## DataSourcesApi

| Method | Signature | Description |
|---|---|---|
| LoadAsync | `ValueTask<string> LoadAsync(DataSourceOptions source, ct)` | Loads GeoJSON/TopoJSON/KML/KMZ/CZML/GPX; returns its id |
| SetVisibleAsync | `ValueTask SetVisibleAsync(string id, bool visible)` | Shows/hides |
| RemoveAsync | `ValueTask RemoveAsync(string id)` | Removes and destroys |
| CountAsync | `ValueTask<int> CountAsync(string id)` | Number of entities (0 if unknown) |

## PrimitivesApi

| Method | Signature | Description |
|---|---|---|
| AddPointsAsync | `ValueTask<string> AddPointsAsync(PointCollectionOptions points, ct)` | GPU point collection (100k+ points); returns its id |
| RemoveAsync | `ValueTask RemoveAsync(string id)` | Removes the collection |
| SetVisibleAsync | `ValueTask SetVisibleAsync(string id, bool visible)` | Shows/hides |

## ClockApi

| Method | Signature | Description |
|---|---|---|
| ConfigureAsync | `ValueTask ConfigureAsync(ClockOptions options)` | Start/stop/current time, multiplier, range, animate (partial) |
| PlayAsync | `ValueTask PlayAsync()` | `ShouldAnimate = true` |
| PauseAsync | `ValueTask PauseAsync()` | `ShouldAnimate = false` |
| SetMultiplierAsync | `ValueTask SetMultiplierAsync(double multiplier)` | Simulation seconds per real second |
| SetTimeAsync | `ValueTask SetTimeAsync(DateTimeOffset time)` | Jumps to a time |
| GetStateAsync | `ValueTask<ClockState> GetStateAsync()` | Current time, multiplier, animating |

## SceneApi

| Method | Signature | Description |
|---|---|---|
| SetModeAsync | `ValueTask SetModeAsync(SceneMode mode, double duration = 1.5)` | Morphs 3D / 2D / Columbus view |
| ConfigureAsync | `ValueTask ConfigureAsync(SceneSettings settings)` | Lighting, shadows, atmosphere, fog, post-processing (partial) |
| SetWeatherAsync | `ValueTask SetWeatherAsync(WeatherOptions weather)` | Rain, snow, fog post-process, or none |
| CaptureScreenshotAsync | `ValueTask<string> CaptureScreenshotAsync(ct)` | PNG data URL of the current frame |
| CaptureScreenshotPngAsync | `ValueTask<byte[]> CaptureScreenshotPngAsync(ct)` | PNG bytes |
| PickAsync | `ValueTask<PickResult?> PickAsync(double x, double y)` | Picks at a screen position (CSS px) |
| ToScreenAsync | `ValueTask<Cartesian2?> ToScreenAsync(GeoPoint position)` | World → screen; `null` if not visible |
| ConfigureEventsAsync | `ValueTask ConfigureEventsAsync(CesiumEventOptions events)` | Changes forwarded events at runtime (full object) |

## DrawingApi

| Method | Signature | Description |
|---|---|---|
| DrawAsync | `ValueTask<DrawResult?> DrawAsync(DrawOptions options, ct)` | Interactive drawing; `null` if cancelled |
| CancelAsync | `ValueTask CancelAsync()` | Cancels the active drawing |
| ClearAsync | `ValueTask ClearAsync()` | Removes all drawn shapes and labels |
| BeginEditAsync | `ValueTask BeginEditAsync(string entityId)` | Draggable vertices (polyline, polygon, positioned entity) |
| EndEditAsync | `ValueTask EndEditAsync()` | Leaves editing mode |

## Options and results

### Creation and configuration

| Type | Key members |
|---|---|
| `CesiumViewerOptions` | `const DefaultCesiumVersion = "1.146.0"`, `CesiumBaseUrl`, `IonAccessToken`, `BaseLayer`, `NoBaseLayer`, `Terrain`, `SceneMode`, `InitialView`, widget flags (`Animation`, `Timeline`, `BaseLayerPicker`, `Geocoder`, `HomeButton`, `SceneModePicker`, `NavigationHelpButton`, `FullscreenButton` all `false`; `InfoBox`, `SelectionIndicator`, `ShowCredits` all `true`), `RequestRenderMode`, `MaximumRenderTimeChange`, `ResolutionScale`, `MsaaSamples`, `EnableLighting`, `Shadows`, `DepthTestAgainstTerrain`, `ShowFps`, `BackgroundColor`, `GlobeBaseColor`, `Events` |
| `CesiumEventOptions` | `Click` (true), `RightClick`, `DoubleClick`, `MouseMove`, `MouseMoveThrottleMs` (100), `CameraChanged` (true), `ClockTick`, `ClockTickThrottleMs` (500), `SelectedEntityChanged` (true), `LoadingProgress` |
| `CameraView` | `required Destination`, `Heading`, `Pitch`, `Roll`, `Duration`; `static At(lon, lat, height, heading = 0, pitch = -90, roll = 0)` |
| `CameraControllerOptions` | `EnableRotate`, `EnableTranslate`, `EnableZoom`, `EnableTilt`, `EnableLook`, `MinimumZoomDistance`, `MaximumZoomDistance`, `EnableCollisionDetection` |
| `FlightModeOptions` | `Speed` (120 m/s), `ScaleWithHeight` (true), `WalkHeight` |
| `ClockOptions` | `StartTime`, `StopTime`, `CurrentTime`, `Multiplier`, `ClockRange`, `ShouldAnimate`, `ZoomTimeline` |
| `SceneSettings` | `EnableLighting`, `ShowSun`, `ShowMoon`, `ShowSkyBox`, `ShowSkyAtmosphere`, `ShowGroundAtmosphere`, `ShowGlobe`, `DepthTestAgainstTerrain`, `BackgroundColor`, `GlobeBaseColor`, `Shadows`, `SoftShadows`, `ShadowDarkness`, `ShadowMapSize`, `TerrainShadows`, `FogEnabled`, `FogDensity`, `ShowWaterEffect`, `AtmosphereHueShift`, `AtmosphereSaturationShift`, `AtmosphereBrightnessShift`, `AtmosphereLightIntensity`, `LightIntensity`, `LightColor`, `CameraLight`, `HighDynamicRange`, `Fxaa`, `MsaaSamples`, `ResolutionScale`, `ShowFps`, `Bloom`, `BloomContrast`, `BloomBrightness`, `AmbientOcclusion`, `AmbientOcclusionIntensity`, `GlobeTranslucency`, `GlobeFrontAlpha`, `VerticalExaggeration`, `VerticalExaggerationRelativeHeight`, `SplitPosition` |
| `WeatherOptions` | `Kind` (None), `Intensity` (0.6), `WindAngle` (−10°) |
| `DrawOptions` | `Mode` (Polyline), `Color`, `FillColor`, `Width`, `ClampToGround` (default true), `KeepEntity` (default true), `ShowMeasurementLabel` (default true for measure/polyline/polygon) |

### Entities

| Type | Key members |
|---|---|
| `EntityOptions` | `Id`, `Name`, `Description` (HTML), `Show`, `Position`, `SampledPosition`, `Orientation` (`HeadingPitchRoll`), `OrientToVelocity`, `Availability` (`TimeInterval`), `ParentId`, `Properties`; graphics `Point`, `Billboard`, `Label`, `Polyline`, `Polygon`, `Ellipse`, `Rectangle`, `Box`, `Cylinder`, `Ellipsoid`, `Corridor`, `Wall`, `Model`, `Path` |
| `TimeInterval` | `record(DateTimeOffset Start, DateTimeOffset Stop)` |
| `SampledPositions` | `required Times`, `required Positions`, `Interpolation`, `InterpolationDegree`, `Extrapolate`; `static From(IEnumerable<(DateTimeOffset, GeoPoint)>, InterpolationKind = Lagrange, int degree = 3)` |
| `PointGraphics` | `PixelSize` (10), `Color`, `OutlineColor`, `OutlineWidth`, `HeightReference`, `ScaleByDistance`, `DistanceDisplayCondition`, `AlwaysOnTop` |
| `BillboardGraphics` | `Image`, `PinColor`, `PinText`, `PinIcon` (Maki id), `PinSize` (48), `Scale`, `Width`, `Height`, `Color`, `Rotation`, `VerticalOrigin`, `HorizontalOrigin`, `PixelOffset`, `HeightReference`, `ScaleByDistance`, `DistanceDisplayCondition`, `AlwaysOnTop` |
| `LabelGraphics` | `required Text`, `Font`, `FillColor`, `OutlineColor`, `OutlineWidth`, `Style`, `ShowBackground`, `BackgroundColor`, `Scale`, `PixelOffset`, `VerticalOrigin`, `HorizontalOrigin`, `HeightReference`, `ScaleByDistance`, `DistanceDisplayCondition`, `AlwaysOnTop` |
| `PolylineGraphics` | `required Positions`, `Width` (3), `Material`, `DepthFailMaterial`, `ClampToGround`, `Geodesic`, `DistanceDisplayCondition` |
| `PolygonGraphics` | `required Positions`, `Holes`, `Material`, `Fill`, `Outline`, `OutlineColor`, `Height`, `ExtrudedHeight`, `PerPositionHeight`, `HeightReference`, `ExtrudedHeightReference`, `Shadows`, `ClassifyGround` |
| `EllipseGraphics` | `required SemiMajorAxis`, `required SemiMinorAxis`, `Rotation`, `Material`, `Fill`, `Outline`, `OutlineColor`, `Height`, `ExtrudedHeight`, `HeightReference` |
| `RectangleGraphics` | `required Coordinates`, `Material`, `Fill`, `Outline`, `OutlineColor`, `Height`, `ExtrudedHeight` |
| `BoxGraphics` | `required Dimensions` (`Cartesian3`, m), `Material`, `Fill`, `Outline`, `OutlineColor`, `HeightReference`, `Shadows` |
| `CylinderGraphics` | `required Length`, `required TopRadius`, `required BottomRadius`, `Material`, `Fill`, `Outline`, `OutlineColor`, `Slices`, `HeightReference`, `Shadows` |
| `EllipsoidGraphics` | `required Radii`, `InnerRadii`, `MinimumClock`, `MaximumClock`, `MinimumCone`, `MaximumCone`, `Material`, `Fill`, `Outline`, `OutlineColor`, `StackPartitions`, `SlicePartitions`, `HeightReference`; `static Dome(double range, CesiumMaterial? material = null, CesiumColor? outline = null)`, `static Sector(double range, double minAzimuth, double maxAzimuth, double maxElevation = 60, CesiumMaterial? material = null)` |
| `CorridorGraphics` | `required Positions`, `required Width`, `Material`, `Height`, `ExtrudedHeight`, `Outline`, `OutlineColor` |
| `WallGraphics` | `required Positions`, `MinimumHeights`, `MaximumHeights`, `Material`, `Outline`, `OutlineColor` |
| `ModelGraphics` | `required Uri`, `Scale`, `MinimumPixelSize`, `MaximumScale`, `RunAnimations`, `Color`, `SilhouetteColor`, `SilhouetteSize`, `HeightReference`, `Shadows`, `DistanceDisplayCondition` |
| `PathGraphics` | `LeadTime`, `TrailTime`, `Width` (2), `Resolution` (60), `Material` (yellow glow) |
| `EntityPositionUpdate` | `readonly record struct(string Id, GeoPoint Position, double? Heading = null)` |
| `CesiumMaterial` | `Kind`, `Color`, `SecondaryColor`, … ; factories `Solid`, `Glow`, `Dash`, `Arrow`, `Outline`, `Stripe`, `Grid`, `Checkerboard`, `FromImage`; implicit from `CesiumColor` |
| `DistanceDisplayCondition` | `record struct(double Near, double Far)` |
| `NearFarScalar` | `record struct(double Near, double NearValue, double Far, double FarValue)` |

### Layers, tiles, data

| Type | Key members |
|---|---|
| `ImageryLayerOptions` | `Provider`, `Url`, `Layers`, `Style`, `TileMatrixSetId`, `Format`, `Parameters`, `Subdomains`, `Key`, `MapStyle`, `AssetId`, `MinimumLevel`, `MaximumLevel`, `Rectangle`, `Credit`, `Color`, `Alpha`, `Brightness`, `Contrast`, `Hue`, `Saturation`, `Gamma`, `Show`, `SplitDirection`; factories `OpenStreetMap()`, `Satellite()`, `Streets()`, `Topographic()`, `Dark()`, `Light()`, `FromTemplate(url, credit = null, maximumLevel = null)`, `Wms(url, layers, format = "image/png", transparent = true)`, `Wmts(url, layer, tileMatrixSetId, style = "default", format = "image/png", maximumLevel = null)` |
| `ImageryLayerUpdate` | `Alpha`, `Brightness`, `Contrast`, `Hue`, `Saturation`, `Gamma`, `Show`, `SplitDirection` |
| `TerrainOptions` | `Provider`, `Url`, `AssetId`, `RequestVertexNormals`, `RequestWaterMask`; `Ellipsoid()`, `WorldTerrain(bool lighting = true, bool water = true)`, `ArcGis()` |
| `TilesetOptions` | `Kind`, `Url`, `AssetId`, `Key`, `MaximumScreenSpaceError`, `Show`, `FlyTo`, `HeightOffset`, `Style`, `PointCloudShading`, `CustomShader`, `Shadows`, `ClippingPolygons`, `ClipInverse`, `SkipLevelOfDetail`, `DynamicScreenSpaceError`, `DebugShowBoundingVolume`; `FromUrl(url, flyTo = false)`, `FromIon(assetId, flyTo = false)`, `OsmBuildings()`, `GooglePhotorealistic(key = null)` |
| `TilesetStyle` | `Color`, `ColorConditions` (`IReadOnlyList<string[]>`), `Show`, `PointSize`, `Defines` |
| `PointCloudShading` | `Attenuation`, `MaximumAttenuation`, `EyeDomeLighting`, `EyeDomeLightingStrength`, `EyeDomeLightingRadius` |
| `CustomShaderOptions` | `FragmentShaderText`, `VertexShaderText`, `LightingModel` ("PBR"/"UNLIT"), `Uniforms` (`Dictionary<string,double>`), `AnimateTime` (`u_time`) |
| `DataSourceOptions` | `Kind`, `Url`, `Data`, `Name`, `FlyTo`, `ClampToGround`, `Stroke`, `StrokeWidth`, `Fill`, `MarkerColor`, `MarkerSize`, `ExtrudeByProperty`, `ExtrudeScale`, `RandomFill`; `GeoJsonUrl`, `GeoJsonData`, `Kml`, `Czml`, `Gpx`, `TopoJson` |
| `PointCollectionOptions` | `required Positions`, `Colors`, `Color`, `PixelSize` (4), `OutlineColor`, `OutlineWidth`, `ScaleByDistance` |

### Results

| Type | Members |
|---|---|
| `CameraState` | `Position`, `Heading`, `Pitch`, `Roll`, `ViewRectangle?` |
| `PickResult` | `Kind`, `Screen`, `Position?`, `EntityId`, `EntityName`, `Properties`, `OwnerId` |
| `DrawResult` | `Mode`, `EntityId`, `Positions`, `Radius?`, computed `Length`, `Area` |
| `EntityEditedResult` | `EntityId`, `Positions` |
| `ClockState` | `CurrentTime`, `Multiplier`, `ShouldAnimate` |
| `CesiumError` | `Message`, `Source`, `Stack` |
| `ProfileSample` | `record struct(double Distance, GeoPoint Position)` |
| `CesiumInteropException` | `Method`, `JsMessage` |

## Enums

| Enum | Values |
|---|---|
| `SceneMode` | `Scene3D`, `Scene2D`, `ColumbusView` |
| `HeightReference` | `None`, `ClampToGround`, `RelativeToGround` |
| `VerticalOrigin` | `Center`, `Bottom`, `Baseline`, `Top` |
| `HorizontalOrigin` | `Center`, `Left`, `Right` |
| `LabelStyle` | `Fill`, `Outline`, `FillAndOutline` |
| `ShadowMode` | `Disabled`, `Enabled`, `CastOnly`, `ReceiveOnly` |
| `InterpolationKind` | `Linear`, `Lagrange`, `Hermite` |
| `ClockRange` | `Unbounded`, `Clamped`, `LoopStop` |
| `MaterialKind` | `Color`, `PolylineGlow`, `PolylineDash`, `PolylineArrow`, `PolylineOutline`, `Stripe`, `Grid`, `Checkerboard`, `Image` |
| `ImageryProviderKind` | `OpenStreetMap`, `ArcGisWorldImagery`, `ArcGisWorldStreetMap`, `ArcGisWorldTopo`, `ArcGisMapServer`, `Bing`, `Wms`, `Wmts`, `Tms`, `UrlTemplate`, `Ion`, `SingleTile`, `TileCoordinates`, `Grid` |
| `TerrainProviderKind` | `Ellipsoid`, `CesiumWorldTerrain`, `CesiumTerrainServer`, `Ion`, `ArcGisElevation` |
| `TilesetKind` | `Url`, `Ion`, `OsmBuildings`, `GooglePhotorealistic` |
| `DataSourceKind` | `GeoJson`, `TopoJson`, `Kml`, `Czml`, `Gpx` |
| `DrawMode` | `Point`, `Polyline`, `Polygon`, `Rectangle`, `Circle`, `MeasureDistance`, `MeasureArea` |
| `CameraDirection` | `Forward`, `Backward`, `Left`, `Right`, `Up`, `Down` |
| `SplitDirection` | `None`, `Left`, `Right` |
| `WeatherKind` | `None`, `Rain`, `Snow`, `Fog` |
| `PickKind` | `None`, `Globe`, `Entity`, `TileFeature`, `Primitive` |

## Math

| Type | Members |
|---|---|
| `GeoPoint` | `record struct(double Longitude, double Latitude, double Height = 0)`; `FromRadians(lon, lat, h = 0)`, `ToCartographic()`, `ToCartesian()` (ECEF), `WithHeight(h)`; JSON `[lon,lat,h]` |
| `Cartographic` | `record struct(double Longitude, double Latitude, double Height = 0)` in radians; `FromDegrees`, `ToGeoPoint()` |
| `Cartesian2` | `X`, `Y`; `Zero`, `Magnitude`, `+ - *`, `Dot`, `Distance` |
| `Cartesian3` | `X`, `Y`, `Z`; `Zero`, `UnitX/Y/Z`, `Magnitude`, `MagnitudeSquared`, `Normalize()`, `+ - * /`, unary `-`, `Dot`, `Cross`, `Distance`, `Lerp`, `AngleBetween` (rad), `ToGeoPoint()` (from ECEF) |
| `Cartesian4` | `X`, `Y`, `Z`, `W`; `Zero`, `Magnitude`, `+ - *`, `Dot` |
| `HeadingPitchRoll` | `record struct(double Heading, double Pitch = 0, double Roll = 0)` (degrees) |
| `HeadingPitchRange` | `record struct(double Heading, double Pitch, double Range)` (degrees, meters) |
| `GeoRectangle` | `record struct(West, South, East, North)` (degrees); `Center`, `Contains(GeoPoint)`, `static FromPoints(IEnumerable<GeoPoint>)` |
| `GeoMath` | `DegreesPerRadian`, `RadiansPerDegree`, `TwoPi`, `ToRadians`, `ToDegrees`, `NormalizeLongitude` → [−180,180), `NormalizeBearing` → [0,360) |
| `CesiumColor` | `record struct(float R, float G, float B, float A = 1)`; `FromBytes`, `FromHex`, `FromHsl`, `WithAlpha`, `Lerp`, `ToCss()`; implicit from `string`; palette constants |
| `Ellipsoid` | `record(double SemiMajorAxis, double InverseFlattening)`; `Wgs84`, `Flattening`, `SemiMinorAxis`, `EccentricitySquared`, `SecondEccentricitySquared`, `AuthalicRadius` |

## Geodesy

`public static class Geodesy` works on WGS84, with results in meters, m² and degrees.

| Method | Signature | Description |
|---|---|---|
| MeanEarthRadius | `const double MeanEarthRadius = 6371008.8` | IUGG mean radius |
| HaversineDistance | `double HaversineDistance(GeoPoint a, GeoPoint b)` | Spherical distance (fast, ~0.5 %) |
| Distance | `double Distance(GeoPoint a, GeoPoint b)` | Vincenty ellipsoidal distance |
| SlantDistance | `double SlantDistance(GeoPoint a, GeoPoint b)` | 3D straight-line distance including heights |
| Bearing | `double Bearing(GeoPoint a, GeoPoint b)` | Initial bearing [0,360) |
| Inverse | `GeodesicInverse Inverse(GeoPoint p1, GeoPoint p2)` | Distance + initial/final bearing (spherical fallback near antipodes) |
| Destination | `GeoPoint Destination(GeoPoint start, double bearing, double distance)` | Direct problem; keeps start height |
| Interpolate | `GeoPoint[] Interpolate(GeoPoint a, GeoPoint b, int count)` | `count` ≥ 2 points along the geodesic |
| Midpoint | `GeoPoint Midpoint(GeoPoint a, GeoPoint b)` | Geodesic midpoint |
| PathLength | `double PathLength(IReadOnlyList<GeoPoint> path)` | Polyline length |
| Perimeter | `double Perimeter(IReadOnlyList<GeoPoint> ring)` | Closed ring length |
| Area | `double Area(IReadOnlyList<GeoPoint> ring)` | Ring area on the authalic sphere (~0.1 %) |
| Centroid | `GeoPoint Centroid(IReadOnlyList<GeoPoint> points)` | ECEF-averaged centroid, height 0 |
| Contains | `bool Contains(IReadOnlyList<GeoPoint> ring, GeoPoint p)` | Planar point-in-polygon (lon/lat space) |
| Circle | `GeoPoint[] Circle(GeoPoint center, double radius, int segments = 64)` | Geodesic circle ring |
| FormatDistance | `string FormatDistance(double meters)` | "850.0 m" / "12.40 km" |
| FormatArea | `string FormatArea(double squareMeters)` | "m²" / "ha" / "km²" |

`GeodesicInverse` is `record struct(double Distance, double InitialBearing, double FinalBearing)`.

## CoordinateConverter

| Method | Signature | Description |
|---|---|---|
| WebMercatorMaxLatitude | `const double WebMercatorMaxLatitude = 85.05112877980659` | |
| GeodeticToEcef | `Cartesian3 GeodeticToEcef(GeoPoint p, Ellipsoid? ellipsoid = null)` | Lon/lat/h → ECEF |
| EcefToGeodetic | `GeoPoint EcefToGeodetic(Cartesian3 c, Ellipsoid? ellipsoid = null)` | ECEF → lon/lat/h (iterative) |
| EcefToEnu | `Cartesian3 EcefToEnu(Cartesian3 ecef, GeoPoint origin, Ellipsoid? ellipsoid = null)` | ECEF → local East-North-Up |
| EnuToEcef | `Cartesian3 EnuToEcef(Cartesian3 enu, GeoPoint origin, Ellipsoid? ellipsoid = null)` | ENU → ECEF |
| GeodeticToEnu | `Cartesian3 GeodeticToEnu(GeoPoint point, GeoPoint origin)` | Lon/lat/h → ENU |
| EnuToGeodetic | `GeoPoint EnuToGeodetic(Cartesian3 enu, GeoPoint origin)` | ENU → lon/lat/h |
| GeodeticToWebMercator | `Cartesian2 GeodeticToWebMercator(GeoPoint p)` | → EPSG:3857 meters (lat clamped) |
| WebMercatorToGeodetic | `GeoPoint WebMercatorToGeodetic(Cartesian2 m)` | EPSG:3857 → lon/lat |
| GeodeticToTile | `(int X, int Y) GeodeticToTile(GeoPoint p, int zoom)` | Slippy-map tile indices |
| ToDms | `string ToDms(double degrees, bool isLatitude)` | e.g. `6°10'31.4"S` |

## RealtimeEntityFeed

Namespace `CesiumNet.Realtime`. `public sealed class RealtimeEntityFeed : IAsyncDisposable`.

| Member | Signature | Description |
|---|---|---|
| ctor | `RealtimeEntityFeed(CesiumController controller, TimeSpan? interval = null)` | Starts a flush loop (default 100 ms) |
| Push | `void Push(EntityPositionUpdate update)` | Queues; latest wins per id; thread-safe |
| Push | `void Push(string id, GeoPoint position, double? heading = null)` | Convenience overload |
| FlushAsync | `ValueTask FlushAsync()` | Sends pending updates now |
| PublishedCount | `long PublishedCount { get; }` | Updates sent so far |
| PendingCount | `int PendingCount { get; }` | Entities waiting for the next flush |
| FlushFailed | `event EventHandler<Exception>? FlushFailed` | Raised on the thread pool when a flush throws |
| DisposeAsync | `ValueTask DisposeAsync()` | Stops the loop (pending updates are dropped) |

## KeplerianOrbit

Namespace `CesiumNet.Aerospace`. `public sealed record KeplerianOrbit`.

| Member | Signature | Description |
|---|---|---|
| Constants | `Mu`, `J2`, `EarthRotationRate` | Earth constants |
| Elements | `required SemiMajorAxis` (m), `Eccentricity`, `Inclination`, `RightAscension`, `ArgumentOfPerigee`, `MeanAnomaly` (degrees), `required Epoch`, `IncludeJ2` (true) | |
| Period | `TimeSpan Period { get; }` | Orbital period |
| Circular | `static KeplerianOrbit Circular(double altitude, double inclination, double rightAscension, double phase, DateTimeOffset epoch)` | Circular orbit from altitude |
| InertialPosition | `Cartesian3 InertialPosition(DateTimeOffset time)` | ECI position (m) |
| PositionAt | `GeoPoint PositionAt(DateTimeOffset time)` | Earth-fixed lon/lat/h |
| Sample | `IReadOnlyList<(DateTimeOffset Time, GeoPoint Position)> Sample(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)` | Samples (step must be > 0) |
| ToSampledPositions | `SampledPositions ToSampledPositions(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)` | Lagrange degree 5 |
| GroundTrack | `GeoPoint[] GroundTrack(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)` | Sub-satellite points (h = 0) |
| FootprintRadius | `static double FootprintRadius(double altitude, double minElevation = 10)` | Ground radius visible above an elevation mask |
| GreenwichSiderealAngle | `static double GreenwichSiderealAngle(DateTimeOffset time)` | GMST (rad) |

## CesiumIonClient

Namespace `CesiumNet.Ion`. `public sealed class CesiumIonClient(HttpClient httpClient, string accessToken)`.

| Member | Signature | Description |
|---|---|---|
| ListAssetsAsync | `Task<IReadOnlyList<IonAsset>> ListAssetsAsync(ct)` | Requires scope `assets:list` |
| GetAssetAsync | `Task<IonAsset?> GetAssetAsync(long id, ct)` | Requires `assets:read`; `null` on 404 |
| WellKnownAssets | `CesiumWorldTerrain = 1`, `BingAerial = 2`, `BingAerialWithLabels = 3`, `BingRoad = 4`, `CesiumOsmBuildings = 96188`, `GooglePhotorealistic3DTiles = 2275207`, `Sentinel2 = 3954`, `EarthAtNight = 3812` | Public asset ids |
| `IonAsset` | `Id`, `Name`, `Description`, `Type`, `Bytes`, `DateAdded`, `Status`, `PercentComplete` | |

## Interop

Namespace `CesiumNet.Interop`.

| Member | Signature | Description |
|---|---|---|
| ICesiumTransport | `ValueTask<string?> InvokeAsync(string method, string argsJson, ct)` | Implement to host CesiumJS elsewhere |
| CesiumScripts.BridgeScript | `static string BridgeScript { get; }` | `cesium-net.js` source (embedded) |
| CesiumScripts.DefaultCesiumBaseUrl | `static string DefaultCesiumBaseUrl(string? version = null)` | jsDelivr URL for a CesiumJS version |
| CesiumScripts.ToJsLiteral | `static string ToJsLiteral(string? value)` | Safe JS string literal |
| `GeoPointJsonConverter`, `CesiumColorJsonConverter` | `JsonConverter<T>` | `[lon,lat,h]` and `#RRGGBBAA` |

## Blazor (`CesiumNet.Blazor`)

| Type | Members |
|---|---|
| `CesiumViewer` | Parameters `Options`, `Class`, `Style`, `ChildContent`, `LoadingContent`, `ErrorContent`, captured attributes; callbacks `OnReady`, `OnLeftClick`, `OnRightClick`, `OnDoubleClick`, `OnMouseMove`, `OnCameraChanged`, `OnSelectedEntityChanged`, `OnClockTick`, `OnDrawCompleted`, `OnEntityEdited`, `OnError`; `Controller`, `IsReady`, `DisposeAsync()` |
| `CesiumNetBlazorOptions` | `IonAccessToken`, `CesiumBaseUrl` |
| `CesiumNetServiceCollectionExtensions` | `IServiceCollection AddCesiumNet(this IServiceCollection services, Action<CesiumNetBlazorOptions>? configure = null)` |

## Avalonia (`CesiumNet.Avalonia`)

| Type | Members |
|---|---|
| `CesiumView : Decorator` | `Controller`, `Options`, `IsReady`, `WebView` (`NativeWebView`), events `Ready` (`CesiumReadyEventArgs`), `LoadFailed` (`CesiumError`) |
| `CesiumReadyEventArgs` | `Controller` |
| `CesiumLocalServer : IDisposable` | `static Shared`, `BaseUrl`, `string MapFolder(string name, string physicalPath)`, `Dispose()` |

---
Made by Gravicode Studios, led by Kang Fadhil.
