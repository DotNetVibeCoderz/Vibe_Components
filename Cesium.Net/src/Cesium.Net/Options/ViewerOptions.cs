namespace CesiumNet;

/// <summary>
/// Options used when the viewer is created. Everything is optional — defaults give an
/// OpenStreetMap globe with no Cesium ion dependency and a minimal, clean UI.
/// </summary>
public sealed class CesiumViewerOptions
{
    /// <summary>The CesiumJS version loaded from the CDN when <see cref="CesiumBaseUrl"/> is not set.</summary>
    public const string DefaultCesiumVersion = "1.146.0";

    /// <summary>
    /// Base URL of the CesiumJS build (the folder that contains <c>Cesium.js</c>, <c>Widgets/</c>, <c>Workers/</c>, <c>Assets/</c>).
    /// Defaults to jsDelivr. Point this at a self-hosted copy for offline/air-gapped deployments.
    /// </summary>
    public string? CesiumBaseUrl { get; set; }

    /// <summary>Cesium ion access token. Required for Cesium World Terrain, OSM Buildings, Bing and ion assets.</summary>
    public string? IonAccessToken { get; set; }

    /// <summary>Base imagery layer. Defaults to OpenStreetMap. Set <see cref="NoBaseLayer"/> for none.</summary>
    public ImageryLayerOptions? BaseLayer { get; set; }
    public bool NoBaseLayer { get; set; }

    /// <summary>Initial terrain. Defaults to the smooth ellipsoid.</summary>
    public TerrainOptions? Terrain { get; set; }

    public SceneMode SceneMode { get; set; } = SceneMode.Scene3D;

    /// <summary>Initial camera view.</summary>
    public CameraView? InitialView { get; set; }

    // Built-in CesiumJS widgets (all off by default; Cesium.Net encourages your own UI).
    public bool Animation { get; set; }
    public bool Timeline { get; set; }
    public bool BaseLayerPicker { get; set; }
    public bool Geocoder { get; set; }
    public bool HomeButton { get; set; }
    public bool SceneModePicker { get; set; }
    public bool NavigationHelpButton { get; set; }
    public bool FullscreenButton { get; set; }
    public bool InfoBox { get; set; } = true;
    public bool SelectionIndicator { get; set; } = true;
    /// <summary>Show the credit/attribution bar. Required by most data licenses — keep enabled in production.</summary>
    public bool ShowCredits { get; set; } = true;

    /// <summary>
    /// Render only when something changes (big CPU/GPU/battery savings for static scenes).
    /// Cesium.Net requests renders automatically after every API call.
    /// </summary>
    public bool RequestRenderMode { get; set; }

    /// <summary>With <see cref="RequestRenderMode"/>, max simulation-time change (seconds) before a render is forced.</summary>
    public double? MaximumRenderTimeChange { get; set; }

    /// <summary>Rendering resolution multiplier (e.g. 0.75 for faster rendering on weak GPUs).</summary>
    public double? ResolutionScale { get; set; }

    /// <summary>MSAA samples (1 = off, 4 = default high quality).</summary>
    public int? MsaaSamples { get; set; }

    public bool EnableLighting { get; set; }
    public bool Shadows { get; set; }
    public bool DepthTestAgainstTerrain { get; set; }
    public bool ShowFps { get; set; }
    public CesiumColor? BackgroundColor { get; set; }
    public CesiumColor? GlobeBaseColor { get; set; }

    /// <summary>Which interop events are raised (opting out of unused ones saves bandwidth).</summary>
    public CesiumEventOptions Events { get; set; } = new();
}

/// <summary>Controls which events the JavaScript side forwards to .NET.</summary>
public sealed class CesiumEventOptions
{
    public bool Click { get; set; } = true;
    public bool RightClick { get; set; }
    public bool DoubleClick { get; set; }
    /// <summary>Mouse-move events (throttled to <see cref="MouseMoveThrottleMs"/>).</summary>
    public bool MouseMove { get; set; }
    public int MouseMoveThrottleMs { get; set; } = 100;
    /// <summary>Camera move-end events with the new camera state.</summary>
    public bool CameraChanged { get; set; } = true;
    /// <summary>Clock tick events (throttled to <see cref="ClockTickThrottleMs"/>).</summary>
    public bool ClockTick { get; set; }
    public int ClockTickThrottleMs { get; set; } = 500;
    public bool SelectedEntityChanged { get; set; } = true;
    /// <summary>Tile loading progress (number of pending tiles).</summary>
    public bool LoadingProgress { get; set; }
}

/// <summary>A camera destination.</summary>
public sealed class CameraView
{
    public required GeoPoint Destination { get; set; }
    /// <summary>Degrees clockwise from north.</summary>
    public double? Heading { get; set; }
    /// <summary>Degrees; -90 looks straight down.</summary>
    public double? Pitch { get; set; }
    public double? Roll { get; set; }
    /// <summary>Flight duration in seconds (fly-to only).</summary>
    public double? Duration { get; set; }

    public static CameraView At(double longitude, double latitude, double height, double heading = 0, double pitch = -90, double roll = 0)
        => new() { Destination = new GeoPoint(longitude, latitude, height), Heading = heading, Pitch = pitch, Roll = roll };
}

