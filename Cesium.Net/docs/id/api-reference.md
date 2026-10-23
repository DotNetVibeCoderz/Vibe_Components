[English](../en/api-reference.md)

# Referensi API

Halaman ini disusun dari permukaan publik di `src/`. Kecuali disebutkan lain, semuanya ada di namespace `CesiumNet`. `ct` adalah singkatan dari `CancellationToken cancellationToken = default`.

- [CesiumController](#cesiumcontroller)
- [CameraApi](#cameraapi) · [EntitiesApi](#entitiesapi) · [ImageryApi](#imageryapi) · [TerrainApi](#terrainapi) · [TilesetsApi](#tilesetsapi) · [DataSourcesApi](#datasourcesapi) · [PrimitivesApi](#primitivesapi) · [ClockApi](#clockapi) · [SceneApi](#sceneapi) · [DrawingApi](#drawingapi)
- [Opsi & hasil](#opsi-dan-hasil) · [Enum](#enum) · [Math](#math) · [Geodesy](#geodesy) · [CoordinateConverter](#coordinateconverter)
- [RealtimeEntityFeed](#realtimeentityfeed) · [KeplerianOrbit](#keplerianorbit) · [CesiumIonClient](#cesiumionclient) · [Interop](#interop)
- [Blazor](#blazor-cesiumnetblazor) · [Avalonia](#avalonia-cesiumnetavalonia)

## CesiumController

`public sealed class CesiumController` adalah API untuk satu viewer. Host yang membuatnya. `new CesiumController(ICesiumTransport)` bersifat publik untuk host kustom.

| Member | Signature | Deskripsi |
|---|---|---|
| Camera | `CameraApi Camera { get; }` | Kontrol kamera |
| Entities | `EntitiesApi Entities { get; }` | Entitas |
| Imagery | `ImageryApi Imagery { get; }` | Lapisan imagery |
| Terrain | `TerrainApi Terrain { get; }` | Terrain dan elevasi |
| Tilesets | `TilesetsApi Tilesets { get; }` | 3D Tiles |
| DataSources | `DataSourcesApi DataSources { get; }` | GeoJSON, TopoJSON, KML/KMZ, CZML, GPX |
| Primitives | `PrimitivesApi Primitives { get; }` | Point collection volume besar |
| Clock | `ClockApi Clock { get; }` | Clock simulasi |
| Scene | `SceneApi Scene { get; }` | Mode scene, efek, picking, screenshot, event |
| Drawing | `DrawingApi Drawing { get; }` | Menggambar, mengukur, mengedit |
| IsReady | `bool IsReady { get; }` | Viewer sudah dibuat |
| WhenReady | `Task WhenReady { get; }` | Selesai saat siap; faulted jika gagal dimuat |
| ExecuteScriptAsync | `ValueTask<string?> ExecuteScriptAsync(string script, ct)` | Mengevaluasi JS dengan `viewer`, `Cesium`, `scene` dalam scope; mengembalikan JSON |
| RequestRenderAsync | `ValueTask RequestRenderAsync()` | Meminta satu frame (mode request-render, setelah skrip) |
| SerializeOptions | `static string SerializeOptions(CesiumViewerOptions options)` | Menyerialisasi opsi untuk panggilan create milik host (lanjutan) |
| HandleHostEvent | `void HandleHostEvent(string name, string? json)` | Titik masuk event host → controller (tersembunyi; untuk host kustom) |
| HandleHostFailure | `void HandleHostFailure(Exception exception)` | Host melaporkan kegagalan muat (tersembunyi; untuk host kustom) |

Event:

| Event | Tipe |
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

| Metode | Signature | Deskripsi |
|---|---|---|
| FlyToAsync | `ValueTask FlyToAsync(CameraView view, ct)` | Terbang halus; selesai saat penerbangan berakhir atau dibatalkan |
| FlyToAsync | `ValueTask FlyToAsync(double longitude, double latitude, double height, double heading = 0, double pitch = -90, double? duration = null, ct)` | Terbang ke lon/lat/tinggi (derajat, meter) |
| SetViewAsync | `ValueTask SetViewAsync(CameraView view)` | Mengatur tampilan seketika |
| FlyToRectangleAsync | `ValueTask FlyToRectangleAsync(GeoRectangle rectangle, double? duration = null)` | Terbang hingga persegi memenuhi tampilan |
| FlyHomeAsync | `ValueTask FlyHomeAsync(double? duration = null)` | Terbang ke tampilan home default (kembali saat dimulai) |
| FlyToObjectAsync | `ValueTask FlyToObjectAsync(string id, HeadingPitchRange? offset = null, double? duration = null)` | Terbang ke entitas, data source, tileset, atau point collection |
| LookAtAsync | `ValueTask LookAtAsync(GeoPoint target, HeadingPitchRange offset)` | Mengunci kamera agar mengorbit target |
| ReleaseLookAtAsync | `ValueTask ReleaseLookAtAsync()` | Melepas kunci look-at |
| TrackEntityAsync | `ValueTask TrackEntityAsync(string? entityId)` | Mengikuti entitas; `null` menghentikan |
| StartOrbitAsync | `ValueTask StartOrbitAsync(GeoPoint target, double range, double pitch = -30, double degreesPerSecond = 6)` | Orbit terus-menerus mengelilingi satu titik |
| StartSpinAsync | `ValueTask StartSpinAsync(double degreesPerSecond = 2)` | Memutar globe |
| StopAnimationAsync | `ValueTask StopAnimationAsync()` | Menghentikan orbit/spin |
| ZoomAsync | `ValueTask ZoomAsync(double? meters = null, bool zoomIn = true)` | Zoom masuk/keluar; default 20 % (masuk) / 25 % (keluar) dari tinggi |
| MoveAsync | `ValueTask MoveAsync(CameraDirection direction, double? meters = null)` | Geser (pan); default 10 % dari tinggi |
| RotateAsync | `ValueTask RotateAsync(CameraDirection direction, double degrees = 5)` | Berputar mengelilingi pusat globe |
| LookAsync | `ValueTask LookAsync(CameraDirection direction, double degrees = 5)` | Menoleh di tempat: Left/Right yaw, Up/Down tilt |
| TwistAsync | `ValueTask TwistAsync(double degrees)` | Roll kamera |
| GetStateAsync | `ValueTask<CameraState> GetStateAsync(ct)` | Posisi, heading, pitch, roll, view rectangle saat ini |
| ConfigureControllerAsync | `ValueTask ConfigureControllerAsync(CameraControllerOptions options)` | Mengaktifkan/menonaktifkan input, batas zoom, deteksi tabrakan |
| EnableFlightModeAsync | `ValueTask EnableFlightModeAsync(FlightModeOptions? options = null)` | Orang pertama / terbang bebas dengan WASD/QE + mouse-look |
| DisableFlightModeAsync | `ValueTask DisableFlightModeAsync()` | Keluar dari flight mode |

## EntitiesApi

| Metode | Signature | Deskripsi |
|---|---|---|
| AddAsync | `ValueTask<string> AddAsync(EntityOptions entity, ct)` | Menambah entitas; mengembalikan id |
| AddRangeAsync | `ValueTask<string[]> AddRangeAsync(IReadOnlyList<EntityOptions> entities, ct)` | Menambah banyak entitas dalam satu panggilan dengan event ditangguhkan |
| UpdateAsync | `ValueTask UpdateAsync(string id, EntityOptions changes)` | Menerapkan properti non-null; objek grafis menggantikan yang lama |
| UpdatePositionsAsync | `ValueTask UpdatePositionsAsync(IReadOnlyList<EntityPositionUpdate> updates)` | Memindahkan banyak entitas dalam satu panggilan (id tak dikenal dilewati) |
| AddSamplesAsync | `ValueTask AddSamplesAsync(string id, SampledPositions samples)` | Menambahkan sampel waktu ke entitas bertipe sampled position |
| SetVisibleAsync | `ValueTask SetVisibleAsync(string id, bool visible)` | Tampilkan/sembunyikan |
| RemoveAsync | `ValueTask<bool> RemoveAsync(string id)` | Menghapus; `false` jika tidak ditemukan |
| ClearAsync | `ValueTask ClearAsync()` | Menghapus semua entitas viewer, termasuk bentuk hasil gambar |
| SelectAsync | `ValueTask SelectAsync(string? id)` | Memilih (info box + indikator); `null` membatalkan pilihan |
| GetPositionAsync | `ValueTask<GeoPoint?> GetPositionAsync(string id)` | Posisi pada waktu clock saat ini |
| GetIdsAsync | `ValueTask<string[]> GetIdsAsync()` | Id dalam koleksi entitas viewer |

## ImageryApi

| Metode | Signature | Deskripsi |
|---|---|---|
| AddAsync | `ValueTask<string> AddAsync(ImageryLayerOptions layer, ct)` | Menambah lapisan di paling atas; mengembalikan id |
| SetBaseLayerAsync | `ValueTask<string> SetBaseLayerAsync(ImageryLayerOptions layer, ct)` | Mengganti lapisan paling bawah; mengembalikan `"base"` |
| UpdateAsync | `ValueTask UpdateAsync(string id, ImageryLayerUpdate update)` | Opasitas, penyesuaian warna, visibilitas, sisi split |
| SetAlphaAsync | `ValueTask SetAlphaAsync(string id, double alpha)` | Opasitas 0..1 |
| RemoveAsync | `ValueTask RemoveAsync(string id)` | Menghapus lapisan |
| RaiseAsync / LowerAsync | `ValueTask RaiseAsync(string id)` / `ValueTask LowerAsync(string id)` | Naik/turun satu tingkat |
| RaiseToTopAsync / LowerToBottomAsync | `ValueTask RaiseToTopAsync(string id)` / `ValueTask LowerToBottomAsync(string id)` | Pindah ke paling atas/bawah |
| SetSplitPositionAsync | `ValueTask SetSplitPositionAsync(double position)` | Posisi swipe 0..1 |
| GetIdsAsync | `ValueTask<string[]> GetIdsAsync()` | Id lapisan yang dikenal, dari bawah ke atas |

## TerrainApi

| Metode | Signature | Deskripsi |
|---|---|---|
| SetProviderAsync | `ValueTask SetProviderAsync(TerrainOptions terrain, ct)` | Mengganti provider terrain |
| SetExaggerationAsync | `ValueTask SetExaggerationAsync(double factor, double relativeHeight = 0)` | Eksagerasi vertikal |
| SampleHeightsAsync | `ValueTask<GeoPoint[]> SampleHeightsAsync(IReadOnlyList<GeoPoint> points, ct)` | Tinggi terrain paling detail (di `Height`) |
| GetHeightAsync | `ValueTask<double?> GetHeightAsync(GeoPoint point, ct)` | Tinggi pada satu titik |
| GetProfileAsync | `ValueTask<ProfileSample[]> GetProfileAsync(GeoPoint start, GeoPoint end, int samples = 100, ct)` | Profil elevasi sepanjang geodesik (`samples` ≥ 2) |
| SetClippingPolygonsAsync | `ValueTask SetClippingPolygonsAsync(IReadOnlyList<IReadOnlyList<GeoPoint>>? polygons, bool inverse = false)` | Menyembunyikan globe di dalam (atau di luar) poligon; `null` menghapus |

## TilesetsApi

| Metode | Signature | Deskripsi |
|---|---|---|
| AddAsync | `ValueTask<string> AddAsync(TilesetOptions tileset, ct)` | Memuat tileset; mengembalikan id |
| SetStyleAsync | `ValueTask SetStyleAsync(string id, TilesetStyle? style)` | Style deklaratif; `null` me-reset |
| UpdateAsync | `ValueTask UpdateAsync(string id, TilesetOptions changes)` | Show, SSE, style, shading point cloud, offset tinggi, clipping, shader |
| SetShaderUniformAsync | `ValueTask SetShaderUniformAsync(string id, string name, double value)` | Mengatur uniform float pada custom shader |
| RemoveAsync | `ValueTask RemoveAsync(string id)` | Menghapus tileset |

## DataSourcesApi

| Metode | Signature | Deskripsi |
|---|---|---|
| LoadAsync | `ValueTask<string> LoadAsync(DataSourceOptions source, ct)` | Memuat GeoJSON/TopoJSON/KML/KMZ/CZML/GPX; mengembalikan id |
| SetVisibleAsync | `ValueTask SetVisibleAsync(string id, bool visible)` | Tampilkan/sembunyikan |
| RemoveAsync | `ValueTask RemoveAsync(string id)` | Menghapus dan menghancurkan |
| CountAsync | `ValueTask<int> CountAsync(string id)` | Jumlah entitas (0 jika tidak dikenal) |

## PrimitivesApi

| Metode | Signature | Deskripsi |
|---|---|---|
| AddPointsAsync | `ValueTask<string> AddPointsAsync(PointCollectionOptions points, ct)` | Point collection GPU (100rb+ titik); mengembalikan id |
| RemoveAsync | `ValueTask RemoveAsync(string id)` | Menghapus koleksi |
| SetVisibleAsync | `ValueTask SetVisibleAsync(string id, bool visible)` | Tampilkan/sembunyikan |

## ClockApi

| Metode | Signature | Deskripsi |
|---|---|---|
| ConfigureAsync | `ValueTask ConfigureAsync(ClockOptions options)` | Waktu mulai/akhir/saat ini, multiplier, rentang, animasi (parsial) |
| PlayAsync | `ValueTask PlayAsync()` | `ShouldAnimate = true` |
| PauseAsync | `ValueTask PauseAsync()` | `ShouldAnimate = false` |
| SetMultiplierAsync | `ValueTask SetMultiplierAsync(double multiplier)` | Detik simulasi per detik nyata |
| SetTimeAsync | `ValueTask SetTimeAsync(DateTimeOffset time)` | Lompat ke suatu waktu |
| GetStateAsync | `ValueTask<ClockState> GetStateAsync()` | Waktu saat ini, multiplier, status animasi |

## SceneApi

| Metode | Signature | Deskripsi |
|---|---|---|
| SetModeAsync | `ValueTask SetModeAsync(SceneMode mode, double duration = 1.5)` | Morph ke 3D / 2D / Columbus view |
| ConfigureAsync | `ValueTask ConfigureAsync(SceneSettings settings)` | Pencahayaan, bayangan, atmosfer, kabut, post-processing (parsial) |
| SetWeatherAsync | `ValueTask SetWeatherAsync(WeatherOptions weather)` | Post-process hujan, salju, kabut, atau tidak ada |
| CaptureScreenshotAsync | `ValueTask<string> CaptureScreenshotAsync(ct)` | Data URL PNG dari frame saat ini |
| CaptureScreenshotPngAsync | `ValueTask<byte[]> CaptureScreenshotPngAsync(ct)` | Byte PNG |
| PickAsync | `ValueTask<PickResult?> PickAsync(double x, double y)` | Pick pada posisi layar (piksel CSS) |
| ToScreenAsync | `ValueTask<Cartesian2?> ToScreenAsync(GeoPoint position)` | Dunia → layar; `null` jika tidak terlihat |
| ConfigureEventsAsync | `ValueTask ConfigureEventsAsync(CesiumEventOptions events)` | Mengubah event yang diteruskan saat runtime (objek lengkap) |

## DrawingApi

| Metode | Signature | Deskripsi |
|---|---|---|
| DrawAsync | `ValueTask<DrawResult?> DrawAsync(DrawOptions options, ct)` | Menggambar interaktif; `null` jika dibatalkan |
| CancelAsync | `ValueTask CancelAsync()` | Membatalkan gambar yang aktif |
| ClearAsync | `ValueTask ClearAsync()` | Menghapus semua bentuk dan label hasil gambar |
| BeginEditAsync | `ValueTask BeginEditAsync(string entityId)` | Verteks bisa diseret (polyline, polygon, entitas berposisi) |
| EndEditAsync | `ValueTask EndEditAsync()` | Keluar dari mode edit |

## Opsi dan hasil

### Pembuatan dan konfigurasi

| Tipe | Member utama |
|---|---|
| `CesiumViewerOptions` | `const DefaultCesiumVersion = "1.146.0"`, `CesiumBaseUrl`, `IonAccessToken`, `BaseLayer`, `NoBaseLayer`, `Terrain`, `SceneMode`, `InitialView`, flag widget (`Animation`, `Timeline`, `BaseLayerPicker`, `Geocoder`, `HomeButton`, `SceneModePicker`, `NavigationHelpButton`, `FullscreenButton` semuanya `false`; `InfoBox`, `SelectionIndicator`, `ShowCredits` semuanya `true`), `RequestRenderMode`, `MaximumRenderTimeChange`, `ResolutionScale`, `MsaaSamples`, `EnableLighting`, `Shadows`, `DepthTestAgainstTerrain`, `ShowFps`, `BackgroundColor`, `GlobeBaseColor`, `Events` |
| `CesiumEventOptions` | `Click` (true), `RightClick`, `DoubleClick`, `MouseMove`, `MouseMoveThrottleMs` (100), `CameraChanged` (true), `ClockTick`, `ClockTickThrottleMs` (500), `SelectedEntityChanged` (true), `LoadingProgress` |
| `CameraView` | `required Destination`, `Heading`, `Pitch`, `Roll`, `Duration`; `static At(lon, lat, height, heading = 0, pitch = -90, roll = 0)` |
| `CameraControllerOptions` | `EnableRotate`, `EnableTranslate`, `EnableZoom`, `EnableTilt`, `EnableLook`, `MinimumZoomDistance`, `MaximumZoomDistance`, `EnableCollisionDetection` |
| `FlightModeOptions` | `Speed` (120 m/s), `ScaleWithHeight` (true), `WalkHeight` |
| `ClockOptions` | `StartTime`, `StopTime`, `CurrentTime`, `Multiplier`, `ClockRange`, `ShouldAnimate`, `ZoomTimeline` |
| `SceneSettings` | `EnableLighting`, `ShowSun`, `ShowMoon`, `ShowSkyBox`, `ShowSkyAtmosphere`, `ShowGroundAtmosphere`, `ShowGlobe`, `DepthTestAgainstTerrain`, `BackgroundColor`, `GlobeBaseColor`, `Shadows`, `SoftShadows`, `ShadowDarkness`, `ShadowMapSize`, `TerrainShadows`, `FogEnabled`, `FogDensity`, `ShowWaterEffect`, `AtmosphereHueShift`, `AtmosphereSaturationShift`, `AtmosphereBrightnessShift`, `AtmosphereLightIntensity`, `LightIntensity`, `LightColor`, `CameraLight`, `HighDynamicRange`, `Fxaa`, `MsaaSamples`, `ResolutionScale`, `ShowFps`, `Bloom`, `BloomContrast`, `BloomBrightness`, `AmbientOcclusion`, `AmbientOcclusionIntensity`, `GlobeTranslucency`, `GlobeFrontAlpha`, `VerticalExaggeration`, `VerticalExaggerationRelativeHeight`, `SplitPosition` |
| `WeatherOptions` | `Kind` (None), `Intensity` (0.6), `WindAngle` (−10°) |
| `DrawOptions` | `Mode` (Polyline), `Color`, `FillColor`, `Width`, `ClampToGround` (default true), `KeepEntity` (default true), `ShowMeasurementLabel` (default true untuk mode ukur/polyline/polygon) |

### Entitas

| Tipe | Member utama |
|---|---|
| `EntityOptions` | `Id`, `Name`, `Description` (HTML), `Show`, `Position`, `SampledPosition`, `Orientation` (`HeadingPitchRoll`), `OrientToVelocity`, `Availability` (`TimeInterval`), `ParentId`, `Properties`; grafis `Point`, `Billboard`, `Label`, `Polyline`, `Polygon`, `Ellipse`, `Rectangle`, `Box`, `Cylinder`, `Ellipsoid`, `Corridor`, `Wall`, `Model`, `Path` |
| `TimeInterval` | `record(DateTimeOffset Start, DateTimeOffset Stop)` |
| `SampledPositions` | `required Times`, `required Positions`, `Interpolation`, `InterpolationDegree`, `Extrapolate`; `static From(IEnumerable<(DateTimeOffset, GeoPoint)>, InterpolationKind = Lagrange, int degree = 3)` |
| `PointGraphics` | `PixelSize` (10), `Color`, `OutlineColor`, `OutlineWidth`, `HeightReference`, `ScaleByDistance`, `DistanceDisplayCondition`, `AlwaysOnTop` |
| `BillboardGraphics` | `Image`, `PinColor`, `PinText`, `PinIcon` (id Maki), `PinSize` (48), `Scale`, `Width`, `Height`, `Color`, `Rotation`, `VerticalOrigin`, `HorizontalOrigin`, `PixelOffset`, `HeightReference`, `ScaleByDistance`, `DistanceDisplayCondition`, `AlwaysOnTop` |
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
| `PathGraphics` | `LeadTime`, `TrailTime`, `Width` (2), `Resolution` (60), `Material` (glow kuning) |
| `EntityPositionUpdate` | `readonly record struct(string Id, GeoPoint Position, double? Heading = null)` |
| `CesiumMaterial` | `Kind`, `Color`, `SecondaryColor`, … ; factory `Solid`, `Glow`, `Dash`, `Arrow`, `Outline`, `Stripe`, `Grid`, `Checkerboard`, `FromImage`; implicit dari `CesiumColor` |
| `DistanceDisplayCondition` | `record struct(double Near, double Far)` |
| `NearFarScalar` | `record struct(double Near, double NearValue, double Far, double FarValue)` |

### Lapisan, tile, data

| Tipe | Member utama |
|---|---|
| `ImageryLayerOptions` | `Provider`, `Url`, `Layers`, `Style`, `TileMatrixSetId`, `Format`, `Parameters`, `Subdomains`, `Key`, `MapStyle`, `AssetId`, `MinimumLevel`, `MaximumLevel`, `Rectangle`, `Credit`, `Color`, `Alpha`, `Brightness`, `Contrast`, `Hue`, `Saturation`, `Gamma`, `Show`, `SplitDirection`; factory `OpenStreetMap()`, `Satellite()`, `Streets()`, `Topographic()`, `Dark()`, `Light()`, `FromTemplate(url, credit = null, maximumLevel = null)`, `Wms(url, layers, format = "image/png", transparent = true)`, `Wmts(url, layer, tileMatrixSetId, style = "default", format = "image/png", maximumLevel = null)` |
| `ImageryLayerUpdate` | `Alpha`, `Brightness`, `Contrast`, `Hue`, `Saturation`, `Gamma`, `Show`, `SplitDirection` |
| `TerrainOptions` | `Provider`, `Url`, `AssetId`, `RequestVertexNormals`, `RequestWaterMask`; `Ellipsoid()`, `WorldTerrain(bool lighting = true, bool water = true)`, `ArcGis()` |
| `TilesetOptions` | `Kind`, `Url`, `AssetId`, `Key`, `MaximumScreenSpaceError`, `Show`, `FlyTo`, `HeightOffset`, `Style`, `PointCloudShading`, `CustomShader`, `Shadows`, `ClippingPolygons`, `ClipInverse`, `SkipLevelOfDetail`, `DynamicScreenSpaceError`, `DebugShowBoundingVolume`; `FromUrl(url, flyTo = false)`, `FromIon(assetId, flyTo = false)`, `OsmBuildings()`, `GooglePhotorealistic(key = null)` |
| `TilesetStyle` | `Color`, `ColorConditions` (`IReadOnlyList<string[]>`), `Show`, `PointSize`, `Defines` |
| `PointCloudShading` | `Attenuation`, `MaximumAttenuation`, `EyeDomeLighting`, `EyeDomeLightingStrength`, `EyeDomeLightingRadius` |
| `CustomShaderOptions` | `FragmentShaderText`, `VertexShaderText`, `LightingModel` ("PBR"/"UNLIT"), `Uniforms` (`Dictionary<string,double>`), `AnimateTime` (`u_time`) |
| `DataSourceOptions` | `Kind`, `Url`, `Data`, `Name`, `FlyTo`, `ClampToGround`, `Stroke`, `StrokeWidth`, `Fill`, `MarkerColor`, `MarkerSize`, `ExtrudeByProperty`, `ExtrudeScale`, `RandomFill`; `GeoJsonUrl`, `GeoJsonData`, `Kml`, `Czml`, `Gpx`, `TopoJson` |
| `PointCollectionOptions` | `required Positions`, `Colors`, `Color`, `PixelSize` (4), `OutlineColor`, `OutlineWidth`, `ScaleByDistance` |

### Hasil

| Tipe | Member |
|---|---|
| `CameraState` | `Position`, `Heading`, `Pitch`, `Roll`, `ViewRectangle?` |
| `PickResult` | `Kind`, `Screen`, `Position?`, `EntityId`, `EntityName`, `Properties`, `OwnerId` |
| `DrawResult` | `Mode`, `EntityId`, `Positions`, `Radius?`, hasil hitung `Length`, `Area` |
| `EntityEditedResult` | `EntityId`, `Positions` |
| `ClockState` | `CurrentTime`, `Multiplier`, `ShouldAnimate` |
| `CesiumError` | `Message`, `Source`, `Stack` |
| `ProfileSample` | `record struct(double Distance, GeoPoint Position)` |
| `CesiumInteropException` | `Method`, `JsMessage` |

## Enum

| Enum | Nilai |
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

| Tipe | Member |
|---|---|
| `GeoPoint` | `record struct(double Longitude, double Latitude, double Height = 0)`; `FromRadians(lon, lat, h = 0)`, `ToCartographic()`, `ToCartesian()` (ECEF), `WithHeight(h)`; JSON `[lon,lat,h]` |
| `Cartographic` | `record struct(double Longitude, double Latitude, double Height = 0)` dalam radian; `FromDegrees`, `ToGeoPoint()` |
| `Cartesian2` | `X`, `Y`; `Zero`, `Magnitude`, `+ - *`, `Dot`, `Distance` |
| `Cartesian3` | `X`, `Y`, `Z`; `Zero`, `UnitX/Y/Z`, `Magnitude`, `MagnitudeSquared`, `Normalize()`, `+ - * /`, unary `-`, `Dot`, `Cross`, `Distance`, `Lerp`, `AngleBetween` (rad), `ToGeoPoint()` (dari ECEF) |
| `Cartesian4` | `X`, `Y`, `Z`, `W`; `Zero`, `Magnitude`, `+ - *`, `Dot` |
| `HeadingPitchRoll` | `record struct(double Heading, double Pitch = 0, double Roll = 0)` (derajat) |
| `HeadingPitchRange` | `record struct(double Heading, double Pitch, double Range)` (derajat, meter) |
| `GeoRectangle` | `record struct(West, South, East, North)` (derajat); `Center`, `Contains(GeoPoint)`, `static FromPoints(IEnumerable<GeoPoint>)` |
| `GeoMath` | `DegreesPerRadian`, `RadiansPerDegree`, `TwoPi`, `ToRadians`, `ToDegrees`, `NormalizeLongitude` → [−180,180), `NormalizeBearing` → [0,360) |
| `CesiumColor` | `record struct(float R, float G, float B, float A = 1)`; `FromBytes`, `FromHex`, `FromHsl`, `WithAlpha`, `Lerp`, `ToCss()`; implicit dari `string`; konstanta palet |
| `Ellipsoid` | `record(double SemiMajorAxis, double InverseFlattening)`; `Wgs84`, `Flattening`, `SemiMinorAxis`, `EccentricitySquared`, `SecondEccentricitySquared`, `AuthalicRadius` |

## Geodesy

`public static class Geodesy` bekerja pada WGS84, dengan hasil dalam meter, m², dan derajat.

| Metode | Signature | Deskripsi |
|---|---|---|
| MeanEarthRadius | `const double MeanEarthRadius = 6371008.8` | Radius rata-rata IUGG |
| HaversineDistance | `double HaversineDistance(GeoPoint a, GeoPoint b)` | Jarak di bola (cepat, ~0,5 %) |
| Distance | `double Distance(GeoPoint a, GeoPoint b)` | Jarak elipsoidal Vincenty |
| SlantDistance | `double SlantDistance(GeoPoint a, GeoPoint b)` | Jarak garis lurus 3D termasuk tinggi |
| Bearing | `double Bearing(GeoPoint a, GeoPoint b)` | Bearing awal [0,360) |
| Inverse | `GeodesicInverse Inverse(GeoPoint p1, GeoPoint p2)` | Jarak + bearing awal/akhir (fallback bola dekat antipoda) |
| Destination | `GeoPoint Destination(GeoPoint start, double bearing, double distance)` | Masalah langsung; tinggi awal dipertahankan |
| Interpolate | `GeoPoint[] Interpolate(GeoPoint a, GeoPoint b, int count)` | `count` ≥ 2 titik sepanjang geodesik |
| Midpoint | `GeoPoint Midpoint(GeoPoint a, GeoPoint b)` | Titik tengah geodesik |
| PathLength | `double PathLength(IReadOnlyList<GeoPoint> path)` | Panjang polyline |
| Perimeter | `double Perimeter(IReadOnlyList<GeoPoint> ring)` | Panjang ring tertutup |
| Area | `double Area(IReadOnlyList<GeoPoint> ring)` | Luas ring pada bola autalik (~0,1 %) |
| Centroid | `GeoPoint Centroid(IReadOnlyList<GeoPoint> points)` | Centroid rata-rata ECEF, tinggi 0 |
| Contains | `bool Contains(IReadOnlyList<GeoPoint> ring, GeoPoint p)` | Uji point-in-polygon planar (ruang lon/lat) |
| Circle | `GeoPoint[] Circle(GeoPoint center, double radius, int segments = 64)` | Ring lingkaran geodesik |
| FormatDistance | `string FormatDistance(double meters)` | "850.0 m" / "12.40 km" |
| FormatArea | `string FormatArea(double squareMeters)` | "m²" / "ha" / "km²" |

`GeodesicInverse` adalah `record struct(double Distance, double InitialBearing, double FinalBearing)`.

## CoordinateConverter

| Metode | Signature | Deskripsi |
|---|---|---|
| WebMercatorMaxLatitude | `const double WebMercatorMaxLatitude = 85.05112877980659` | |
| GeodeticToEcef | `Cartesian3 GeodeticToEcef(GeoPoint p, Ellipsoid? ellipsoid = null)` | Lon/lat/h → ECEF |
| EcefToGeodetic | `GeoPoint EcefToGeodetic(Cartesian3 c, Ellipsoid? ellipsoid = null)` | ECEF → lon/lat/h (iteratif) |
| EcefToEnu | `Cartesian3 EcefToEnu(Cartesian3 ecef, GeoPoint origin, Ellipsoid? ellipsoid = null)` | ECEF → East-North-Up lokal |
| EnuToEcef | `Cartesian3 EnuToEcef(Cartesian3 enu, GeoPoint origin, Ellipsoid? ellipsoid = null)` | ENU → ECEF |
| GeodeticToEnu | `Cartesian3 GeodeticToEnu(GeoPoint point, GeoPoint origin)` | Lon/lat/h → ENU |
| EnuToGeodetic | `GeoPoint EnuToGeodetic(Cartesian3 enu, GeoPoint origin)` | ENU → lon/lat/h |
| GeodeticToWebMercator | `Cartesian2 GeodeticToWebMercator(GeoPoint p)` | → meter EPSG:3857 (lintang dibatasi) |
| WebMercatorToGeodetic | `GeoPoint WebMercatorToGeodetic(Cartesian2 m)` | EPSG:3857 → lon/lat |
| GeodeticToTile | `(int X, int Y) GeodeticToTile(GeoPoint p, int zoom)` | Indeks tile slippy-map |
| ToDms | `string ToDms(double degrees, bool isLatitude)` | mis. `6°10'31.4"S` |

## RealtimeEntityFeed

Namespace `CesiumNet.Realtime`. `public sealed class RealtimeEntityFeed : IAsyncDisposable`.

| Member | Signature | Deskripsi |
|---|---|---|
| ctor | `RealtimeEntityFeed(CesiumController controller, TimeSpan? interval = null)` | Memulai loop flush (default 100 ms) |
| Push | `void Push(EntityPositionUpdate update)` | Mengantre; yang terbaru menang per id; thread-safe |
| Push | `void Push(string id, GeoPoint position, double? heading = null)` | Overload praktis |
| FlushAsync | `ValueTask FlushAsync()` | Mengirim update tertunda sekarang |
| PublishedCount | `long PublishedCount { get; }` | Jumlah update yang sudah dikirim |
| PendingCount | `int PendingCount { get; }` | Entitas yang menunggu flush berikutnya |
| FlushFailed | `event EventHandler<Exception>? FlushFailed` | Dipicu di thread pool saat flush gagal |
| DisposeAsync | `ValueTask DisposeAsync()` | Menghentikan loop (update tertunda dibuang) |

## KeplerianOrbit

Namespace `CesiumNet.Aerospace`. `public sealed record KeplerianOrbit`.

| Member | Signature | Deskripsi |
|---|---|---|
| Konstanta | `Mu`, `J2`, `EarthRotationRate` | Konstanta Bumi |
| Elemen | `required SemiMajorAxis` (m), `Eccentricity`, `Inclination`, `RightAscension`, `ArgumentOfPerigee`, `MeanAnomaly` (derajat), `required Epoch`, `IncludeJ2` (true) | |
| Period | `TimeSpan Period { get; }` | Periode orbit |
| Circular | `static KeplerianOrbit Circular(double altitude, double inclination, double rightAscension, double phase, DateTimeOffset epoch)` | Orbit lingkaran dari ketinggian |
| InertialPosition | `Cartesian3 InertialPosition(DateTimeOffset time)` | Posisi ECI (m) |
| PositionAt | `GeoPoint PositionAt(DateTimeOffset time)` | Lon/lat/h dalam kerangka tetap Bumi |
| Sample | `IReadOnlyList<(DateTimeOffset Time, GeoPoint Position)> Sample(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)` | Sampel (step harus > 0) |
| ToSampledPositions | `SampledPositions ToSampledPositions(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)` | Lagrange derajat 5 |
| GroundTrack | `GeoPoint[] GroundTrack(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)` | Titik sub-satelit (h = 0) |
| FootprintRadius | `static double FootprintRadius(double altitude, double minElevation = 10)` | Radius di permukaan yang terlihat di atas mask elevasi |
| GreenwichSiderealAngle | `static double GreenwichSiderealAngle(DateTimeOffset time)` | GMST (rad) |

## CesiumIonClient

Namespace `CesiumNet.Ion`. `public sealed class CesiumIonClient(HttpClient httpClient, string accessToken)`.

| Member | Signature | Deskripsi |
|---|---|---|
| ListAssetsAsync | `Task<IReadOnlyList<IonAsset>> ListAssetsAsync(ct)` | Butuh scope `assets:list` |
| GetAssetAsync | `Task<IonAsset?> GetAssetAsync(long id, ct)` | Butuh `assets:read`; `null` pada 404 |
| WellKnownAssets | `CesiumWorldTerrain = 1`, `BingAerial = 2`, `BingAerialWithLabels = 3`, `BingRoad = 4`, `CesiumOsmBuildings = 96188`, `GooglePhotorealistic3DTiles = 2275207`, `Sentinel2 = 3954`, `EarthAtNight = 3812` | Id aset publik |
| `IonAsset` | `Id`, `Name`, `Description`, `Type`, `Bytes`, `DateAdded`, `Status`, `PercentComplete` | |

## Interop

Namespace `CesiumNet.Interop`.

| Member | Signature | Deskripsi |
|---|---|---|
| ICesiumTransport | `ValueTask<string?> InvokeAsync(string method, string argsJson, ct)` | Implementasikan untuk meng-host CesiumJS di tempat lain |
| CesiumScripts.BridgeScript | `static string BridgeScript { get; }` | Sumber `cesium-net.js` (embedded) |
| CesiumScripts.DefaultCesiumBaseUrl | `static string DefaultCesiumBaseUrl(string? version = null)` | URL jsDelivr untuk satu versi CesiumJS |
| CesiumScripts.ToJsLiteral | `static string ToJsLiteral(string? value)` | Literal string JS yang aman |
| `GeoPointJsonConverter`, `CesiumColorJsonConverter` | `JsonConverter<T>` | `[lon,lat,h]` dan `#RRGGBBAA` |

## Blazor (`CesiumNet.Blazor`)

| Tipe | Member |
|---|---|
| `CesiumViewer` | Parameter `Options`, `Class`, `Style`, `ChildContent`, `LoadingContent`, `ErrorContent`, atribut tertangkap; callback `OnReady`, `OnLeftClick`, `OnRightClick`, `OnDoubleClick`, `OnMouseMove`, `OnCameraChanged`, `OnSelectedEntityChanged`, `OnClockTick`, `OnDrawCompleted`, `OnEntityEdited`, `OnError`; `Controller`, `IsReady`, `DisposeAsync()` |
| `CesiumNetBlazorOptions` | `IonAccessToken`, `CesiumBaseUrl` |
| `CesiumNetServiceCollectionExtensions` | `IServiceCollection AddCesiumNet(this IServiceCollection services, Action<CesiumNetBlazorOptions>? configure = null)` |

## Avalonia (`CesiumNet.Avalonia`)

| Tipe | Member |
|---|---|
| `CesiumView : Decorator` | `Controller`, `Options`, `IsReady`, `WebView` (`NativeWebView`), event `Ready` (`CesiumReadyEventArgs`), `LoadFailed` (`CesiumError`) |
| `CesiumReadyEventArgs` | `Controller` |
| `CesiumLocalServer : IDisposable` | `static Shared`, `BaseUrl`, `string MapFolder(string name, string physicalPath)`, `Dispose()` |

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
