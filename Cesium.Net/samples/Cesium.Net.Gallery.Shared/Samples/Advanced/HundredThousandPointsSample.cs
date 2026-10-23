using System.Diagnostics;

namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class HundredThousandPointsSample : GallerySample
{
    public override string Id => "points-100k";
    public override string CategoryId => "advanced";
    public override Text Title => ("100,000 points", "100.000 titik");
    public override Text Summary => (
        "100,000 synthetic points clustered around Indonesian cities, each with its own colour, drawn as one GPU point collection in a single call. The log shows how long generation and rendering took.",
        "100.000 titik sintetis yang mengelompok di sekitar kota-kota Indonesia, masing-masing dengan warnanya sendiri, digambar sebagai satu koleksi titik GPU dalam satu panggilan. Log menampilkan lama pembuatan dan perenderan.");
    public override GeoPoint? Location => new(112.0, -6.5);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("regen", ("Regenerate 100k", "Buat ulang 100rb"), ctx => Generate(ctx, Random.Shared.Next())),
        new("toggle", ("Show / hide", "Tampilkan / sembunyikan"), async ctx =>
        {
            var visible = !ctx.Get("visible", true);
            ctx.State["visible"] = visible;
            await ctx.Cesium.Primitives.SetVisibleAsync(ctx.Get<string>("points"), visible);
        }),
        new("java", ("Zoom to Java", "Perbesar ke Jawa"), ctx => ctx.Cesium.Camera.FlyToAsync(110.0, -7.2, 900_000, duration: 2).AsTask()),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        await Generate(ctx, seed: 2026);
        await ctx.Cesium.Camera.FlyToAsync(113.0, -4.5, 4_200_000, duration: 2);
    }

    // <snippet>
    private static readonly (GeoPoint Center, double Weight)[] Cities =
    [
        (new(106.85, -6.21), 10), (new(112.75, -7.25), 5), (new(107.61, -6.91), 5), (new(98.67, 3.60), 4),
        (new(110.42, -6.97), 3), (new(119.43, -5.15), 3), (new(104.76, -2.98), 2), (new(115.21, -8.67), 2),
        (new(116.83, -1.24), 1.5), (new(124.84, 1.47), 1), (new(140.72, -2.59), 0.7), (new(109.34, -0.03), 1),
    ];

    private static async Task Generate(SampleContext ctx, int seed)
    {
        const int count = 100_000;
        var watch = Stopwatch.StartNew();
        var random = new Random(seed);
        double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(Math.Tau * random.NextDouble());

        // Pick a city by weight, scatter around it; colour by distance from the city centre.
        var palette = Enumerable.Range(0, 8).Select(i => CesiumColor.FromHsl(0.55 - i * 0.07, 0.9, 0.55)).ToArray();
        var totalWeight = Cities.Sum(c => c.Weight);
        var positions = new GeoPoint[count];
        var colors = new CesiumColor[count];
        for (var i = 0; i < count; i++)
        {
            var pick = random.NextDouble() * totalWeight;
            var city = Cities.First(c => (pick -= c.Weight) <= 0).Center;
            var spread = Math.Abs(Gaussian()) * 0.35;
            var angle = random.NextDouble() * Math.Tau;
            positions[i] = new GeoPoint(city.Longitude + spread * Math.Cos(angle), city.Latitude + spread * Math.Sin(angle));
            colors[i] = palette[Math.Min(palette.Length - 1, (int)(spread / 0.08))];
        }
        var generated = watch.ElapsedMilliseconds;

        // One interop call, one GPU primitive.
        if (ctx.Get<string?>("points") is { } old) await ctx.Cesium.Primitives.RemoveAsync(old);
        watch.Restart();
        ctx.State["points"] = await ctx.Cesium.Primitives.AddPointsAsync(new PointCollectionOptions
        {
            Positions = positions,
            Colors = colors,
            PixelSize = 3,
            ScaleByDistance = new NearFarScalar(100_000, 2, 5_000_000, 0.8),
        });
        ctx.Log($"{count:N0} points: generated in {generated} ms, sent and added in {watch.ElapsedMilliseconds} ms.");
    }
    // </snippet>
}
