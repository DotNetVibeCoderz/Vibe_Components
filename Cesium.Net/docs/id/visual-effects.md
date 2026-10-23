[English](../en/visual-effects.md)

# Efek visual

Efek scene diatur lewat `Scene.ConfigureAsync(SceneSettings)`. Hanya properti yang tidak null yang diterapkan, jadi setiap panggilan adalah pembaruan parsial.

## Matahari, bulan, dan pencahayaan

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings
{
    EnableLighting = true,          // bayangan siang/malam dari posisi matahari pada waktu clock
    ShowSun = true, ShowMoon = true, ShowSkyBox = true,
    LightIntensity = 2.0,           // intensitas cahaya scene (matahari)
    LightColor = "#FFF1D6",
});
await cesium.Clock.SetTimeAsync(new DateTimeOffset(2025, 6, 21, 11, 0, 0, TimeSpan.Zero)); // posisi matahari mengikuti clock
```

| Pengaturan | Efek |
|---|---|
| `EnableLighting` | Globe disinari matahari (sisi malam menjadi gelap) |
| `ShowSun`, `ShowMoon`, `ShowSkyBox`, `ShowGlobe` | Menampilkan/menyembunyikan benda langit, bintang, dan globe |
| `LightIntensity`, `LightColor` | Kekuatan dan rona cahaya scene (matahari) |
| `CameraLight = true` | Mengganti matahari dengan cahaya terarah yang mengikuti kamera (seperti senter), berguna untuk memeriksa model saat malam. `false` mengembalikan matahari. |
| `HighDynamicRange` | Rendering HDR |

Bulan dirender sebagai objek, tetapi CesiumJS tidak menerangi scene dengan cahaya bulan. Untuk scene malam, turunkan `LightIntensity`, beri rona pada `LightColor`, atau gunakan `CameraLight`.

## Bayangan

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings
{
    Shadows = true, SoftShadows = true, ShadowDarkness = 0.35, ShadowMapSize = 4096,
    TerrainShadows = ShadowMode.Enabled,
});
```

Bayangan membutuhkan matahari, jadi aktifkan lighting atau pertahankan cahaya matahari bawaan. Model, box, cylinder, dan polygon ikut serta melalui properti `Shadows` masing-masing, sedangkan tileset melalui `TilesetOptions.Shadows`. Shadow map mahal, jadi gunakan 2048 pada GPU yang lemah.

## Atmosfer dan kabut

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings
{
    ShowSkyAtmosphere = true, ShowGroundAtmosphere = true,
    AtmosphereHueShift = -0.05, AtmosphereSaturationShift = 0.1, AtmosphereBrightnessShift = -0.1,
    AtmosphereLightIntensity = 12,
    FogEnabled = true, FogDensity = 0.0003,
});
```

Matikan kedua flag atmosfer untuk tampilan datar ala dashboard.

## Laut / air

```csharp
await cesium.Terrain.SetProviderAsync(TerrainOptions.WorldTerrain(lighting: true, water: true)); // token ion
await cesium.Scene.ConfigureAsync(new SceneSettings { ShowWaterEffect = true, EnableLighting = true });
```

Efek air membutuhkan terrain dengan **water mask**. Cesium World Terrain menyediakannya; provider ellipsoid dan ArcGIS tidak.

## Post-processing

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings
{
    Bloom = true, BloomContrast = 128, BloomBrightness = -0.3,
    AmbientOcclusion = true, AmbientOcclusionIntensity = 3,
    Fxaa = true, MsaaSamples = 4,
});
```

Ambient occlusion adalah kontrol yang paling mendekati "ambient lighting" yang tersedia. Efek ini mahal, jadi aktifkan untuk gambar diam atau tampilan dekat.

## Cuaca

```csharp
await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.Rain, Intensity = 0.7, WindAngle = -15 });
await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.Snow, Intensity = 0.5 });
await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.Fog,  Intensity = 0.6 });  // berbasis kedalaman
await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.None });
```

Ini adalah shader post-process screen-space: efek visual, bukan simulasi. Hujan dan salju membuat scene terus dirender ulang bahkan dalam mode request-render. Untuk efek lain seperti debu, night vision, atau tampilan termal, tambahkan `PostProcessStage` sendiri lewat `ExecuteScriptAsync`:

```csharp
await cesium.ExecuteScriptAsync("""
    viewer.scene.postProcessStages.add(Cesium.PostProcessStageLibrary.createBlackAndWhiteStage())
    """);
await cesium.RequestRenderAsync();
```

## Bawah tanah dan translusensi

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings { GlobeTranslucency = true, GlobeFrontAlpha = 0.35, DepthTestAgainstTerrain = true });
```

Anda bisa menggabungkannya dengan `Terrain.SetClippingPolygonsAsync` untuk membuat bukaan. Lihat [Citra & terrain](imagery-terrain.md#memotong-clipping-globe).

## Warna latar dan globe

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings { BackgroundColor = "#0B1016", GlobeBaseColor = "#15212A", ShowSkyBox = false });
```

## Screenshot

```csharp
string dataUrl = await cesium.Scene.CaptureScreenshotAsync();        // "data:image/png;base64,…"
byte[] png = await cesium.Scene.CaptureScreenshotPngAsync();
await File.WriteAllBytesAsync("frame.png", png);                       // Avalonia / sisi server
```

Frame dirender tepat sebelum diambil, jadi mode request-render aman. Canvas menjadi tainted, dan pengambilan gagal dengan exception, jika ada imagery atau tekstur yang dimuat **tanpa header CORS**.

## Custom shader dan material

- **3D Tiles**: `TilesetOptions.CustomShader` dengan `u_time` beranimasi dan uniform float; lihat [3D Tiles & data](3d-tiles-and-data.md#clipping-dan-custom-shader-digital-twin).
- **Entitas**: jenis `CesiumMaterial` bawaan (glow, dash, arrow, outline, stripe, grid, checkerboard, image).
- **Lainnya**, seperti material Fabric, image-based lighting, atau custom shader untuk model: gunakan `ExecuteScriptAsync`.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
