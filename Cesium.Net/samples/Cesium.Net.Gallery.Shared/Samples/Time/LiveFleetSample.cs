using CesiumNet.Realtime;

namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class LiveFleetSample : GallerySample
{
    public override string Id => "live-fleet";
    public override string CategoryId => "time";
    public override Text Title => ("Live fleet", "Armada real-time");
    public override Text Summary => (
        "200 simulated vessels in the Java Sea, moved by a C# simulation and streamed to the globe through a RealtimeEntityFeed at 10 updates per second. Speed up time, follow a vessel, or check how many updates were sent.",
        "200 kapal simulasi di Laut Jawa, digerakkan oleh simulasi C# dan dialirkan ke globe melalui RealtimeEntityFeed 10 kali per detik. Percepat waktu, ikuti sebuah kapal, atau periksa jumlah pembaruan yang telah dikirim.");
    public override GeoPoint? Location => new(111.0, -5.0);

    private static readonly GeoRectangle Sea = new(106.5, -6.4, 115.5, -3.6);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("fast", ("Time ×300", "Waktu ×300"), ctx => { ctx.State["timeScale"] = 300.0; ctx.Log("Simulation running at 300× real time."); return Task.CompletedTask; }),
        new("normal", ("Time ×30", "Waktu ×30"), ctx => { ctx.State["timeScale"] = 30.0; ctx.Log("Simulation running at 30× real time."); return Task.CompletedTask; }),
        new("follow", ("Follow vessel 7", "Ikuti kapal 7"), async ctx =>
        {
            await ctx.Cesium.Camera.FlyToObjectAsync("vessel-7", new HeadingPitchRange(0, -45, 25_000), duration: 2);
            await ctx.Cesium.Camera.TrackEntityAsync("vessel-7");
        }),
        new("overview", ("Whole Java Sea", "Seluruh Laut Jawa"), async ctx =>
        {
            await ctx.Cesium.Camera.TrackEntityAsync(null);
            await ctx.Cesium.Camera.FlyToRectangleAsync(Sea, duration: 2);
        }),
        new("stats", ("Show feed statistics", "Statistik feed"), ctx =>
        {
            var feed = ctx.Get<RealtimeEntityFeed>("feed");
            ctx.Log($"Feed: {feed.PublishedCount:N0} position updates published, {feed.PendingCount} pending.");
            return Task.CompletedTask;
        }),
    ];

    private enum VesselType { Cargo, Tanker, Fishing, Passenger }

    private sealed class Vessel
    {
        public required string Id { get; init; }
        public GeoPoint Position { get; set; }
        public double Heading { get; set; }
        public double SpeedKnots { get; init; }
    }

    private static string Arrow(string color) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24'><path d='M12 2 L19 21 L12 16 L5 21 Z' fill='{color}' stroke='#15212A' stroke-width='1.5'/></svg>"));

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        var random = new Random(7);
        var icons = new Dictionary<VesselType, string>
        {
            [VesselType.Cargo] = Arrow("#00C7FF"), [VesselType.Tanker] = Arrow("#FF9500"),
            [VesselType.Fishing] = Arrow("#34C759"), [VesselType.Passenger] = Arrow("#FF2D92"),
        };

        // 1. Create 200 vessels in one call.
        var vessels = new List<Vessel>();
        var entities = new List<EntityOptions>();
        for (var i = 0; i < 200; i++)
        {
            var type = (VesselType)random.Next(4);
            var v = new Vessel
            {
                Id = $"vessel-{i}",
                Position = new GeoPoint(Sea.West + random.NextDouble() * (Sea.East - Sea.West), Sea.South + random.NextDouble() * (Sea.North - Sea.South)),
                Heading = random.NextDouble() * 360,
                SpeedKnots = type == VesselType.Fishing ? 6 + random.NextDouble() * 4 : 10 + random.NextDouble() * 10,
            };
            vessels.Add(v);
            entities.Add(new EntityOptions
            {
                Id = v.Id,
                Name = $"{type} {i}",
                Position = v.Position,
                Properties = new() { ["type"] = type.ToString(), ["speed"] = $"{v.SpeedKnots:F1} kn" },
                Billboard = new BillboardGraphics { Image = icons[type], Scale = 0.8, Rotation = v.Heading },
            });
        }
        await cesium.Entities.AddRangeAsync(entities);
        await cesium.Camera.FlyToRectangleAsync(Sea, duration: 2);

        // 2. A feed batches position updates and flushes them every 100 ms (10 Hz).
        var feed = new RealtimeEntityFeed(cesium, TimeSpan.FromMilliseconds(100));
        ctx.Resources.Add(feed);                 // disposed when the sample stops
        ctx.State["feed"] = feed;
        ctx.State["timeScale"] = 300.0;
        ctx.Log("200 vessels created; simulation running at 300× real time.");

        // 3. The simulation loop: dead-reckoning with gentle course changes, until ctx.Token is cancelled.
        _ = SimulateAsync();
        async Task SimulateAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            var lastReport = DateTime.UtcNow;
            try
            {
                while (await timer.WaitForNextTickAsync(ctx.Token))
                {
                    var dt = 0.1 * ctx.Get("timeScale", 300.0);
                    foreach (var v in vessels)
                    {
                        v.Heading += (random.NextDouble() - 0.5) * 4;
                        var next = Geodesy.Destination(v.Position, v.Heading, v.SpeedKnots * 0.514444 * dt);
                        if (!Sea.Contains(next)) v.Heading = Geodesy.Bearing(v.Position, Sea.Center);
                        else v.Position = next;
                        feed.Push(v.Id, v.Position, v.Heading);
                    }
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(10))
                    {
                        lastReport = DateTime.UtcNow;
                        ctx.Log($"{feed.PublishedCount:N0} updates published so far.");
                    }
                }
            }
            catch (OperationCanceledException) { /* sample stopped */ }
        }
        // </snippet>
    }
}
