using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;
using MediaPipeNet.Imaging;
using MediaPipeNet.Tasks.Vision;

namespace AquaCulture.ComputerVision.MediaPipe;

/// <summary>Size of one fish or shrimp measured from a segmentation mask.</summary>
/// <param name="LengthPx">Extent along the body's principal axis (snout to tail tip), in image pixels.</param>
/// <param name="DepthPx">Extent across the principal axis (body depth), in image pixels.</param>
/// <param name="AngleDeg">Orientation of the principal axis (0 = horizontal).</param>
/// <param name="CentroidX">Mask centroid, normalised 0–1 (used to follow the animal across video frames).</param>
public sealed record FishMeasurement(double LengthPx, double DepthPx, double AreaPx, double AngleDeg, double CentroidX, double CentroidY,
    double? LengthCm, double? WeightG, double MaskCoverage)
{
    /// <summary>True when the mask looks like one elongated animal (aspect ≥ 2, 0.2–60 % of the frame).</summary>
    public bool Plausible => DepthPx > 0 && LengthPx / DepthPx >= 2 && MaskCoverage is > 0.002 and < 0.6;
}

/// <summary>
/// Measures fish and shrimp length from photos or video frames with MediaPipe's MagicTouch interactive segmenter (MediaPipeNet): one tap
/// (or the frame centre) on the animal gives a mask; the length is the extent along the principal axis (PCA), so tilted animals measure
/// correctly. With a scale (<see cref="CmPerPixel"/>, for instance from a measuring board) lengths become cm and, with a species,
/// weights through the species length–weight relationship.
/// </summary>
public sealed class FishMeasurer : IDisposable
{
    private readonly InteractiveSegmenter _segmenter;

    public FishMeasurer(SpeciesProfile? species = null, double? cmPerPixel = null, InteractiveSegmenterOptions? options = null)
    {
        Species = species; CmPerPixel = cmPerPixel;
        _segmenter = InteractiveSegmenter.Create(options ?? new InteractiveSegmenterOptions { OutputConfidenceMasks = true });
    }

    public SpeciesProfile? Species { get; }
    /// <summary>Image scale in cm per pixel at the animal's distance; null to report pixels only.</summary>
    public double? CmPerPixel { get; set; }
    /// <summary>Mask confidence threshold.</summary>
    public float Threshold { get; init; } = 0.5f;

    /// <summary>Scale from a reference object of known length seen in the image (e.g. a 30 cm measuring board spanning 600 px).</summary>
    public static double ScaleFromReference(double referenceLengthPx, double referenceLengthCm) => referenceLengthCm / referenceLengthPx;

    /// <summary>Measures the animal under the normalised point (<paramref name="x"/>, <paramref name="y"/>), default the image centre.</summary>
    public FishMeasurement Measure(MPImage image, float x = 0.5f, float y = 0.5f)
    {
        var r = _segmenter.Segment(image, RegionOfInterest.FromKeypoint(x, y), null!);
        var m = r.ConfidenceMask ?? r.ConfidenceMasks[0];
        return FromMask(m.Data, m.Width, m.Height, image.Width, image.Height, Threshold, CmPerPixel, Species);
    }

    public FishMeasurement Measure(string imagePath, float x = 0.5f, float y = 0.5f)
    {
        using var img = MPImage.Load(imagePath);
        return Measure(img, x, y);
    }

    /// <summary>
    /// Measures one animal through a sequence of frames, re-centring the tap on the previous mask's centroid, and returns the median of the
    /// plausible measurements (frames where the animal bends or leaves the view are dropped).
    /// </summary>
    public (FishMeasurement? Median, IReadOnlyList<FishMeasurement> Frames) MeasureVideo(IEnumerable<MPImage> frames, float x = 0.5f, float y = 0.5f)
    {
        var list = new List<FishMeasurement>();
        foreach (var f in frames)
        {
            var m = Measure(f, x, y);
            list.Add(m);
            if (m.Plausible) { x = (float)m.CentroidX; y = (float)m.CentroidY; }
        }
        var ok = list.Where(m => m.Plausible).OrderBy(m => m.LengthPx).ToList();
        return (ok.Count == 0 ? null : ok[ok.Count / 2], list);
    }

    /// <summary>Principal-axis measurement of a confidence mask (exposed for masks from other segmenters).</summary>
    public static FishMeasurement FromMask(ReadOnlySpan<float> mask, int maskW, int maskH, int imageW, int imageH, float threshold = 0.5f,
        double? cmPerPixel = null, SpeciesProfile? species = null)
    {
        double sx = imageW / (double)maskW, sy = imageH / (double)maskH;
        double n = 0, mx = 0, my = 0;
        for (int j = 0; j < maskH; j++)
            for (int i = 0; i < maskW; i++)
                if (mask[j * maskW + i] > threshold) { n++; mx += (i + 0.5) * sx; my += (j + 0.5) * sy; }
        if (n == 0) return new FishMeasurement(0, 0, 0, 0, 0.5, 0.5, null, null, 0);
        mx /= n; my /= n;
        double cxx = 0, cyy = 0, cxy = 0;
        for (int j = 0; j < maskH; j++)
            for (int i = 0; i < maskW; i++)
                if (mask[j * maskW + i] > threshold)
                {
                    double dx = (i + 0.5) * sx - mx, dy = (j + 0.5) * sy - my;
                    cxx += dx * dx; cyy += dy * dy; cxy += dx * dy;
                }
        double theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        double ux = Math.Cos(theta), uy = Math.Sin(theta);
        double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
        for (int j = 0; j < maskH; j++)
            for (int i = 0; i < maskW; i++)
                if (mask[j * maskW + i] > threshold)
                {
                    double dx = (i + 0.5) * sx - mx, dy = (j + 0.5) * sy - my;
                    double a = dx * ux + dy * uy, b = -dx * uy + dy * ux;
                    minA = Math.Min(minA, a); maxA = Math.Max(maxA, a); minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
                }
        double pixel = Math.Sqrt(sx * sy);
        double length = maxA - minA + pixel, depth = maxB - minB + pixel, area = n * sx * sy;
        double? cm = cmPerPixel is { } k ? Math.Round(length * k, 2) : null;
        double? w = cm is { } l && species is not null ? Math.Round(species.WeightFromLength(l), 2) : null;
        return new FishMeasurement(Math.Round(length, 1), Math.Round(depth, 1), Math.Round(area), Math.Round(theta * 180 / Math.PI, 1),
            mx / imageW, my / imageH, cm, w, n / (maskW * (double)maskH));
    }

    public void Dispose() => _segmenter.Dispose();
}

public static class MediaPipeBuilderExtensions
{
    /// <summary>Marks the MediaPipe measurer as available (create one per camera or species).</summary>
    public static AquaBuilder AddMediaPipeMeasurement(this AquaBuilder b) => b.TryAddModule("ComputerVision.MediaPipe", _ => { });
}
