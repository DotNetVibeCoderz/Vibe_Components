namespace GeoPandas.Net.Geometry.Crs;

/// <summary>Reference ellipsoid defined by semi-major axis and inverse flattening.</summary>
public sealed record Ellipsoid(string Name, double A, double InverseFlattening)
{
    /// <summary>Flattening f. Zero for a sphere.</summary>
    public double F => InverseFlattening == 0 ? 0 : 1.0 / InverseFlattening;
    /// <summary>Semi-minor axis b.</summary>
    public double B => A * (1 - F);
    /// <summary>First eccentricity squared e².</summary>
    public double E2 => F * (2 - F);
    /// <summary>First eccentricity e.</summary>
    public double E => Math.Sqrt(E2);
    public bool IsSphere => InverseFlattening == 0;

    public static Ellipsoid Wgs84 { get; } = new("WGS 84", 6378137.0, 298.257223563);
    public static Ellipsoid Grs80 { get; } = new("GRS 1980", 6378137.0, 298.257222101);
    public static Ellipsoid Clarke1866 { get; } = new("Clarke 1866", 6378206.4, 294.9786982139);
    public static Ellipsoid Clarke1880 { get; } = new("Clarke 1880 (RGS)", 6378249.145, 293.465);
    public static Ellipsoid Bessel1841 { get; } = new("Bessel 1841", 6377397.155, 299.1528128);
    public static Ellipsoid International1924 { get; } = new("International 1924", 6378388.0, 297.0);
    public static Ellipsoid Airy1830 { get; } = new("Airy 1830", 6377563.396, 299.3249646);
    public static Ellipsoid Grs67 { get; } = new("GRS 1967", 6378160.0, 298.247167427);
    public static Ellipsoid Krassowsky1940 { get; } = new("Krassowsky 1940", 6378245.0, 298.3);
    public static Ellipsoid Sphere { get; } = new("Popular Visualisation Sphere", 6378137.0, 0);

    /// <summary>Creates from axes, deriving inverse flattening (0 if a == b).</summary>
    public static Ellipsoid FromAxes(string name, double a, double b) =>
        new(name, a, Math.Abs(a - b) < 1e-9 ? 0 : a / (a - b));

    /// <summary>Look up an ellipsoid by PROJ.4 / WKT name.</summary>
    public static Ellipsoid? FromName(string name)
    {
        var n = Normalize(name);
        return n switch
        {
            "wgs84" or "wgs1984" => Wgs84,
            "grs80" or "grs1980" => Grs80,
            "clrk66" or "clarke1866" => Clarke1866,
            "clrk80" or "clarke1880" or "clarke1880rgs" => Clarke1880,
            "bessel" or "bessel1841" => Bessel1841,
            "intl" or "international1924" or "international1909" or "hayford" => International1924,
            "airy" or "airy1830" => Airy1830,
            "grs67" or "grs1967" => Grs67,
            "krass" or "krassowsky1940" or "krassovsky1940" => Krassowsky1940,
            _ => null
        };
    }

    internal static string Normalize(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int k = 0;
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) buf[k++] = char.ToLowerInvariant(ch);
        return new string(buf[..k]);
    }
}

/// <summary>Geodetic datum: an ellipsoid plus an optional 7-parameter Helmert shift to WGS 84
/// (position-vector convention, metres / arc-seconds / ppm, same as PROJ <c>+towgs84</c>).</summary>
public sealed record Datum(string Name, Ellipsoid Ellipsoid, double[]? ToWgs84 = null)
{
    public static Datum Wgs84 { get; } = new("WGS_1984", Ellipsoid.Wgs84);

    /// <summary>Grid shift to WGS 84 / the grid's target datum (NTv2), used instead of <see cref="ToWgs84"/> when set (PROJ <c>+nadgrids</c>).</summary>
    public HorizontalGridShift? GridShift { get; init; }

    /// <summary>True when no datum shift is needed relative to WGS 84.</summary>
    public bool IsWgs84Compatible => GridShift is null && (ToWgs84 is null || ToWgs84.All(v => v == 0));

    public bool Equals(Datum? other) =>
        other is not null && Ellipsoid == other.Ellipsoid && ReferenceEquals(GridShift, other.GridShift) &&
        (ToWgs84 ?? new double[7]).Take(7).SequenceEqual((other.ToWgs84 ?? new double[7]).Take(7));

    public override int GetHashCode() => HashCode.Combine(Ellipsoid, ToWgs84 is null ? 0 : ToWgs84.Sum());
}
