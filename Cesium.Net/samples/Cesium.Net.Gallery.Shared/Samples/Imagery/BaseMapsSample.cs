namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class BaseMapsSample : GallerySample
{
    public override string Id => "base-maps";
    public override string CategoryId => "imagery";
    public override Text Title => ("Base maps", "Peta dasar");
    public override Text Summary => (
        "Southern Bali seen through different key-free base maps. Swap between satellite, streets, topographic, OpenStreetMap and dark styles; the labels drawn on top are kept.",
        "Bali selatan dilihat melalui berbagai peta dasar tanpa kunci API. Ganti antara satelit, jalan, topografi, OpenStreetMap, dan gaya gelap; label di atasnya tetap ada.");
    public override GeoPoint? Location => new(115.1889, -8.7090);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("satellite", ("Satellite", "Satelit"), ctx => Use(ctx, ImageryLayerOptions.Satellite(), "Esri World Imagery")),
        new("streets", ("Streets", "Jalan"), ctx => Use(ctx, ImageryLayerOptions.Streets(), "Esri World Street Map")),
        new("topo", ("Topographic", "Topografi"), ctx => Use(ctx, ImageryLayerOptions.Topographic(), "Esri World Topographic")),
        new("osm", ("OpenStreetMap", "OpenStreetMap"), ctx => Use(ctx, ImageryLayerOptions.OpenStreetMap(), "OpenStreetMap")),
        new("dark", ("Dark", "Gelap"), ctx => Use(ctx, ImageryLayerOptions.Dark(), "Esri Dark Gray Canvas")),
    ];

    private static async Task Use(SampleContext ctx, ImageryLayerOptions layer, string name)
    {
        await ctx.Cesium.Imagery.SetBaseLayerAsync(layer);
        ctx.Log($"Base layer: {name}");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        // Overlays (entities, layers above the base) survive a base-layer swap.
        var places = new (string Name, GeoPoint Position)[]
        {
            ("Ngurah Rai Airport", new(115.1672, -8.7482)),
            ("Kuta", new(115.1686, -8.7180)),
            ("Sanur", new(115.2620, -8.6930)),
            ("Denpasar", new(115.2126, -8.6705)),
            ("Nusa Dua", new(115.2300, -8.8000)),
        };
        await cesium.Entities.AddRangeAsync(places.Select(p => new EntityOptions
        {
            Name = p.Name,
            Position = p.Position,
            Point = new PointGraphics { PixelSize = 8, Color = CesiumColor.Yellow, OutlineColor = CesiumColor.Black, OutlineWidth = 2 },
            Label = new LabelGraphics { Text = p.Name, PixelOffset = new Cartesian2(0, -16), ShowBackground = true, BackgroundColor = "#15212ACC" },
        }).ToList());

        await cesium.Camera.FlyToAsync(115.20, -8.93, 26_000, heading: 0, pitch: -50, duration: 3);

        // Replace the bottom layer. None of the built-in base maps needs an API key.
        await cesium.Imagery.SetBaseLayerAsync(ImageryLayerOptions.Streets());
        ctx.Log("Base layer: Esri World Street Map");
        // </snippet>
    }
}
