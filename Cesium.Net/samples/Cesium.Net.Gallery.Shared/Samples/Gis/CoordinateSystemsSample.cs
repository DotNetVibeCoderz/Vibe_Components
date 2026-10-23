namespace CesiumNet.Gallery.Samples;

[SampleOrder(3)]
public sealed class CoordinateSystemsSample : GallerySample
{
    public override string Id => "coordinate-systems";
    public override string CategoryId => "gis";
    public override Text Title => ("Coordinate systems", "Sistem koordinat");
    public override Text Summary => (
        "Click anywhere and the same point is converted in C# to WGS84 decimal degrees, DMS, ECEF, local ENU relative to Monas, Web Mercator and an XYZ map tile. A marker and a line from Monas show the point.",
        "Klik di mana saja dan titik yang sama dikonversi di C# ke derajat desimal WGS84, DMS, ECEF, ENU lokal relatif terhadap Monas, Web Mercator, dan tile peta XYZ. Penanda dan garis dari Monas menunjukkan titik tersebut.");
    public override GeoPoint? Location => GalleryDefaults.Monas;

    public override IReadOnlyList<SampleAction> Actions =>
    [
        new("bundaran", ("Convert Bundaran HI", "Konversi Bundaran HI"), ctx => Report(ctx, new GeoPoint(106.8230, -6.1950))),
        new("kota", ("Convert Kota Tua", "Konversi Kota Tua"), ctx => Report(ctx, new GeoPoint(106.8133, -6.1352))),
        new("clear", ("Clear markers", "Hapus penanda"), async ctx =>
        {
            await ctx.Cesium.Entities.ClearAsync();
            await AddMonas(ctx);
        }),
    ];

    private static Task AddMonas(SampleContext ctx) => ctx.Cesium.Entities.AddAsync(new EntityOptions
    {
        Name = "Monas (ENU origin)",
        Position = GalleryDefaults.Monas,
        Billboard = new BillboardGraphics { PinColor = "#F5C518", PinIcon = "monument" },
    }).AsTask();

    // <snippet>
    public override async Task RunAsync(SampleContext ctx)
    {
        var cesium = ctx.Cesium;
        await AddMonas(ctx);

        void OnClick(object? sender, PickResult pick)
        {
            if (pick.Position is { } p) _ = Report(ctx, p);
        }
        cesium.LeftClick += OnClick;
        ctx.Resources.Add(Disposable.Create(() => cesium.LeftClick -= OnClick));

        await cesium.Camera.FlyToAsync(106.8272, -6.2050, 6_000, pitch: -60, duration: 3);
        ctx.Log("Click anywhere on the map.");
    }

    private static async Task Report(SampleContext ctx, GeoPoint p)
    {
        var monas = GalleryDefaults.Monas;
        var ecef = CoordinateConverter.GeodeticToEcef(p);                 // Earth-centred, Earth-fixed
        var enu = CoordinateConverter.GeodeticToEnu(p.WithHeight(0), monas); // east/north/up from Monas
        var mercator = CoordinateConverter.GeodeticToWebMercator(p);      // EPSG:3857 metres
        var (tileX, tileY) = CoordinateConverter.GeodeticToTile(p, 15);   // slippy-map tile

        ctx.Log($"WGS84: {p.Latitude:F6}, {p.Longitude:F6}");
        ctx.Log($"DMS: {CoordinateConverter.ToDms(p.Latitude, true)} {CoordinateConverter.ToDms(p.Longitude, false)}");
        ctx.Log($"ECEF: X {ecef.X:N1} m, Y {ecef.Y:N1} m, Z {ecef.Z:N1} m");
        ctx.Log($"ENU from Monas: E {enu.X:N1} m, N {enu.Y:N1} m, U {enu.Z:N1} m " +
                $"({Geodesy.FormatDistance(Geodesy.Distance(monas, p))}, bearing {Geodesy.Bearing(monas, p):F1}°)");
        ctx.Log($"Web Mercator: x {mercator.X:N1}, y {mercator.Y:N1}");
        ctx.Log($"XYZ tile z15: {tileX}/{tileY}");

        await ctx.Cesium.Entities.AddRangeAsync(
        [
            new EntityOptions { Position = p, Point = new PointGraphics { PixelSize = 10, Color = CesiumColor.Cyan, OutlineColor = CesiumColor.Black, OutlineWidth = 2, AlwaysOnTop = true } },
            new EntityOptions { Polyline = new PolylineGraphics { Positions = [monas, p.WithHeight(0)], Width = 3, Material = CesiumMaterial.Dash(CesiumColor.Cyan), ClampToGround = true } },
        ]);
    }
    // </snippet>
}
