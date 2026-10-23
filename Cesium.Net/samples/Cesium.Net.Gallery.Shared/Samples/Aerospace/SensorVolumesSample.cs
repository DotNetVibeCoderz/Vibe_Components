namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class SensorVolumesSample : GallerySample
{
    public override string Id => "sensor-volumes";
    public override string CategoryId => "aerospace";
    public override Text Title => ("Sensor volumes", "Volume sensor");
    public override Text Summary => (
        "Illustrative coverage around the Sunda Strait: radar domes on both shores, electro-optical wedges watching the shipping lane, and a sonar hemisphere below a patrol ship. Sweep the wedges or make the sea translucent to see underwater.",
        "Ilustrasi cakupan di sekitar Selat Sunda: kubah radar di kedua pantai, irisan elektro-optik yang mengawasi jalur pelayaran, dan setengah bola sonar di bawah kapal patroli. Sapukan irisan atau buat laut tembus pandang untuk melihat ke bawah air.");
    public override GeoPoint? Location => new(105.80, -5.95);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("sweep", ("Sweep EO/IR sensors", "Sapukan sensor EO/IR"), async ctx =>
        {
            for (var step = 0; step <= 36 && !ctx.Token.IsCancellationRequested; step++)
            {
                var offset = Math.Sin(step / 36.0 * Math.Tau) * 40;
                foreach (var (id, center, range) in Wedges)
                    await ctx.Cesium.Entities.UpdateAsync(id, new EntityOptions { Ellipsoid = EllipsoidGraphics.Sector(range, center + offset - 20, center + offset + 20, maxElevation: 30) });
                await Task.Delay(120, ctx.Token);
            }
            ctx.Log("Sweep finished.");
        }),
        new("domes", ("Toggle radar domes", "Kubah radar aktif/nonaktif"), async ctx =>
        {
            var show = !ctx.Get("domes", true);
            ctx.State["domes"] = show;
            foreach (var id in new[] { "radar-anyer", "radar-bakauheni" }) await ctx.Cesium.Entities.SetVisibleAsync(id, show);
            ctx.Log(show ? "Radar domes shown." : "Radar domes hidden.");
        }),
        new("underwater", ("See underwater", "Lihat bawah air"), async ctx =>
        {
            var on = !ctx.Get("underwater", false);
            ctx.State["underwater"] = on;
            await ctx.Cesium.Scene.ConfigureAsync(new SceneSettings { GlobeTranslucency = on, GlobeFrontAlpha = 0.35 });
            if (on) await ctx.Cesium.Camera.FlyToAsync(105.905, -6.030, 2_500, heading: 320, pitch: -18, duration: 2);
            ctx.Log(on ? "Sea surface translucent: the sonar volume is visible below the ship." : "Sea surface opaque again.");
        }),
        new("overview", ("Overview", "Tampilan umum"), ctx => ctx.Cesium.Camera.FlyToAsync(105.95, -6.45, 55_000, heading: 340, pitch: -35, duration: 2).AsTask()),
    ];

    // Electro-optical wedges: id, centre azimuth (degrees from east, counter-clockwise), range.
    private static readonly (string Id, double Center, double Range)[] Wedges = [("eo-merak", -60, 14_000), ("eo-ketapang", 120, 14_000)];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        // Radar domes: an upper hemisphere of the radar's range.
        await cesium.Entities.AddRangeAsync(
        [
            new() { Id = "radar-anyer", Name = "Radar Anyer (illustrative)", Position = new(105.885, -6.062),
                    Ellipsoid = EllipsoidGraphics.Dome(18_000, CesiumColor.Cyan.WithAlpha(0.12f), CesiumColor.Cyan.WithAlpha(0.5f)) },
            new() { Id = "radar-bakauheni", Name = "Radar Bakauheni (illustrative)", Position = new(105.752, -5.870),
                    Ellipsoid = EllipsoidGraphics.Dome(18_000, CesiumColor.Lime.WithAlpha(0.12f), CesiumColor.Lime.WithAlpha(0.5f)) },
        ]);

        // EO/IR wedges: an azimuth sector up to 30° elevation.
        var wedgeSites = new[] { new GeoPoint(106.000, -5.925, 40), new GeoPoint(105.770, -5.900, 40) };
        for (var i = 0; i < Wedges.Length; i++)
        {
            var (id, center, range) = Wedges[i];
            await cesium.Entities.AddAsync(new EntityOptions
            {
                Id = id,
                Name = $"EO/IR {id[3..]}",
                Position = wedgeSites[i],
                Ellipsoid = EllipsoidGraphics.Sector(range, center - 20, center + 20, maxElevation: 30),
            });
        }

        // Sonar: the lower hemisphere (cone 90°–180°) below a patrol ship.
        var ship = new GeoPoint(105.860, -5.975);
        await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Patrol ship sonar (illustrative)",
            Position = ship,
            Billboard = new BillboardGraphics { PinColor = CesiumColor.Blue, PinIcon = "ferry" },
            Ellipsoid = new EllipsoidGraphics
            {
                Radii = new Cartesian3(3_000, 3_000, 3_000),
                MinimumCone = 90,
                MaximumCone = 180,
                Material = CesiumColor.Purple.WithAlpha(0.3f),
                Outline = true,
                OutlineColor = CesiumColor.Purple,
            },
        });

        await cesium.Camera.FlyToAsync(105.95, -6.45, 55_000, heading: 340, pitch: -35, duration: 3);
        ctx.Log("2 radar domes (18 km), 2 EO/IR wedges (14 km), 1 sonar hemisphere (3 km).");
        // </snippet>
    }
}
