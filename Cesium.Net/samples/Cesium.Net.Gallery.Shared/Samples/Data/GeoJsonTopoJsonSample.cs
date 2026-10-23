using System.Text.Json;

namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class GeoJsonTopoJsonSample : GallerySample
{
    public override string Id => "geojson-topojson";
    public override string CategoryId => "data";
    public override Text Title => ("GeoJSON & TopoJSON", "GeoJSON & TopoJSON");
    public override Text Summary => (
        "Rough outlines of four Indonesian regions, generated as GeoJSON in C# and extruded by population, plus the US states loaded from TopoJSON with random fills. Click a shape to see its properties.",
        "Garis besar kasar empat wilayah Indonesia, dibuat sebagai GeoJSON di C# dan diekstrusi menurut jumlah penduduk, ditambah negara bagian AS dari TopoJSON dengan isian acak. Klik bentuk untuk melihat propertinya.");
    public override GeoPoint? Location => new(112.0, -7.5);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("flat", ("Indonesia, flat", "Indonesia, datar"), ctx => LoadRegions(ctx, extrude: false)),
        new("extrude", ("Indonesia, extruded by population", "Indonesia, ekstrusi menurut penduduk"), ctx => LoadRegions(ctx, extrude: true)),
        new("us", ("US states (TopoJSON)", "Negara bagian AS (TopoJSON)"), async ctx =>
        {
            await ClearAsync(ctx);
            var id = await ctx.Cesium.DataSources.LoadAsync(new DataSourceOptions
            {
                Kind = DataSourceKind.TopoJson,
                Url = "https://raw.githubusercontent.com/CesiumGS/cesium/main/Apps/SampleData/ne_10m_us_states.topojson",
                Stroke = CesiumColor.White,
                RandomFill = true,
                FlyTo = true,
            });
            ctx.State["ds"] = id;
            ctx.Log($"TopoJSON: {await ctx.Cesium.DataSources.CountAsync(id)} entities with random fills.");
        }),
        new("clear", ("Clear", "Bersihkan"), ClearAsync),
    ];

    private static async Task ClearAsync(SampleContext ctx)
    {
        if (ctx.Get<string?>("ds") is { } id) await ctx.Cesium.DataSources.RemoveAsync(id);
        ctx.State["ds"] = null;
    }

    public override Task RunAsync(SampleContext ctx) => LoadRegions(ctx, extrude: true);

    // <snippet>
    // Rough, simplified outlines — illustrative only. Population in millions.
    private static readonly (string Name, double Population, double[][] Ring)[] Regions =
    [
        ("DKI Jakarta", 10.7, [[106.69, -6.10], [106.97, -6.09], [106.97, -6.37], [106.80, -6.37], [106.69, -6.21]]),
        ("DI Yogyakarta", 3.7, [[110.01, -7.79], [110.20, -7.67], [110.45, -7.55], [110.60, -7.70], [110.83, -7.95], [110.70, -8.20], [110.40, -8.05], [110.12, -7.95]]),
        ("Bali", 4.4, [[114.43, -8.17], [115.09, -8.11], [115.59, -8.28], [115.71, -8.40], [115.51, -8.53], [115.26, -8.70], [115.17, -8.85], [115.08, -8.62], [114.62, -8.40]]),
        ("Lombok", 3.8, [[116.06, -8.48], [116.10, -8.38], [116.30, -8.25], [116.70, -8.30], [116.73, -8.60], [116.55, -8.90], [116.30, -8.90], [115.85, -8.80]]),
    ];

    private static async Task LoadRegions(SampleContext ctx, bool extrude)
    {
        await ClearAsync(ctx);

        // Build a GeoJSON FeatureCollection in C#; rings must be closed.
        var geoJson = JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = Regions.Select(r => new
            {
                type = "Feature",
                properties = new { name = r.Name, population_millions = r.Population },
                geometry = new { type = "Polygon", coordinates = new[] { r.Ring.Append(r.Ring[0]).ToArray() } },
            }),
        });

        var id = await ctx.Cesium.DataSources.LoadAsync(new DataSourceOptions
        {
            Kind = DataSourceKind.GeoJson,
            Data = geoJson,
            Stroke = CesiumColor.White,
            Fill = CesiumColor.Blue.WithAlpha(0.6f),
            RandomFill = true,
            ExtrudeByProperty = extrude ? "population_millions" : null,
            ExtrudeScale = 5_000,               // 1 million people → 5 km
        });
        ctx.State["ds"] = id;
        ctx.Log($"GeoJSON ({geoJson.Length:N0} bytes): {await ctx.Cesium.DataSources.CountAsync(id)} regions{(extrude ? ", extruded by population" : "")}.");

        await ctx.Cesium.Camera.FlyToAsync(112.5, -14.0, 900_000, pitch: -45, duration: 2);
    }
    // </snippet>
}
