using CesiumNet.Ion;

namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class CesiumIonSample : GallerySample
{
    public override string Id => "cesium-ion";
    public override string CategoryId => "advanced";
    public override Text Title => ("Cesium ion", "Cesium ion");
    public override Text Summary => (
        "With a Cesium ion token, Jakarta is shown on Cesium World Terrain with OSM Buildings, and your ion assets are listed from C#. Without a token, the sample falls back to Esri terrain and the log explains how to add one.",
        "Dengan token Cesium ion, Jakarta ditampilkan di atas Cesium World Terrain dengan OSM Buildings, dan aset ion Anda didaftar dari C#. Tanpa token, contoh ini memakai terrain Esri dan log menjelaskan cara menambahkan token.");
    public override GeoPoint? Location => new(106.8227, -6.2088);
    public override bool UsesIon => true;

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("assets", ("List my ion assets", "Daftar aset ion saya"), ListAssets),
        new("skyline", ("Jakarta skyline", "Cakrawala Jakarta"), ctx => ctx.Cesium.Camera.FlyToAsync(106.8150, -6.2350, 1_200, heading: 15, pitch: -20, duration: 3).AsTask()),
        new("bromo", ("Bromo (terrain)", "Bromo (terrain)"), ctx => ctx.Cesium.Camera.FlyToAsync(112.9750, -7.9950, 5_500, heading: 340, pitch: -22, duration: 3).AsTask()),
    ];

    // <snippet>
    public override async Task RunAsync(SampleContext ctx)
    {
        var cesium = ctx.Cesium;

        if (!ctx.HasIonToken)
        {
            await cesium.Terrain.SetProviderAsync(TerrainOptions.ArcGis());
            ctx.Log("No Cesium ion token, so Esri terrain is used instead.");
            ctx.Log("To unlock ion: create a free account at ion.cesium.com, copy a token from Access Tokens,");
            ctx.Log("then paste it in the gallery Settings (or set Cesium:IonToken in appsettings.json).");
            await cesium.Camera.FlyToAsync(106.8150, -6.2350, 1_200, heading: 15, pitch: -20, duration: 3);
            return;
        }

        // The token was passed in CesiumViewerOptions.IonAccessToken; ion assets now just work.
        await cesium.Terrain.SetProviderAsync(TerrainOptions.WorldTerrain());
        await cesium.Tilesets.AddAsync(TilesetOptions.OsmBuildings());
        ctx.Log("Cesium World Terrain + OSM Buildings loaded.");
        await cesium.Camera.FlyToAsync(106.8150, -6.2350, 1_200, heading: 15, pitch: -20, duration: 3);

        await ListAssets(ctx);
    }

    private static async Task ListAssets(SampleContext ctx)
    {
        if (!ctx.HasIonToken) { ctx.Log("Add an ion token in Settings to list your assets."); return; }

        // The ion REST API, called from C# (server-side in Blazor, native in Avalonia).
        var ion = new CesiumIonClient(ctx.Http, ctx.IonToken!);
        var assets = await ion.ListAssetsAsync(ctx.Token);
        ctx.Log($"Your ion account has {assets.Count} asset(s):");
        foreach (var a in assets.Take(10))
            ctx.Log($"  #{a.Id} {a.Name} — {a.Type}, {a.Bytes / 1_048_576.0:N1} MB, {a.Status}");
    }
    // </snippet>
}
