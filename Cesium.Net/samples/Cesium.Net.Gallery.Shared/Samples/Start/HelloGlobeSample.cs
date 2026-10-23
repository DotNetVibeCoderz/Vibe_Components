namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class HelloGlobeSample : GallerySample
{
    public override string Id => "hello-globe";
    public override string CategoryId => "start";
    public override Text Title => ("Hello, globe", "Halo, globe");
    public override Text Summary => (
        "Create a viewer, fly the camera to Jakarta and drop a labelled pin on Monas — the smallest useful Cesium.Net program.",
        "Buat viewer, terbangkan kamera ke Jakarta, lalu taruh pin berlabel di Monas — program Cesium.Net paling sederhana.");
    public override GeoPoint? Location => GalleryDefaults.Monas;

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("fly", ("Fly to Monas", "Terbang ke Monas"), ctx => ctx.Cesium.Camera.FlyToAsync(106.8272, -6.1820, 1_800, pitch: -35).AsTask()),
        new("home", ("Whole archipelago", "Seluruh nusantara"), ctx => ctx.Cesium.Camera.FlyToAsync(117.5, -2.5, 6_500_000).AsTask()),
        new("spin", ("Spin the globe", "Putar globe"), async ctx =>
        {
            await ctx.Cesium.Camera.FlyToAsync(117.5, -2.5, 20_000_000, duration: 2);
            await ctx.Cesium.Camera.StartSpinAsync(4);
        }),
        new("stop", ("Stop spinning", "Hentikan putaran"), ctx => ctx.Cesium.Camera.StopAnimationAsync().AsTask()),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        await cesium.Entities.AddAsync(new EntityOptions
        {
            Id = "monas",
            Name = "Monumen Nasional",
            Description = "<p>National Monument, Jakarta — 132 m tall, completed 1975.</p>",
            Position = new GeoPoint(106.827153, -6.175392),
            Billboard = new BillboardGraphics { PinColor = "#1F4FE0", PinIcon = "monument", HeightReference = HeightReference.ClampToGround },
            Label = new LabelGraphics
            {
                Text = "Monas",
                PixelOffset = new Cartesian2(0, -58),
                ShowBackground = true,
                BackgroundColor = "#15212ACC",
            },
        });

        await cesium.Camera.FlyToAsync(new CameraView
        {
            Destination = new GeoPoint(106.8272, -6.1880, 2_500),
            Heading = 0,
            Pitch = -40,
            Duration = 4,
        });
        ctx.Log("Camera arrived over Monas, Jakarta.");
        // </snippet>
    }
}
