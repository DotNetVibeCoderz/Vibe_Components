namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class EntityShowcaseSample : GallerySample
{
    public override string Id => "entity-showcase";
    public override string CategoryId => "gis";
    public override Text Title => ("Entity showcase", "Ragam entitas");
    public override Text Summary => (
        "Every entity type in one scene: points, pins, labels, glowing and dashed lines, extruded polygons, ellipses, rectangles, boxes, cylinders, corridors and walls.",
        "Semua jenis entitas dalam satu adegan: titik, pin, label, garis bercahaya dan putus-putus, poligon terekstrusi, elips, persegi, kotak, silinder, koridor, dan dinding.");
    public override GeoPoint? Location => new(106.8456, -6.2088);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("overview", ("Overview", "Tampilan umum"), ctx => ctx.Cesium.Camera.FlyToAsync(106.845, -6.262, 9_000, pitch: -45).AsTask()),
        new("clear", ("Remove all", "Hapus semua"), ctx => ctx.Cesium.Entities.ClearAsync().AsTask()),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        var origin = new GeoPoint(106.8456, -6.2088);
        GeoPoint At(double eastMeters, double northMeters, double height = 0)
            => CoordinateConverter.EnuToGeodetic(new Cartesian3(eastMeters, northMeters, height), origin);

        var entities = new List<EntityOptions>
        {
            new() { Name = "Point", Position = At(-3000, 1500), Point = new PointGraphics { PixelSize = 14, Color = CesiumColor.Yellow, OutlineColor = CesiumColor.Black, OutlineWidth = 2 } },
            new() { Name = "Pin", Position = At(-1500, 1500), Billboard = new BillboardGraphics { PinColor = CesiumColor.Red, PinText = "A" } },
            new() { Name = "Label", Position = At(0, 1500), Label = new LabelGraphics { Text = "Jakarta Selatan", ShowBackground = true } },
            new()
            {
                Name = "Glow line",
                Polyline = new PolylineGraphics { Positions = [At(-3000, 600), At(3000, 600)], Width = 12, Material = CesiumMaterial.Glow(CesiumColor.Cyan) },
            },
            new()
            {
                Name = "Dashed arrow",
                Polyline = new PolylineGraphics { Positions = [At(-3000, 0), At(3000, 0)], Width = 6, Material = CesiumMaterial.Dash(CesiumColor.Orange) },
            },
            new()
            {
                Name = "Extruded polygon",
                Polygon = new PolygonGraphics
                {
                    Positions = [At(-3000, -700), At(-1800, -700), At(-1800, -1700), At(-3000, -1700)],
                    ExtrudedHeight = 400,
                    Material = CesiumColor.Blue.WithAlpha(0.7f),
                    Outline = true,
                    OutlineColor = CesiumColor.White,
                },
            },
            new() { Name = "Ellipse", Position = At(-800, -1200), Ellipse = new EllipseGraphics { SemiMajorAxis = 600, SemiMinorAxis = 350, Material = CesiumMaterial.Stripe(CesiumColor.Green, CesiumColor.White), Rotation = 30 } },
            new() { Name = "Rectangle", Rectangle = new RectangleGraphics { Coordinates = GeoRectangle.FromPoints([At(300, -700), At(1300, -1700)]), Material = CesiumMaterial.Checkerboard(CesiumColor.Purple, CesiumColor.White) } },
            new() { Name = "Box", Position = At(2300, -1200, 250), Box = new BoxGraphics { Dimensions = new Cartesian3(500, 500, 500), Material = CesiumColor.Red.WithAlpha(0.8f), Outline = true } },
            new() { Name = "Cylinder", Position = At(-2400, -2900, 400), Cylinder = new CylinderGraphics { Length = 800, TopRadius = 0, BottomRadius = 400, Material = CesiumColor.Teal } },
            new()
            {
                Name = "Corridor",
                Corridor = new CorridorGraphics { Positions = [At(-1200, -2400), At(0, -3200), At(1200, -2400)], Width = 250, Material = CesiumMaterial.Grid(CesiumColor.Yellow) },
            },
            new()
            {
                Name = "Wall",
                Wall = new WallGraphics { Positions = [At(1800, -2400, 600), At(2600, -3000, 300), At(3200, -2300, 600)], Material = CesiumColor.Magenta.WithAlpha(0.6f), Outline = true },
            },
        };

        // One interop call for the whole batch.
        var ids = await cesium.Entities.AddRangeAsync(entities);
        ctx.Log($"Added {ids.Length} entities in a single call.");

        await cesium.Camera.FlyToAsync(106.845, -6.262, 9_000, pitch: -45);
        // </snippet>
    }
}
