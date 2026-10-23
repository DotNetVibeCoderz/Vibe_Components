[English](../en/feature-matrix.md)

# Matriks fitur

Halaman ini memetakan setiap butir daftar fitur CesiumJS (`features.md`) ke cara Cesium.Net mendukungnya.

| Tanda | Arti |
|---|---|
| ✅ API | Ada API C# bertipe |
| 🟡 Parsial | Sebagian dibungkus; sisanya lewat `ExecuteScriptAsync` atau kode Anda sendiri |
| 🔧 Skrip | Tidak dibungkus; bisa diakses lewat `ExecuteScriptAsync` (CesiumJS mentah) |
| ⚙️ Bawaan | Dikerjakan otomatis oleh CesiumJS; tidak ada yang perlu dipanggil |
| ➖ N/A | Tidak berlaku untuk wrapper .NET, atau merupakan fitur sisi layanan |
| ❌ Tidak tercakup | Tidak tersedia; gunakan alat lain |

Kolom Sample berisi id contoh galeri (`/sample/{id}` di galeri Blazor). Lihat [Galeri](gallery.md).

## 1. Rendering engine

| Fitur | Status | Cara |
|---|---|---|
| Visualisasi globe 3D | ✅ API | `CesiumViewer` / `CesiumView`, `SceneMode.Scene3D` (contoh `hello-globe`) |
| Model Bumi elipsoidal | ✅ API | WGS84 di semua tempat; `Ellipsoid.Wgs84`, `TerrainOptions.Ellipsoid()` |
| Hamburan atmosfer | ✅ API | `SceneSettings.ShowSkyAtmosphere`, `ShowGroundAtmosphere`, `Atmosphere*Shift`, `AtmosphereLightIntensity` |
| Rendering skybox dan horizon | 🟡 Parsial | `ShowSkyBox`, `BackgroundColor`; tekstur skybox kustom lewat skrip |
| Mode 2D dan Columbus view | ✅ API | `Scene.SetModeAsync`, `CesiumViewerOptions.SceneMode` (contoh `scene-modes`) |
| Rendering dengan akselerasi GPU | ⚙️ Bawaan | WebGL di browser atau WebView |
| Frustum culling | ⚙️ Bawaan | |
| Occlusion culling | ⚙️ Bawaan | Horizon culling oleh CesiumJS |
| Level of detail | ✅ API | 3D Tiles `MaximumScreenSpaceError`, `SkipLevelOfDetail`, `DynamicScreenSpaceError`; `DistanceDisplayCondition` (contoh `discrete-lod`) |
| Instanced rendering | 🟡 Parsial | 3D Tiles ter-instance (`i3dm`) di-render native; `Primitives.AddPointsAsync` mem-batch titik; primitive `GeometryInstance` generik lewat skrip |

## 2. Terrain & elevasi

| Fitur | Status | Cara |
|---|---|---|
| Cesium World Terrain | ✅ API | `TerrainOptions.WorldTerrain()` (token ion) |
| Provider terrain kustom | ✅ API | `CesiumTerrainServer` (URL quantized-mesh), aset `Ion`, `ArcGisElevation` (ImageServer kustom); provider JS kustom lewat skrip |
| Eksagerasi terrain | ✅ API | `Terrain.SetExaggerationAsync` (contoh `terrain-exaggeration`) |
| Clipping terrain | ✅ API | `Terrain.SetClippingPolygonsAsync`, `GlobeTranslucency` (contoh `globe-clipping`) |
| Bayangan terrain | ✅ API | `SceneSettings.TerrainShadows` |
| Kueri elevasi | ✅ API | `Terrain.GetHeightAsync` |
| Sampling tinggi | ✅ API | `Terrain.SampleHeightsAsync` (batch, paling detail) |
| Profil terrain | ✅ API | `Terrain.GetProfileAsync` (contoh `elevation-profile`) |
| Ground clamping | ✅ API | `HeightReference`, `ClampToGround`, `ClassifyGround`, `DataSourceOptions.ClampToGround` |

