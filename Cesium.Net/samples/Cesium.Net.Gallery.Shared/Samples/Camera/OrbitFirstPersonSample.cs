namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class OrbitFirstPersonSample : GallerySample
{
    public override string Id => "orbit-first-person";
    public override string CategoryId => "camera";
    public override Text Title => ("Orbit & first-person", "Orbit & orang pertama");
    public override Text Summary => (
        "Circle Borobudur temple with an automatic orbit, or switch to first-person flight and steer with the keyboard. Small look, rotate and zoom steps are also available as buttons.",
        "Kelilingi Candi Borobudur dengan orbit otomatis, atau beralih ke terbang orang pertama dan kemudikan dengan keyboard. Langkah kecil menoleh, memutar, dan zoom juga tersedia sebagai tombol.");
    public override GeoPoint? Location => Borobudur;
    public override bool UsesIon => true;

    private static readonly GeoPoint Borobudur = new(110.2038, -7.6079, 265);
    private static readonly Text OrbitGroup = ("Orbit", "Orbit");
    private static readonly Text FlightGroup = ("First-person", "Orang pertama");
    private static readonly Text NudgeGroup = ("Nudge", "Geser");

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o =>
        o.Terrain = string.IsNullOrWhiteSpace(ionToken) ? TerrainOptions.ArcGis() : TerrainOptions.WorldTerrain());

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("orbit", ("Orbit Borobudur", "Orbit Borobudur"), ctx => ctx.Cesium.Camera.StartOrbitAsync(Borobudur, range: 650, pitch: -25, degreesPerSecond: 8).AsTask()) { Group = OrbitGroup },
        new("stop", ("Stop orbit", "Hentikan orbit"), ctx => ctx.Cesium.Camera.StopAnimationAsync().AsTask()) { Group = OrbitGroup },
        new("fly", ("Enable flight mode", "Aktifkan mode terbang"), async ctx =>
        {
            await ctx.Cesium.Camera.StopAnimationAsync();
            await ctx.Cesium.Camera.EnableFlightModeAsync(new FlightModeOptions { Speed = 40 });
            ctx.Log("Flight mode: W/S forward/back, A/D left/right, Q/E down/up, drag to look, Shift = faster.");
        }) { Group = FlightGroup },
        new("walk", ("Walk at 2 m", "Berjalan di 2 m"), async ctx =>
        {
            await ctx.Cesium.Camera.StopAnimationAsync();
            await ctx.Cesium.Camera.SetViewAsync(CameraView.At(110.2038, -7.6110, 280, heading: 0, pitch: 0));
            await ctx.Cesium.Camera.EnableFlightModeAsync(new FlightModeOptions { Speed = 6, WalkHeight = 2 });
            ctx.Log("Walk mode: the camera stays 2 m above the ground. Use W/A/S/D.");
        }) { Group = FlightGroup },
        new("land", ("Exit flight mode", "Keluar mode terbang"), async ctx =>
        {
            await ctx.Cesium.Camera.DisableFlightModeAsync();
            ctx.Log("Mouse navigation restored.");
        }) { Group = FlightGroup },
        new("look", ("Look left 15°", "Menoleh kiri 15°"), ctx => ctx.Cesium.Camera.LookAsync(CameraDirection.Left, 15).AsTask()) { Group = NudgeGroup },
        new("rotate", ("Rotate 10° around the temple", "Putar 10° mengelilingi candi"), async ctx =>
        {
            // RotateAsync pivots around the current reference frame: lock onto the temple first.
            var camera = ctx.Cesium.Camera;
            await camera.StopAnimationAsync();
            var state = await camera.GetStateAsync();
            await camera.LookAtAsync(Borobudur, new HeadingPitchRange(state.Heading, -25, 650));
            await camera.RotateAsync(CameraDirection.Right, 10);
            await camera.ReleaseLookAtAsync();
        }) { Group = NudgeGroup },
        new("zoom", ("Zoom in", "Perbesar"), ctx => ctx.Cesium.Camera.ZoomAsync().AsTask()) { Group = NudgeGroup },
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Candi Borobudur",
            Position = Borobudur,
            Label = new LabelGraphics { Text = "Candi Borobudur", HeightReference = HeightReference.RelativeToGround, PixelOffset = new Cartesian2(0, -40), ShowBackground = true },
        });

        // Orbit: the camera circles the target until another camera call stops it.
        await cesium.Camera.StartOrbitAsync(Borobudur, range: 650, pitch: -25, degreesPerSecond: 8);
        ctx.Log("Orbiting Borobudur at 650 m.");

        // First-person flight is one call away:
        //   await cesium.Camera.EnableFlightModeAsync(new FlightModeOptions { Speed = 40 });
        // W/A/S/D move, Q/E down/up, drag to look, Shift to go faster.
        // </snippet>
    }
}
