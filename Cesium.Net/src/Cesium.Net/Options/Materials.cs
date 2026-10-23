namespace CesiumNet;

/// <summary>
/// A surface/line material. Use the static factories, e.g. <c>CesiumMaterial.Glow(CesiumColor.Cyan)</c>.
/// A <see cref="CesiumColor"/> converts implicitly to a solid color material.
/// </summary>
public sealed class CesiumMaterial
{
    public MaterialKind Kind { get; init; } = MaterialKind.Color;
    public CesiumColor? Color { get; init; }
    /// <summary>Second color (dash gap, stripe/checkerboard odd color, outline color).</summary>
    public CesiumColor? SecondaryColor { get; init; }
    public double? GlowPower { get; init; }
    public double? TaperPower { get; init; }
    public double? DashLength { get; init; }
    public double? OutlineWidth { get; init; }
    public double? Repeat { get; init; }
    public double? RepeatY { get; init; }
    public double? CellAlpha { get; init; }
    public double? LineCount { get; init; }
    public double? LineThickness { get; init; }
    public bool? Horizontal { get; init; }
    /// <summary>Image URL (or data URL) for <see cref="MaterialKind.Image"/>.</summary>
    public string? Image { get; init; }
    public bool? Transparent { get; init; }

    public static CesiumMaterial Solid(CesiumColor color) => new() { Kind = MaterialKind.Color, Color = color };
    public static CesiumMaterial Glow(CesiumColor color, double glowPower = 0.25, double taperPower = 1) => new() { Kind = MaterialKind.PolylineGlow, Color = color, GlowPower = glowPower, TaperPower = taperPower };
    public static CesiumMaterial Dash(CesiumColor color, CesiumColor? gapColor = null, double dashLength = 16) => new() { Kind = MaterialKind.PolylineDash, Color = color, SecondaryColor = gapColor, DashLength = dashLength };
    public static CesiumMaterial Arrow(CesiumColor color) => new() { Kind = MaterialKind.PolylineArrow, Color = color };
    public static CesiumMaterial Outline(CesiumColor color, CesiumColor outlineColor, double outlineWidth = 2) => new() { Kind = MaterialKind.PolylineOutline, Color = color, SecondaryColor = outlineColor, OutlineWidth = outlineWidth };
    public static CesiumMaterial Stripe(CesiumColor even, CesiumColor odd, double repeat = 8, bool horizontal = true) => new() { Kind = MaterialKind.Stripe, Color = even, SecondaryColor = odd, Repeat = repeat, Horizontal = horizontal };
    public static CesiumMaterial Grid(CesiumColor color, double cellAlpha = 0.1, double lineCount = 8, double lineThickness = 1) => new() { Kind = MaterialKind.Grid, Color = color, CellAlpha = cellAlpha, LineCount = lineCount, LineThickness = lineThickness };
    public static CesiumMaterial Checkerboard(CesiumColor even, CesiumColor odd, double repeat = 4) => new() { Kind = MaterialKind.Checkerboard, Color = even, SecondaryColor = odd, Repeat = repeat };
    public static CesiumMaterial FromImage(string url, double repeat = 1, bool transparent = true) => new() { Kind = MaterialKind.Image, Image = url, Repeat = repeat, Transparent = transparent };

    public static implicit operator CesiumMaterial(CesiumColor color) => Solid(color);
}

/// <summary>Distance range (meters from camera) within which a graphic is visible.</summary>
public readonly record struct DistanceDisplayCondition(double Near, double Far);

/// <summary>Scales a value between <paramref name="NearValue"/> at <paramref name="Near"/> meters and <paramref name="FarValue"/> at <paramref name="Far"/> meters.</summary>
public readonly record struct NearFarScalar(double Near, double NearValue, double Far, double FarValue);