## 3. Imagery & lapisan peta

| Fitur | Status | Cara |
|---|---|---|
| Citra satelit | ✅ API | `ImageryLayerOptions.Satellite()` (Esri), aset ion, Bing (contoh `base-maps`) |
| Peta jalan | ✅ API | `OpenStreetMap()`, `Streets()`, `Dark()`, `Light()` |
| Peta topografi | ✅ API | `Topographic()` |
| Peta cuaca | 🟡 Parsial | Layanan cuaca WMS/WMTS/XYZ apa pun sebagai lapisan; tidak ada provider cuaca bawaan (contoh `weather-radar`) |
| OpenStreetMap | ✅ API | Lapisan dasar default |
| Bing Maps | ✅ API | `ImageryProviderKind.Bing` + `Key`, atau aset ion 2–4 |
| Layanan ArcGIS | ✅ API | `ArcGisMapServer`, preset Esri, terrain `ArcGisElevation` |
| WMS | ✅ API | `ImageryLayerOptions.Wms(...)` (contoh `wms-wmts`) |
| WMTS | ✅ API | `ImageryLayerOptions.Wmts(...)` |
| TMS | ✅ API | `ImageryProviderKind.Tms` |
| Provider imagery kustom | 🟡 Parsial | `UrlTemplate`, `SingleTile`, `Grid`, `TileCoordinates`; subclass `ImageryProvider` JS lewat skrip |
| Opasitas & blending lapisan | ✅ API | `Alpha`, `Brightness`, `Contrast`, `Hue`, `Saturation`, `Gamma`, `SplitDirection` + `SetSplitPositionAsync`, urutan (contoh `layer-blending`) |

## 4. OGC 3D Tiles

| Fitur | Status | Cara |
|---|---|---|
| Streaming dataset masif | ✅ API | `Tilesets.AddAsync` (URL, ion, OSM Buildings, Google Photorealistic) |
| LOD runtime | ✅ API | `MaximumScreenSpaceError` lewat `UpdateAsync` (contoh `discrete-lod`) |
| Pemuatan bergantung tampilan | ⚙️ Bawaan | Ditambah `SkipLevelOfDetail`, `DynamicScreenSpaceError` |
| Visualisasi smart city | ✅ API | OSM Buildings + kondisi `TilesetStyle` (contoh `feature-styling`) |
| Visualisasi BIM | 🟡 Parsial | Memuat tileset BIM (mis. hasil konversi ion); picking fitur beserta properti; show/warna per fitur lewat ekspresi style. Tidak ada API pohon BIM atau elemen |
| Point cloud LiDAR | ✅ API | `PointCloudShading` (attenuation, eye-dome lighting), style `PointSize` (contoh `point-cloud`) |
| Mesh fotogrametri | ✅ API | Tileset, `HeightOffset`, poligon clipping |
| Dukungan digital twin | 🟡 Parsial | Tile + custom shader + clipping + entitas real-time; logika twin dari Anda |

## 5. Model & format 3D

| Fitur | Status | Cara |
|---|---|---|
| glTF / GLB | ✅ API | `ModelGraphics` |
| CZML | ✅ API | `DataSourceKind.Czml` |
| GeoJSON | ✅ API | `DataSourceOptions.GeoJsonUrl/GeoJsonData`, styling, ekstrusi |
| KML / KMZ | ✅ API | `DataSourceOptions.Kml` (KMZ lewat URL) |
| GPX | ✅ API | `DataSourceOptions.Gpx` |
| TopoJSON | ✅ API | `DataSourceOptions.TopoJson` |
| Animasi | 🟡 Parsial | `ModelGraphics.RunAnimations` (semua klip); memilih klip atau articulation tertentu lewat skrip |
| Material PBR | ⚙️ Bawaan | PBR glTF di-render native; tint `Color`, `Silhouette*` |
| Transformasi runtime | ✅ API | `Entities.UpdateAsync` (posisi, orientasi, skala), `UpdatePositionsAsync` dengan heading |

