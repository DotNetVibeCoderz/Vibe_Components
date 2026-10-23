namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class SceneModesSample : GallerySample
{
    public override string Id => "scene-modes";
    public override string CategoryId => "start";
    public override Text Title => ("Scene modes", "Mode tampilan");
    public override Text Summary => (
        "Domestic flight routes from Jakarta drawn on the globe. Switch between the 3D globe, a flat 2D map and the 2.5D Columbus view; the routes stay in place.",
        "Rute penerbangan domestik dari Jakarta digambar di globe. Beralih antara globe 3D, peta datar 2D, dan tampilan Columbus 2.5D; rute tetap di tempatnya.");
    public override GeoPoint? Location => new(106.8456, -6.2088);

    private static readonly GeoRectangle Indonesia = new(94, -11.5, 141.5, 6.5);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("3d", ("3D globe", "Globe 3D"), ctx => Morph(ctx, SceneMode.Scene3D, "3D globe")),
        new("2d", ("2D map", "Peta 2D"), ctx => Morph(ctx, SceneMode.Scene2D, "2D map")),
        new("columbus", ("Columbus view (2.5D)", "Tampilan Columbus (2.5D)"), ctx => Morph(ctx, SceneMode.ColumbusView, "Columbus view")),
    ];

    private static async Task Morph(SampleContext ctx, SceneMode mode, string name)
    {
        ctx.Log($"Morphing to {name}…");
        // Completes when the morph has finished. A morph resets the camera, so frame Indonesia again.
        await ctx.Cesium.Scene.SetModeAsync(mode, duration: 1.5);
        await ctx.Cesium.Camera.FlyToRectangleAsync(Indonesia, duration: 1.5);
        ctx.Log($"Now in {name}.");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        var jakarta = new GeoPoint(106.8456, -6.2088);
        var destinations = new (string Name, GeoPoint Position)[]
        {
            ("Medan", new(98.6722, 3.5952)),
            ("Balikpapan", new(116.8312, -1.2379)),
            ("Makassar", new(119.4327, -5.1477)),
            ("Denpasar", new(115.2126, -8.6705)),
            ("Jayapura", new(140.7181, -2.5916)),
        };

        var entities = new List<EntityOptions>
        {
            new() { Name = "Jakarta", Position = jakarta, Billboard = new BillboardGraphics { PinColor = "#F5C518", PinIcon = "airport" } },
        };
        foreach (var (name, position) in destinations)
        {
            entities.Add(new EntityOptions
            {
                Name = name,
                Position = position,
                Point = new PointGraphics { PixelSize = 9, Color = CesiumColor.White, OutlineColor = CesiumColor.Blue, OutlineWidth = 3 },
                Label = new LabelGraphics { Text = name, PixelOffset = new Cartesian2(0, -18) },
            });
            entities.Add(new EntityOptions
            {
                Name = $"Jakarta – {name}",
                Polyline = new PolylineGraphics { Positions = [jakarta, position], Width = 6, Material = CesiumMaterial.Glow(CesiumColor.Cyan, 0.2) },
            });
            ctx.Log($"Jakarta → {name}: {Geodesy.FormatDistance(Geodesy.Distance(jakarta, position))}");
        }
        await cesium.Entities.AddRangeAsync(entities);

        await cesium.Camera.FlyToRectangleAsync(new GeoRectangle(94, -11.5, 141.5, 6.5), duration: 2);

        // Switching mode is a single call — try the actions on the right.
        await cesium.Scene.SetModeAsync(SceneMode.Scene3D);
        // </snippet>
    }
}
