namespace CesiumNet.Gallery.Samples;

[SampleOrder(2)]
public sealed class PickingSample : GallerySample
{
    public override string Id => "picking";
    public override string CategoryId => "gis";
    public override Text Title => ("Picking & properties", "Picking & properti");
    public override Text Summary => (
        "Public facilities in central Jakarta carry custom properties. Click a pin and the C# click handler logs its properties; click the ground and it logs the coordinates.",
        "Fasilitas umum di Jakarta Pusat membawa properti khusus. Klik pin dan handler klik C# mencatat propertinya; klik tanah dan handler mencatat koordinatnya.");
    public override GeoPoint? Location => new(106.8300, -6.1800);

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("select", ("Select a random facility", "Pilih fasilitas acak"), async ctx =>
        {
            var ids = ctx.Get<string[]>("ids");
            var id = ids[Random.Shared.Next(ids.Length)];
            await ctx.Cesium.Entities.SelectAsync(id);
            ctx.Log($"Selected {id} from C#.");
        }),
        new("deselect", ("Clear selection", "Hapus seleksi"), ctx => ctx.Cesium.Entities.SelectAsync(null).AsTask()),
        new("overview", ("Overview", "Tampilan umum"), ctx => ctx.Cesium.Camera.FlyToAsync(106.8300, -6.2050, 4_500, pitch: -50, duration: 2).AsTask()),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        // <snippet>
        var cesium = ctx.Cesium;
        var facilities = new (string Id, string Name, string Type, string Icon, CesiumColor Color, GeoPoint Position, Dictionary<string, string> Info)[]
        {
            ("rscm", "RSUPN Cipto Mangunkusumo", "Hospital", "hospital", CesiumColor.Red, new(106.8455, -6.1970),
                new() { ["beds"] = "1,000+", ["operator"] = "Ministry of Health", ["emergency"] = "24 h" }),
            ("gambir", "Stasiun Gambir", "Railway station", "rail", CesiumColor.Blue, new(106.8307, -6.1767),
                new() { ["lines"] = "Long distance", ["opened"] = "1884" }),
            ("istiqlal", "Masjid Istiqlal", "Mosque", "religious-muslim", CesiumColor.Green, new(106.8313, -6.1702),
                new() { ["capacity"] = "200,000", ["opened"] = "1978" }),
            ("katedral", "Gereja Katedral Jakarta", "Church", "religious-christian", CesiumColor.Purple, new(106.8333, -6.1691),
                new() { ["style"] = "Neo-Gothic", ["consecrated"] = "1901" }),
            ("perpusnas", "Perpustakaan Nasional", "Library", "library", CesiumColor.Orange, new(106.8270, -6.1813),
                new() { ["floors"] = "24", ["opened"] = "2017" }),
        };

        var ids = await cesium.Entities.AddRangeAsync(facilities.Select(f => new EntityOptions
        {
            Id = f.Id,
            Name = f.Name,
            Position = f.Position,
            // Properties travel with the entity and come back in PickResult.Properties.
            Properties = new Dictionary<string, string>(f.Info) { ["type"] = f.Type },
            Billboard = new BillboardGraphics { PinColor = f.Color, PinIcon = f.Icon },
        }).ToList());
        ctx.State["ids"] = ids;

        void OnClick(object? sender, PickResult pick)
        {
            if (pick.Kind == PickKind.Entity && pick.Properties is { } props)
                ctx.Log($"{pick.EntityName}: " + string.Join(", ", props.Select(kv => $"{kv.Key} = {kv.Value}")));
            else if (pick.Position is { } p)
                ctx.Log($"Ground at {p.Latitude:F5}, {p.Longitude:F5} — no facility here.");
        }
        cesium.LeftClick += OnClick;
        // Unsubscribe when the sample stops.
        ctx.Resources.Add(Disposable.Create(() => cesium.LeftClick -= OnClick));

        await cesium.Camera.FlyToAsync(106.8300, -6.2050, 4_500, pitch: -50, duration: 3);
        ctx.Log($"{ids.Length} facilities added — click one.");
        // </snippet>
    }
}
