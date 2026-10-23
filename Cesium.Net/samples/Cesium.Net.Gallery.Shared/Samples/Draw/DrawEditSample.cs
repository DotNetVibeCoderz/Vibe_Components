namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class DrawEditSample : GallerySample
{
    public override string Id => "draw-edit";
    public override string CategoryId => "draw";
    public override Text Title => ("Draw & edit", "Menggambar & mengedit");
    public override Text Summary => (
        "Sketch points, lines, polygons, rectangles and circles over Merdeka Square, then drag the vertices of the last shape to edit it. Click to add vertices, double-click or right-click to finish, Esc to cancel.",
        "Buat sketsa titik, garis, poligon, persegi, dan lingkaran di atas Lapangan Merdeka, lalu seret simpul bentuk terakhir untuk mengeditnya. Klik untuk menambah simpul, klik ganda atau klik kanan untuk selesai, Esc untuk batal.");
    public override GeoPoint? Location => GalleryDefaults.Monas;

    private static readonly Text DrawGroup = ("Draw", "Gambar");
    private static readonly Text EditGroup = ("Edit", "Edit");

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("point", ("Point", "Titik"), ctx => Draw(ctx, DrawMode.Point)) { Group = DrawGroup },
        new("line", ("Polyline", "Polyline"), ctx => Draw(ctx, DrawMode.Polyline)) { Group = DrawGroup },
        new("polygon", ("Polygon", "Poligon"), ctx => Draw(ctx, DrawMode.Polygon)) { Group = DrawGroup },
        new("rectangle", ("Rectangle", "Persegi"), ctx => Draw(ctx, DrawMode.Rectangle)) { Group = DrawGroup },
        new("circle", ("Circle", "Lingkaran"), ctx => Draw(ctx, DrawMode.Circle)) { Group = DrawGroup },
        new("edit", ("Edit last shape", "Edit bentuk terakhir"), async ctx =>
        {
            await ctx.Cesium.Drawing.BeginEditAsync(ctx.Get<string>("last"));
            ctx.Log("Editing: drag the yellow handles, then press “End edit”.");
        }) { Group = EditGroup },
        new("end", ("End edit", "Selesai edit"), async ctx =>
        {
            await ctx.Cesium.Drawing.EndEditAsync();
            ctx.Log("Edit finished.");
        }) { Group = EditGroup },
        new("clear", ("Clear drawings", "Hapus gambar"), async ctx =>
        {
            await ctx.Cesium.Drawing.ClearAsync();
            ctx.State["last"] = ctx.Get<string>("park");
            ctx.Log("Drawings cleared.");
        }) { Group = EditGroup },
    ];

    // <snippet>
    public override async Task RunAsync(SampleContext ctx)
    {
        var cesium = ctx.Cesium;

        // A starting shape to edit: the outline of Merdeka Square.
        var park = await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Lapangan Merdeka",
            Polygon = new PolygonGraphics
            {
                Positions = [new(106.8215, -6.1700), new(106.8325, -6.1700), new(106.8330, -6.1808), new(106.8215, -6.1810)],
                Material = CesiumColor.Green.WithAlpha(0.3f),
            },
        });
        ctx.State["park"] = park;
        ctx.State["last"] = park;

        // Report every vertex drag.
        void OnEdited(object? sender, EntityEditedResult e)
            => ctx.Log($"{e.EntityId} now has {e.Positions.Count} vertices, area {Geodesy.FormatArea(Geodesy.Area(e.Positions))}.");
        cesium.EntityEdited += OnEdited;
        ctx.Resources.Add(Disposable.Create(() => cesium.EntityEdited -= OnEdited));

        await cesium.Camera.FlyToAsync(106.8272, -6.1880, 1_600, pitch: -55, duration: 3);
        ctx.Log("Pick a shape to draw, or edit the square's outline.");
    }

    private static async Task Draw(SampleContext ctx, DrawMode mode)
    {
        ctx.Log($"Drawing a {mode}: click on the map, double-click or right-click to finish.");

        // Completes when the user finishes (or null when cancelled with Esc).
        var result = await ctx.Cesium.Drawing.DrawAsync(new DrawOptions
        {
            Mode = mode,
            Color = "#F5C518",
            ClampToGround = true,
            KeepEntity = true,
        }, ctx.Token);

        if (result?.EntityId is null) { ctx.Log("Drawing cancelled."); return; }
        ctx.State["last"] = result.EntityId;
        ctx.Log($"{mode} {result.EntityId}: {result.Positions.Count} vertices" +
                (result.Radius is { } r ? $", radius {Geodesy.FormatDistance(r)}" : ""));
    }
    // </snippet>
}
