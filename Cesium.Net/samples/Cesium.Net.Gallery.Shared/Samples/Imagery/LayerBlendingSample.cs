namespace CesiumNet.Gallery.Samples;

[SampleOrder(3)]
public sealed class LayerBlendingSample : GallerySample
{
    public override string Id => "layer-blending";
    public override string CategoryId => "imagery";
    public override Text Title => ("Layer blending & swipe", "Blending lapisan & swipe");
    public override Text Summary => (
        "Central Jakarta with a street map stacked on satellite imagery. Compare them side by side with a swipe line, blend them with opacity, or darken the satellite layer.",
        "Jakarta Pusat dengan peta jalan ditumpuk di atas citra satelit. Bandingkan berdampingan dengan garis swipe, campurkan dengan opasitas, atau gelapkan lapisan satelit.");
    public override GeoPoint? Location => new(106.8227, -6.1950);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("swipe", ("Swipe: streets on the right", "Swipe: jalan di kanan"), async ctx =>
        {
            await ctx.Cesium.Imagery.UpdateAsync(ctx.Get<string>("streets"), new ImageryLayerUpdate { Alpha = 1, SplitDirection = SplitDirection.Right });
            await ctx.Cesium.Imagery.SetSplitPositionAsync(0.5);
            ctx.Log("Swipe at 50%: satellite left, streets right.");
        }),
        new("move", ("Move swipe line", "Geser garis swipe"), async ctx =>
        {
            var next = ctx.Get("split", 0.5) switch { 0.5 => 0.75, 0.75 => 0.25, _ => 0.5 };
            ctx.State["split"] = next;
            await ctx.Cesium.Imagery.SetSplitPositionAsync(next);
            ctx.Log($"Swipe line at {next:P0} of the view width.");
        }),
        new("blend", ("Blend 50%", "Campur 50%"), async ctx =>
        {
            await ctx.Cesium.Imagery.UpdateAsync(ctx.Get<string>("streets"), new ImageryLayerUpdate { Alpha = 0.5, SplitDirection = SplitDirection.None });
            ctx.Log("Streets layer over the full view at 50% opacity.");
        }),
        new("darken", ("Darken satellite", "Gelapkan satelit"), async ctx =>
        {
            var dark = !ctx.Get("dark", false);
            ctx.State["dark"] = dark;
            await ctx.Cesium.Imagery.UpdateAsync("base", new ImageryLayerUpdate { Brightness = dark ? 0.45 : 1, Saturation = dark ? 0.3 : 1 });
            ctx.Log(dark ? "Satellite brightness 0.45, saturation 0.3." : "Satellite colours restored.");
        }),
        new("hide", ("Satellite only", "Hanya satelit"), async ctx =>
        {
            await ctx.Cesium.Imagery.UpdateAsync(ctx.Get<string>("streets"), new ImageryLayerUpdate { Alpha = 0, SplitDirection = SplitDirection.None });
            ctx.Log("Streets layer hidden (alpha 0).");
        }),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        // The base layer (satellite) is "base"; add a street map on top of it.
        var streets = await cesium.Imagery.AddAsync(new ImageryLayerOptions
        {
            Provider = ImageryProviderKind.ArcGisWorldStreetMap,
            SplitDirection = SplitDirection.Right,   // only drawn right of the swipe line
        });
        ctx.State["streets"] = streets;

        // The split position is shared by all split layers: 0 = left edge, 1 = right edge.
        await cesium.Imagery.SetSplitPositionAsync(0.5);

        // Any layer can be tuned later: alpha, brightness, contrast, hue, saturation, gamma.
        await cesium.Imagery.UpdateAsync("base", new ImageryLayerUpdate { Contrast = 1.15 });

        var ids = await cesium.Imagery.GetIdsAsync();
        ctx.Log($"Layers bottom → top: {string.Join(", ", ids)}");

        await cesium.Camera.FlyToAsync(106.8227, -6.2050, 4_500, pitch: -60, duration: 3);
        // </snippet>
    }
}
