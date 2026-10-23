using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CesiumNet;

/// <summary>
/// An RGBA color (components <c>R</c>, <c>G</c>, <c>B</c>, <c>A</c> in 0..1). Serialized to JavaScript as a CSS color string
/// (<c>#RRGGBBAA</c>) that CesiumJS parses with <c>Color.fromCssColorString</c>.
/// </summary>
[JsonConverter(typeof(CesiumColorJsonConverter))]
public readonly record struct CesiumColor(float R, float G, float B, float A = 1f)
{
    public static CesiumColor FromBytes(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Parses <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
    public static CesiumColor FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var s = hex.AsSpan().TrimStart('#');
        static byte Nib(char c) => (byte)(Convert.ToByte(c.ToString(), 16) * 17);
        return s.Length switch
        {
            3 => FromBytes(Nib(s[0]), Nib(s[1]), Nib(s[2])),
            4 => FromBytes(Nib(s[0]), Nib(s[1]), Nib(s[2]), Nib(s[3])),
            6 => FromBytes(Hex(s[..2]), Hex(s[2..4]), Hex(s[4..6])),
            8 => FromBytes(Hex(s[..2]), Hex(s[2..4]), Hex(s[4..6]), Hex(s[6..8])),
            _ => throw new FormatException($"Invalid hex color '{hex}'."),
        };

        static byte Hex(ReadOnlySpan<char> two) => byte.Parse(two, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Creates a color from HSL (all components 0..1).</summary>
    public static CesiumColor FromHsl(double hue, double saturation, double lightness, float alpha = 1f)
    {
        if (saturation == 0) return new((float)lightness, (float)lightness, (float)lightness, alpha);
        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;
        return new((float)HueToRgb(p, q, hue + 1.0 / 3), (float)HueToRgb(p, q, hue), (float)HueToRgb(p, q, hue - 1.0 / 3), alpha);

        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }

    /// <summary>Returns a copy with a different alpha.</summary>
    public CesiumColor WithAlpha(float alpha) => this with { A = alpha };

    /// <summary>Linearly interpolates between two colors.</summary>
    public static CesiumColor Lerp(CesiumColor a, CesiumColor b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    /// <summary>CSS hex string <c>#RRGGBBAA</c>.</summary>
    public string ToCss()
    {
        static byte Byte(float v) => (byte)System.Math.Clamp((int)System.Math.Round(v * 255), 0, 255);
        return string.Create(9, this, static (span, c) =>
        {
            span[0] = '#';
            Byte(c.R).TryFormat(span[1..], out _, "X2", CultureInfo.InvariantCulture);
            Byte(c.G).TryFormat(span[3..], out _, "X2", CultureInfo.InvariantCulture);
            Byte(c.B).TryFormat(span[5..], out _, "X2", CultureInfo.InvariantCulture);
            Byte(c.A).TryFormat(span[7..], out _, "X2", CultureInfo.InvariantCulture);
        });
    }

    public override string ToString() => ToCss();

    public static implicit operator CesiumColor(string hex) => FromHex(hex);

    public static readonly CesiumColor Transparent = new(0, 0, 0, 0);
    public static readonly CesiumColor White = new(1, 1, 1);
    public static readonly CesiumColor Black = new(0, 0, 0);
    public static readonly CesiumColor Red = FromHex("#FF3B30");
    public static readonly CesiumColor Orange = FromHex("#FF9500");
    public static readonly CesiumColor Yellow = FromHex("#FFCC00");
    public static readonly CesiumColor Lime = FromHex("#A3E635");
    public static readonly CesiumColor Green = FromHex("#34C759");
    public static readonly CesiumColor Teal = FromHex("#14B8A6");
    public static readonly CesiumColor Cyan = FromHex("#00C7FF");
    public static readonly CesiumColor Blue = FromHex("#1F4FE0");
    public static readonly CesiumColor Indigo = FromHex("#5856D6");
    public static readonly CesiumColor Purple = FromHex("#AF52DE");
    public static readonly CesiumColor Magenta = FromHex("#FF2D92");
    public static readonly CesiumColor Gray = FromHex("#8E8E93");
}

/// <summary>Serializes <see cref="CesiumColor"/> as a CSS hex string.</summary>
public sealed class CesiumColorJsonConverter : JsonConverter<CesiumColor>
{
    public override CesiumColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => CesiumColor.FromHex(reader.GetString() ?? "#FFFFFFFF");

    public override void Write(Utf8JsonWriter writer, CesiumColor value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToCss());
}
