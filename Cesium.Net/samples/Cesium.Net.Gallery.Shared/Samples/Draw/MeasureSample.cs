namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class MeasureSample : GallerySample
{
    public override string Id => "measure";
    public override string CategoryId => "draw";
    public override Text Title => ("Measure distance & area", "Ukur jarak & luas");
    public override Text Summary => (
        "Measure paths and areas around Borobudur temple. A live label follows the cursor while you click, and the final length, area and perimeter are computed in C# on the WGS84 ellipsoid.",
        "Ukur jalur dan luas di sekitar Candi Borobudur. Label langsung mengikuti kursor saat Anda mengklik, dan panjang, luas, serta keliling akhir dihitung di C# pada elipsoid WGS84.");
    public override GeoPoint? Location => new(110.2038, -7.6079);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("distance", ("Measure distance", "Ukur jarak"), ctx => Measure(ctx, DrawMode.MeasureDistance)),
        new("area", ("Measure area", "Ukur luas"), ctx => Measure(ctx, DrawMode.MeasureArea)),
        new("clear", ("Clear measurements", "Hapus pengukuran"), async ctx =>
        {
            await ctx.Cesium.Drawing.ClearAsync();
            ctx.Log("Measurements cleared.");
        }),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // The temple's base is roughly a 118 m square; measure it from C# for reference.
        var corners = new[] { new GeoPoint(110.20327, -7.60738), new GeoPoint(110.20436, -7.60738), new GeoPoint(110.20436, -7.60846), new GeoPoint(110.20327, -7.60846) };
        ctx.Log($"Reference: Borobudur base ≈ {Geodesy.FormatArea(Geodesy.Area(corners))}, perimeter {Geodesy.FormatDistance(Geodesy.Perimeter(corners))}.");
        await ctx.Cesium.Camera.FlyToAsync(110.2038, -7.6110, 420, pitch: -55, duration: 3);
        ctx.Log("Choose “Measure distance” or “Measure area”, then click on the map.");
    }

    // <snippet>
    private static async Task Measure(SampleContext ctx, DrawMode mode)
    {
        // Measure modes show a live label; the result comes back as a DrawResult.
        var result = await ctx.Cesium.Drawing.DrawAsync(new DrawOptions
        {
            Mode = mode,                    // MeasureDistance or MeasureArea
            Color = "#F5C518",
            ShowMeasurementLabel = true,
        }, ctx.Token);
        if (result is null) { ctx.Log("Measurement cancelled."); return; }

        // Geodesic computations in C# (ellipsoidal, not flat-earth).
        if (mode == DrawMode.MeasureDistance)
        {
            ctx.Log($"Distance: {Geodesy.FormatDistance(result.Length)} over {result.Positions.Count - 1} segment(s)");
            for (var i = 1; i < result.Positions.Count; i++)
            {
                var (a, b) = (result.Positions[i - 1], result.Positions[i]);
                ctx.Log($"  leg {i}: {Geodesy.FormatDistance(Geodesy.Distance(a, b))}, bearing {Geodesy.Bearing(a, b):F0}°");
            }
        }
        else
        {
            ctx.Log($"Area: {Geodesy.FormatArea(result.Area)}, perimeter {Geodesy.FormatDistance(Geodesy.Perimeter(result.Positions))}, {result.Positions.Count} vertices");
        }
    }
    // </snippet>
}
