using System.Globalization;
using Visual.Net.Core.Colors;
using Visual.Net.Core.Scales;

namespace Blazor.AI.Visualization;

/// <summary>A linear mapping from data space to pixel space with "nice" ticks (Visual.Net's MaxNLocator).</summary>
public readonly struct LinearAxis
{
    public LinearAxis(double min, double max, double pixelStart, double pixelEnd, int maxTicks = 6, bool nice = true)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            min = 0;
            max = 1;
        }

        if (Math.Abs(max - min) < 1e-12)
        {
            var pad = Math.Abs(min) < 1e-12 ? 1 : Math.Abs(min) * 0.1;
            min -= pad;
            max += pad;
        }

        var locator = new MaxNLocator(maxTicks);
        if (nice)
        {
            (min, max) = locator.Round(min, max);
        }

        Min = min;
        Max = max;
        PixelStart = pixelStart;
        PixelEnd = pixelEnd;
        Ticks = locator.Tick(min, max).Where(t => t >= min - 1e-9 && t <= max + 1e-9).ToArray();
    }

    public double Min { get; }

    public double Max { get; }

    public double PixelStart { get; }

    public double PixelEnd { get; }

    public IReadOnlyList<double> Ticks { get; }

    public double Map(double value) => PixelStart + (value - Min) / (Max - Min) * (PixelEnd - PixelStart);
}

public static class ChartMath
{
    /// <summary>Series colours, derived from the batik palette; extended with Visual.Net's Tab10 beyond six series.</summary>
    private static readonly string[] Brand = ["#c9853f", "#3a4aa8", "#2ead9c", "#c2414b", "#8a6fd1", "#d4b13a"];

    public static string SeriesColor(int index) =>
        index < Brand.Length ? Brand[index] : Colormaps.Tab10.Colors[index % Colormaps.Tab10.Count].ToHex();

    /// <summary>Maps a value in [min, max] to a colour of a Visual.Net colormap (viridis, coolwarm, magma...).</summary>
    public static string ColormapColor(string colormap, double value, double min, double max)
    {
        var t = max - min < 1e-12 ? 0.5 : Math.Clamp((value - min) / (max - min), 0, 1);
        var cmap = Colormaps.TryGet(colormap, out var found) ? found : Colormaps.Viridis;
        return cmap.At(t).ToHex();
    }

    /// <summary>Black or white text, whichever reads better on <paramref name="hex"/>.</summary>
    public static string ReadableOn(string hex) => Color.Parse(hex).ReadableForeground().ToHex();

    public static string Fmt(double value) => value switch
    {
        _ when !double.IsFinite(value) => "–",
        _ when Math.Abs(value) >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        _ when Math.Abs(value) >= 1_000_000 => (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        _ when Math.Abs(value) >= 10_000 => (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ when Math.Abs(value) >= 100 => value.ToString("0", CultureInfo.InvariantCulture),
        _ when Math.Abs(value) >= 1 => value.ToString("0.##", CultureInfo.InvariantCulture),
        _ => value.ToString("0.###", CultureInfo.InvariantCulture),
    };

    /// <summary>Invariant-culture number for SVG attributes.</summary>
    public static string S(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Path(IEnumerable<(double X, double Y)> points)
    {
        var builder = new System.Text.StringBuilder();
        var first = true;
        foreach (var (x, y) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                first = true;
                continue;
            }

            builder.Append(first ? 'M' : 'L').Append(S(x)).Append(',').Append(S(y)).Append(' ');
            first = false;
        }

        return builder.ToString();
    }
}
