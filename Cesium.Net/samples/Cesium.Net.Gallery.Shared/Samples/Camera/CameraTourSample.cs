namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class CameraTourSample : GallerySample
{
    public override string Id => "camera-tour";
    public override string CategoryId => "camera";
    public override Text Title => ("Camera tour", "Tur kamera");
    public override Text Summary => (
        "A guided flight across Indonesian landmarks, from Monas to Raja Ampat, built from a C# list of camera views. The tour stops cleanly when you press Stop or leave the sample.",
        "Penerbangan berpemandu melintasi ikon-ikon Indonesia, dari Monas hingga Raja Ampat, disusun dari daftar tampilan kamera di C#. Tur berhenti dengan rapi saat Anda menekan Hentikan atau meninggalkan contoh.");
    public override GeoPoint? Location => new(117.5, -2.5);
    public override bool UsesIon => true;

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o =>
        o.Terrain = string.IsNullOrWhiteSpace(ionToken) ? TerrainOptions.ArcGis() : TerrainOptions.WorldTerrain());

    private static readonly (string Name, GeoPoint Target, CameraView View)[] Stops =
    [
        ("Monas, Jakarta", new(106.8272, -6.1754), CameraView.At(106.8272, -6.1840, 1_400, heading: 0, pitch: -40)),
        ("Borobudur", new(110.2038, -7.6079), CameraView.At(110.2038, -7.6125, 900, heading: 0, pitch: -35)),
        ("Mount Bromo", new(112.9530, -7.9425), CameraView.At(112.9750, -7.9950, 5_500, heading: 340, pitch: -22)),
        ("Tanah Lot, Bali", new(115.0868, -8.6212), CameraView.At(115.0880, -8.6260, 700, heading: 350, pitch: -30)),
        ("Komodo Island", new(119.4890, -8.5500), CameraView.At(119.5600, -8.7000, 22_000, heading: 330, pitch: -30)),
        ("Lake Toba", new(98.8700, 2.6200), CameraView.At(98.8700, 2.2500, 45_000, heading: 0, pitch: -35)),
        ("Raja Ampat", new(130.5000, -0.5000), CameraView.At(130.6000, -0.9000, 60_000, heading: 340, pitch: -40)),
    ];

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("start", ("Start tour", "Mulai tur"), ctx => { StartTour(ctx); return Task.CompletedTask; }),
        new("stop", ("Stop tour", "Hentikan tur"), ctx =>
        {
            ctx.Get<CancellationTokenSource?>("tour")?.Cancel();
            return Task.CompletedTask;
        }),
        new("overview", ("Archipelago overview", "Tampilan nusantara"), async ctx =>
        {
            ctx.Get<CancellationTokenSource?>("tour")?.Cancel();
            await ctx.Cesium.Camera.FlyToAsync(117.5, -2.5, 6_500_000, duration: 2);
        }),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        await ctx.Cesium.Entities.AddRangeAsync(Stops.Select(s => new EntityOptions
        {
            Name = s.Name,
            Position = s.Target,
            Billboard = new BillboardGraphics { PinColor = "#1F4FE0", PinIcon = "star", HeightReference = HeightReference.ClampToGround },
            Label = new LabelGraphics { Text = s.Name, PixelOffset = new Cartesian2(0, -58), ShowBackground = true, HeightReference = HeightReference.ClampToGround },
        }).ToList());
        StartTour(ctx);
    }

    private static void StartTour(SampleContext ctx)
    {
        ctx.Get<CancellationTokenSource?>("tour")?.Cancel();
        // Linked to ctx.Token, so leaving the sample also stops the tour.
        var tour = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        ctx.State["tour"] = tour;
        _ = TourAsync(ctx, tour.Token);
    }

    // <snippet>
    private static async Task TourAsync(SampleContext ctx, CancellationToken token)
    {
        var camera = ctx.Cesium.Camera;
        try
        {
            foreach (var (name, _, view) in Stops)
            {
                ctx.Log($"Flying to {name}…");
                view.Duration = 5;
                await camera.FlyToAsync(view, token);      // completes when the flight ends
                await Task.Delay(TimeSpan.FromSeconds(3), token); // linger, then move on
            }
            ctx.Log("Tour finished.");
        }
        catch (OperationCanceledException)
        {
            ctx.Log("Tour stopped.");
        }
    }
    // </snippet>
}
