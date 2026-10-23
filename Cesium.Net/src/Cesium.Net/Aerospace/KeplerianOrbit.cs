namespace CesiumNet.Aerospace;

/// <summary>
/// Classical Keplerian orbital elements with a two-body propagator (plus J2 nodal regression).
/// Produces Earth-fixed positions for satellites, ground tracks and coverage analysis.
/// Adequate for visualization; use SGP4 with TLEs for operational accuracy.
/// </summary>
public sealed record KeplerianOrbit
{
    /// <summary>Earth gravitational parameter (m³/s²).</summary>
    public const double Mu = 3.986004418e14;
    /// <summary>Earth J2 zonal harmonic.</summary>
    public const double J2 = 1.08262668e-3;
    /// <summary>Earth rotation rate (rad/s).</summary>
    public const double EarthRotationRate = 7.2921150e-5;

    /// <summary>Semi-major axis in meters.</summary>
    public required double SemiMajorAxis { get; init; }
    public double Eccentricity { get; init; }
    /// <summary>Inclination in degrees.</summary>
    public double Inclination { get; init; }
    /// <summary>Right ascension of the ascending node in degrees.</summary>
    public double RightAscension { get; init; }
    /// <summary>Argument of perigee in degrees.</summary>
    public double ArgumentOfPerigee { get; init; }
    /// <summary>Mean anomaly at epoch in degrees.</summary>
    public double MeanAnomaly { get; init; }
    public required DateTimeOffset Epoch { get; init; }
    /// <summary>Apply J2 secular drift of the node and perigee.</summary>
    public bool IncludeJ2 { get; init; } = true;

    /// <summary>Orbital period.</summary>
    public TimeSpan Period => TimeSpan.FromSeconds(GeoMath.TwoPi * System.Math.Sqrt(System.Math.Pow(SemiMajorAxis, 3) / Mu));

    /// <summary>Creates a circular orbit from altitude (meters above the equatorial radius).</summary>
    public static KeplerianOrbit Circular(double altitude, double inclination, double rightAscension, double phase, DateTimeOffset epoch)
        => new() { SemiMajorAxis = Ellipsoid.Wgs84.SemiMajorAxis + altitude, Inclination = inclination, RightAscension = rightAscension, MeanAnomaly = phase, Epoch = epoch };

    /// <summary>Position in the Earth-centered inertial frame (meters) at a time.</summary>
    public Cartesian3 InertialPosition(DateTimeOffset time)
    {
        var dt = (time - Epoch).TotalSeconds;
        var a = SemiMajorAxis;
        var e = Eccentricity;
        var n = System.Math.Sqrt(Mu / (a * a * a));
        var i = GeoMath.ToRadians(Inclination);
        var raan = GeoMath.ToRadians(RightAscension);
        var argp = GeoMath.ToRadians(ArgumentOfPerigee);

        if (IncludeJ2)
        {
            var p = a * (1 - e * e);
            var re = Ellipsoid.Wgs84.SemiMajorAxis;
            var factor = 1.5 * n * J2 * (re / p) * (re / p);
            raan += -factor * System.Math.Cos(i) * dt;
            argp += factor * (2 - 2.5 * System.Math.Sin(i) * System.Math.Sin(i)) * dt;
        }

        var m = GeoMath.ToRadians(MeanAnomaly) + n * dt;
        var ea = SolveKepler(m, e);
        var nu = 2 * System.Math.Atan2(System.Math.Sqrt(1 + e) * System.Math.Sin(ea / 2), System.Math.Sqrt(1 - e) * System.Math.Cos(ea / 2));
        var r = a * (1 - e * System.Math.Cos(ea));

        var u = argp + nu;
        double cosU = System.Math.Cos(u), sinU = System.Math.Sin(u);
        double cosO = System.Math.Cos(raan), sinO = System.Math.Sin(raan);
        double cosI = System.Math.Cos(i), sinI = System.Math.Sin(i);
        return new Cartesian3(
            r * (cosO * cosU - sinO * sinU * cosI),
            r * (sinO * cosU + cosO * sinU * cosI),
            r * (sinU * sinI));
    }

    /// <summary>Earth-fixed geodetic position (degrees, meters) at a time.</summary>
    public GeoPoint PositionAt(DateTimeOffset time)
    {
        var eci = InertialPosition(time);
        var gmst = GreenwichSiderealAngle(time);
        double c = System.Math.Cos(gmst), s = System.Math.Sin(gmst);
        var ecef = new Cartesian3(c * eci.X + s * eci.Y, -s * eci.X + c * eci.Y, eci.Z);
        return ecef.ToGeoPoint();
    }

    /// <summary>Samples the orbit between two times.</summary>
    public IReadOnlyList<(DateTimeOffset Time, GeoPoint Position)> Sample(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
        var list = new List<(DateTimeOffset, GeoPoint)>((int)((stop - start) / step) + 1);
        for (var t = start; t <= stop; t += step) list.Add((t, PositionAt(t)));
        return list;
    }

    /// <summary>Builds <see cref="SampledPositions"/> for an entity.</summary>
    public SampledPositions ToSampledPositions(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)
        => SampledPositions.From(Sample(start, stop, step), InterpolationKind.Lagrange, 5);

    /// <summary>Sub-satellite ground track points (height 0).</summary>
    public GeoPoint[] GroundTrack(DateTimeOffset start, DateTimeOffset stop, TimeSpan step)
        => Sample(start, stop, step).Select(s => s.Position.WithHeight(0)).ToArray();

    /// <summary>Radius (meters) of the ground footprint visible above <paramref name="minElevation"/> degrees.</summary>
    public static double FootprintRadius(double altitude, double minElevation = 10)
    {
        var re = Geodesy.MeanEarthRadius;
        var el = GeoMath.ToRadians(minElevation);
        var eta = System.Math.Asin(re / (re + altitude) * System.Math.Cos(el));
        var lambda = System.Math.PI / 2 - el - eta;
        return re * lambda;
    }

    /// <summary>Greenwich mean sidereal angle (radians) for a UTC time (IAU 1982).</summary>
    public static double GreenwichSiderealAngle(DateTimeOffset time)
    {
        var jd = time.UtcDateTime.ToOADate() + 2415018.5;
        var t = (jd - 2451545.0) / 36525.0;
        var gmstSeconds = 67310.54841 + (876600.0 * 3600 + 8640184.812866) * t + 0.093104 * t * t - 6.2e-6 * t * t * t;
        var rad = GeoMath.ToRadians(gmstSeconds % 86400 / 240.0);
        rad %= GeoMath.TwoPi;
        return rad < 0 ? rad + GeoMath.TwoPi : rad;
    }

    private static double SolveKepler(double m, double e)
    {
        m %= GeoMath.TwoPi;
        var ea = e < 0.8 ? m : System.Math.PI;
        for (var k = 0; k < 15; k++)
        {
            var d = (ea - e * System.Math.Sin(ea) - m) / (1 - e * System.Math.Cos(ea));
            ea -= d;
            if (System.Math.Abs(d) < 1e-12) break;
        }
        return ea;
    }
}
