using System.Net.Http.Headers;
using System.Text.Json;
using FellowOakDicom;
using Medical.Core;
using Medical.Dicom;

namespace Medical.AI;

/// <summary>One hyperdense region found on one slice.</summary>
public sealed record LesionFinding(int Slice, int AreaPixels, double AreaMm2, double MeanHu, double CentroidX, double CentroidY);

public sealed record HemorrhageReport(
    bool Suspected,
    double VolumeMl,
    IReadOnlyList<LesionFinding> Findings,
    int SlicesAnalyzed,
    string Method,
    Localized Impression);

/// <summary>
/// Classical, explainable non-contrast CT head screen for acute intracranial haemorrhage: 3×3 smoothing, 50–90 HU
/// window, suppression of voxels within 3 px of bone (partial-volume ring), 4-connected components ≥ <c>minAreaPixels</c>.
/// A triage aid that prioritises studies for radiologist review — not a diagnosis. For learned models use the MONAI
/// service through <see cref="ImagingAiClient"/>.
/// </summary>
public static class HemorrhageScreener
{
    public static HemorrhageReport Analyze(IReadOnlyList<DicomFile> series, int minAreaPixels = 12)
    {
        if (series.Count == 0 || !IsHeadCt(DicomImaging.Describe(series[0].Dataset)))
            return new HemorrhageReport(false, 0, [], 0, "not applicable",
                new("Not applicable — this screen is validated only for non-contrast CT of the head.", "Tidak berlaku — skrining ini hanya untuk CT kepala tanpa kontras."));
        var findings = new List<LesionFinding>();
        double volumeMm3 = 0;
        foreach (var f in series.OrderBy(f => DicomImaging.Describe(f.Dataset).InstanceNumber ?? 0))
        {
            var info = DicomImaging.Describe(f.Dataset);
            var hu = ToHu(f.Dataset, info);
            var mask = Mask(hu, info.Columns, info.Rows);
            double px = (info.PixelSpacingRow ?? 1) * (info.PixelSpacingColumn ?? 1);
            foreach (var c in Components(mask, info.Columns, info.Rows).Where(c => c.Count >= minAreaPixels))
            {
                double mean = c.Average(i => hu[i]);
                findings.Add(new LesionFinding(info.InstanceNumber ?? 0, c.Count, Math.Round(c.Count * px, 1), Math.Round(mean, 1),
                    Math.Round(c.Average(i => i % info.Columns), 1), Math.Round(c.Average(i => i / info.Columns), 1)));
                volumeMm3 += c.Count * px * (info.SliceThickness ?? 1);
            }
        }
        double ml = Math.Round(volumeMm3 / 1000, 2);
        bool suspected = ml >= 0.1;
        var text = suspected
            ? new Localized($"Hyperdense collection suspicious for acute haemorrhage, ≈ {ml:0.0} mL across {findings.Select(x => x.Slice).Distinct().Count()} slice(s) — prioritise for radiologist review.",
                            $"Area hiperdens mencurigakan perdarahan akut, ≈ {ml:0.0} mL pada {findings.Select(x => x.Slice).Distinct().Count()} irisan — prioritaskan untuk dibaca radiolog.")
            : new Localized("No hyperdense collection above threshold.", "Tidak tampak area hiperdens di atas ambang.");
        return new HemorrhageReport(suspected, ml, findings, series.Count, "HU-threshold 50–90 + bone suppression + connected components", text);
    }