/// <summary>Camera input settings.</summary>
public sealed class CameraControllerOptions
{
    public bool? EnableRotate { get; set; }
    public bool? EnableTranslate { get; set; }
    public bool? EnableZoom { get; set; }
    public bool? EnableTilt { get; set; }
    public bool? EnableLook { get; set; }
    public double? MinimumZoomDistance { get; set; }
    public double? MaximumZoomDistance { get; set; }
    public bool? EnableCollisionDetection { get; set; }
}

/// <summary>Configures the simulation clock (used by time-dynamic data and the timeline).</summary>
public sealed class ClockOptions
{
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? StopTime { get; set; }
    public DateTimeOffset? CurrentTime { get; set; }
    /// <summary>Simulation seconds per real second.</summary>
    public double? Multiplier { get; set; }
    public ClockRange? ClockRange { get; set; }
    public bool? ShouldAnimate { get; set; }
    /// <summary>Zoom the timeline widget to the start/stop range.</summary>
    public bool? ZoomTimeline { get; set; }
}

/// <summary>
/// Scene rendering and visual-effect settings. Only non-null properties are applied, so a
/// settings object can be used as a partial update.
/// </summary>
public sealed class SceneSettings
{
    public bool? EnableLighting { get; set; }
    public bool? ShowSun { get; set; }
    public bool? ShowMoon { get; set; }
    public bool? ShowSkyBox { get; set; }
    public bool? ShowSkyAtmosphere { get; set; }
    public bool? ShowGroundAtmosphere { get; set; }
    public bool? ShowGlobe { get; set; }
    public bool? DepthTestAgainstTerrain { get; set; }
    public CesiumColor? BackgroundColor { get; set; }
    public CesiumColor? GlobeBaseColor { get; set; }

    /// <summary>Dynamic shadows from the sun (entities, models, 3D tiles).</summary>
    public bool? Shadows { get; set; }
    public bool? SoftShadows { get; set; }
    public double? ShadowDarkness { get; set; }
    public int? ShadowMapSize { get; set; }
    public ShadowMode? TerrainShadows { get; set; }

    public bool? FogEnabled { get; set; }
    public double? FogDensity { get; set; }

    /// <summary>Animated water on oceans (requires terrain with a water mask).</summary>
    public bool? ShowWaterEffect { get; set; }

    public double? AtmosphereHueShift { get; set; }
    public double? AtmosphereSaturationShift { get; set; }
    public double? AtmosphereBrightnessShift { get; set; }
    public double? AtmosphereLightIntensity { get; set; }

    /// <summary>Intensity of the scene light (sun).</summary>
    public double? LightIntensity { get; set; }
    public CesiumColor? LightColor { get; set; }
    /// <summary>Use a fixed directional light (flashlight-like, follows camera) instead of the sun.</summary>
    public bool? CameraLight { get; set; }

    public bool? HighDynamicRange { get; set; }
    public bool? Fxaa { get; set; }
    public int? MsaaSamples { get; set; }
    public double? ResolutionScale { get; set; }
    public bool? ShowFps { get; set; }

    public bool? Bloom { get; set; }
    public double? BloomContrast { get; set; }
    public double? BloomBrightness { get; set; }
    public bool? AmbientOcclusion { get; set; }
    public double? AmbientOcclusionIntensity { get; set; }

    /// <summary>Make the globe translucent to see underground features.</summary>
    public bool? GlobeTranslucency { get; set; }
    public double? GlobeFrontAlpha { get; set; }
    /// <summary>Terrain vertical exaggeration factor.</summary>
    public double? VerticalExaggeration { get; set; }
    public double? VerticalExaggerationRelativeHeight { get; set; }
    /// <summary>Imagery split position (0..1) for swipe comparisons.</summary>
    public double? SplitPosition { get; set; }
}

/// <summary>Screen-space weather effect.</summary>
public sealed class WeatherOptions
{
    public WeatherKind Kind { get; set; } = WeatherKind.None;
    /// <summary>0..1</summary>
    public double Intensity { get; set; } = 0.6;
    /// <summary>Wind angle in degrees (rain/snow slant).</summary>
    public double WindAngle { get; set; } = -10;
}

/// <summary>Options for interactive drawing.</summary>
public sealed class DrawOptions
{
    public DrawMode Mode { get; set; } = DrawMode.Polyline;
    public CesiumColor? Color { get; set; }
    public CesiumColor? FillColor { get; set; }
    public double? Width { get; set; }
    public bool? ClampToGround { get; set; }
    /// <summary>Keep the drawn shape as an entity (true) or remove it after <c>DrawCompleted</c>.</summary>
    public bool? KeepEntity { get; set; }
    /// <summary>Show a live measurement label (for measure modes).</summary>
    public bool? ShowMeasurementLabel { get; set; }
}

/// <summary>First-person / free-flight camera options.</summary>
public sealed class FlightModeOptions
{
    /// <summary>Movement speed in meters per second (scaled automatically with altitude when <see cref="ScaleWithHeight"/>).</summary>
    public double Speed { get; set; } = 120;
    public bool ScaleWithHeight { get; set; } = true;
    /// <summary>Keep the camera at a fixed height above ground (walk mode).</summary>
    public double? WalkHeight { get; set; }
}
