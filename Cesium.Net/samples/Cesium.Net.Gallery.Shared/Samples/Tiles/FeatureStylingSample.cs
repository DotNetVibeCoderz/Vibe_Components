namespace CesiumNet.Gallery.Samples;

[SampleOrder(3)]
public sealed class FeatureStylingSample : GallerySample
{
    public override string Id => "feature-styling";
    public override string CategoryId => "tiles";
    public override Text Title => ("Feature styling & custom shader", "Styling fitur & custom shader");
    public override Text Summary => (
        "Buildings coloured by their height property with a declarative 3D Tiles style, then repainted by an animated GLSL custom shader. With an ion token this uses OSM Buildings in Jakarta. Click a building to see its properties.",
        "Gedung diwarnai berdasarkan properti ketinggiannya dengan style 3D Tiles deklaratif, lalu diwarnai ulang oleh custom shader GLSL beranimasi. Dengan token ion, contoh ini memakai OSM Buildings di Jakarta. Klik gedung untuk melihat propertinya.");
    public override GeoPoint? Location => new(106.8227, -6.2088);
    public override bool UsesIon => true;

    private const string PulseShader = """
        void fragmentMain(FragmentInput fsInput, inout czm_modelMaterial material) {
            float height = fsInput.attributes.positionMC.z;
            float wave = 0.5 + 0.5 * sin(u_time * 2.0 - height * 0.08);
            material.diffuse = mix(vec3(0.07, 0.20, 0.55), vec3(1.0, 0.78, 0.10), wave);
        }
        """;

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("height", ("Colour by height", "Warna menurut tinggi"), ctx => ApplyHeightStyle(ctx)),
        new("tall", ("Only tall buildings", "Hanya gedung tinggi"), async ctx =>
        {
            var h = ctx.Get<string>("heightExpr");
            await ctx.Cesium.Tilesets.SetStyleAsync(ctx.Get<string>("tileset"), new TilesetStyle { Color = "color('#F5C518')", Show = $"{h} > {ctx.Get<int>("tallFrom")}" });
            ctx.Log($"Showing only features with {h} > {ctx.Get<int>("tallFrom")} m.");
        }),
        new("shader", ("Animated custom shader", "Custom shader beranimasi"), async ctx =>
        {
            await ctx.Cesium.Tilesets.UpdateAsync(ctx.Get<string>("tileset"), new TilesetOptions
            {
                CustomShader = new CustomShaderOptions { FragmentShaderText = PulseShader, AnimateTime = true },
            });
            ctx.Log("Custom shader on: a colour wave moves up the buildings (u_time uniform).");
        }),
        new("reset", ("Reset", "Atur ulang"), async ctx =>
        {
            var id = ctx.Get<string>("tileset");
            await ctx.Cesium.Tilesets.UpdateAsync(id, new TilesetOptions { CustomShader = new CustomShaderOptions() });
            await ctx.Cesium.Tilesets.SetStyleAsync(id, null);
            ctx.Log("Custom shader removed and style reset.");
        }),
    ];

    private static async Task ApplyHeightStyle(SampleContext ctx)
    {
        var h = ctx.Get<string>("heightExpr");
        await ctx.Cesium.Tilesets.UpdateAsync(ctx.Get<string>("tileset"), new TilesetOptions { CustomShader = new CustomShaderOptions() });
        await ctx.Cesium.Tilesets.SetStyleAsync(ctx.Get<string>("tileset"), HeightStyle(h, ctx.Get<int>("tallFrom")));
        ctx.Log($"Style: colour by {h}.");
    }

    // <snippet>
    public override async Task RunAsync(SampleContext ctx)
    {
        var cesium = ctx.Cesium;
        string tileset, heightExpr;
        int tallFrom;

        if (ctx.HasIonToken)
        {
            // Cesium OSM Buildings: ~350 million buildings with an estimated height.
            tileset = await cesium.Tilesets.AddAsync(TilesetOptions.OsmBuildings());
            heightExpr = "${feature['cesium#estimatedHeight']}";
            tallFrom = 120;
            await cesium.Camera.FlyToAsync(106.8150, -6.2350, 1_800, heading: 20, pitch: -30, duration: 3);
        }
        else
        {
            // A small public sample tileset whose features carry a numeric "Height".
            tileset = await cesium.Tilesets.AddAsync(TilesetOptions.FromUrl(
                "https://raw.githubusercontent.com/CesiumGS/cesium/main/Apps/SampleData/Cesium3DTiles/Tilesets/Tileset/tileset.json"));
            heightExpr = "${Height}";
            tallFrom = 40;
            await cesium.Camera.FlyToObjectAsync(tileset, new HeadingPitchRange(20, -30, 420), duration: 2);
            ctx.Log("No ion token: using the Cesium sample tileset (add a token for OSM Buildings in Jakarta).");
        }
        ctx.State["tileset"] = tileset;
        ctx.State["heightExpr"] = heightExpr;
        ctx.State["tallFrom"] = tallFrom;

        await cesium.Tilesets.SetStyleAsync(tileset, HeightStyle(heightExpr, tallFrom));
        ctx.Log($"Styled by {heightExpr}. Click a building to read its properties.");
    }

    // Ordered conditions: the first one that matches decides the colour.
    private static TilesetStyle HeightStyle(string height, int tall) => new()
    {
        ColorConditions =
        [
            [$"{height} >= {tall * 2}", "color('#FF3B30')"],
            [$"{height} >= {tall}", "color('#FF9500')"],
            [$"{height} >= {tall / 2}", "color('#FFCC00')"],
            ["true", "color('#E8EEF5')"],
        ],
    };
    // </snippet>
}
