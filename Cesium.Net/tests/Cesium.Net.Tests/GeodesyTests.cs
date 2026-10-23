namespace CesiumNet.Tests;

public class GeodesyTests
{
    // Vincenty's published test case: Flinders Peak → Buninyong (Australia).
    private static readonly GeoPoint FlindersPeak = new(144.42486788888889, -37.95103341666667);
    private static readonly GeoPoint Buninyong = new(143.92649552777778, -37.65282113888889);

    [Fact]
    public void Inverse_MatchesVincentyReference()
    {
        var r = Geodesy.Inverse(FlindersPeak, Buninyong);
        Assert.Equal(54972.271, r.Distance, 0.01);
        Assert.Equal(306.8681583, r.InitialBearing, 1e-5); // 306°52'05.37"
        Assert.Equal(307.1736306, r.FinalBearing, 1e-5);   // 307°10'25.07"
    }

    [Fact]
    public void Destination_IsInverseOfInverse()
    {
        var r = Geodesy.Inverse(FlindersPeak, Buninyong);
        var p = Geodesy.Destination(FlindersPeak, r.InitialBearing, r.Distance);
        Assert.Equal(Buninyong.Latitude, p.Latitude, 1e-8);
        Assert.Equal(Buninyong.Longitude, p.Longitude, 1e-8);
    }

    [Fact]
    public void Distance_IdenticalPoints_IsZero() => Assert.Equal(0, Geodesy.Distance(FlindersPeak, FlindersPeak));

    [Fact]
    public void Haversine_IsCloseToEllipsoidal()
    {
        var jakarta = new GeoPoint(106.8456, -6.2088);
        var surabaya = new GeoPoint(112.7521, -7.2575);
        var ellipsoidal = Geodesy.Distance(jakarta, surabaya);
        var spherical = Geodesy.HaversineDistance(jakarta, surabaya);
        Assert.InRange(ellipsoidal, 655_000, 665_000);
        Assert.InRange(Math.Abs(ellipsoidal - spherical) / ellipsoidal, 0, 0.006);
    }

    [Fact]
    public void Inverse_NearlyAntipodal_DoesNotThrow()
    {
        var r = Geodesy.Inverse(new GeoPoint(0, 0), new GeoPoint(179.7, 0.3));
        Assert.InRange(r.Distance, 19_900_000, 20_100_000);
    }

    [Fact]
    public void Area_OneDegreeCellAtEquator()
    {
        // A 1°×1° cell at the equator is ~12,364 km² on WGS84.
        GeoPoint[] cell = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Assert.Equal(12_364e6, Geodesy.Area(cell), 0.005 * 12_364e6);
    }

    [Fact]
    public void Area_IsOrientationIndependent()
    {
        GeoPoint[] ring = [new(106.80, -6.20), new(106.85, -6.20), new(106.85, -6.15), new(106.80, -6.15)];
        Assert.Equal(Geodesy.Area(ring), Geodesy.Area(ring.Reverse().ToArray()), 1e-6);
    }

    [Fact]
    public void Circle_HasRequestedRadius()
    {
        var center = new GeoPoint(110.2038, -7.6079); // Borobudur
        foreach (var p in Geodesy.Circle(center, 5_000, 16))
            Assert.Equal(5_000, Geodesy.Distance(center, p), 0.01);
    }

    [Fact]
    public void Interpolate_ProducesEvenSpacing()
    {
        var a = new GeoPoint(106.8, -6.2, 0);
        var b = new GeoPoint(112.7, -7.3, 1000);
        var pts = Geodesy.Interpolate(a, b, 11);
        Assert.Equal(11, pts.Length);
        Assert.Equal(a, pts[0]);
        Assert.Equal(b, pts[^1]);
        var step = Geodesy.Distance(a, b) / 10;
        for (var i = 1; i < pts.Length; i++)
            Assert.Equal(step, Geodesy.Distance(pts[i - 1], pts[i]), 0.5);
        Assert.Equal(500, pts[5].Height, 1e-9);
    }

    [Fact]
    public void Contains_PointInPolygon()
    {
        GeoPoint[] square = [new(0, 0), new(2, 0), new(2, 2), new(0, 2)];
        Assert.True(Geodesy.Contains(square, new GeoPoint(1, 1)));
        Assert.False(Geodesy.Contains(square, new GeoPoint(3, 1)));
    }

    [Fact]
    public void PathLength_AndPerimeter()
    {
        GeoPoint[] square = [new(0, 0), new(0.01, 0), new(0.01, 0.01), new(0, 0.01)];
        var open = Geodesy.PathLength(square);
        var closed = Geodesy.Perimeter(square);
        Assert.True(closed > open);
        Assert.Equal(open + Geodesy.Distance(square[^1], square[0]), closed, 1e-6);
    }

    [Theory]
    [InlineData(850, "850.0 m")]
    [InlineData(12_400, "12.40 km")]
    public void FormatDistance(double meters, string expected) => Assert.Equal(expected, Geodesy.FormatDistance(meters));

    [Theory]
    [InlineData(500, "500.0 m²")]
    [InlineData(25_000, "2.50 ha")]
    [InlineData(3_000_000, "3.00 km²")]
    public void FormatArea(double m2, string expected) => Assert.Equal(expected, Geodesy.FormatArea(m2));
}
