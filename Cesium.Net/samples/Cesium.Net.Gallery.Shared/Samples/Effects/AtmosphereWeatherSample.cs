namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class AtmosphereWeatherSample : GallerySample
{
    public override string Id => "atmosphere-weather";
    public override string CategoryId => "effects";
    public override Text Title => ("Atmosphere & weather", "Atmosfer & cuaca");
    public override Text Summary => (
        "The Dieng Plateau in Central Java under changing skies. Add rain, snow or fog as screen-space effects, then tune scene fog, bloom and the atmosphere hue.",
        "Dataran Tinggi Dieng di Jawa Tengah di bawah langit yang berubah. Tambahkan hujan, salju, atau kabut sebagai efek layar, lalu atur kabut adegan, bloom, dan rona atmosfer.");
    public override GeoPoint? Location => new(109.9100, -7.2000);
    public override bool UsesIon => true;

    private static readonly Text WeatherGroup = ("Weather", "Cuaca");
    private static readonly Text AtmosphereGroup = ("Atmosphere", "Atmosfer");

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o =>
        o.Terrain = string.IsNullOrWhiteSpace(ionToken) ? TerrainOptions.ArcGis() : TerrainOptions.WorldTerrain());

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("rain", ("Rain", "Hujan"), ctx => Weather(ctx, WeatherKind.Rain, 0.7)) { Group = WeatherGroup },
        new("snow", ("Snow", "Salju"), ctx => Weather(ctx, WeatherKind.Snow, 0.6)) { Group = WeatherGroup },
        new("fog", ("Fog", "Kabut"), ctx => Weather(ctx, WeatherKind.Fog, 0.7)) { Group = WeatherGroup },
        new("clear", ("Clear sky", "Langit cerah"), ctx => Weather(ctx, WeatherKind.None, 0)) { Group = WeatherGroup },
        new("bloom", ("Toggle bloom", "Bloom aktif/nonaktif"), async ctx =>
        {
            var on = !ctx.Get("bloom", false);
            ctx.State["bloom"] = on;
            await ctx.Cesium.Scene.ConfigureAsync(new SceneSettings { Bloom = on, BloomContrast = 128, BloomBrightness = -0.2 });
            ctx.Log($"Bloom {(on ? "on" : "off")}.");
        }) { Group = AtmosphereGroup },
        new("sunset", ("Sunset hue", "Rona senja"), async ctx =>
        {
            var on = !ctx.Get("sunset", false);
            ctx.State["sunset"] = on;
            await ctx.Cesium.Scene.ConfigureAsync(on
                ? new SceneSettings { AtmosphereHueShift = -0.08, AtmosphereSaturationShift = 0.3, AtmosphereBrightnessShift = -0.1 }
                : new SceneSettings { AtmosphereHueShift = 0, AtmosphereSaturationShift = 0, AtmosphereBrightnessShift = 0 });
            ctx.Log(on ? "Atmosphere shifted towards a warm sunset hue." : "Atmosphere hue reset.");
        }) { Group = AtmosphereGroup },
        new("scenefog", ("Dense scene fog", "Kabut adegan tebal"), async ctx =>
        {
            var dense = !ctx.Get("denseFog", false);
            ctx.State["denseFog"] = dense;
            await ctx.Cesium.Scene.ConfigureAsync(new SceneSettings { FogEnabled = true, FogDensity = dense ? 0.0012 : 0.0002 });
            ctx.Log($"Scene fog density {(dense ? 0.0012 : 0.0002)}.");
        }) { Group = AtmosphereGroup },
    ];

    private static async Task Weather(SampleContext ctx, WeatherKind kind, double intensity)
    {
        await ctx.Cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = kind, Intensity = intensity });
        ctx.Log(kind == WeatherKind.None ? "Weather cleared." : $"{kind} at intensity {intensity:P0}.");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        // Look north-east across the plateau towards the Java Sea, a little above the ground.
        await cesium.Camera.FlyToAsync(109.8900, -7.2350, 3_600, heading: 30, pitch: -8, duration: 3);

        // Built-in atmosphere and fog settings.
        await cesium.Scene.ConfigureAsync(new SceneSettings
        {
            FogEnabled = true,
            FogDensity = 0.0002,
            ShowGroundAtmosphere = true,
            AtmosphereLightIntensity = 12,
        });

        // Screen-space weather: rain, snow or fog — one call each.
        await cesium.Scene.SetWeatherAsync(new WeatherOptions { Kind = WeatherKind.Rain, Intensity = 0.7, WindAngle = -12 });
        ctx.Log("Rain at intensity 70%.");
        // </snippet>
    }
}
