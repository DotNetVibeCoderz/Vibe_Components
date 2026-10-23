[Bahasa Indonesia](../id/visual-effects.md)

# Visual effects

Scene effects go through `Scene.ConfigureAsync(SceneSettings)`. Only non-null properties are applied, so each call is a partial update.

## Sun, moon and lighting

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings
{
    EnableLighting = true,          // day/night shading from the sun position at the clock time
    ShowSun = true, ShowMoon = true, ShowSkyBox = true,
    LightIntensity = 2.0,           // scene light (sun) intensity
    LightColor = "#FFF1D6",
});
await cesium.Clock.SetTimeAsync(new DateTimeOffset(2025, 6, 21, 11, 0, 0, TimeSpan.Zero)); // sun position follows the clock
```

| Setting | Effect |
|---|---|
| `EnableLighting` | Globe shaded by the sun (night side darkens) |
| `ShowSun`, `ShowMoon`, `ShowSkyBox`, `ShowGlobe` | Toggle celestial objects, stars and the globe |
| `LightIntensity`, `LightColor` | Scene light (sun) strength and tint |
| `CameraLight = true` | Replaces the sun with a directional light that follows the camera (flashlight), which is handy for inspecting models at night. `false` restores the sun. |
| `HighDynamicRange` | HDR rendering |

The moon is rendered as an object, but CesiumJS does not light the scene with moonlight. For night scenes, lower `LightIntensity`, tint `LightColor`, or use `CameraLight`.

## Shadows

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings
{
    Shadows = true, SoftShadows = true, ShadowDarkness = 0.35, ShadowMapSize = 4096,
    TerrainShadows = ShadowMode.Enabled,
});
```

Shadows need the sun, so enable lighting or keep the default sun light. Models, boxes, cylinders and polygons opt in through their own `Shadows` property, and tilesets through `TilesetOptions.Shadows`. Shadow maps are expensive, so use 2048 on weak GPUs.

## Atmosphere and fog

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings
{
    ShowSkyAtmosphere = true, ShowGroundAtmosphere = true,
    AtmosphereHueShift = -0.05, AtmosphereSaturationShift = 0.1, AtmosphereBrightnessShift = -0.1,
    AtmosphereLightIntensity = 12,
    FogEnabled = true, FogDensity = 0.0003,
});
```

Turn both atmosphere flags off for a flat, dashboard-like look.

## Ocean / water

```csharp
await cesium.Terrain.SetProviderAsync(TerrainOptions.WorldTerrain(lighting: true, water: true)); // ion token
await cesium.Scene.ConfigureAsync(new SceneSettings { ShowWaterEffect = true, EnableLighting = true });
```

The water effect needs terrain with a **water mask**. Cesium World Terrain provides one; the ellipsoid and ArcGIS providers don't.

## Post-processing

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings
{
    Bloom = true, BloomContrast = 128, BloomBrightness = -0.3,
    AmbientOcclusion = true, AmbientOcclusionIntensity = 3,
    Fxaa = true, MsaaSamples = 4,
});
```

Ambient occlusion is the closest thing to "ambient lighting" control that is exposed. It is costly, so enable it for stills or close-up views.

## Weather

```csharp
await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.Rain, Intensity = 0.7, WindAngle = -15 });
await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.Snow, Intensity = 0.5 });
await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.Fog,  Intensity = 0.6 });  // depth-based
await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.None });
```

These are screen-space post-process shaders: a visual effect, not a simulation. Rain and snow keep the scene re-rendering even in request-render mode. For other effects such as dust, night vision or thermal looks, add your own `PostProcessStage` via `ExecuteScriptAsync`:

```csharp
await cesium.ExecuteScriptAsync("""
    viewer.scene.postProcessStages.add(Cesium.PostProcessStageLibrary.createBlackAndWhiteStage())
    """);
await cesium.RequestRenderAsync();
```

## Underground and translucency

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings { GlobeTranslucency = true, GlobeFrontAlpha = 0.35, DepthTestAgainstTerrain = true });
```

You can combine this with `Terrain.SetClippingPolygonsAsync` to cut openings. See [Imagery & terrain](imagery-terrain.md#clipping-the-globe).

## Background and globe colors

```csharp
await cesium.Scene.ConfigureAsync(new SceneSettings { BackgroundColor = "#0B1016", GlobeBaseColor = "#15212A", ShowSkyBox = false });
```

## Screenshots

```csharp
string dataUrl = await cesium.Scene.CaptureScreenshotAsync();        // "data:image/png;base64,…"
byte[] png = await cesium.Scene.CaptureScreenshotPngAsync();
await File.WriteAllBytesAsync("frame.png", png);                       // Avalonia / server-side
```

The frame is rendered right before capture, so request-render mode is fine. The canvas becomes tainted, and the capture throws, if any imagery or texture was loaded **without CORS** headers.

## Custom shaders and materials

- **3D Tiles**: `TilesetOptions.CustomShader` with animated `u_time` and float uniforms; see [3D Tiles & data](3d-tiles-and-data.md#clipping-and-custom-shaders-digital-twins).
- **Entities**: built-in `CesiumMaterial` kinds (glow, dash, arrow, outline, stripe, grid, checkerboard, image).
- **Anything else**, such as Fabric materials, image-based lighting or model custom shaders: use `ExecuteScriptAsync`.

---
Made by Gravicode Studios, led by Kang Fadhil.
