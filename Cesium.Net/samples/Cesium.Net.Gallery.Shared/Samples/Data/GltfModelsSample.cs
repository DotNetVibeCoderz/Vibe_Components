namespace CesiumNet.Gallery.Samples;

[SampleOrder(1)]
public sealed class GltfModelsSample : GallerySample
{
    public override string Id => "gltf-models";
    public override string CategoryId => "data";
    public override Text Title => ("glTF models", "Model glTF");
    public override Text Summary => (
        "An aircraft, a truck and a hovering drone at Soekarno-Hatta Airport, loaded from glTF files with their built-in animations running. Focus on each model or toggle the selection silhouettes.",
        "Pesawat, truk, dan drone yang melayang di Bandara Soekarno-Hatta, dimuat dari berkas glTF dengan animasi bawaannya berjalan. Fokus ke tiap model atau nyalakan/matikan siluet seleksi.");
    public override GeoPoint? Location => new(106.6559, -6.1256);

    private const string Models = "https://raw.githubusercontent.com/CesiumGS/cesium/main/Apps/SampleData/models/";

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("aircraft", ("Focus aircraft", "Fokus pesawat"), ctx => Focus(ctx, "aircraft", 40)),
        new("truck", ("Focus truck", "Fokus truk"), ctx => Focus(ctx, "truck", 18)),
        new("drone", ("Focus drone", "Fokus drone"), ctx => Focus(ctx, "drone", 12)),
        new("silhouettes", ("Toggle silhouettes", "Siluet aktif/nonaktif"), async ctx =>
        {
            var on = !ctx.Get("silhouettes", true);
            ctx.State["silhouettes"] = on;
            foreach (var (id, uri, color) in Fleet)
                await ctx.Cesium.Entities.UpdateAsync(id, new EntityOptions { Model = new ModelGraphics { Uri = uri, RunAnimations = true, MinimumPixelSize = 48, SilhouetteColor = color, SilhouetteSize = on ? 2.5 : 0 } });
            ctx.Log(on ? "Silhouettes on." : "Silhouettes off.");
        }),
    ];

    private static readonly (string Id, string Uri, CesiumColor Silhouette)[] Fleet =
    [
        ("aircraft", Models + "CesiumAir/Cesium_Air.glb", CesiumColor.Yellow),
        ("truck", Models + "CesiumMilkTruck/CesiumMilkTruck.glb", CesiumColor.Cyan),
        ("drone", Models + "CesiumDrone/CesiumDrone.glb", CesiumColor.Magenta),
    ];

    private static async Task Focus(SampleContext ctx, string id, double range)
    {
        await ctx.Cesium.Camera.FlyToObjectAsync(id, new HeadingPitchRange(45, -20, range), duration: 2);
        ctx.Log($"Focused on the {id}.");
    }

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        var apron = new GeoPoint(106.6559, -6.1256);
        GeoPoint At(double east, double north, double up = 0)
            => CoordinateConverter.EnuToGeodetic(new Cartesian3(east, north, up), apron);

        var placements = new[]
        {
            (Id: "aircraft", Name: "Cesium Air", Position: At(0, 0, 2.5), Heading: 60.0),
            (Id: "truck", Name: "Milk truck", Position: At(22, -14), Heading: 150.0),
            (Id: "drone", Name: "Drone", Position: At(-16, 10, 12), Heading: 0.0),
        };
        for (var i = 0; i < placements.Length; i++)
        {
            var p = placements[i];
            await cesium.Entities.AddAsync(new EntityOptions
            {
                Id = p.Id,
                Name = p.Name,
                Position = p.Position,
                Orientation = new HeadingPitchRoll(p.Heading),
                Model = new ModelGraphics
                {
                    Uri = Fleet[i].Uri,
                    MinimumPixelSize = 48,      // stays visible when zoomed out
                    RunAnimations = true,       // propellers and wheels
                    SilhouetteColor = Fleet[i].Silhouette,
                    SilhouetteSize = 2.5,
                },
            });
        }

        // glTF animations advance with the simulation clock.
        await cesium.Clock.PlayAsync();
        await cesium.Camera.FlyToAsync(106.6556, -6.1262, 60, heading: 20, pitch: -22, duration: 3);
        ctx.Log("Three glTF models loaded with animations running.");
        // </snippet>
    }
}
