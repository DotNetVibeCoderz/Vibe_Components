namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class FlightSimulationSample : GallerySample
{
    public override string Id => "flight-simulation";
    public override string CategoryId => "time";
    public override Text Title => ("Flight simulation", "Simulasi penerbangan");
    public override Text Summary => (
        "A simulated flight from Jakarta to Bali: the aircraft follows time-tagged positions with climb, cruise and descent, turns along its velocity and leaves a trail. Use the timeline or the speed actions to scrub through the roughly 80-minute flight.",
        "Simulasi penerbangan dari Jakarta ke Bali: pesawat mengikuti posisi bertanda waktu dengan fase naik, jelajah, dan turun, berbelok mengikuti arah kecepatannya, dan meninggalkan jejak. Gunakan timeline atau aksi kecepatan untuk menelusuri penerbangan sekitar 80 menit.");
    public override GeoPoint? Location => new(110.9, -7.4);

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o => { o.Timeline = true; o.Animation = true; });

    private static readonly Text SpeedGroup = ("Speed", "Kecepatan");
    private static readonly Text CameraGroup = ("Camera", "Kamera");

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("x10", ("10× speed", "Kecepatan 10×"), ctx => Speed(ctx, 10)) { Group = SpeedGroup },
        new("x60", ("60× speed", "Kecepatan 60×"), ctx => Speed(ctx, 60)) { Group = SpeedGroup },
        new("x300", ("300× speed", "Kecepatan 300×"), ctx => Speed(ctx, 300)) { Group = SpeedGroup },
        new("pause", ("Pause / play", "Jeda / putar"), async ctx =>
        {
            var state = await ctx.Cesium.Clock.GetStateAsync();
            await ctx.Cesium.Clock.ConfigureAsync(new ClockOptions { ShouldAnimate = !state.ShouldAnimate });
            ctx.Log(state.ShouldAnimate ? $"Paused at {state.CurrentTime.ToOffset(TimeSpan.FromHours(7)):HH:mm:ss} WIB." : "Playing.");
        }) { Group = SpeedGroup },
        new("track", ("Follow aircraft", "Ikuti pesawat"), ctx => ctx.Cesium.Camera.TrackEntityAsync("flight").AsTask()) { Group = CameraGroup },
        new("free", ("Whole route", "Seluruh rute"), async ctx =>
        {
            await ctx.Cesium.Camera.TrackEntityAsync(null);
            await ctx.Cesium.Camera.FlyToRectangleAsync(new GeoRectangle(105.5, -10.5, 116.5, -4.5), duration: 2);
        }) { Group = CameraGroup },
    ];

    private static async Task Speed(SampleContext ctx, double multiplier)
    {
        await ctx.Cesium.Clock.ConfigureAsync(new ClockOptions { Multiplier = multiplier, ShouldAnimate = true });
        ctx.Log($"Clock multiplier {multiplier}×");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        var cgk = new GeoPoint(106.6559, -6.1256);   // Soekarno-Hatta
        var dps = new GeoPoint(115.1672, -8.7482);   // Ngurah Rai
        var departure = new DateTimeOffset(2026, 3, 2, 1, 0, 0, TimeSpan.Zero); // 08:00 WIB

        // Great-circle points with a climb / cruise / descent altitude profile.
        var route = Geodesy.Interpolate(cgk, dps, 80);
        var total = Geodesy.Distance(cgk, dps);
        static double Smooth(double a, double b, double x) { var t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

        var samples = new List<(DateTimeOffset, GeoPoint)>();
        var time = departure;
        for (var i = 0; i < route.Length; i++)
        {
            var f = (double)i / (route.Length - 1);
            var altitude = 11_000 * Smooth(0, 0.18, f) * (1 - Smooth(0.80, 1, f));
            if (i > 0)
            {
                var speed = 120 + 110 * (altitude / 11_000);   // 120 m/s near the ground, 230 m/s cruising
                time = time.AddSeconds(total / (route.Length - 1) / speed);
            }
            samples.Add((time, route[i].WithHeight(altitude)));
        }
        var arrival = time;

        await cesium.Entities.AddAsync(new EntityOptions
        {
            Id = "flight",
            Name = "Jakarta → Bali (simulated)",
            SampledPosition = SampledPositions.From(samples, InterpolationKind.Lagrange, 3),
            OrientToVelocity = true,
            Availability = new TimeInterval(departure, arrival),
            Model = new ModelGraphics
            {
                Uri = "https://raw.githubusercontent.com/CesiumGS/cesium/main/Apps/SampleData/models/CesiumAir/Cesium_Air.glb",
                MinimumPixelSize = 64,
                RunAnimations = true,
            },
            Path = new PathGraphics { TrailTime = 7_200, LeadTime = 0, Width = 4, Material = CesiumMaterial.Glow(CesiumColor.Yellow, 0.2) },
        });

        // Drive the timeline: loop the flight at 60× real time.
        await cesium.Clock.ConfigureAsync(new ClockOptions
        {
            StartTime = departure,
            StopTime = arrival,
            CurrentTime = departure,
            Multiplier = 60,
            ClockRange = ClockRange.LoopStop,
            ShouldAnimate = true,
            ZoomTimeline = true,
        });
        await cesium.Camera.TrackEntityAsync("flight");

        ctx.Log($"Route {Geodesy.FormatDistance(total)}, flight time {(arrival - departure):h\\:mm}, {samples.Count} samples.");
        // </snippet>
    }
}
