namespace CesiumNet.Gallery.Samples;

[SampleOrder(4)]
public sealed class SpatialQueriesSample : GallerySample
{
    public override string Id => "spatial-queries";
    public override string CategoryId => "gis";
    public override Text Title => ("Spatial queries", "Kueri spasial");
    public override Text Summary => (
        "300 random sensor points around North Jakarta and an illustrative flood-risk zone. C# tests which points fall inside the zone, finds the point nearest to wherever you click, and counts points within a radius.",
        "300 titik sensor acak di sekitar Jakarta Utara dan zona rawan banjir ilustratif. C# menguji titik mana yang berada di dalam zona, mencari titik terdekat dari lokasi yang Anda klik, dan menghitung titik dalam radius tertentu.");
    public override GeoPoint? Location => new(106.8400, -6.1500);

    // Illustrative zone along the coast of North Jakarta.
    private static readonly GeoPoint[] Zone =
    [
        new(106.7400, -6.1000), new(106.8200, -6.1050), new(106.8900, -6.0950), new(106.9600, -6.1050),
        new(106.9500, -6.1500), new(106.8800, -6.1600), new(106.8100, -6.1450), new(106.7500, -6.1400),
    ];

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("regen", ("New random points", "Titik acak baru"), ctx => Generate(ctx, Random.Shared.Next())),
        new("monas", ("Nearest to Monas", "Terdekat dari Monas"), ctx => Nearest(ctx, GalleryDefaults.Monas)),
        new("radius", ("Points within 3 km of Monas", "Titik dalam 3 km dari Monas"), async ctx =>
        {
            var points = ctx.Get<List<GeoPoint>>("points");
            var within = points.Count(p => Geodesy.Distance(GalleryDefaults.Monas, p) <= 3_000);
            await ctx.Cesium.Entities.AddAsync(new EntityOptions
            {
                Name = "3 km radius",
                Polyline = new PolylineGraphics { Positions = Geodesy.Circle(GalleryDefaults.Monas, 3_000), Width = 3, Material = CesiumMaterial.Dash(CesiumColor.Yellow), ClampToGround = true },
            });
            ctx.Log($"{within} of {points.Count} points lie within 3 km of Monas.");
        }),
    ];

    public override async Task RunAsync(SampleContext ctx)
    {
        void OnClick(object? sender, PickResult pick)
        {
            if (pick.Position is { } p) _ = Nearest(ctx, p);
        }
        ctx.Cesium.LeftClick += OnClick;
        ctx.Resources.Add(Disposable.Create(() => ctx.Cesium.LeftClick -= OnClick));

        await Generate(ctx, seed: 42);
        await ctx.Cesium.Camera.FlyToAsync(106.8500, -6.2600, 22_000, pitch: -55, duration: 3);
        ctx.Log("Click the map to find the nearest sensor.");
    }

    // <snippet>
    private static async Task Generate(SampleContext ctx, int seed)
    {
        var cesium = ctx.Cesium;
        await cesium.Entities.ClearAsync();

        // The zone polygon.
        await cesium.Entities.AddAsync(new EntityOptions
        {
            Name = "Flood-risk zone (illustrative)",
            Polygon = new PolygonGraphics { Positions = Zone, Height = 0, Material = CesiumColor.Red.WithAlpha(0.25f), Outline = true, OutlineColor = CesiumColor.Red },
        });

        // Random points; classify each one with a point-in-polygon test in C#.
        var random = new Random(seed);
        var points = Enumerable.Range(0, 300)
            .Select(_ => new GeoPoint(106.70 + random.NextDouble() * 0.30, -6.25 + random.NextDouble() * 0.17))
            .ToList();
        var inside = points.Select(p => Geodesy.Contains(Zone, p)).ToList();

        await cesium.Entities.AddRangeAsync(points.Select((p, i) => new EntityOptions
        {
            Id = $"sensor-{i}",
            Name = $"Sensor {i}",
            Position = p,
            Properties = new() { ["inZone"] = inside[i] ? "yes" : "no" },
            Point = new PointGraphics { PixelSize = inside[i] ? 9 : 6, Color = inside[i] ? CesiumColor.Red : CesiumColor.White, OutlineColor = CesiumColor.Black, OutlineWidth = 1 },
        }).ToList());

        ctx.State["points"] = points;
        ctx.Log($"{inside.Count(x => x)} of {points.Count} sensors are inside the zone " +
                $"({Geodesy.FormatArea(Geodesy.Area(Zone))}).");
    }

    private static async Task Nearest(SampleContext ctx, GeoPoint from)
    {
        var points = ctx.Get<List<GeoPoint>>("points");
        var (index, distance) = points
            .Select((p, i) => (Index: i, Distance: Geodesy.Distance(from, p)))
            .MinBy(x => x.Distance);

        if (ctx.Get<string?>("nearestLine") is { } old) await ctx.Cesium.Entities.RemoveAsync(old);
        ctx.State["nearestLine"] = await ctx.Cesium.Entities.AddAsync(new EntityOptions
        {
            Polyline = new PolylineGraphics { Positions = [from.WithHeight(0), points[index]], Width = 4, Material = CesiumMaterial.Glow(CesiumColor.Cyan), ClampToGround = true },
        });
        await ctx.Cesium.Entities.SelectAsync($"sensor-{index}");
        ctx.Log($"Nearest sensor: #{index}, {Geodesy.FormatDistance(distance)} away (bearing {Geodesy.Bearing(from, points[index]):F0}°).");
    }
    // </snippet>
}
