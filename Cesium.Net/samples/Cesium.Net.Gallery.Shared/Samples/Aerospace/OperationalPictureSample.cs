namespace CesiumNet.Gallery.Samples;

[SampleOrder(3)]
public sealed class OperationalPictureSample : GallerySample
{
    public override string Id => "operational-picture";
    public override string CategoryId => "aerospace";
    public override Text Title => ("Common operational picture", "Gambaran operasional bersama");
    public override Text Summary => (
        "A fictional maritime exercise in the Natuna Sea on a dark base map: friendly and hostile units, threat rings, detection zones and patrol tracks. Toggle each layer and read the legend in the event log.",
        "Latihan maritim fiktif di Laut Natuna di atas peta dasar gelap: unit kawan dan lawan, cincin ancaman, zona deteksi, dan jalur patroli. Nyalakan/matikan tiap lapisan dan baca legendanya di log peristiwa.");
    public override GeoPoint? Location => new(108.2, 4.0);

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o =>
    {
        o.BaseLayer = ImageryLayerOptions.Dark();
        o.InitialView = CameraView.At(108.3, 2.9, 480_000, pitch: -62);
    });

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("threats", ("Threat rings", "Cincin ancaman"), ctx => Toggle(ctx, "threat", "Threat rings")),
        new("detection", ("Detection zones", "Zona deteksi"), ctx => Toggle(ctx, "detection", "Detection zones")),
        new("patrols", ("Patrol tracks", "Jalur patroli"), ctx => Toggle(ctx, "patrol", "Patrol tracks")),
        new("overview", ("Overview", "Tampilan umum"), ctx => ctx.Cesium.Camera.FlyToAsync(108.3, 2.9, 480_000, pitch: -62, duration: 2).AsTask()),
    ];

    private static async Task Toggle(SampleContext ctx, string layer, string name)
    {
        var visible = !ctx.Get($"show-{layer}", true);
        ctx.State[$"show-{layer}"] = visible;
        foreach (var id in ctx.Get<Dictionary<string, List<string>>>("layers")[layer])
            await ctx.Cesium.Entities.SetVisibleAsync(id, visible);
        ctx.Log($"{name} {(visible ? "shown" : "hidden")}.");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        CesiumColor friendly = "#3B82F6", hostile = "#EF4444", unknown = "#F5C518", detection = "#22D3EE";
        var layers = new Dictionary<string, List<string>> { ["unit"] = [], ["threat"] = [], ["detection"] = [], ["patrol"] = [] };
        async Task Add(string layer, EntityOptions e) => layers[layer].Add(await cesium.Entities.AddAsync(e));

        // Units: pins with a short designator (all fictional).
        var units = new (string Code, string Name, CesiumColor Color, GeoPoint At)[]
        {
            ("F1", "Frigate Alpha", friendly, new(108.05, 3.75)), ("F2", "Corvette Bravo", friendly, new(108.55, 3.35)),
            ("F3", "Patrol aircraft", friendly, new(107.80, 4.30)), ("H1", "Hostile surface group", hostile, new(109.10, 4.70)),
            ("H2", "Hostile fast boat", hostile, new(108.95, 4.15)), ("U1", "Unidentified contact", unknown, new(107.55, 4.85)),
        };
        foreach (var u in units)
            await Add("unit", new EntityOptions
            {
                Name = $"{u.Code} — {u.Name}",
                Position = u.At,
                Billboard = new BillboardGraphics { PinColor = u.Color, PinText = u.Code, PinSize = 40 },
                Label = new LabelGraphics { Text = u.Name, Scale = 0.8, PixelOffset = new Cartesian2(0, -48), FillColor = u.Color },
            });

        // Threat rings: dashed circles computed in C# around hostile units.
        foreach (var (center, radius) in new[] { (units[3].At, 60_000.0), (units[4].At, 25_000.0) })
            await Add("threat", new EntityOptions
            {
                Name = $"Threat ring {radius / 1000:N0} km",
                Polyline = new PolylineGraphics { Positions = Geodesy.Circle(center, radius, 96), Width = 3, Material = CesiumMaterial.Dash(hostile, dashLength: 20) },
            });

        // Detection zones: translucent radar coverage of friendly units.
        foreach (var (center, radius) in new[] { (units[0].At, 80_000.0), (units[1].At, 55_000.0) })
            await Add("detection", new EntityOptions
            {
                Name = $"Detection {radius / 1000:N0} km",
                Position = center,
                Ellipse = new EllipseGraphics { SemiMajorAxis = radius, SemiMinorAxis = radius, Material = detection.WithAlpha(0.12f), Outline = true, OutlineColor = detection.WithAlpha(0.6f), Height = 0 },
            });

        // Patrol tracks: arrows showing planned routes.
        await Add("patrol", new EntityOptions
        {
            Name = "Patrol route Alpha",
            Polyline = new PolylineGraphics { Positions = [units[0].At, new(108.40, 4.40), new(108.80, 3.90), new(108.55, 3.35)], Width = 12, Material = CesiumMaterial.Arrow(friendly) },
        });
        await Add("patrol", new EntityOptions
        {
            Name = "Patrol route Bravo",
            Polyline = new PolylineGraphics { Positions = [units[2].At, new(107.40, 4.60), new(107.60, 5.10), new(108.20, 5.00)], Width = 12, Material = CesiumMaterial.Arrow(friendly.WithAlpha(0.7f)) },
        });
        ctx.State["layers"] = layers;

        ctx.Log("Legend: blue = friendly · red = hostile · yellow = unidentified");
        ctx.Log("Legend: red dashes = threat ring · cyan disc = detection zone · arrows = patrol track");
        ctx.Log($"{layers.Sum(l => l.Value.Count)} objects on the picture (fictional exercise).");
        // </snippet>
    }
}
