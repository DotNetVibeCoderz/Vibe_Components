using Agritech.Core.Data;

namespace Agritech.Drones;

/// <summary>Connected region detected in an image.</summary>
public sealed record Blob(int Id, int PixelCount, int MinX, int MinY, int MaxX, int MaxY, double CenterX, double CenterY);

public sealed record WeedDetectionResult(
    double VegetationCoverPct, double WeedCoverPct, IReadOnlyList<int> CropRowCentersX, IReadOnlyList<Blob> Weeds, bool[] VegetationMask, bool[] WeedMask);

/// <summary>
/// Classical between-row weed detection for top-down RGB imagery:
/// 1) Excess-Green index (ExG = 2g − r − b on chromatic coordinates) + Otsu threshold → vegetation mask;
/// 2) column projection peaks → crop rows; 3) vegetation outside a row band → weed; 4) connected components → weed patches.
/// </summary>
public static class WeedDetector
{
    public static WeedDetectionResult Detect(RgbImage img, int expectedRows = 0, double rowBandFraction = 0.3, int minBlobPixels = 6)
    {
        int w = img.Width, h = img.Height, n = w * h;
        var exg = new double[n];
        var px = img.Pixels;
        for (int i = 0; i < n; i++)
        {
            double r = px[i * 3], g = px[i * 3 + 1], b = px[i * 3 + 2];
            double s = r + g + b;
            exg[i] = s == 0 ? 0 : (2 * g - r - b) / s;
        }
        double thr = Math.Max(Stats.OtsuThreshold(exg), 0.05);
        var veg = new bool[n];
        int vegCount = 0;
        for (int i = 0; i < n; i++) if (veg[i] = exg[i] > thr) vegCount++;

        // Column projection and peak finding (rows assumed roughly vertical in the frame).
        var proj = new double[w];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (veg[y * w + x]) proj[x]++;
        var smooth = TimeSeriesExtensions.MovingAverage(proj, Math.Max(3, w / 40));
        // centre the moving average
        int shift = Math.Max(3, w / 40) / 2;
        var centred = new double[w];
        for (int x = 0; x < w; x++) centred[x] = smooth[Math.Min(w - 1, x + shift)];
        var rows = FindPeaks(centred, expectedRows, minSeparation: Math.Max(8, w / 12));

        double rowPitch = rows.Count > 1 ? (rows[^1] - rows[0]) / (double)(rows.Count - 1) : w;
        double half = rowPitch * rowBandFraction / 2;
        var weed = new bool[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!veg[i]) continue;
                bool inRow = rows.Any(rx => Math.Abs(x - rx) <= half);
                weed[i] = !inRow;
            }
        var blobs = ConnectedComponents(weed, w, h, minBlobPixels);
        int weedPx = blobs.Sum(b => b.PixelCount);
        return new WeedDetectionResult(Math.Round(vegCount * 100.0 / n, 2), Math.Round(weedPx * 100.0 / n, 2), rows, blobs, veg, weed);
    }

    private static List<int> FindPeaks(double[] s, int expected, int minSeparation)
    {
        var cands = new List<(int X, double V)>();
        double mean = Stats.Mean(s);
        for (int i = 1; i < s.Length - 1; i++)
            if (s[i] >= s[i - 1] && s[i] >= s[i + 1] && s[i] > mean * 1.2) cands.Add((i, s[i]));
        var picked = new List<int>();
        foreach (var c in cands.OrderByDescending(c => c.V))
        {
            if (picked.All(p => Math.Abs(p - c.X) >= minSeparation)) picked.Add(c.X);
            if (expected > 0 && picked.Count == expected) break;
        }
        picked.Sort();
        return picked;
    }

    /// <summary>4-connected component labelling (iterative flood fill).</summary>
    public static List<Blob> ConnectedComponents(bool[] mask, int w, int h, int minPixels = 1)
    {
        var labels = new int[mask.Length];
        var blobs = new List<Blob>();
        var stack = new Stack<int>();
        int next = 0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0) continue;
            next++;
            int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0; long sx = 0, sy = 0;
            stack.Push(start); labels[start] = next;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                count++; sx += x; sy += y;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                if (x > 0) Visit(p - 1); if (x < w - 1) Visit(p + 1);
                if (y > 0) Visit(p - w); if (y < h - 1) Visit(p + w);
            }
            if (count >= minPixels) blobs.Add(new Blob(blobs.Count + 1, count, minX, minY, maxX, maxY, sx / (double)count, sy / (double)count));
        }
        return blobs;

        void Visit(int q)
        {
            if (mask[q] && labels[q] == 0) { labels[q] = next; stack.Push(q); }
        }
    }
}

public sealed record DiseaseAssessment(string Label, double Confidence, double SeverityPct, string Advice);

/// <summary>Pluggable plant disease classifier (e.g. wrap an ONNX/TorchSharp/ML.NET model).</summary>
public interface IPlantDiseaseClassifier
{
    DiseaseAssessment Classify(RgbImage leafImage);
}

/// <summary>
/// Model-free baseline: quantifies lesion severity as the share of chlorotic (yellow) and necrotic (brown) pixels within
/// the plant tissue — the standard visual "percent diseased area" metric. Swap in a trained CNN for species-specific labels.
/// </summary>
public sealed class LesionSeverityAnalyzer : IPlantDiseaseClassifier
{
    public DiseaseAssessment Classify(RgbImage img)
    {
        int healthy = 0, chlorotic = 0, necrotic = 0;
        var px = img.Pixels;
        for (int i = 0; i < px.Length; i += 3)
        {
            var (hue, sat, val) = Hsv(px[i], px[i + 1], px[i + 2]);
            if (val < 0.12 || sat < 0.15) continue; // background / shadow / glare
            if (hue >= 70 && hue <= 170) healthy++;
            else if (hue >= 40 && hue < 70) chlorotic++;
            else if (hue < 40 || hue > 330) necrotic++;
        }
        int tissue = healthy + chlorotic + necrotic;
        if (tissue == 0) return new DiseaseAssessment("no-plant", 0, 0, "No plant tissue detected in image.");
        double sev = (chlorotic + necrotic) * 100.0 / tissue;
        double necShare = (chlorotic + necrotic) == 0 ? 0 : necrotic / (double)(chlorotic + necrotic);
        string label = sev < 3 ? "healthy" : necShare > 0.6 ? "necrotic-lesions (fungal/bacterial spot suspected)" : "chlorosis (nutrient deficiency or viral suspected)";
        string advice = sev < 3 ? "No action required; continue scouting."
            : sev < 15 ? "Early infection: remove affected leaves, improve airflow, scout neighbouring plants within 3 days."
            : "Severe: confirm diagnosis with extension officer/lab and apply recommended protectant; isolate area.";
        double conf = Math.Clamp(0.55 + Math.Abs(sev - 10) / 100, 0.55, 0.9);
        return new DiseaseAssessment(label, Math.Round(conf, 2), Math.Round(sev, 2), advice);
    }

    private static (double H, double S, double V) Hsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
