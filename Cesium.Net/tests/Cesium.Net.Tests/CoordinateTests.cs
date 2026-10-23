namespace CesiumNet.Tests;

public class CoordinateTests
{
    [Fact]
    public void Ecef_AtEquatorPrimeMeridian()
    {
        var c = CoordinateConverter.GeodeticToEcef(new GeoPoint(0, 0, 0));
        Assert.Equal(6378137.0, c.X, 1e-6);
        Assert.Equal(0, c.Y, 1e-6);
        Assert.Equal(0, c.Z, 1e-6);
    }

    [Fact]
    public void Ecef_AtNorthPole()
    {
        var c = CoordinateConverter.GeodeticToEcef(new GeoPoint(0, 90, 0));
        Assert.Equal(Ellipsoid.Wgs84.SemiMinorAxis, c.Z, 1e-6);
    }

    [Theory]
    [InlineData(106.827153, -6.175392, 15.0)]
    [InlineData(-122.4194, 37.7749, 2_000.0)]
    [InlineData(10.0, 89.999, 100.0)]
    [InlineData(-45.0, -89.5, 8_848.0)]
    [InlineData(179.9, 0.0, 400_000.0)]
    public void Ecef_RoundTrip(double lon, double lat, double h)
    {
        var p = new GeoPoint(lon, lat, h);
        var back = CoordinateConverter.EcefToGeodetic(CoordinateConverter.GeodeticToEcef(p));
        Assert.Equal(lon, back.Longitude, 1e-9);
        Assert.Equal(lat, back.Latitude, 1e-9);
        Assert.Equal(h, back.Height, 1e-4);
    }

    [Fact]
    public void Enu_RoundTripAndAxes()
    {
        var origin = new GeoPoint(106.827153, -6.175392, 0);
        var east = CoordinateConverter.EnuToGeodetic(new Cartesian3(1000, 0, 0), origin);
        var north = CoordinateConverter.EnuToGeodetic(new Cartesian3(0, 1000, 0), origin);
        Assert.True(east.Longitude > origin.Longitude);
        Assert.True(north.Latitude > origin.Latitude);

        var enu = CoordinateConverter.GeodeticToEnu(east, origin);
        Assert.Equal(1000, enu.X, 1e-6);
        Assert.Equal(0, enu.Y, 1e-6);
        Assert.Equal(0, enu.Z, 1e-6);
    }

    [Fact]
    public void WebMercator_KnownValuesAndRoundTrip()
    {
        var m = CoordinateConverter.GeodeticToWebMercator(new GeoPoint(180, 0));
        Assert.Equal(20037508.342789244, m.X, 1e-6);
        Assert.Equal(0, m.Y, 1e-6);

        var p = new GeoPoint(106.827153, -6.175392);
        var back = CoordinateConverter.WebMercatorToGeodetic(CoordinateConverter.GeodeticToWebMercator(p));
        Assert.Equal(p.Longitude, back.Longitude, 1e-9);
        Assert.Equal(p.Latitude, back.Latitude, 1e-9);
    }

    [Fact]
    public void Tile_ForMonasAtZoom10()
    {
        // Matches the OSM slippy-map formula used by tile.openstreetmap.org.
        var (x, y) = CoordinateConverter.GeodeticToTile(new GeoPoint(106.827153, -6.175392), 10);
        Assert.Equal(815, x);
        Assert.Equal(529, y);
    }

    [Theory]
    [InlineData(-6.175392, true, "6°10'31.4\"S")]
    [InlineData(106.827153, false, "106°49'37.8\"E")]
    [InlineData(-6.9999999, true, "7°00'00.0\"S")]
    public void Dms(double value, bool isLat, string expected) => Assert.Equal(expected, CoordinateConverter.ToDms(value, isLat));

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(180, -180)]
    [InlineData(45, 45)]
    public void NormalizeLongitude(double input, double expected) => Assert.Equal(expected, GeoMath.NormalizeLongitude(input), 1e-12);

    [Fact]
    public void AuthalicRadius_IsWgs84Value() => Assert.Equal(6371007.181, Ellipsoid.Wgs84.AuthalicRadius, 1e-3);

    [Fact]
    public void Cartesian_VectorMath()
    {
        var a = new Cartesian3(1, 0, 0);
        var b = new Cartesian3(0, 1, 0);
        Assert.Equal(new Cartesian3(0, 0, 1), Cartesian3.Cross(a, b));
        Assert.Equal(0, Cartesian3.Dot(a, b));
        Assert.Equal(Math.PI / 2, Cartesian3.AngleBetween(a, b), 1e-12);
        Assert.Equal(1, new Cartesian3(3, 4, 0).Normalize().Magnitude, 1e-12);
    }

    [Fact]
    public void GeoRectangle_FromPoints()
    {
        var r = GeoRectangle.FromPoints([new GeoPoint(106, -7), new GeoPoint(108, -5), new GeoPoint(107, -6)]);
        Assert.Equal(new GeoRectangle(106, -7, 108, -5), r);
        Assert.True(r.Contains(new GeoPoint(107, -6)));
        Assert.Equal(new GeoPoint(107, -6), r.Center);
    }
}
