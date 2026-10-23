namespace CesiumNet.Gallery.Samples;

[SampleOrder(3)]
public sealed class GlobeClippingSample : GallerySample
{
    public override string Id => "globe-clipping";
    public override string CategoryId => "terrain";
    public override Text Title => ("Globe clipping", "Clipping globe");
    public override Text Summary => (
        "An illustrative underground metro tunnel beneath Jalan Sudirman, Jakarta. Cut a hole in the globe to look underground, keep only the cut-out, or make the ground translucent.",
        "Ilustrasi terowongan MRT bawah tanah di bawah Jalan Sudirman, Jakarta. Lubangi globe untuk melihat ke bawah tanah, sisakan hanya potongannya, atau buat permukaan tanah tembus pandang.");
    public override GeoPoint? Location => new(106.8215, -6.2010);

    private static readonly GeoPoint[] Excavation =
    [
        new(106.8170, -6.1930), new(106.8265, -6.1930), new(106.8265, -6.2105), new(106.8170, -6.2105),
    ];

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o => o.DepthTestAgainstTerrain = true);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("clip", ("Cut a hole", "Buat lubang"), async ctx =>
        {
            await ctx.Cesium.Terrain.SetClippingPolygonsAsync([Excavation]);
            ctx.Log("Globe clipped inside the excavation polygon.");
        }),
        new("inverse", ("Keep only the cut-out", "Sisakan potongan saja"), async ctx =>
        {
            await ctx.Cesium.Terrain.SetClippingPolygonsAsync([Excavation], inverse: true);
            ctx.Log("Inverse clipping: only the polygon is drawn.");
        }),
        new("translucent", ("Translucent ground", "Tanah tembus pandang"), async ctx =>
        {
            var on = !ctx.Get("translucent", false);
            ctx.State["translucent"] = on;
            await ctx.Cesium.Scene.ConfigureAsync(new SceneSettings { GlobeTranslucency = on, GlobeFrontAlpha = 0.45 });
            ctx.Log(on ? "Globe translucency on (front alpha 0.45)." : "Globe translucency off.");
        }),
        new("reset", ("Remove clipping", "Hapus clipping"), async ctx =>
        {
            await ctx.Cesium.Terrain.SetClippingPolygonsAsync(null);
            ctx.Log("Clipping removed.");
        }),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        // An illustrative tunnel 18–28 m below Jalan Sudirman, with three stations.
        var route = new[] { new GeoPoint(106.8220, -6.2350), new GeoPoint(106.8228, -6.2010), new GeoPoint(106.8230, -6.1935) };
        await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Tunnel",
            Corridor = new CorridorGraphics { Positions = route, Width = 14, Height = -28, ExtrudedHeight = -18, Material = CesiumColor.Orange.WithAlpha(0.9f), Outline = true, OutlineColor = CesiumColor.Black },
        });
        foreach (var (name, at) in new[] { ("Setiabudi", route[0]), ("Dukuh Atas", route[1]), ("Bundaran HI", route[2]) })
        {
            await cesium.Entities.AddAsync(new EntityOptions
            {
                Name = name,
                Position = at.WithHeight(-21),
                Box = new BoxGraphics { Dimensions = new Cartesian3(40, 160, 14), Material = CesiumColor.Cyan.WithAlpha(0.9f), Outline = true },
                Label = new LabelGraphics { Text = name, AlwaysOnTop = true, ShowBackground = true, PixelOffset = new Cartesian2(0, -30) },
            });
        }

        // Cut a hole in the globe: everything inside the polygon is not drawn.
        // Hiding the sky atmosphere keeps it from glowing through the opening.
        await cesium.Scene.ConfigureAsync(new SceneSettings { ShowSkyAtmosphere = false });
        await cesium.Terrain.SetClippingPolygonsAsync([Excavation]);
        ctx.Log("Globe clipped — the tunnel is visible through the opening.");

        await cesium.Camera.FlyToAsync(106.8140, -6.2185, 900, heading: 25, pitch: -32, duration: 3);
        // </snippet>
    }
}
