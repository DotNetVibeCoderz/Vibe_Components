using System.Globalization;

namespace Electrical.Visualization;

/// <summary>8-bit RGBA color.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Hex notation <c>#rrggbb</c> (alpha ignored).</summary>
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>Parses <c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>.</summary>
    public static Rgba Parse(string hex)
    {
        var s = hex.AsSpan().TrimStart('#');
        static byte P(ReadOnlySpan<char> c) => byte.Parse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return s.Length switch
        {
            3 => new((byte)(P(s[..1]) * 17), (byte)(P(s[1..2]) * 17), (byte)(P(s[2..3]) * 17)),
            6 => new(P(s[..2]), P(s[2..4]), P(s[4..6])),
            8 => new(P(s[..2]), P(s[2..4]), P(s[4..6]), P(s[6..8])),
            _ => throw new FormatException($"Invalid color '{hex}'."),
        };
    }

    /// <summary>Linear interpolation in sRGB space (t in [0,1]).</summary>
    public static Rgba Lerp(Rgba a, Rgba b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static byte L(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return new(L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t), L(a.A, b.A, t));
    }

    public override string ToString() => Hex;
}

/// <summary>Piecewise-linear color ramp over a value domain.</summary>
public sealed class ColorRamp
{
    private readonly (double Pos, Rgba Color)[] _stops;

    /// <summary>Creates a ramp from stops (positions in domain units, any order).</summary>
    public ColorRamp(string name, params (double Position, Rgba Color)[] stops)
    {
        if (stops.Length < 2) throw new ArgumentException("A ramp needs at least two stops.", nameof(stops));
        Name = name;
        _stops = stops.OrderBy(s => s.Position).ToArray();
    }

    /// <summary>Creates a ramp from hex colors evenly spread over [min, max].</summary>
    public static ColorRamp FromHex(string name, double min, double max, params string[] colors) =>
        new(name, colors.Select((c, i) => (min + (max - min) * i / (colors.Length - 1), Rgba.Parse(c))).ToArray());

    public string Name { get; }
    /// <summary>Lowest stop position.</summary>
    public double Min => _stops[0].Pos;
    /// <summary>Highest stop position.</summary>
    public double Max => _stops[^1].Pos;
    public IReadOnlyList<(double Position, Rgba Color)> Stops => _stops;

    /// <summary>Color at <paramref name="value"/> (clamped to the domain; NaN maps to mid-grey).</summary>
    public Rgba Map(double value)
    {
        if (double.IsNaN(value)) return new Rgba(128, 128, 128);
        if (value <= _stops[0].Pos) return _stops[0].Color;
        if (value >= _stops[^1].Pos) return _stops[^1].Color;
        for (int i = 1; i < _stops.Length; i++)
            if (value <= _stops[i].Pos)
            {
                var (p0, c0) = _stops[i - 1];
                var (p1, c1) = _stops[i];
                return Rgba.Lerp(c0, c1, (value - p0) / (p1 - p0));
            }
        return _stops[^1].Color;
    }

    /// <summary>Hex color at <paramref name="value"/>.</summary>
    public string Hex(double value) => Map(value).Hex;

    /// <summary>Ramp re-scaled to a new domain (same colors, positions mapped linearly).</summary>
    public ColorRamp Rescale(double min, double max) =>
        new(Name, _stops.Select(s => (min + (s.Pos - Min) / (Max - Min) * (max - min), s.Color)).ToArray());

    /// <summary>Voltage ramp: diverging around 1.0 pu, blue (low) → green (nominal) → red (high), saturating at 1 ± <paramref name="span"/> pu.</summary>
    public static ColorRamp Voltage(double span = 0.1) =>
        FromHex("voltage", 1 - span, 1 + span, "#2563eb", "#38bdf8", "#2bb673", "#f5a524", "#e5484d");

    /// <summary>Branch loading ramp [%]: green (0 %) → amber (70 %) → red (100 %) → dark red (≥ 120 %).</summary>
    public static ColorRamp Loading { get; } = new("loading",
        (0, Rgba.Parse("#2bb673")), (50, Rgba.Parse("#8cc63f")), (70, Rgba.Parse("#f5a524")), (100, Rgba.Parse("#e5484d")), (120, Rgba.Parse("#9f1239")));

    /// <summary>Perceptually uniform viridis approximation over [0,1].</summary>
    public static ColorRamp Viridis { get; } = FromHex("viridis", 0, 1, "#440154", "#3b528b", "#21918c", "#5ec962", "#fde725");

    /// <summary>Brand heat ramp over [0,1]: navy → cyan → amber → red (for heatmaps and rasters).</summary>
    public static ColorRamp Thermal { get; } = FromHex("thermal", 0, 1, "#0b1626", "#155e75", "#22d3ee", "#f5a524", "#e5484d");
}

/// <summary>Convenience color look-ups used by diagrams and maps.</summary>
public static class ColorRamps
{
    private static readonly ColorRamp V = ColorRamp.Voltage();

    /// <summary>Hex color for a bus voltage [pu] (±0.1 pu saturation).</summary>
    public static string VoltageHex(double vmPu) => V.Hex(vmPu);

    /// <summary>Hex color for a branch loading [% of rating].</summary>
    public static string LoadingHex(double percent) => ColorRamp.Loading.Hex(percent);
}
