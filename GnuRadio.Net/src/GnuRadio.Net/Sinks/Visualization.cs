using System.Globalization;

namespace GnuRadio.Sinks;

/// <summary>256-entry colour lookup tables for waterfalls and intensity plots, shared by the Blazor and Avalonia components.</summary>
public sealed class Colormap
{
    private Colormap(string name, uint[] rgba)
    {
        Name = name;
        Rgba = rgba;
    }

    public string Name { get; }

    /// <summary>Packed 0xAABBGGRR values (byte order R, G, B, A in memory), ready for canvas ImageData and BGRA swaps.</summary>
    public uint[] Rgba { get; }

    /// <summary>Phosphor: black → deep teal → green → amber → white, modelled on a long-persistence CRT.</summary>
    public static Colormap Phosphor { get; } = FromStops("phosphor",
        (0.00, 0x05, 0x08, 0x0A), (0.30, 0x06, 0x3B, 0x45), (0.55, 0x1F, 0x9E, 0x6B),
        (0.75, 0xC8, 0xD8, 0x3A), (0.90, 0xFF, 0xB0, 0x2E), (1.00, 0xFF, 0xF6, 0xE0));

    /// <summary>Perceptually uniform, colour-blind friendly (approximation of matplotlib's viridis).</summary>
    public static Colormap Viridis { get; } = FromStops("viridis",
        (0.00, 0x44, 0x01, 0x54), (0.25, 0x3B, 0x52, 0x8B), (0.50, 0x21, 0x91, 0x8C),
        (0.75, 0x5E, 0xC9, 0x62), (1.00, 0xFD, 0xE7, 0x25));

    /// <summary>Approximation of matplotlib's inferno.</summary>
    public static Colormap Inferno { get; } = FromStops("inferno",
        (0.00, 0x00, 0x00, 0x04), (0.25, 0x42, 0x0A, 0x68), (0.50, 0x93, 0x26, 0x67),
        (0.75, 0xDD, 0x51, 0x3A), (0.90, 0xFC, 0xA5, 0x0A), (1.00, 0xFC, 0xFF, 0xA4));

    public static Colormap Grayscale { get; } = FromStops("grayscale", (0, 0, 0, 0), (1, 255, 255, 255));

    public static IReadOnlyList<Colormap> All { get; } = [Phosphor, Viridis, Inferno, Grayscale];

    public static Colormap FromName(string name) =>
        All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Phosphor;

    public static Colormap FromStops(string name, params (double Position, int R, int G, int B)[] stops)
    {
        var lut = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255.0;
            var k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1].Position) k++;
            var (p0, r0, g0, b0) = stops[k];
            var (p1, r1, g1, b1) = stops[Math.Min(k + 1, stops.Length - 1)];
            var f = p1 > p0 ? Math.Clamp((t - p0) / (p1 - p0), 0, 1) : 0;
            var r = (uint)Math.Round(r0 + (r1 - r0) * f);
            var g = (uint)Math.Round(g0 + (g1 - g0) * f);
            var b = (uint)Math.Round(b0 + (b1 - b0) * f);
            lut[i] = 0xFF000000u | (b << 16) | (g << 8) | r;
        }
        return new Colormap(name, lut);
    }

    /// <summary>Colour of <paramref name="value"/> scaled linearly from [min, max].</summary>
    public uint Map(float value, float min, float max)
    {
        var t = (value - min) / (max - min);
        var i = (int)(t * 255f);
        return Rgba[Math.Clamp(i, 0, 255)];
    }

    /// <summary>Maps a row of dB values to RGBA bytes (4 per value).</summary>
    public void MapRow(ReadOnlySpan<float> values, float min, float max, Span<byte> rgba)
    {
        var scale = 255f / (max - min);
        for (var i = 0; i < values.Length; i++)
        {
            var idx = Math.Clamp((int)((values[i] - min) * scale), 0, 255);
            var c = Rgba[idx];
            rgba[4 * i] = (byte)c;
            rgba[4 * i + 1] = (byte)(c >> 8);
            rgba[4 * i + 2] = (byte)(c >> 16);
            rgba[4 * i + 3] = 255;
        }
    }
}

/// <summary>Axis tick and label helpers shared by the chart components.</summary>
public static class Axis
{
    /// <summary>"Nice" tick positions (1, 2, 5 × 10^n steps) covering [min, max].</summary>
    public static double[] NiceTicks(double min, double max, int approximateCount = 6)
    {
        if (!(max > min) || approximateCount < 1) return [min];
        var raw = (max - min) / approximateCount;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var residual = raw / magnitude;
        var step = (residual > 5 ? 10 : residual > 2 ? 5 : residual > 1 ? 2 : 1) * magnitude;
        var first = Math.Ceiling(min / step) * step;
        var ticks = new List<double>();
        for (var t = first; t <= max + step * 1e-9; t += step) ticks.Add(Math.Abs(t) < step * 1e-9 ? 0 : t);
        return [.. ticks];
    }

    /// <summary>Formats a frequency with Hz/kHz/MHz/GHz units.</summary>
    public static string FormatFrequency(double hz, int decimals = 2)
    {
        var a = Math.Abs(hz);
        var (value, unit) = a >= 1e9 ? (hz / 1e9, "GHz") : a >= 1e6 ? (hz / 1e6, "MHz") : a >= 1e3 ? (hz / 1e3, "kHz") : (hz, "Hz");
        return value.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture) + " " + unit;
    }

    /// <summary>Formats seconds with s/ms/µs units.</summary>
    public static string FormatTime(double seconds)
    {
        var a = Math.Abs(seconds);
        return a >= 1 ? $"{seconds.ToString("0.##", CultureInfo.InvariantCulture)} s"
             : a >= 1e-3 ? $"{(seconds * 1e3).ToString("0.##", CultureInfo.InvariantCulture)} ms"
             : $"{(seconds * 1e6).ToString("0.##", CultureInfo.InvariantCulture)} µs";
    }

    public static string Format(double value) =>
        Math.Abs(value) >= 1e4 || (Math.Abs(value) < 1e-2 && value != 0)
            ? value.ToString("0.#E0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
}
