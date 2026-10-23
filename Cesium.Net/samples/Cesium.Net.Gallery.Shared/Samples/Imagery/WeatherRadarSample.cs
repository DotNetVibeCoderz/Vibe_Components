using System.Text.Json;

namespace CesiumNet.Gallery.Samples;

[SampleOrder(4)]
public sealed class WeatherRadarSample : GallerySample
{
    public override string Id => "weather-radar";
    public override string CategoryId => "imagery";
    public override Text Title => ("Weather radar overlay", "Overlay radar cuaca");
    public override Text Summary => (
        "Live precipitation radar from RainViewer, fetched in C# and drawn as an XYZ tile layer over Southeast Asia. Animate the last two hours or change the opacity.",
        "Radar curah hujan langsung dari RainViewer, diambil di C# dan ditampilkan sebagai lapisan tile XYZ di atas Asia Tenggara. Animasikan dua jam terakhir atau ubah opasitasnya.");
    public override GeoPoint? Location => new(101.5, 2.0);

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o => o.BaseLayer = ImageryLayerOptions.Dark());

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("animate", ("Animate last 2 hours", "Animasikan 2 jam terakhir"), Animate),
        new("latest", ("Latest frame", "Frame terbaru"), async ctx =>
        {
            ctx.Get<CancellationTokenSource?>("loop")?.Cancel();
            await ShowFrame(ctx, ctx.Get<List<(long Time, string Id)>>("frames").Count - 1);
        }),
        new("opacity", ("Toggle opacity", "Ubah opasitas"), async ctx =>
        {
            ctx.State["alpha"] = ctx.Get("alpha", 0.8) > 0.5 ? 0.4 : 0.8;
            await ShowFrame(ctx, ctx.Get("frame", 0));
            ctx.Log($"Radar opacity {ctx.Get("alpha", 0.8):P0}");
        }),
        new("malacca", ("Zoom to the Strait of Malacca", "Perbesar ke Selat Malaka"), ctx => ctx.Cesium.Camera.FlyToAsync(101.5, 2.0, 1_800_000, duration: 2).AsTask()),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        await cesium.Camera.FlyToAsync(112.0, -2.0, 7_000_000, duration: 2);

        // 1. Ask RainViewer (from C#) which radar frames exist.
        using var json = JsonDocument.Parse(await ctx.Http.GetStringAsync("https://api.rainviewer.com/public/weather-maps.json", ctx.Token));
        var host = json.RootElement.GetProperty("host").GetString();
        var past = json.RootElement.GetProperty("radar").GetProperty("past").EnumerateArray().ToList();

        // 2. Add every frame as an XYZ layer (transparent for now) so tiles preload.
        var frames = new List<(long Time, string Id)>();
        foreach (var frame in past)
        {
            var template = $"{host}{frame.GetProperty("path").GetString()}/256/{{z}}/{{x}}/{{y}}/2/1_1.png";
            var layer = ImageryLayerOptions.FromTemplate(template, credit: "RainViewer", maximumLevel: 7);
            layer.Alpha = 0;
            frames.Add((frame.GetProperty("time").GetInt64(), await cesium.Imagery.AddAsync(layer)));
        }
        ctx.State["frames"] = frames;
        ctx.Log($"RainViewer returned {frames.Count} radar frames.");

        // 3. Show the most recent one.
        await ShowFrame(ctx, frames.Count - 1);
        // </snippet>
    }

    private static async Task ShowFrame(SampleContext ctx, int index)
    {
        var frames = ctx.Get<List<(long Time, string Id)>>("frames");
        if (frames is null || frames.Count == 0) return;
        var previous = ctx.Get("frame", -1);
        if (previous >= 0 && previous != index) await ctx.Cesium.Imagery.SetAlphaAsync(frames[previous].Id, 0);
        await ctx.Cesium.Imagery.SetAlphaAsync(frames[index].Id, ctx.Get("alpha", 0.8));
        ctx.State["frame"] = index;
        var time = DateTimeOffset.FromUnixTimeSeconds(frames[index].Time).ToOffset(TimeSpan.FromHours(7));
        ctx.Log($"Radar frame {time:HH:mm} WIB ({index + 1}/{frames.Count})");
    }

    private static async Task Animate(SampleContext ctx)
    {
        var frames = ctx.Get<List<(long Time, string Id)>>("frames");
        if (frames is null) return;
        ctx.Get<CancellationTokenSource?>("loop")?.Cancel();
        var loop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        ctx.State["loop"] = loop;
        try
        {
            for (var round = 0; round < 3; round++)
                for (var i = 0; i < frames.Count; i++)
                {
                    await ShowFrame(ctx, i);
                    await Task.Delay(700, loop.Token);
                }
        }
        catch (OperationCanceledException) { }
    }
}