## 6. Visualisasi time-dynamic

| Fitur | Status | Cara |
|---|---|---|
| Kontrol timeline | ✅ API | Widget `Animation`/`Timeline`, `ClockApi`, `ClockTick` |
| Pemutaran historis | ✅ API | `ClockOptions` (rentang, multiplier, `LoopStop`), `SampledPositions` |
| Pelacakan aset | ✅ API | `RealtimeEntityFeed`, `TrackEntityAsync` |
| Simulasi pesawat | ✅ API | `SampledPosition` + `OrientToVelocity` + `Path` |
| Pelacakan kapal | ✅ API | Feed real-time / sampled positions |
| Pelacakan kendaraan | ✅ API | Feed real-time / sampled positions |
| Pelacakan satelit | ✅ API | `KeplerianOrbit` → `SampledPositions` (propagasi SGP4/TLE tidak disertakan) |
| Simulasi berbasis waktu | ✅ API | Clock + `Availability` + `AddSamplesAsync` |

## 7. Dirgantara & satelit

| Fitur | Status | Cara |
|---|---|---|
| Visualisasi orbit | ✅ API | `KeplerianOrbit.ToSampledPositions` + `PathGraphics` |
| Ground track | ✅ API | `KeplerianOrbit.GroundTrack` |
| Perencanaan misi | 🟡 Parsial | Bahan dasar: `Geodesy` (waypoint, bearing), profil terrain, koridor. Tidak ada UI perencana atau optimizer |
| Jalur penerbangan | ✅ API | Sampled positions, polyline geodesik, koridor |
| Visualisasi lalu lintas udara | ✅ API | `RealtimeEntityFeed`, billboard dan model yang mengikuti heading |
| Analisis cakupan | 🟡 Parsial | `KeplerianOrbit.FootprintRadius`, dome dan sektor; tidak ada perhitungan access window atau visibilitas |
| Volume sensor | ✅ API | `EllipsoidGraphics.Dome`/`Sector`, sudut clock/cone, kerucut via `CylinderGraphics` |

## 8. GIS & analisis spasial

| Fitur | Status | Cara |
|---|---|---|
| Point, polyline, polygon | ✅ API | Grafis entitas (contoh `entity-showcase`) |
| Persegi & lingkaran | ✅ API | `RectangleGraphics`, `EllipseGraphics`, `Geodesy.Circle` |
| Koridor & dinding | ✅ API | `CorridorGraphics`, `WallGraphics` |
| Perhitungan jarak | ✅ API | `Geodesy.Distance` (Vincenty), `DrawMode.MeasureDistance` |
| Perhitungan luas | ✅ API | `Geodesy.Area`, `DrawMode.MeasureArea` |
| Konversi koordinat | ✅ API | `CoordinateConverter` |
| Kueri spasial | 🟡 Parsial | `Geodesy.Contains`, `GeoRectangle.Contains`, view rectangle, cek radius. Tidak ada indeks spasial atau operasi topologi (union, buffer poligon) |
| Ray casting | 🟡 Parsial | Picking sinar layar lewat `Scene.PickAsync` / event klik (tile, terrain, elipsoid); perpotongan sinar sembarang (`scene.pickFromRay`) lewat skrip |

## 9. Sistem entitas

| Fitur | Status | Cara |
|---|---|---|
| Billboard | ✅ API | `BillboardGraphics` |
| Marker | ✅ API | `PinColor`/`PinText` (PinBuilder) |
| Ikon | ✅ API | URL/data URL `Image`, `PinIcon` (Maki) |
| Polyline | ✅ API | `PolylineGraphics` + material |
| Polygon | ✅ API | `PolygonGraphics` (lubang, ekstrusi, klasifikasi) |
| Objek volume | ✅ API | Box, silinder, elipsoid, poligon terekstrusi, dinding |
| Entitas dinamis | ✅ API | `UpdateAsync`, `UpdatePositionsAsync`, sampled positions |

