namespace GDAL.Net.Rendering;

/// <summary>Display style of a vector layer, shared by the Blazor and Avalonia components.</summary>
public sealed record VectorStyle
{
    public Rgba Fill { get; init; } = new(42, 157, 143, 90);
    public Rgba Stroke { get; init; } = new(18, 38, 58);
    public double StrokeWidth { get; init; } = 1.2;
    public double PointRadius { get; init; } = 4;
    /// <summary>Attribute shown as a label next to points / polygon centers.</summary>
    public string? LabelField { get; init; }
    /// <summary>Optional per-feature fill (e.g. classified by attribute).</summary>
    public Func<VectorFeature, Rgba?>? FillSelector { get; init; }
    /// <summary>Optional per-feature point radius (e.g. proportional symbols).</summary>
    public Func<VectorFeature, double?>? RadiusSelector { get; init; }

    public Rgba FillFor(VectorFeature f) => FillSelector?.Invoke(f) ?? Fill;
    public double RadiusFor(VectorFeature f) => RadiusSelector?.Invoke(f) ?? PointRadius;

    public static VectorStyle Default { get; } = new();
    public static VectorStyle Lines(Rgba color, double width = 1.2) => new() { Stroke = color, StrokeWidth = width, Fill = new Rgba(0, 0, 0, 0) };
    public static VectorStyle Points(Rgba fill, double radius = 4) => new() { Fill = fill, PointRadius = radius, Stroke = new Rgba(255, 255, 255) };
}

/// <summary>A vector layer with its style, ready for display.</summary>
public sealed record StyledVector(VectorData Data, VectorStyle Style, string? Title = null);
