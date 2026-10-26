using System.Globalization;
using System.Text;

namespace GDAL.Net.Rendering;

/// <summary>An RGBA color.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        var v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return s.Length == 8
            ? new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public string ToCss() => A == 255 ? ToHex() : $"rgba({R},{G},{B},{(A / 255.0).ToString("0.###", CultureInfo.InvariantCulture)})";

    public static Rgba Lerp(Rgba a, Rgba b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t),
        (byte)Math.Round(a.A + (b.A - a.A) * t));
}

/// <summary>A color gradient defined by stops in [0, 1]. Produces 256-entry lookup tables for fast colorization.</summary>
public sealed class ColorRamp
{
    private readonly (double Position, Rgba Color)[] _stops;
    private byte[]? _lut;

    public ColorRamp(string name, params (double Position, Rgba Color)[] stops)
    {
        if (stops.Length < 2) throw new ArgumentException("A ramp needs at least two stops.");
        Name = name;
        _stops = [.. stops.OrderBy(s => s.Position)];
    }

    public static ColorRamp FromHex(string name, params string[] colors)
        => new(name, colors.Select((c, i) => ((double)i / (colors.Length - 1), Rgba.FromHex(c))).ToArray());

    public string Name { get; }

    public IReadOnlyList<(double Position, Rgba Color)> Stops => _stops;

    /// <summary>Color at position t in [0, 1].</summary>
    public Rgba Evaluate(double t)
    {
        t = Math.Clamp(t, 0, 1);
        for (var i = 1; i < _stops.Length; i++)
        {
            if (t <= _stops[i].Position)
            {
                var (p0, c0) = _stops[i - 1];
                var (p1, c1) = _stops[i];
                return Rgba.Lerp(c0, c1, p1 > p0 ? (t - p0) / (p1 - p0) : 0);
            }
        }
        return _stops[^1].Color;
    }

    /// <summary>256 RGBA entries (1024 bytes) — the format used by <see cref="Processing.Kernels.Colorize"/>.</summary>
    public ReadOnlySpan<byte> Lut
    {
        get
        {
            if (_lut is null)
            {
                var lut = new byte[1024];
                for (var i = 0; i < 256; i++)
                {
                    var c = Evaluate(i / 255.0);
                    lut[i * 4] = c.R;
                    lut[i * 4 + 1] = c.G;
                    lut[i * 4 + 2] = c.B;
                    lut[i * 4 + 3] = c.A;
                }
                _lut = lut;
            }
            return _lut;
        }
    }

    public ColorRamp Reversed() => new(Name + " (reversed)", _stops.Select(s => (1 - s.Position, s.Color)).ToArray());

    /// <summary>CSS linear-gradient for legends.</summary>
    public string ToCssGradient(string direction = "to right")
        => $"linear-gradient({direction}, {string.Join(", ", _stops.Select(s => $"{s.Color.ToCss()} {(s.Position * 100).ToString("0.#", CultureInfo.InvariantCulture)}%"))})";

    /// <summary>gdaldem color-relief color file content mapping the ramp onto [min, max] (with "nv" transparent nodata).</summary>
    public string ToGdalColorFile(double min, double max, int steps = 16)
    {
        var sb = new StringBuilder();
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var c = Evaluate(t);
            var v = min + (max - min) * t;
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(c.R).Append(' ').Append(c.G).Append(' ').Append(c.B).Append(' ').Append(c.A).Append('\n');
        }
        sb.Append("nv 0 0 0 0\n");
        return sb.ToString();
    }

    public override string ToString() => Name;

    // ------------------------------------------------------------------ built-in ramps

    public static ColorRamp Grayscale { get; } = FromHex("Grayscale", "#000000", "#ffffff");
    public static ColorRamp Viridis { get; } = FromHex("Viridis", "#440154", "#482878", "#3e4989", "#31688e", "#26828e", "#1f9e89", "#35b779", "#6ece58", "#b5de2b", "#fde725");
    public static ColorRamp Magma { get; } = FromHex("Magma", "#000004", "#1c1044", "#4f127b", "#812581", "#b5367a", "#e55064", "#fb8761", "#fec287", "#fcfdbf");
    public static ColorRamp Inferno { get; } = FromHex("Inferno", "#000004", "#1f0c48", "#550f6d", "#88226a", "#ba3655", "#e35933", "#f98e09", "#f9cb35", "#fcffa4");
    public static ColorRamp Turbo { get; } = FromHex("Turbo", "#30123b", "#4662d7", "#36aaf9", "#1ae4b6", "#72fe5e", "#c8ef34", "#faba39", "#f66b19", "#ca2a04", "#7a0403");
    public static ColorRamp Terrain { get; } = new("Terrain",
        (0.00, Rgba.FromHex("#0b4f6c")), (0.08, Rgba.FromHex("#2a9d8f")), (0.18, Rgba.FromHex("#57cc99")),
        (0.35, Rgba.FromHex("#a7c957")), (0.55, Rgba.FromHex("#e9c46a")), (0.72, Rgba.FromHex("#bc6c25")),
        (0.88, Rgba.FromHex("#8d6e63")), (1.00, Rgba.FromHex("#f8f9fa")));
    public static ColorRamp Ndvi { get; } = new("NDVI",
        (0.00, Rgba.FromHex("#a50026")), (0.25, Rgba.FromHex("#d73027")), (0.40, Rgba.FromHex("#fdae61")),
        (0.50, Rgba.FromHex("#fee08b")), (0.60, Rgba.FromHex("#d9ef8b")), (0.75, Rgba.FromHex("#66bd63")), (1.00, Rgba.FromHex("#006837")));
    public static ColorRamp Water { get; } = FromHex("Water", "#f7fbff", "#c6dbef", "#6baed6", "#2171b5", "#08306b");
    public static ColorRamp Spectral { get; } = FromHex("Spectral", "#9e0142", "#d53e4f", "#f46d43", "#fdae61", "#fee08b", "#e6f598", "#abdda4", "#66c2a5", "#3288bd", "#5e4fa2");
    public static ColorRamp Heat { get; } = FromHex("Heat", "#000000", "#7f0000", "#ff0000", "#ffff00", "#ffffff");
    public static ColorRamp Ocean { get; } = FromHex("Ocean", "#03045e", "#0077b6", "#00b4d8", "#90e0ef", "#caf0f8");

    /// <summary>All built-in ramps.</summary>
    public static IReadOnlyList<ColorRamp> All { get; } = [Viridis, Terrain, Ndvi, Magma, Inferno, Turbo, Spectral, Water, Ocean, Heat, Grayscale];

    public static ColorRamp? Find(string name) => All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
}
