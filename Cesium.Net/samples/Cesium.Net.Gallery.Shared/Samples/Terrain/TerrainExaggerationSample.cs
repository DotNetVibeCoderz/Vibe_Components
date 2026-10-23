namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class TerrainExaggerationSample : GallerySample
{
    public override string Id => "terrain-exaggeration";
    public override string CategoryId => "terrain";
    public override Text Title => ("Terrain & exaggeration", "Terrain & eksagerasi");
    public override Text Summary => (
        "The Bromo Tengger Semeru volcanoes in East Java on real elevation data. Exaggerate the relief up to 4× and read summit heights sampled from the terrain.",
        "Gunung Bromo, Tengger, dan Semeru di Jawa Timur di atas data elevasi nyata. Lebih-lebihkan relief hingga 4× dan baca ketinggian puncak yang diambil dari terrain.");
    public override GeoPoint? Location => new(112.9530, -7.9425);
    public override bool UsesIon => true;

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("x1", ("Real scale (1×)", "Skala nyata (1×)"), ctx => Exaggerate(ctx, 1)),
        new("x2", ("Exaggerate 2×", "Eksagerasi 2×"), ctx => Exaggerate(ctx, 2)),
        new("x4", ("Exaggerate 4×", "Eksagerasi 4×"), ctx => Exaggerate(ctx, 4)),
        new("bromo", ("Bromo caldera", "Kaldera Bromo"), ctx => ctx.Cesium.Camera.FlyToAsync(112.985, -7.995, 6_500, heading: 330, pitch: -25, duration: 3).AsTask()),
        new("semeru", ("Semeru summit", "Puncak Semeru"), ctx => ctx.Cesium.Camera.FlyToAsync(112.955, -8.200, 7_500, heading: 350, pitch: -18, duration: 3).AsTask()),
    ];

    private static async Task Exaggerate(SampleContext ctx, double factor)
    {
        await ctx.Cesium.Terrain.SetExaggerationAsync(factor);
        ctx.Log($"Vertical exaggeration {factor}×");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        // Cesium World Terrain with an ion token, Esri WorldElevation3D without one.
        await cesium.Terrain.SetProviderAsync(ctx.HasIonToken ? TerrainOptions.WorldTerrain() : TerrainOptions.ArcGis());
        ctx.Log(ctx.HasIonToken ? "Terrain: Cesium World Terrain" : "Terrain: Esri WorldElevation3D (no token needed)");

        var peaks = new (string Name, GeoPoint Position)[]
        {
            ("Bromo", new(112.9530, -7.9425)),
            ("Batok", new(112.9440, -7.9330)),
            ("Semeru", new(112.9224, -8.1077)),
            ("Penanjakan", new(112.9510, -7.9050)),
        };

        // Sample the real ground height under each summit, then label it.
        var heights = await cesium.Terrain.SampleHeightsAsync(peaks.Select(p => p.Position).ToList());
        for (var i = 0; i < peaks.Length; i++)
        {
            ctx.Log($"{peaks[i].Name}: {heights[i].Height:N0} m above the ellipsoid");
            await cesium.Entities.AddAsync(new EntityOptions
            {
                Name = peaks[i].Name,
                Position = peaks[i].Position,
                Label = new LabelGraphics
                {
                    Text = $"{peaks[i].Name}\n{heights[i].Height:N0} m",
                    HeightReference = HeightReference.ClampToGround,
                    VerticalOrigin = VerticalOrigin.Bottom,
                    ShowBackground = true,
                    BackgroundColor = "#15212ACC",
                    AlwaysOnTop = true,
                },
            });
        }

        await cesium.Camera.FlyToAsync(113.07, -8.22, 11_000, heading: 318, pitch: -18, duration: 4);
        await cesium.Terrain.SetExaggerationAsync(2);
        ctx.Log("Vertical exaggeration 2×");
        // </snippet>
    }
}
