namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class WmsWmtsSample : GallerySample
{
    public override string Id => "wms-wmts";
    public override string CategoryId => "imagery";
    public override Text Title => ("WMS & WMTS", "WMS & WMTS");
    public override Text Summary => (
        "NASA GIBS Earth-observation layers over Indonesia via the OGC WMTS and WMS standards. Switch between today's MODIS true colour, night lights, aerosol and active fires.",
        "Lapisan observasi Bumi NASA GIBS di atas Indonesia melalui standar OGC WMTS dan WMS. Beralih antara warna asli MODIS hari ini, cahaya malam, aerosol, dan titik api aktif.");
    public override GeoPoint? Location => new(113.9213, -0.7893);

    private const string GibsWmts = "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/wmts.cgi";
    private const string GibsWms = "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi";

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("truecolor", ("MODIS true colour (WMTS)", "Warna asli MODIS (WMTS)"), ctx => Show(ctx, "MODIS Terra true colour, today (WMTS)",
            ImageryLayerOptions.Wmts(GibsWmts, "MODIS_Terra_CorrectedReflectance_TrueColor", "GoogleMapsCompatible_Level9", format: "image/jpeg", maximumLevel: 8))),
        new("night", ("Night lights (WMTS)", "Cahaya malam (WMTS)"), ctx => Show(ctx, "VIIRS Black Marble night lights (WMTS)",
            ImageryLayerOptions.Wmts(GibsWmts, "VIIRS_Black_Marble", "GoogleMapsCompatible_Level8", maximumLevel: 8))),
        new("aerosol", ("Aerosol (WMS)", "Aerosol (WMS)"), ctx => Show(ctx, "MODIS Terra aerosol optical depth (WMS)",
            ImageryLayerOptions.Wms(GibsWms, "MODIS_Terra_Aerosol"), alpha: 0.75)),
        new("fires", ("Active fires (WMS)", "Titik api aktif (WMS)"), ctx => Show(ctx, "VIIRS NOAA-20 thermal anomalies (WMS)",
            ImageryLayerOptions.Wms(GibsWms, "VIIRS_NOAA20_Thermal_Anomalies_375m_All"))),
        new("none", ("Remove overlay", "Hapus overlay"), async ctx =>
        {
            if (ctx.Get<string?>("overlay") is { } id) await ctx.Cesium.Imagery.RemoveAsync(id);
            ctx.State["overlay"] = null;
            ctx.Log("Overlay removed — satellite base only.");
        }),
    ];

    private static async Task Show(SampleContext ctx, string name, ImageryLayerOptions layer, double alpha = 1)
    {
        if (ctx.Get<string?>("overlay") is { } previous) await ctx.Cesium.Imagery.RemoveAsync(previous);
        layer.Alpha = alpha;
        ctx.State["overlay"] = await ctx.Cesium.Imagery.AddAsync(layer);
        ctx.Log($"Overlay: {name}");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        // WMTS: pre-rendered tiles in a named tile matrix set (Web Mercator here).
        var trueColor = ImageryLayerOptions.Wmts(
            url: "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/wmts.cgi",
            layer: "MODIS_Terra_CorrectedReflectance_TrueColor",
            tileMatrixSetId: "GoogleMapsCompatible_Level9",
            format: "image/jpeg",
            maximumLevel: 8);
        ctx.State["overlay"] = await cesium.Imagery.AddAsync(trueColor);
        ctx.Log("Overlay: MODIS Terra true colour, today (WMTS)");

        // WMS: images rendered on demand for any bounding box — good for thematic data.
        var coastlines = ImageryLayerOptions.Wms(
            url: "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi",
            layers: "Coastlines_15m");
        await cesium.Imagery.AddAsync(coastlines);
        ctx.Log("Coastlines added on top (WMS).");

        await cesium.Camera.FlyToAsync(117.5, -2.0, 5_500_000, duration: 2);
        // </snippet>
    }
}
