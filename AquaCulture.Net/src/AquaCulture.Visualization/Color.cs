using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace AquaCulture.Visualization;

/// <summary>RGBA colour.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        uint v = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return h.Length == 8 ? new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v) : new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    public static Rgba Lerp(Rgba a, Rgba b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t), (byte)Math.Round(a.A + (b.A - a.A) * t));
}

/// <summary>Piecewise-linear colour ramp between a minimum and maximum value.</summary>
public sealed class ColorRamp
{
    private readonly (double Pos, Rgba Color)[] _stops;

    public ColorRamp(string name, double min, double max, params (double Pos, string Hex)[] stops)
    {
        Name = name; Min = min; Max = max;
        _stops = stops.Select(s => (s.Pos, Rgba.Parse(s.Hex))).OrderBy(s => s.Pos).ToArray();
    }

    public string Name { get; }
    public double Min { get; }
    public double Max { get; }
    public IReadOnlyList<(double Pos, Rgba Color)> Stops => _stops;

    public Rgba this[double value]
    {
        get
        {
            if (double.IsNaN(value)) return new Rgba(0, 0, 0, 0);
            double t = Math.Clamp((value - Min) / (Max - Min), 0, 1);
            for (int i = 1; i < _stops.Length; i++)
                if (t <= _stops[i].Pos) return Rgba.Lerp(_stops[i - 1].Color, _stops[i].Color, (t - _stops[i - 1].Pos) / (_stops[i].Pos - _stops[i - 1].Pos));
            return _stops[^1].Color;
        }
    }

    public ColorRamp WithRange(double min, double max) => new(Name, min, max, _stops.Select(s => (s.Pos, s.Color.Hex)).ToArray());

    /// <summary>Dissolved oxygen 0–10 mg/L: hypoxic coral → amber → sea green → deep teal.</summary>
    public static ColorRamp Oxygen { get; } = new("Oxygen", 0, 10, (0, "#7a1f1f"), (0.2, "#e0614a"), (0.4, "#e9b44c"), (0.6, "#7cc6a4"), (0.8, "#1fa3a8"), (1, "#0b4f6c"));
    /// <summary>Water temperature 20–36 °C.</summary>
    public static ColorRamp Thermal { get; } = new("Thermal", 20, 36, (0, "#1d3f72"), (0.3, "#3e8fb0"), (0.5, "#9ad1c2"), (0.7, "#f2c66d"), (0.85, "#e8823a"), (1, "#b2302a"));
    /// <summary>Depth / bathymetry.</summary>
    public static ColorRamp Depth { get; } = new("Depth", 0, 5, (0, "#cfeee8"), (0.4, "#5fb6c2"), (0.75, "#1f6f8b"), (1, "#0b2e44"));
    /// <summary>Concentration of a pollutant / waste (0 = clean).</summary>
    public static ColorRamp Waste { get; } = new("Waste", 0, 1, (0, "#e8f3ef"), (0.3, "#c9d98a"), (0.6, "#c9a24b"), (1, "#6b3d1f"));
    /// <summary>Score 0–100 (WQI): critical → excellent.</summary>
    public static ColorRamp Score { get; } = new("Score", 0, 100, (0, "#a3302a"), (0.35, "#e0614a"), (0.55, "#e9b44c"), (0.75, "#4fb389"), (1, "#127a7a"));
    /// <summary>Terrain elevation.</summary>
    public static ColorRamp Terrain { get; } = new("Terrain", 0, 6, (0, "#4f8f6f"), (0.4, "#a7c48a"), (0.7, "#d9c38f"), (1, "#8a6a4a"));
}

/// <summary>Minimal PNG encoder (8-bit RGBA, zlib) with raster helpers.</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFF)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static byte[] Encode(ReadOnlySpan<Rgba> pixels, int width, int height)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Pixel count mismatch.");
        var raw = new byte[height * (width * 4 + 1)];
        for (int y = 0; y < height; y++)
        {
            int o = y * (width * 4 + 1);
            raw[o] = 0;
            for (int x = 0; x < width; x++) { var p = pixels[y * width + x]; int i = o + 1 + x * 4; raw[i] = p.R; raw[i + 1] = p.G; raw[i + 2] = p.B; raw[i + 3] = p.A; }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(raw);
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr); Chunk(ms, "IDAT", z.ToArray()); Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(len, data.Length); s.Write(len);
        Span<byte> t = stackalloc byte[4]; for (int i = 0; i < 4; i++) t[i] = (byte)type[i]; s.Write(t);
        s.Write(data);
        uint crc = Crc(data, Crc(t)) ^ 0xFFFFFFFF;
        Span<byte> c = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(c, crc); s.Write(c);
    }

    /// <summary>Colours a float raster (row-major, row 0 at the top) with a ramp; NaN becomes transparent. <paramref name="scale"/> enlarges cells.</summary>
    public static byte[] Raster(ReadOnlySpan<float> values, int width, int height, ColorRamp ramp, int scale = 1, bool flipY = false)
    {
        var px = new Rgba[width * scale * height * scale];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var c = ramp[values[(flipY ? height - 1 - y : y) * width + x]];
                for (int dy = 0; dy < scale; dy++) for (int dx = 0; dx < scale; dx++) px[(y * scale + dy) * width * scale + x * scale + dx] = c;
            }
        return Encode(px, width * scale, height * scale);
    }

    public static string DataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
}
