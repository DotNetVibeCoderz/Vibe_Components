namespace Blazor.AI.MediaPipe;

/// <summary>Colour-based leaf health measurement.</summary>
public sealed record LeafHealth(double LeafCoverage, double Green, double Yellow, double Lesion, OverlayMask? LesionMask)
{
    /// <summary>0 (healthy) … 1 (severe) from lesion and yellowing shares of the leaf area.</summary>
    public double Severity => Math.Clamp(Lesion * 4 + Yellow * 1.2, 0, 1);

    public string Assessment => LeafCoverage < 0.05 ? "No leaf in view"
        : Severity < 0.15 ? "Looks healthy"
        : Severity < 0.4 ? "Mild symptoms"
        : Severity < 0.7 ? "Moderate symptoms"
        : "Severe symptoms";
}

/// <summary>
/// Measures leaf colour: green (healthy tissue), yellow (chlorosis) and brown (lesions/necrosis) as shares of the
/// leaf area, and returns a mask of lesion pixels for display. A screening heuristic, not a diagnosis.
/// </summary>
public static class LeafAnalysis
{
    public static LeafHealth Analyse(PixelBuffer px, int maskWidth = 192)
    {
        var scale = Math.Min(1.0, maskWidth / (double)px.Width);
        var mw = Math.Max(1, (int)(px.Width * scale));
        var mh = Math.Max(1, (int)(px.Height * scale));
        var mask = new byte[mw * mh];
        long green = 0, yellow = 0, lesion = 0, total = 0;
        for (var y = 0; y < px.Height; y += 2)
        {
            for (var x = 0; x < px.Width; x += 2)
            {
                total++;
                var (h, s, v) = px.Hsv(x, y);
                if (s < 0.2 || v < 0.15)
                {
                    continue;
                }

                if (h is >= 70 and <= 170)
                {
                    green++;
                }
                else if (h is >= 45 and < 70)
                {
                    yellow++;
                }
                else if (h is >= 10 and < 45 && v < 0.75)
                {
                    lesion++;
                    var mx = Math.Min(mw - 1, (int)(x * scale));
                    var my = Math.Min(mh - 1, (int)(y * scale));
                    mask[my * mw + mx] = 255;
                }
            }
        }

        var leaf = green + yellow + lesion;
        var lesionMask = lesion == 0 ? null : new OverlayMask { Width = mw, Height = mh, Data = Convert.ToBase64String(mask), Mode = "tint", Color = "#e5534b", Opacity = 0.7f };
        return leaf == 0
            ? new LeafHealth(0, 0, 0, 0, null)
            : new LeafHealth(leaf / (double)total, green / (double)leaf, yellow / (double)leaf, lesion / (double)leaf, lesionMask);
    }
}
