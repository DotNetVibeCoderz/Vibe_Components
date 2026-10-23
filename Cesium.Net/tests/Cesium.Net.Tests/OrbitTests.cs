using CesiumNet.Aerospace;

namespace CesiumNet.Tests;

public class OrbitTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Period_MatchesIssLikeOrbit()
    {
        var orbit = KeplerianOrbit.Circular(420_000, 51.6, 0, 0, Epoch);
        Assert.Equal(92.8, orbit.Period.TotalMinutes, 0.3);
    }

    [Fact]
    public void Circular_KeepsAltitudeAndInclination()
    {
        var orbit = KeplerianOrbit.Circular(550_000, 53, 30, 0, Epoch) with { IncludeJ2 = false };
        double maxLat = 0;
        foreach (var (_, p) in orbit.Sample(Epoch, Epoch + orbit.Period, TimeSpan.FromSeconds(30)))
        {
            // Height above the ellipsoid varies with latitude by up to ~21 km for a circular orbit.
            Assert.InRange(p.Height, 520_000, 575_000);
            maxLat = Math.Max(maxLat, Math.Abs(p.Latitude));
        }
        Assert.Equal(53, maxLat, 0.5);
    }

    [Fact]
    public void GroundTrack_DriftsWestwardEachOrbit()
    {
        var orbit = KeplerianOrbit.Circular(500_000, 97.4, 0, 0, Epoch) with { IncludeJ2 = false };
        var a = orbit.PositionAt(Epoch);
        var b = orbit.PositionAt(Epoch + orbit.Period);
        // Earth rotates ~0.25°/min under the satellite → ~23.7° westward shift per ~94.6 min orbit.
        var shift = GeoMath.NormalizeLongitude(a.Longitude - b.Longitude);
        Assert.Equal(23.7, shift, 0.5);
    }

    [Fact]
    public void GreenwichSiderealAngle_AtJ2000()
    {
        // GMST at 2000-01-01 12:00 UT1 is 280.46061837°.
        var gmst = KeplerianOrbit.GreenwichSiderealAngle(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(280.46061837, GeoMath.ToDegrees(gmst), 1e-4);
    }

    [Fact]
    public void FootprintRadius_IsReasonable()
    {
        // A 550 km satellite seen above 25° elevation covers a ~940 km radius.
        Assert.Equal(940_000, KeplerianOrbit.FootprintRadius(550_000, 25), 30_000d);
    }

    [Fact]
    public void ToSampledPositions_HasMatchingCounts()
    {
        var orbit = KeplerianOrbit.Circular(700_000, 98, 0, 0, Epoch);
        var s = orbit.ToSampledPositions(Epoch, Epoch.AddHours(1), TimeSpan.FromMinutes(1));
        Assert.Equal(61, s.Times.Count);
        Assert.Equal(s.Times.Count, s.Positions.Count);
        Assert.Equal(InterpolationKind.Lagrange, s.Interpolation);
    }
}
