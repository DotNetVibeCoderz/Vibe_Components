namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class LightingShadowsSample : GallerySample
{
    public override string Id => "lighting-shadows";
    public override string CategoryId => "effects";
    public override Text Title => ("Lighting & shadows", "Pencahayaan & bayangan");
    public override Text Summary => (
        "A milk truck and a few block buildings near Monas cast sun shadows. Set the time of day to morning, noon, evening or night, or let a whole day pass at high speed.",
        "Truk susu dan beberapa gedung blok di dekat Monas memancarkan bayangan matahari. Atur waktu ke pagi, siang, sore, atau malam, atau biarkan satu hari berlalu dengan cepat.");
    public override GeoPoint? Location => Origin;

    private static readonly GeoPoint Origin = new(106.8240, -6.1790);
    // Times are in WIB (UTC+7).
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 6, 21, hour, minute, 0, TimeSpan.FromHours(7));

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o =>
    {
        o.EnableLighting = true;
        o.Shadows = true;
    });

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("morning", ("Morning 07:00", "Pagi 07:00"), ctx => SetTime(ctx, At(7))),
        new("noon", ("Noon 12:00", "Siang 12:00"), ctx => SetTime(ctx, At(12))),
        new("evening", ("Evening 17:15", "Sore 17:15"), ctx => SetTime(ctx, At(17, 15))),
        new("night", ("Night 21:00", "Malam 21:00"), ctx => SetTime(ctx, At(21))),
        new("day", ("Play a whole day", "Putar satu hari"), async ctx =>
        {
            await ctx.Cesium.Clock.ConfigureAsync(new ClockOptions { CurrentTime = At(6), Multiplier = 1_800, ShouldAnimate = true });
            ctx.Log("Clock running at 1,800× — one hour every two seconds.");
        }),
    ];

    private static async Task SetTime(SampleContext ctx, DateTimeOffset time)
    {
        await ctx.Cesium.Clock.ConfigureAsync(new ClockOptions { CurrentTime = time, ShouldAnimate = false });
        ctx.Log($"Time set to {time:HH:mm} WIB.");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        GeoPoint At(double east, double north, double up = 0)
            => CoordinateConverter.EnuToGeodetic(new Cartesian3(east, north, up), Origin);

        // Shadows from the sun on entities and the ground.
        await cesium.Scene.ConfigureAsync(new SceneSettings
        {
            EnableLighting = true,
            Shadows = true,
            SoftShadows = true,
            ShadowDarkness = 0.35,
            TerrainShadows = ShadowMode.ReceiveOnly,
        });

        await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Milk truck",
            Position = At(0, 0),
            Orientation = new HeadingPitchRoll(30),
            Model = new ModelGraphics
            {
                Uri = "https://raw.githubusercontent.com/CesiumGS/cesium/main/Apps/SampleData/models/CesiumMilkTruck/CesiumMilkTruck.glb",
                Shadows = ShadowMode.Enabled,
                Scale = 4,
            },
        });
        var blocks = new (double East, double North, double Height)[] { (-40, 25, 40), (35, 30, 25), (-30, -35, 60), (45, -25, 15) };
        foreach (var (east, north, height) in blocks)
            await cesium.Entities.AddAsync(new EntityOptions
            {
                Name = $"Block {height} m",
                Position = At(east, north, height / 2),
                Box = new BoxGraphics { Dimensions = new Cartesian3(18, 18, height), Material = CesiumColor.FromHex("#E8EEF5"), Shadows = ShadowMode.Enabled },
            });

        // Lighting follows the simulation clock — set it to a summer-solstice morning.
        await cesium.Clock.ConfigureAsync(new ClockOptions { CurrentTime = new DateTimeOffset(2026, 6, 21, 8, 0, 0, TimeSpan.FromHours(7)), ShouldAnimate = false });
        await cesium.Camera.FlyToAsync(Origin.Longitude + 0.0006, Origin.Latitude - 0.0016, 140, heading: 340, pitch: -35, duration: 3);
        ctx.Log("Time set to 08:00 WIB, 21 June.");
        // </snippet>
    }
}
