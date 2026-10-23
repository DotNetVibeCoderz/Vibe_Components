using CesiumNet.Aerospace;

namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class SatelliteConstellationSample : GallerySample
{
    public override string Id => "satellite-constellation";
    public override string CategoryId => "aerospace";
    public override Text Title => ("Satellite constellation", "Konstelasi satelit");
    public override Text Summary => (
        "A Walker-style constellation of 24 satellites in 4 orbital planes at 550 km, propagated in C# with Keplerian orbits and J2 drift. Ground tracks and coverage footprints move as the clock runs.",
        "Konstelasi bergaya Walker berisi 24 satelit dalam 4 bidang orbit pada ketinggian 550 km, dipropagasi di C# dengan orbit Kepler dan pergeseran J2. Jejak darat dan footprint cakupan bergerak saat jam berjalan.");
    public override GeoPoint? Location => new(117.5, -2.5);

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o =>
    {
        o.Timeline = true;
        o.Animation = true;
        o.InitialView = CameraView.At(117.5, -2.5, 22_000_000);
    });

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("footprints", ("Footprints: all planes", "Footprint: semua bidang"), async ctx =>
        {
            var all = !ctx.Get("allFootprints", false);
            ctx.State["allFootprints"] = all;
            foreach (var id in ctx.Get<List<string>>("footprints").Where(id => !id.StartsWith("fp-0-")))
                await ctx.Cesium.Entities.SetVisibleAsync(id, all);
            ctx.Log(all ? "Footprints shown for all 24 satellites." : "Footprints shown for plane A only.");
        }),
        new("fast", ("Speed 300×", "Kecepatan 300×"), ctx => ctx.Cesium.Clock.ConfigureAsync(new ClockOptions { Multiplier = 300, ShouldAnimate = true }).AsTask()),
        new("realtime", ("Real time (1×)", "Waktu nyata (1×)"), ctx => ctx.Cesium.Clock.ConfigureAsync(new ClockOptions { Multiplier = 1, ShouldAnimate = true }).AsTask()),
        new("follow", ("Follow SAT-A1", "Ikuti SAT-A1"), ctx => ctx.Cesium.Camera.TrackEntityAsync("sat-0-0").AsTask()),
        new("overview", ("Indonesia overview", "Tampilan Indonesia"), async ctx =>
        {
            await ctx.Cesium.Camera.TrackEntityAsync(null);
            await ctx.Cesium.Camera.FlyToAsync(117.5, -2.5, 22_000_000, duration: 2);
        }),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        const double altitude = 550_000, inclination = 53;
        const int planes = 4, perPlane = 6;
        var start = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var stop = start.AddHours(3);
        var step = TimeSpan.FromSeconds(60);
        var footprint = KeplerianOrbit.FootprintRadius(altitude, minElevation: 25);
        string[] planeColors = ["#00C7FF", "#F5C518", "#FF2D92", "#34C759"];

        var entities = new List<EntityOptions>();
        var footprintIds = new List<string>();
        for (var p = 0; p < planes; p++)
        {
            for (var s = 0; s < perPlane; s++)
            {
                // Planes spread 360°/P in RAAN; satellites 360°/S in phase, offset per plane (Walker F = 1).
                var orbit = KeplerianOrbit.Circular(altitude, inclination,
                    rightAscension: p * 360.0 / planes,
                    phase: s * 360.0 / perPlane + p * 360.0 / (planes * perPlane),
                    epoch: start);
                var samples = orbit.Sample(start, stop, step);
                CesiumColor color = planeColors[p];
                var name = $"SAT-{(char)('A' + p)}{s + 1}";

                entities.Add(new EntityOptions
                {
                    Id = $"sat-{p}-{s}",
                    Name = name,
                    SampledPosition = orbit.ToSampledPositions(start, stop, step),
                    Point = new PointGraphics { PixelSize = 8, Color = color, OutlineColor = CesiumColor.White, OutlineWidth = 1 },
                    Label = new LabelGraphics { Text = name, Scale = 0.7, PixelOffset = new Cartesian2(0, -14) },
                });

                // Coverage footprint on the ground, following the sub-satellite point.
                footprintIds.Add($"fp-{p}-{s}");
                entities.Add(new EntityOptions
                {
                    Id = $"fp-{p}-{s}",
                    Show = p == 0,
                    SampledPosition = SampledPositions.From(samples.Select(x => (x.Time, x.Position.WithHeight(0)))),
                    Ellipse = new EllipseGraphics { SemiMajorAxis = footprint, SemiMinorAxis = footprint, Material = color.WithAlpha(0.15f), Outline = true, OutlineColor = color, Height = 0 },
                });

                // One ground track (one orbital period) per plane.
                if (s == 0)
                    entities.Add(new EntityOptions
                    {
                        Name = $"Ground track plane {(char)('A' + p)}",
                        Polyline = new PolylineGraphics { Positions = orbit.GroundTrack(start, start + orbit.Period, step), Width = 2, Material = color.WithAlpha(0.7f) },
                    });
            }
        }
        await cesium.Entities.AddRangeAsync(entities);
        ctx.State["footprints"] = footprintIds;

        await cesium.Clock.ConfigureAsync(new ClockOptions
        {
            StartTime = start, StopTime = stop, CurrentTime = start,
            Multiplier = 60, ClockRange = ClockRange.LoopStop, ShouldAnimate = true, ZoomTimeline = true,
        });

        var period = KeplerianOrbit.Circular(altitude, inclination, 0, 0, start).Period;
        ctx.Log($"{planes * perPlane} satellites, period {period:h\\:mm\\:ss}, footprint radius {Geodesy.FormatDistance(footprint)} (25° min elevation).");
        // </snippet>
    }
}
