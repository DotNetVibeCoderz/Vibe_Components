namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class DiscreteLodSample : GallerySample
{
    public override string Id => "discrete-lod";
    public override string CategoryId => "tiles";
    public override Text Title => ("Discrete LOD tileset", "Tileset LOD diskret");
    public override Text Summary => (
        "A 3D Tiles model stored at three levels of detail. Show the tile bounding volumes and change the screen-space error to see when Cesium switches to a finer or coarser level.",
        "Model 3D Tiles yang disimpan dalam tiga tingkat detail. Tampilkan bounding volume tile dan ubah screen-space error untuk melihat kapan Cesium beralih ke tingkat lebih halus atau kasar.");
    public override GeoPoint? Location => new(-75.6120, 40.0420);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("bounds", ("Toggle bounding volumes", "Tampilkan/sembunyikan bounding volume"), async ctx =>
        {
            var on = !ctx.Get("bounds", false);
            ctx.State["bounds"] = on;
            await ctx.Cesium.Tilesets.UpdateAsync(ctx.Get<string>("tileset"), new TilesetOptions { DebugShowBoundingVolume = on });
            ctx.Log(on ? "Bounding volumes shown." : "Bounding volumes hidden.");
        }),
        new("sse1", ("Sharpest (SSE 1)", "Paling tajam (SSE 1)"), ctx => SetError(ctx, 1)),
        new("sse16", ("Default (SSE 16)", "Bawaan (SSE 16)"), ctx => SetError(ctx, 16)),
        new("sse256", ("Coarsest (SSE 256)", "Paling kasar (SSE 256)"), ctx => SetError(ctx, 256)),
        new("far", ("Zoom out", "Perkecil"), ctx => ctx.Cesium.Camera.ZoomAsync(zoomIn: false).AsTask()),
    ];

    private static async Task SetError(SampleContext ctx, double sse)
    {
        await ctx.Cesium.Tilesets.UpdateAsync(ctx.Get<string>("tileset"), new TilesetOptions { MaximumScreenSpaceError = sse });
        ctx.Log($"Maximum screen-space error = {sse} px — {(sse <= 1 ? "finest level loads early" : sse >= 256 ? "coarse level kept much longer" : "Cesium default")}.");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        // Each tile holds the same model at a different detail; REPLACE refinement
        // swaps the parent for its child once its screen-space error is too large.
        var tileset = await cesium.Tilesets.AddAsync(new TilesetOptions
        {
            Url = "https://raw.githubusercontent.com/CesiumGS/3d-tiles-samples/main/1.0/TilesetWithDiscreteLOD/tileset.json",
            MaximumScreenSpaceError = 16,
            DebugShowBoundingVolume = true,
        });
        ctx.State["tileset"] = tileset;
        ctx.State["bounds"] = true;

        await cesium.Camera.FlyToObjectAsync(tileset, new HeadingPitchRange(30, -25, 2_500), duration: 2);
        ctx.Log("Tileset loaded. Zoom in and out to watch the level of detail change.");
        // </snippet>
    }
}
