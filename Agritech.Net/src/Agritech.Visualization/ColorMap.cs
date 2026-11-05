namespace Agritech.Visualization;

public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public string Hex => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
    public static Rgba FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte P(int i) => Convert.ToByte(hex.Substring(i, 2), 16);
        return new Rgba(P(0), P(2), P(4), hex.Length >= 8 ? P(6) : (byte)255);
    }
    public static Rgba Lerp(Rgba a, Rgba b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
}

/// <summary>Piecewise-linear color ramp over a value domain.</summary>
public sealed class ColorMap(string name, double min, double max, params (double Stop, string Hex)[] stops)
{
    private readonly (double Stop, Rgba Color)[] _stops = stops.Select(s => (s.Stop, Rgba.FromHex(s.Hex))).OrderBy(s => s.Stop).ToArray();

    public string Name { get; } = name;
    public double Min { get; } = min;
    public double Max { get; } = max;
    public Rgba NoData { get; init; } = new(0, 0, 0, 0);
    public IReadOnlyList<(double Stop, Rgba Color)> Stops => _stops;

    public Rgba Map(double v)
    {
        if (double.IsNaN(v)) return NoData;
        double t = Math.Clamp((v - Min) / (Max - Min), 0, 1);
        if (t <= _stops[0].Stop) return _stops[0].Color;
        for (int i = 1; i < _stops.Length; i++)
            if (t <= _stops[i].Stop)
                return Rgba.Lerp(_stops[i - 1].Color, _stops[i].Color, (t - _stops[i - 1].Stop) / (_stops[i].Stop - _stops[i - 1].Stop));
        return _stops[^1].Color;
    }

    /// <summary>Vegetation (NDVI −0.2…0.9): bare soil browns → vigorous greens.</summary>
    public static readonly ColorMap Ndvi = new("NDVI", -0.2, 0.9,
        (0.0, "#7a4b2a"), (0.25, "#c49a5a"), (0.4, "#e9d98b"), (0.55, "#a6cf5d"), (0.7, "#4f9f3a"), (0.85, "#1f6f2b"), (1.0, "#0b3d1a"));
    /// <summary>Soil moisture / water (0–1).</summary>
    public static readonly ColorMap Water = new("Water", 0, 1, (0, "#f3ead7"), (0.35, "#9fd3d6"), (0.7, "#2f8fb5"), (1, "#123f6b"));
    /// <summary>Diverging change map (−0.3…+0.3).</summary>
    public static readonly ColorMap Change = new("Change", -0.3, 0.3, (0, "#b2432f"), (0.5, "#f4f1e8"), (1, "#2e7d4a"));
    /// <summary>Temperature (10–40 °C).</summary>
    public static readonly ColorMap Heat = new("Heat", 10, 40, (0, "#2b4c7e"), (0.35, "#9ec5a8"), (0.6, "#f2d16b"), (0.8, "#e8833a"), (1, "#a8281f"));
    /// <summary>Categorical palette for management zones / series.</summary>
    public static readonly string[] Categorical = ["#2f6b3a", "#d89b2b", "#3c7ea6", "#b5513a", "#7a5c99", "#5a8f7b", "#c7a36a", "#56606b"];
}
