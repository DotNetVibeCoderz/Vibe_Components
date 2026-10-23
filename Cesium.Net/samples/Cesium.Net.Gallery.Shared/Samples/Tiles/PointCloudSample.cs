namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class PointCloudSample : GallerySample
{
    public override string Id => "point-cloud";
    public override string CategoryId => "tiles";
    public override Text Title => ("Point cloud", "Point cloud");
    public override Text Summary => (
        "A coloured point cloud streamed as 3D Tiles. Turn on attenuation so points grow with geometric error, add eye-dome lighting for depth, or restyle the points.",
        "Point cloud berwarna yang dialirkan sebagai 3D Tiles. Aktifkan atenuasi agar titik membesar sesuai galat geometrik, tambahkan eye-dome lighting untuk kesan kedalaman, atau ubah gaya titik.");
    public override GeoPoint? Location => new(-75.6125, 40.0423);

    public override CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken, o => o.BaseLayer = ImageryLayerOptions.Dark());

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("attenuation", ("Toggle attenuation", "Atenuasi aktif/nonaktif"), async ctx =>
        {
            var on = !ctx.Get("attenuation", true);
            ctx.State["attenuation"] = on;
            await ctx.Cesium.Tilesets.UpdateAsync(ctx.Get<string>("tileset"), new TilesetOptions { PointCloudShading = new PointCloudShading { Attenuation = on, MaximumAttenuation = 8 } });
            ctx.Log($"Attenuation {(on ? "on" : "off")}.");
        }),
        new("edl", ("Toggle eye-dome lighting", "Eye-dome lighting aktif/nonaktif"), async ctx =>
        {
            var on = !ctx.Get("edl", true);
            ctx.State["edl"] = on;
            await ctx.Cesium.Tilesets.UpdateAsync(ctx.Get<string>("tileset"), new TilesetOptions { PointCloudShading = new PointCloudShading { EyeDomeLighting = on, EyeDomeLightingStrength = 1.5, EyeDomeLightingRadius = 1.5 } });
            ctx.Log($"Eye-dome lighting {(on ? "on" : "off")}.");
        }),
        new("style", ("Blue tint, big points", "Rona biru, titik besar"), async ctx =>
        {
            await ctx.Cesium.Tilesets.SetStyleAsync(ctx.Get<string>("tileset"), new TilesetStyle
            {
                Color = "vec4(${COLOR}.r * 0.4, ${COLOR}.g * 0.7, 1.0, 1.0)",
                PointSize = "6.0",
            });
            ctx.Log("Style: blue-tinted colours, 6 px points.");
        }),
        new("reset", ("Original colours", "Warna asli"), async ctx =>
        {
            await ctx.Cesium.Tilesets.SetStyleAsync(ctx.Get<string>("tileset"), null);
            ctx.Log("Style reset to the per-point RGB colours.");
        }),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;

        var tileset = await cesium.Tilesets.AddAsync(new TilesetOptions
        {
            Url = "https://raw.githubusercontent.com/CesiumGS/cesium/main/Apps/SampleData/Cesium3DTiles/PointCloud/PointCloudRGB/tileset.json",
            PointCloudShading = new PointCloudShading
            {
                Attenuation = true,          // size points by geometric error
                MaximumAttenuation = 8,      // … up to 8 px
                EyeDomeLighting = true,      // screen-space shading for depth
                EyeDomeLightingStrength = 1.5,
            },
        });
        ctx.State["tileset"] = tileset;

        await cesium.Camera.FlyToObjectAsync(tileset, new HeadingPitchRange(0, -30, 18), duration: 2);
        ctx.Log("Point cloud loaded with attenuation and eye-dome lighting.");
        // </snippet>
    }
}
