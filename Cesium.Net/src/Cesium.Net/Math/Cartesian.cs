using System.Globalization;
using System.Runtime.CompilerServices;

namespace CesiumNet;

/// <summary>A 2D Cartesian point or vector (e.g. a screen position in pixels).</summary>
public readonly record struct Cartesian2(double X, double Y)
{
    public static readonly Cartesian2 Zero = new(0, 0);
    public double Magnitude => System.Math.Sqrt(X * X + Y * Y);
    public static Cartesian2 operator +(Cartesian2 a, Cartesian2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Cartesian2 operator -(Cartesian2 a, Cartesian2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Cartesian2 operator *(Cartesian2 a, double s) => new(a.X * s, a.Y * s);
    public static double Dot(Cartesian2 a, Cartesian2 b) => a.X * b.X + a.Y * b.Y;
    public static double Distance(Cartesian2 a, Cartesian2 b) => (a - b).Magnitude;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X}, {Y})");
}

/// <summary>A 3D Cartesian point or vector. ECEF positions are in meters.</summary>
public readonly record struct Cartesian3(double X, double Y, double Z)
{
    public static readonly Cartesian3 Zero = new(0, 0, 0);
    public static readonly Cartesian3 UnitX = new(1, 0, 0);
    public static readonly Cartesian3 UnitY = new(0, 1, 0);
    public static readonly Cartesian3 UnitZ = new(0, 0, 1);

    public double MagnitudeSquared
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => X * X + Y * Y + Z * Z;
    }

    public double Magnitude => System.Math.Sqrt(MagnitudeSquared);

    public Cartesian3 Normalize()
    {
        var m = Magnitude;
        return m == 0 ? Zero : new(X / m, Y / m, Z / m);
    }

    public static Cartesian3 operator +(Cartesian3 a, Cartesian3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Cartesian3 operator -(Cartesian3 a, Cartesian3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Cartesian3 operator -(Cartesian3 a) => new(-a.X, -a.Y, -a.Z);
    public static Cartesian3 operator *(Cartesian3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Cartesian3 operator *(double s, Cartesian3 a) => a * s;
    public static Cartesian3 operator /(Cartesian3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Cartesian3 a, Cartesian3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Cartesian3 Cross(Cartesian3 a, Cartesian3 b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static double Distance(Cartesian3 a, Cartesian3 b) => (a - b).Magnitude;

    public static Cartesian3 Lerp(Cartesian3 a, Cartesian3 b, double t) => a + (b - a) * t;

    /// <summary>Angle between two vectors in radians.</summary>
    public static double AngleBetween(Cartesian3 a, Cartesian3 b)
    {
        var c = Cross(a, b).Magnitude;
        return System.Math.Atan2(c, Dot(a, b));
    }

    /// <summary>Converts this ECEF position to a geodetic <see cref="GeoPoint"/> on WGS84.</summary>
    public GeoPoint ToGeoPoint() => CoordinateConverter.EcefToGeodetic(this);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X}, {Y}, {Z})");
}

/// <summary>A 4D Cartesian vector (e.g. homogeneous coordinates or RGBA).</summary>
public readonly record struct Cartesian4(double X, double Y, double Z, double W)
{
    public static readonly Cartesian4 Zero = new(0, 0, 0, 0);
    public double Magnitude => System.Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
    public static Cartesian4 operator +(Cartesian4 a, Cartesian4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
    public static Cartesian4 operator -(Cartesian4 a, Cartesian4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
    public static Cartesian4 operator *(Cartesian4 a, double s) => new(a.X * s, a.Y * s, a.Z * s, a.W * s);
    public static double Dot(Cartesian4 a, Cartesian4 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X}, {Y}, {Z}, {W})");
}

/// <summary>A geodetic position in <b>radians</b> (longitude, latitude) and meters (height), as in CesiumJS.</summary>
public readonly record struct Cartographic(double Longitude, double Latitude, double Height = 0)
{
    public static Cartographic FromDegrees(double longitude, double latitude, double height = 0)
        => new(GeoMath.ToRadians(longitude), GeoMath.ToRadians(latitude), height);

    public GeoPoint ToGeoPoint() => GeoPoint.FromRadians(Longitude, Latitude, Height);
}

/// <summary>Orientation angles in <b>degrees</b>. Heading is clockwise from north, pitch is up from the horizon.</summary>
public readonly record struct HeadingPitchRoll(double Heading, double Pitch = 0, double Roll = 0);

/// <summary>Camera offset relative to a target: heading/pitch in degrees, range in meters.</summary>
public readonly record struct HeadingPitchRange(double Heading, double Pitch, double Range);

/// <summary>An axis-aligned geographic rectangle in degrees.</summary>
public readonly record struct GeoRectangle(double West, double South, double East, double North)
{
    public GeoPoint Center => new((West + East) / 2, (South + North) / 2);
    public bool Contains(GeoPoint p) => p.Longitude >= West && p.Longitude <= East && p.Latitude >= South && p.Latitude <= North;

    public static GeoRectangle FromPoints(IEnumerable<GeoPoint> points)
    {
        double w = double.MaxValue, s = double.MaxValue, e = double.MinValue, n = double.MinValue;
        foreach (var p in points)
        {
            w = System.Math.Min(w, p.Longitude); e = System.Math.Max(e, p.Longitude);
            s = System.Math.Min(s, p.Latitude); n = System.Math.Max(n, p.Latitude);
        }
        if (w == double.MaxValue) throw new ArgumentException("At least one point is required.", nameof(points));
        return new(w, s, e, n);
    }
}

/// <summary>Math helpers shared by the geodesy routines.</summary>
public static class GeoMath
{
    public const double DegreesPerRadian = 180.0 / System.Math.PI;
    public const double RadiansPerDegree = System.Math.PI / 180.0;
    public const double TwoPi = 2 * System.Math.PI;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToRadians(double degrees) => degrees * RadiansPerDegree;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDegrees(double radians) => radians * DegreesPerRadian;

    /// <summary>Normalizes a longitude in degrees into [-180, 180).</summary>
    public static double NormalizeLongitude(double degrees)
    {
        var x = (degrees + 180) % 360;
        if (x < 0) x += 360;
        return x - 180;
    }

    /// <summary>Normalizes an angle in degrees into [0, 360).</summary>
    public static double NormalizeBearing(double degrees)
    {
        var x = degrees % 360;
        return x < 0 ? x + 360 : x;
    }
}
