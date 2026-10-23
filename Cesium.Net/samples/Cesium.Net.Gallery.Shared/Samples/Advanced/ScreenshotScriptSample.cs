using System.Buffers.Binary;

namespace CesiumNet.Gallery.Samples;

[SampleOrder(3)]
public sealed class ScreenshotScriptSample : GallerySample
{
    public override string Id => "screenshot-script";
    public override string CategoryId => "advanced";
    public override Text Title => ("Screenshot & JavaScript", "Screenshot & JavaScript");
    public override Text Summary => (
        "Capture the current view as PNG bytes in C#, and run small JavaScript snippets against the live CesiumJS viewer for features the C# API does not wrap yet.",
        "Tangkap tampilan saat ini sebagai byte PNG di C#, dan jalankan potongan JavaScript kecil pada viewer CesiumJS untuk fitur yang belum dibungkus API C#.");
    public override GeoPoint? Location => GalleryDefaults.Monas;

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("shot", ("Capture screenshot", "Ambil screenshot"), Capture),
        new("version", ("Read CesiumJS version", "Baca versi CesiumJS"), async ctx =>
            ctx.Log($"Cesium.VERSION = {await ctx.Cesium.ExecuteScriptAsync("Cesium.VERSION")}")),
        new("stats", ("Count scene objects", "Hitung objek adegan"), async ctx =>
            ctx.Log("Scene: " + await ctx.Cesium.ExecuteScriptAsync(
                "({ entities: viewer.entities.values.length, primitives: scene.primitives.length, imageryLayers: viewer.imageryLayers.length })"))),
        new("fps", ("Toggle FPS counter (JS)", "Penghitung FPS (JS)"), async ctx =>
        {
            var on = await ctx.Cesium.ExecuteScriptAsync("scene.debugShowFramesPerSecond = !scene.debugShowFramesPerSecond");
            ctx.Log($"scene.debugShowFramesPerSecond = {on}");
        }),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        await ctx.Cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Monas",
            Position = GalleryDefaults.Monas,
            Billboard = new BillboardGraphics { PinColor = "#1F4FE0", PinIcon = "monument" },
        });
        await ctx.Cesium.Camera.FlyToAsync(106.8272, -6.1880, 1_800, pitch: -40, duration: 3);

        // <snippet>
        var cesium = ctx.Cesium;

        // JavaScript escape hatch: `viewer`, `scene` and `Cesium` are in scope; the result comes back as JSON.
        var version = await cesium.ExecuteScriptAsync("Cesium.VERSION");
        var canvas = await cesium.ExecuteScriptAsync("({ width: scene.canvas.width, height: scene.canvas.height })");
        ctx.Log($"CesiumJS {version}, canvas {canvas}");

        // Statements work too, e.g. tweak a property that has no C# wrapper.
        await cesium.ExecuteScriptAsync("scene.globe.showWaterEffect = true; scene.screenSpaceCameraController.inertiaZoom = 0.5;");

        // A screenshot as PNG bytes — save it, attach it to a report, send it to an API…
        await Capture(ctx);
        // </snippet>
    }

    private static async Task Capture(SampleContext ctx)
    {
        var png = await ctx.Cesium.Scene.CaptureScreenshotPngAsync(ctx.Token);
        // PNG header: width and height are big-endian integers at bytes 16 and 20.
        var (width, height) = png.Length > 24
            ? (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)))
            : (0, 0);
        ctx.Log($"Screenshot: {width}×{height} px, {png.Length / 1024.0:N0} KB PNG");
    }
}