## 10. Sistem kamera

| Fitur | Status | Cara |
|---|---|---|
| Zoom | ✅ API | `ZoomAsync`, roda mouse |
| Pan | ✅ API | `MoveAsync` |
| Rotasi | ✅ API | `RotateAsync`, `StartSpinAsync` |
| Tilt | ✅ API | `LookAsync(Up/Down)`, `EnableTilt` |
| FlyTo | ✅ API | `FlyToAsync`, `FlyToRectangleAsync`, `FlyToObjectAsync`, `FlyHomeAsync` |
| LookAt | ✅ API | `LookAtAsync`, `StartOrbitAsync`, `TrackEntityAsync` |
| Kamera orang pertama | ✅ API | `EnableFlightModeAsync(WalkHeight = …)` |
| Kamera terbang bebas | ✅ API | `EnableFlightModeAsync()` |

## 11. Interaktivitas

| Fitur | Status | Cara |
|---|---|---|
| Event mouse | ✅ API | `LeftClick`, `RightClick`, `DoubleClick`, `MouseMove` |
| Gestur sentuh | 🟡 Parsial | Gestur kamera CesiumJS (pinch, rotate, tilt) bekerja native; tap diterima sebagai `LeftClick`. Tidak ada event .NET untuk gestur mentah |
| Picking objek | ✅ API | `PickResult` (`Entity`, `Primitive`), `SelectedEntityChanged` |
| Picking terrain | ✅ API | `PickResult.Position` dengan `Kind = Globe` |
| Picking tile | ✅ API | `PickKind.TileFeature` beserta properti fitur dan `OwnerId` |

## 12. Menggambar & mengedit

| Fitur | Status | Cara |
|---|---|---|
| Menggambar titik | ✅ API | `DrawMode.Point` |
| Menggambar polyline | ✅ API | `DrawMode.Polyline`, `MeasureDistance` |
| Menggambar poligon | ✅ API | `DrawMode.Polygon`, `Rectangle`, `Circle`, `MeasureArea` |
| Edit bentuk dinamis | ✅ API | `Drawing.BeginEditAsync`, `EntityEdited` (seret verteks; tidak bisa menambah/menghapus verteks) |
| Pembaruan runtime | ✅ API | `Entities.UpdateAsync` |

## 13. Efek visual

| Fitur | Status | Cara |
|---|---|---|
| Pencahayaan matahari | ✅ API | `EnableLighting`, `ShowSun`, `LightIntensity`, `LightColor` |
| Pencahayaan bulan | 🟡 Parsial | `ShowMoon` me-render bulan; CesiumJS tidak punya cahaya bulan. Simulasikan dengan `LightColor`/`LightIntensity` atau `CameraLight` |
| Pencahayaan ambient | 🟡 Parsial | `AmbientOcclusion`, `LightIntensity`; image-based lighting lewat skrip |
| Bayangan dinamis | ✅ API | `Shadows`, `SoftShadows`, `ShadowDarkness`, `ShadowMapSize`, `Shadows` per objek |
| Efek atmosfer | ✅ API | Atmosfer langit/permukaan, shift, `HighDynamicRange`, `Bloom` |
| Kabut | ✅ API | `FogEnabled`, `FogDensity`, `WeatherKind.Fog` |
| Rendering laut | ✅ API | `ShowWaterEffect` dengan terrain ber-water mask (World Terrain) |
| Refleksi | 🟡 Parsial | Refleksi lingkungan PBR pada glTF; tidak ada refleksi planar atau screen-space; IBL lewat skrip |
| Efek cuaca kustom | ✅ API | `Scene.SetWeatherAsync` (hujan, salju, kabut); `PostProcessStage` kustom lewat skrip |

