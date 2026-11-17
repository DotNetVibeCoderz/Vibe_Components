using System.Globalization;

namespace GeoPandas.Net.Plotting;

/// <summary>8-bit RGBA colour.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        byte P(int i) => byte.Parse(h.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return h.Length switch
        {
            6 => new Rgba(P(0), P(2), P(4)),
            8 => new Rgba(P(0), P(2), P(4), P(6)),
            _ => throw new FormatException($"Invalid colour '{hex}'.")
        };
    }

    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
    public double Opacity => A / 255.0;
    public Rgba WithAlpha(double opacity) => this with { A = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255) };

    public static Rgba Lerp(Rgba a, Rgba b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgba((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
    }

    public static implicit operator Rgba(string hex) => Parse(hex);

    public static Rgba White => new(255, 255, 255);
    public static Rgba Black => new(0, 0, 0);
    public static Rgba Transparent => new(0, 0, 0, 0);
}

/// <summary>Continuous colour map defined by evenly spaced stops.</summary>
public sealed class ColorMap(string name, params Rgba[] stops)
{
    public string Name { get; } = name;
    public IReadOnlyList<Rgba> Stops { get; } = stops;

    /// <summary>Colour at t ∈ [0, 1].</summary>
    public Rgba Sample(double t)
    {
        if (double.IsNaN(t)) return Rgba.Transparent;
        t = Math.Clamp(t, 0, 1);
        double pos = t * (Stops.Count - 1);
        int i = Math.Min((int)pos, Stops.Count - 2);
        return Rgba.Lerp(Stops[i], Stops[i + 1], pos - i);
    }

    /// <summary>n discrete colours sampled evenly.</summary>
    public Rgba[] Discrete(int n) => Enumerable.Range(0, n).Select(i => n == 1 ? Sample(0.5) : Sample(i / (double)(n - 1))).ToArray();

    public ColorMap Reversed() => new(Name + "_r", [.. Stops.Reverse()]);
}

/// <summary>Built-in colour maps (perceptually uniform matplotlib-style maps and ColorBrewer schemes).</summary>
public static class ColorMaps
{
    private static Rgba[] P(params string[] hex) => hex.Select(Rgba.Parse).ToArray();

    public static ColorMap Viridis { get; } = new("viridis", P("#440154", "#482878", "#3e4989", "#31688e", "#26828e", "#1f9e89", "#35b779", "#6ece58", "#b5de2b", "#fde725"));
    public static ColorMap Magma { get; } = new("magma", P("#000004", "#180f3d", "#440f76", "#721f81", "#9e2f7f", "#cd4071", "#f1605d", "#fd9668", "#feca8d", "#fcfdbf"));
    public static ColorMap Plasma { get; } = new("plasma", P("#0d0887", "#46039f", "#7201a8", "#9c179e", "#bd3786", "#d8576b", "#ed7953", "#fb9f3a", "#fdca26", "#f0f921"));
    public static ColorMap Inferno { get; } = new("inferno", P("#000004", "#1b0c41", "#4a0c6b", "#781c6d", "#a52c60", "#cf4446", "#ed6925", "#fb9b06", "#f7d13d", "#fcffa4"));
    public static ColorMap Cividis { get; } = new("cividis", P("#00224e", "#123570", "#3b496c", "#575d6d", "#707173", "#8a8779", "#a69d75", "#c4b56c", "#e4cf5b", "#fee838"));
    public static ColorMap YlOrRd { get; } = new("YlOrRd", P("#ffffcc", "#ffeda0", "#fed976", "#feb24c", "#fd8d3c", "#fc4e2a", "#e31a1c", "#bd0026", "#800026"));
    public static ColorMap YlGnBu { get; } = new("YlGnBu", P("#ffffd9", "#edf8b1", "#c7e9b4", "#7fcdbb", "#41b6c4", "#1d91c0", "#225ea8", "#253494", "#081d58"));
    public static ColorMap Blues { get; } = new("Blues", P("#f7fbff", "#deebf7", "#c6dbef", "#9ecae1", "#6baed6", "#4292c6", "#2171b5", "#08519c", "#08306b"));
    public static ColorMap Greens { get; } = new("Greens", P("#f7fcf5", "#e5f5e0", "#c7e9c0", "#a1d99b", "#74c476", "#41ab5d", "#238b45", "#006d2c", "#00441b"));
    public static ColorMap RdYlGn { get; } = new("RdYlGn", P("#a50026", "#d73027", "#f46d43", "#fdae61", "#fee08b", "#d9ef8b", "#a6d96a", "#66bd63", "#1a9850", "#006837"));
    public static ColorMap RdBu { get; } = new("RdBu", P("#67001f", "#b2182b", "#d6604d", "#f4a582", "#fddbc7", "#d1e5f0", "#92c5de", "#4393c3", "#2166ac", "#053061"));
    public static ColorMap Spectral { get; } = new("Spectral", P("#9e0142", "#d53e4f", "#f46d43", "#fdae61", "#fee08b", "#e6f598", "#abdda4", "#66c2a5", "#3288bd", "#5e4fa2"));
    public static ColorMap Terrain { get; } = new("terrain", P("#333399", "#0294fa", "#24d36d", "#b6f08a", "#dbd085", "#92735e", "#b6a29e", "#ffffff"));
    public static ColorMap Gray { get; } = new("gray", P("#000000", "#ffffff"));
    /// <summary>NDVI style: brown → yellow → green.</summary>
    public static ColorMap Ndvi { get; } = new("ndvi", P("#8c510a", "#bf812d", "#dfc27d", "#f6e8c3", "#c7eae5", "#80cdc1", "#35978f", "#01665e", "#003c30"));

    /// <summary>Qualitative palette (Tableau 10) for categorical data.</summary>
    public static IReadOnlyList<Rgba> Categorical { get; } = P("#4e79a7", "#f28e2b", "#e15759", "#76b7b2", "#59a14f", "#edc948", "#b07aa1", "#ff9da7", "#9c755f", "#bab0ac");

    public static ColorMap Get(string name)
    {
        bool rev = name.EndsWith("_r", StringComparison.Ordinal);
        var n = rev ? name[..^2] : name;
        var map = n.ToLowerInvariant() switch
        {
            "viridis" => Viridis, "magma" => Magma, "plasma" => Plasma, "inferno" => Inferno, "cividis" => Cividis,
            "ylorrd" => YlOrRd, "ylgnbu" => YlGnBu, "blues" => Blues, "greens" => Greens, "rdylgn" => RdYlGn,
            "rdbu" => RdBu, "spectral" => Spectral, "terrain" => Terrain, "gray" or "grey" => Gray, "ndvi" => Ndvi,
            _ => throw new ArgumentException($"Unknown colour map '{name}'.")
        };
        return rev ? map.Reversed() : map;
    }
}

public enum ClassificationScheme { Continuous, EqualInterval, Quantiles, NaturalBreaks }

/// <summary>Class-break computation for choropleth maps (mapclassify equivalents).</summary>
public static class Classifier
{
    /// <summary>Upper bounds of each class (k values; last = max).</summary>
    public static double[] Breaks(IEnumerable<double> values, int k, ClassificationScheme scheme)
    {
        var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        if (v.Length == 0) return [];
        k = Math.Max(1, Math.Min(k, v.Distinct().Count()));
        return scheme switch
        {
            ClassificationScheme.Quantiles => Enumerable.Range(1, k).Select(i => Quantile(v, i / (double)k)).ToArray(),
            ClassificationScheme.NaturalBreaks => Jenks(v, k),
            _ => Enumerable.Range(1, k).Select(i => v[0] + (v[^1] - v[0]) * i / k).ToArray()
        };
    }

    /// <summary>Index of the class containing <paramref name="x"/>.</summary>
    public static int ClassOf(double x, double[] breaks)
    {
        for (int i = 0; i < breaks.Length; i++) if (x <= breaks[i]) return i;
        return breaks.Length - 1;
    }

    private static double Quantile(double[] sorted, double q)
    {
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>Jenks natural breaks (Fisher dynamic programming). Samples large inputs for speed.</summary>
    private static double[] Jenks(double[] sorted, int k)
    {
        double[] data = sorted;
        if (data.Length > 3000)
        {
            var step = data.Length / 3000.0;
            data = Enumerable.Range(0, 3000).Select(i => sorted[(int)(i * step)]).Append(sorted[^1]).ToArray();
        }
        int n = data.Length;
        var lower = new int[n + 1, k + 1];
        var variance = new double[n + 1, k + 1];
        for (int i = 1; i <= k; i++) { lower[1, i] = 1; variance[1, i] = 0; for (int j = 2; j <= n; j++) variance[j, i] = double.PositiveInfinity; }
        for (int l = 2; l <= n; l++)
        {
            double s1 = 0, s2 = 0, w = 0, var = 0;
            for (int m = 1; m <= l; m++)
            {
                int i3 = l - m + 1;
                double val = data[i3 - 1];
                s2 += val * val; s1 += val; w++;
                var = s2 - s1 * s1 / w;
                int i4 = i3 - 1;
                if (i4 != 0)
                    for (int j = 2; j <= k; j++)
                        if (variance[l, j] >= var + variance[i4, j - 1]) { lower[l, j] = i3; variance[l, j] = var + variance[i4, j - 1]; }
            }
            lower[l, 1] = 1; variance[l, 1] = var;
        }
        var breaks = new double[k];
        breaks[k - 1] = data[n - 1];
        int kk = n;
        for (int j = k; j >= 2; j--)
        {
            int id = lower[kk, j] - 2;
            breaks[j - 2] = data[Math.Max(0, id)];
            kk = lower[kk, j] - 1;
        }
        return breaks;
    }
}
