namespace CesiumNet.Gallery.Samples;

[SampleOrder(3)]
public sealed class KmlCzmlGpxSample : GallerySample
{
    public override string Id => "kml-czml-gpx";
    public override string CategoryId => "data";
    public override Text Title => ("KML, CZML & GPX", "KML, CZML & GPX");
    public override Text Summary => (
        "Three common exchange formats loaded from URLs: KML placemarks of US science facilities, a time-dynamic CZML satellite scene, and a GPX hiking track. Each action loads one format and logs how many entities it produced.",
        "Tiga format pertukaran umum dimuat dari URL: placemark KML fasilitas sains AS, adegan satelit CZML yang dinamis terhadap waktu, dan jalur pendakian GPX. Setiap aksi memuat satu format dan mencatat jumlah entitas yang dihasilkan.");
    public override GeoPoint? Location => new(-98.5, 39.5);

    private const string Data = "https://raw.githubusercontent.com/CesiumGS/cesium/main/Apps/SampleData/";

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o => { o.Timeline = true; o.Animation = true; });

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("kml", ("KML: facilities", "KML: fasilitas"), ctx => Load(ctx, "KML", DataSourceOptions.Kml(Data + "kml/facilities/facilities.kml"))),
        new("czml", ("CZML: satellites", "CZML: satelit"), async ctx =>
        {
            await Load(ctx, "CZML", DataSourceOptions.Czml(Data + "simple.czml"));
            await ctx.Cesium.Clock.PlayAsync();
        }),
        new("gpx", ("GPX: hiking track", "GPX: jalur pendakian"), ctx => Load(ctx, "GPX", DataSourceOptions.Gpx(Data + "gpx/lamina.gpx"))),
        new("clear", ("Clear", "Bersihkan"), async ctx =>
        {
            if (ctx.Get<string?>("ds") is { } id) await ctx.Cesium.DataSources.RemoveAsync(id);
            ctx.State["ds"] = null;
            ctx.Log("Data source removed.");
        }),
    ];

    public override Task RunAsync(SampleContext ctx) => Load(ctx, "KML", DataSourceOptions.Kml(Data + "kml/facilities/facilities.kml"));

    // <snippet>
    private static async Task Load(SampleContext ctx, string format, DataSourceOptions source)
    {
        var cesium = ctx.Cesium;

        // One data source at a time: remove the previous one.
        if (ctx.Get<string?>("ds") is { } previous) await cesium.DataSources.RemoveAsync(previous);

        // DataSourceOptions.Kml / .Czml / .Gpx set the format and fly to the result.
        var started = DateTime.UtcNow;
        var id = await cesium.DataSources.LoadAsync(source, ctx.Token);
        ctx.State["ds"] = id;

        var count = await cesium.DataSources.CountAsync(id);
        ctx.Log($"{format}: {count:N0} entities loaded in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
    }
    // </snippet>
}