    /// <summary>True for CT series of the head/brain (BodyPartExamined or description).</summary>
    public static bool IsHeadCt(DicomImageInfo i) =>
        i.Modality == "CT" && ((i.BodyPart?.Contains("HEAD", StringComparison.OrdinalIgnoreCase) ?? false) || (i.BodyPart?.Contains("BRAIN", StringComparison.OrdinalIgnoreCase) ?? false)
                               || (i.StudyDescription?.Contains("HEAD", StringComparison.OrdinalIgnoreCase) ?? false) || (i.StudyDescription?.Contains("BRAIN", StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>Brain-windowed slice with the detected regions painted red (RGBA PNG) for the viewer.</summary>
    public static byte[] Overlay(DicomFile slice, WindowPreset? window = null)
    {
        var info = DicomImaging.Describe(slice.Dataset);
        var img = DicomImaging.Render(slice.Dataset, window ?? WindowPreset.Brain);
        var mask = Mask(ToHu(slice.Dataset, info), info.Columns, info.Rows);
        var rgba = new byte[img.Gray8.Length * 4];
        for (int i = 0; i < img.Gray8.Length; i++)
        {
            byte g = img.Gray8[i];
            bool m = mask[i];
            rgba[i * 4] = m ? (byte)Math.Min(255, 120 + g / 2) : g;
            rgba[i * 4 + 1] = m ? (byte)(g / 4) : g;
            rgba[i * 4 + 2] = m ? (byte)(g / 4) : g;
            rgba[i * 4 + 3] = 255;
        }
        return PngEncoder.EncodeRgba(rgba, img.Width, img.Height);
    }

    private static float[] ToHu(DicomDataset ds, DicomImageInfo info)
    {
        var raw = DicomImaging.FrameI16(ds);
        var hu = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++) hu[i] = (float)(raw[i] * info.RescaleSlope + info.RescaleIntercept);
        return hu;
    }

    private static bool[] Mask(float[] hu, int w, int h)
    {
        var smooth = new float[hu.Length];
        var bone = new bool[hu.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        s += hu[yy * w + xx]; n++;
                    }
                smooth[y * w + x] = s / n;
                bone[y * w + x] = hu[y * w + x] > 300 || hu[y * w + x] < -200; // bone or air boundary
            }
        var mask = new bool[hu.Length];
        const int r = 3;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = smooth[y * w + x];
                if (v < 50 || v > 90) continue;
                bool nearEdge = false;
                for (int dy = -r; dy <= r && !nearEdge; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h || bone[yy * w + xx]) { nearEdge = true; break; }
                    }
                mask[y * w + x] = !nearEdge;
            }
        return mask;
    }

    private static IEnumerable<List<int>> Components(bool[] mask, int w, int h)
    {
        var seen = new bool[mask.Length];
        var stack = new Stack<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            var comp = new List<int>();
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                comp.Add(p);
                int x = p % w, y = p / w;
                foreach (int q in (ReadOnlySpan<int>)[x > 0 ? p - 1 : -1, x < w - 1 ? p + 1 : -1, y > 0 ? p - w : -1, y < h - 1 ? p + w : -1])
                    if (q >= 0 && mask[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
            }
            yield return comp;
        }
    }
}

/// <summary>
/// Client for the Medical.Net imaging-AI service (<c>services/medical-ai-service</c>, FastAPI + MONAI / PyTorch).
/// Contract: <c>GET /health</c>; <c>POST /v1/ct/hemorrhage</c> with a multipart list of DICOM instances → JSON report.
/// </summary>
public sealed class ImagingAiClient(HttpClient http, string baseUrl)
{
    private readonly string _base = baseUrl.TrimEnd('/');

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try { using var r = await http.GetAsync($"{_base}/health", ct).ConfigureAwait(false); return r.IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    /// <summary>Runs the service's haemorrhage model; falls back to <see cref="HemorrhageScreener"/> when the service is unreachable.</summary>
    public async Task<HemorrhageReport> HemorrhageAsync(IReadOnlyList<DicomFile> series, CancellationToken ct = default)
    {
        if (!await IsHealthyAsync(ct).ConfigureAwait(false)) return HemorrhageScreener.Analyze(series) with { Method = "local fallback: " + HemorrhageScreener.Analyze(series).Method };
        using var content = new MultipartFormDataContent();
        int i = 0;
        foreach (var f in series)
        {
            var ms = new MemoryStream();
            await f.SaveAsync(ms).ConfigureAwait(false);
            var part = new ByteArrayContent(ms.ToArray());
            part.Headers.ContentType = new MediaTypeHeaderValue("application/dicom");
            content.Add(part, "files", $"slice{i++:000}.dcm");
        }
        using var res = await http.PostAsync($"{_base}/v1/ct/hemorrhage", content, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var findings = root.GetProperty("findings").EnumerateArray().Select(e => new LesionFinding(
            e.GetProperty("slice").GetInt32(), e.GetProperty("area_pixels").GetInt32(), e.GetProperty("area_mm2").GetDouble(),
            e.GetProperty("mean_hu").GetDouble(), e.GetProperty("centroid_x").GetDouble(), e.GetProperty("centroid_y").GetDouble())).ToArray();
        return new HemorrhageReport(root.GetProperty("suspected").GetBoolean(), root.GetProperty("volume_ml").GetDouble(), findings,
            root.GetProperty("slices_analyzed").GetInt32(), root.GetProperty("method").GetString() ?? "service",
            new(root.GetProperty("impression_en").GetString() ?? "", root.GetProperty("impression_id").GetString() ?? ""));
    }
}