## 14. Visualisasi sensor & militer

| Fitur | Status | Cara |
|---|---|---|
| Cakupan radar | ✅ API | `EllipsoidGraphics.Dome` |
| Sensor EO/IR | ✅ API | `EllipsoidGraphics.Sector`, kerucut (`CylinderGraphics` dengan `TopRadius = 0`) |
| Visualisasi sonar | ✅ API | Elipsoid belahan bawah (`MinimumCone = 90`, `MaximumCone = 180`) |
| Analisis ancaman | 🟡 Parsial | Range ring (`Geodesy.Circle`), zona, `Geodesy.Contains`, jarak di C#. Tidak ada line-of-sight atau terrain masking |
| Zona deteksi | ✅ API | Poligon/elips + `Geodesy.Contains` |
| Visualisasi medan tempur | 🟡 Parsial | Entitas, pin, path, dan basemap gelap; tidak ada renderer simbol MIL-STD-2525/APP-6 (gunakan gambar sendiri) |
| Common operational picture (COP) | ✅ API | Kombinasi di atas + `RealtimeEntityFeed`; lihat [Dirgantara & pertahanan](aerospace-and-defense.md) |

## 15. Streaming data real-time

| Fitur | Status | Cara |
|---|---|---|
| WebSocket | ✅ API | `RealtimeEntityFeed` + `ClientWebSocket`; lihat [Pelacakan real-time](realtime-tracking.md) |
| SignalR | ✅ API | `RealtimeEntityFeed` + client SignalR |
| MQTT | ✅ API | `RealtimeEntityFeed` + MQTTnet (pustaka transport tidak disertakan) |
| REST API | ✅ API | `RealtimeEntityFeed` + polling, atau `SampledPosition` + `AddSamplesAsync` |
| Pelacakan UAV | ✅ API | Feed + heading + `TrackEntityAsync` |
| Pelacakan kendaraan | ✅ API | Feed + `UpdatePositionsAsync` |
| Pemantauan IoT | ✅ API | Entitas dengan `Properties`, `UpdateAsync` untuk warna status, feed untuk sensor bergerak |

## 16. Sistem koordinat

| Fitur | Status | Cara |
|---|---|---|
| WGS84 | ✅ API | `GeoPoint`, `Ellipsoid.Wgs84` |
| Lintang / bujur | ✅ API | `GeoPoint` (derajat), `Cartographic` (radian), `ToDms` |
| Cartesian2 | ✅ API | `Cartesian2` |
| Cartesian3 | ✅ API | `Cartesian3` (matematika vektor) |
| Cartesian4 | ✅ API | `Cartesian4` |
| ECEF | ✅ API | `GeodeticToEcef`/`EcefToGeodetic`, `GeoPoint.ToCartesian()` |
| ENU | ✅ API | `EcefToEnu`, `EnuToEcef`, `GeodeticToEnu`, `EnuToGeodetic` |
| Web Mercator | ✅ API | `GeodeticToWebMercator`, `WebMercatorToGeodetic`, `GeodeticToTile` |

## 17. Fitur developer

| Fitur | Status | Cara |
|---|---|---|
| Dukungan TypeScript | ➖ N/A | Digantikan API C# bertipe kuat dengan anotasi nullable dan XML docs |
| Integrasi React | ➖ N/A | Wrapper .NET |
| Integrasi Angular | ➖ N/A | Wrapper .NET |
| Integrasi Vue | ➖ N/A | Wrapper .NET |
| Integrasi Blazor | ✅ API | `Cesium.Net.Blazor` (`CesiumViewer`) |
| Integrasi Svelte | ➖ N/A | Wrapper .NET |
| Custom shader | 🟡 Parsial | `CustomShaderOptions` pada 3D Tiles (dengan uniform dan `u_time`); model dan primitive lewat skrip |
| Material kustom | 🟡 Parsial | Jenis `CesiumMaterial` bawaan; material Fabric lewat skrip |
| *(tambahan)* Desktop | ✅ API | `Cesium.Net.Avalonia` (Windows, macOS, Linux) |

