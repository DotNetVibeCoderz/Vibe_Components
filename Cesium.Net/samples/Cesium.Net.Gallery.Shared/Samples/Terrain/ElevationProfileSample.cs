namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class ElevationProfileSample : GallerySample
{
    public override string Id => "elevation-profile";
    public override string CategoryId => "terrain";
    public override Text Title => ("Elevation profile", "Profil elevasi");
    public override Text Summary => (
        "A cross-section over Mount Merapi computed from terrain samples. The line is drawn on the ground, the highest and lowest points are marked, and the log lists distance, ascent and heights.",
        "Penampang melintang Gunung Merapi yang dihitung dari sampel terrain. Garis digambar di permukaan, titik tertinggi dan terendah ditandai, dan log mencantumkan jarak, tanjakan, serta ketinggian.");
    public override GeoPoint? Location => new(110.4457, -7.5407);
    public override bool UsesIon => true;

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o =>
        o.Terrain = string.IsNullOrWhiteSpace(ionToken) ? TerrainOptions.ArcGis() : TerrainOptions.WorldTerrain());

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("merapi", ("Across Merapi", "Melintasi Merapi"), ctx => ProfileAsync(ctx, "Merapi", new(110.370, -7.450), new(110.520, -7.630))),
        new("rinjani", ("Across Rinjani", "Melintasi Rinjani"), ctx => ProfileAsync(ctx, "Rinjani", new(116.330, -8.330), new(116.540, -8.480))),
        new("bromo", ("Bromo to Semeru", "Bromo ke Semeru"), ctx => ProfileAsync(ctx, "Bromo–Semeru", new(112.953, -7.942), new(112.922, -8.108))),
    ];

    public override Task RunAsync(SampleContext ctx) => ProfileAsync(ctx, "Merapi", new(110.370, -7.450), new(110.520, -7.630));

    private static async Task ProfileAsync(SampleContext ctx, string name, GeoPoint start, GeoPoint end)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        await cesium.Entities.ClearAsync();

        // 150 terrain samples along the geodesic from start to end.
        var profile = await cesium.Terrain.GetProfileAsync(start, end, samples: 150, ctx.Token);

        var highest = profile.MaxBy(s => s.Position.Height);
        var lowest = profile.MinBy(s => s.Position.Height);
        double ascent = 0, descent = 0;
        for (var i = 1; i < profile.Length; i++)
        {
            var delta = profile[i].Position.Height - profile[i - 1].Position.Height;
            if (delta > 0) ascent += delta; else descent -= delta;
        }

        ctx.Log($"{name}: {Geodesy.FormatDistance(profile[^1].Distance)}, highest {highest.Position.Height:N0} m, lowest {lowest.Position.Height:N0} m");
        ctx.Log($"Total ascent {ascent:N0} m, descent {descent:N0} m");
        for (var i = 0; i < profile.Length; i += 25)
            ctx.Log($"  {profile[i].Distance / 1000,5:F1} km  {profile[i].Position.Height,6:N0} m");

        // The profile itself, lifted 30 m so it floats just above the slopes.
        await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = $"Profile {name}",
            Polyline = new PolylineGraphics
            {
                Positions = profile.Select(s => s.Position.WithHeight(s.Position.Height + 30)).ToList(),
                Width = 5,
                Material = CesiumMaterial.Outline(CesiumColor.Yellow, CesiumColor.Black, 1.5),
            },
        });
        foreach (var (label, sample, pin) in new[] { ("Highest", highest, CesiumColor.Red), ("Lowest", lowest, CesiumColor.Blue) })
        {
            await cesium.Entities.AddAsync(new EntityOptions
            {
                Name = label,
                Position = sample.Position,
                Billboard = new BillboardGraphics { PinColor = pin, PinText = label[..1], AlwaysOnTop = true },
                Label = new LabelGraphics { Text = $"{label}: {sample.Position.Height:N0} m", PixelOffset = new Cartesian2(0, -56), ShowBackground = true, AlwaysOnTop = true },
            });
        }

        var middle = Geodesy.Midpoint(start, end);
        var bearing = Geodesy.Bearing(start, end);
        // Look across the line, from the left-hand side.
        var eye = Geodesy.Destination(middle, bearing - 90, 16_000);
        await cesium.Camera.FlyToAsync(eye.Longitude, eye.Latitude, highest.Position.Height + 7_000, heading: bearing + 90, pitch: -22, duration: 3);
        // </snippet>
    }
}