## 18. Integrasi Cesium ion

| Fitur | Status | Cara |
|---|---|---|
| Hosting aset | ✅ API | Memakai aset ion lewat id (`FromIon`, `TerrainProviderKind.Ion`, `ImageryProviderKind.Ion`); daftar/periksa dengan `CesiumIonClient` |
| Hosting terrain | ✅ API | `TerrainProviderKind.Ion` |
| Hosting fotogrametri | ✅ API | `TilesetOptions.FromIon` |
| Pembuatan 3D Tiles | ➖ N/A | Dikerjakan ion (upload/tiling). `CesiumIonClient` membaca status (`Status`, `PercentComplete`) tetapi tidak bisa upload |
| Konversi BIM | ➖ N/A | Dikerjakan ion; muat hasilnya dengan `FromIon` |
| Versioning aset | ❌ Tidak tercakup | Tidak disediakan oleh `CesiumIonClient`; gunakan REST API ion langsung |

## 19. Kasus penggunaan enterprise

Kasus-kasus ini adalah kombinasi fitur di atas. Tabel berikut mencantumkan bahan dasar utamanya.

| Kasus | Bahan dasar |
|---|---|
| Smart city | OSM Buildings / tileset kota, styling, ekstrusi GeoJSON, entitas IoT real-time |
| Digital twin | Tileset fotogrametri/BIM, clipping, custom shader, picking fitur, feed real-time |
| Dirgantara | `KeplerianOrbit`, sampled positions, volume sensor, clock |
| Pertahanan | Pola COP, volume sensor, zona dan `Geodesy.Contains`, basemap gelap |
| Agritech | Poligon lahan, `Geodesy.Area`, lapisan NDVI WMS/WMTS, gambar `MeasureArea` |
| Pertambangan | Profil terrain, eksagerasi, clipping/translusensi globe, point cloud |
| Maritim | AIS lewat `RealtimeEntityFeed`, efek air, lapisan imagery batimetri |
| Energi | Koridor dan dinding untuk jaringan dan pipa, profil terrain, picking aset |

## Keunggulan utama CesiumJS dalam Cesium.Net

| Keunggulan | Cakupan |
|---|---|
| Rendering globe 3D skala planet | ✅ |
| Streaming OGC 3D Tiles | ✅ |
| Visualisasi digital twin | 🟡 (bahan dasar) |
| Simulasi satelit & orbit | ✅ (Kepler; tanpa SGP4) |
| Data geospasial time-dynamic | ✅ |
| Rendering point cloud LiDAR | ✅ |
| Fotogrametri masif | ✅ |
| Rendering terrain global | ✅ |
| Situational awareness kelas militer | 🟡 (tanpa simbologi, tanpa line-of-sight) |
| GIS + engine 3D dalam satu platform | ✅ (geodesi C# + CesiumJS) |

## Kekurangan yang diketahui

- Tidak ada propagator SGP4/TLE (hanya Kepler + J2).
- Tidak ada analisis line-of-sight, viewshed, atau terrain masking.
- Tidak ada operasi boolean poligon, buffering, atau indeks spasial.
- Tidak ada simbologi MIL-STD-2525.
- Edit verteks hanya memindahkan verteks yang ada; tidak bisa menyisipkan atau menghapus verteks.
- Custom shader terbatas pada 3D Tiles; material Fabric dan IBL membutuhkan skrip.
- `CesiumIonClient` hanya baca: tidak ada upload, tiling, atau versioning.

Untuk hal lain yang bisa dilakukan CesiumJS, `ExecuteScriptAsync` memberi akses penuh ke `viewer`, `scene`, dan `Cesium`.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
