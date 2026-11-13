using FellowOakDicom;
using Medical.Core;
using Medical.Dicom;
using Medical.Native;

namespace Medical.AI;

/// <summary>One detected pulmonary nodule.</summary>
public sealed record LungNodule(
    int Id,
    Vector3d CenterMm,
    int CenterSlice,
    double LongAxisMm,
    double ShortAxisMm,
    double VolumeMm3,
    double MeanHu,
    string LungRads,
    Localized Recommendation)
{
    /// <summary>Lung-RADS mean diameter: average of long and short axis, rounded to 0.1 mm.</summary>
    public double MeanDiameterMm => Math.Round((LongAxisMm + ShortAxisMm) / 2, 1);
    /// <summary>Diameter of the sphere with the same volume.</summary>
    public double EquivalentDiameterMm => Math.Round(2 * Math.Cbrt(3 * VolumeMm3 / (4 * Math.PI)), 1);
}

public sealed record LungNoduleReport(IReadOnlyList<LungNodule> Nodules, byte[] Mask, double LungVolumeMl, string Method, Localized Impression)
{
    /// <summary>Highest Lung-RADS category among the nodules ("1" when none).</summary>
    public string Category => Nodules.Count == 0 ? "1" : Nodules.Select(n => n.LungRads).OrderBy(c => c switch { "2" => 2, "3" => 3, "4A" => 4, _ => 5 }).Last();
}

/// <summary>
/// Classical, explainable pulmonary nodule detection on chest CT:
/// lung field = air (&lt; −400 HU) not connected to the outside, with 2D holes filled; candidates = soft tissue
/// (&gt; −300 HU) inside the lung field, grouped in 3D; kept when the equivalent diameter is 3–30 mm and the
/// through-plane extent is not vessel-like (≤ 3× the in-plane diameter). Long / short axes come from in-plane principal
/// moments on the largest slice. Categories follow ACR Lung-RADS v2022 for <b>solid</b> nodules at baseline
/// (&lt; 6 mm → 2, 6–&lt; 8 → 3, 8–&lt; 15 → 4A, ≥ 15 → 4B). Juxtapleural and ground-glass nodules are not detected —
/// a triage aid, not a reading. For learned detection use the MONAI <c>lung_nodule_ct_detection</c> bundle in the
/// imaging service.
/// </summary>
public static class LungNoduleDetector
{
    public static LungNoduleReport Detect(DicomVolume v)
    {
        int w = v.Width, h = v.Height, plane = w * h;
        var field = new bool[v.Voxels.Length];
        long lungVoxels = 0;
        for (int k = 0; k < v.Depth; k++)
        {
            var slice = v.Voxels.AsSpan(k * plane, plane);
            var low = new bool[plane];
            for (int i = 0; i < plane; i++) low[i] = slice[i] < -400;
            var outside = FloodFromBorder(low, w, h);
            var lung = new bool[plane];
            for (int i = 0; i < plane; i++) lung[i] = low[i] && !outside[i];
            // keep sizeable air components (lungs, not small gas pockets)
            int minArea = Math.Max(20, plane / 200);
            var keep = new bool[plane];
            foreach (var c in Components2D(lung, w, h)) if (c.Count >= minArea) foreach (int i in c) keep[i] = true;
            // fill holes: everything not reachable from the border without crossing lung
            var notLung = new bool[plane];
            for (int i = 0; i < plane; i++) notLung[i] = !keep[i];
            var reach = FloodFromBorder(notLung, w, h);
            for (int i = 0; i < plane; i++)
            {
                bool inField = keep[i] || !reach[i];
                field[k * plane + i] = inField;
                if (keep[i]) lungVoxels++;
            }
        }

        var cand = new bool[v.Voxels.Length];
        for (int i = 0; i < cand.Length; i++) cand[i] = field[i] && v.Voxels[i] > -300;
        var nodules = new List<LungNodule>();
        var mask = new byte[v.Voxels.Length];
        foreach (var comp in Components3D(cand, w, h, v.Depth))
        {
            double volMm3 = comp.Count * v.VoxelVolumeMm3;
            double eqDiam = 2 * Math.Cbrt(3 * volMm3 / (4 * Math.PI));
            if (eqDiam < 3 || eqDiam > 30) continue;
            var bySlice = comp.GroupBy(i => i / plane).ToDictionary(g => g.Key, g => g.ToList());
            double zExtent = (bySlice.Keys.Max() - bySlice.Keys.Min() + 1) * v.Spacing.Z;
            var (sliceIdx, pts) = bySlice.MaxBy(kv => kv.Value.Count) is var best ? (best.Key, best.Value) : default;
            var (longAxis, shortAxis) = Axes(pts, w, v.Spacing.X, v.Spacing.Y, plane);
            if (zExtent > 3 * Math.Max(longAxis, eqDiam)) continue;       // vessel running through the slices
            if (shortAxis > 0 && longAxis / shortAxis > 3) continue;          // vessel running in-plane
            double cx = 0, cy = 0, cz = 0, hu = 0;
            foreach (int i in comp) { cx += i % w; cy += i / w % h; cz += i / plane; hu += v.Voxels[i]; }
            int id = nodules.Count + 1;
            foreach (int i in comp) mask[i] = (byte)Math.Min(255, id);
            double mean = Math.Round((longAxis + shortAxis) / 2, 1);
            var (cat, rec) = LungRads(mean);
            nodules.Add(new LungNodule(id, v.ToPatient(cx / comp.Count, cy / comp.Count, cz / comp.Count), (int)Math.Round(cz / comp.Count),
                Math.Round(longAxis, 1), Math.Round(shortAxis, 1), Math.Round(volMm3, 0), Math.Round(hu / comp.Count, 1), cat, rec));
        }
        double lungMl = Math.Round(lungVoxels * v.VoxelVolumeMm3 / 1000, 0);
        var impression = nodules.Count == 0
            ? new Localized("No solid pulmonary nodule ≥ 3 mm detected in the lung fields (Lung-RADS 1).", "Tidak tampak nodul paru solid ≥ 3 mm pada lapang paru (Lung-RADS 1).")
            : new Localized(
                $"{nodules.Count} solid nodule(s); largest {nodules.Max(n => n.MeanDiameterMm):0.0} mm mean diameter — Lung-RADS {nodules.MaxBy(n => n.MeanDiameterMm)!.LungRads}. {nodules.MaxBy(n => n.MeanDiameterMm)!.Recommendation.En}",
                $"{nodules.Count} nodul solid; terbesar diameter rerata {nodules.Max(n => n.MeanDiameterMm):0.0} mm — Lung-RADS {nodules.MaxBy(n => n.MeanDiameterMm)!.LungRads}. {nodules.MaxBy(n => n.MeanDiameterMm)!.Recommendation.Id}");
        return new LungNoduleReport(nodules, mask, lungMl, "Lung field (−400 HU, hole fill) + soft-tissue candidates (−300 HU) + 3D shape filter", impression);
    }

    /// <summary>ACR Lung-RADS v2022, baseline screening, solid nodules by mean diameter.</summary>
    public static (string Category, Localized Recommendation) LungRads(double meanDiameterMm) => meanDiameterMm switch
    {
        < 6 => ("2", new("Continue annual screening with LDCT in 12 months.", "Lanjutkan skrining tahunan dengan LDCT 12 bulan lagi.")),
        < 8 => ("3", new("LDCT in 6 months.", "LDCT ulang 6 bulan lagi.")),
        < 15 => ("4A", new("LDCT in 3 months; PET/CT may be considered for solid components ≥ 8 mm.", "LDCT 3 bulan lagi; PET/CT dapat dipertimbangkan untuk komponen solid ≥ 8 mm.")),
        _ => ("4B", new("Diagnostic chest CT with or without contrast, PET/CT and/or tissue sampling; refer to a multidisciplinary team.",
                        "CT toraks diagnostik dengan/tanpa kontras, PET/CT dan/atau biopsi; rujuk ke tim multidisiplin.")),
    };

    private static (double Long, double Short) Axes(List<int> pts, int w, double sx, double sy, int plane)
    {
        double mx = 0, my = 0;
        foreach (int i in pts) { mx += (i % plane % w) * sx; my += (i % plane / w) * sy; }
        mx /= pts.Count; my /= pts.Count;
        double xx = 0, yy = 0, xy = 0;
        foreach (int i in pts)
        {
            double dx = (i % plane % w) * sx - mx, dy = (i % plane / w) * sy - my;
            xx += dx * dx; yy += dy * dy; xy += dx * dy;
        }
        xx /= pts.Count; yy /= pts.Count; xy /= pts.Count;
        double tr = xx + yy, det = xx * yy - xy * xy, disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
        // A filled ellipse with semi-axis a has variance a²/4 along that axis → diameter = 4·sqrt(λ).
        return (4 * Math.Sqrt(tr / 2 + disc), 4 * Math.Sqrt(Math.Max(0, tr / 2 - disc)));
    }

    private static bool[] FloodFromBorder(bool[] passable, int w, int h)
    {
        var seen = new bool[passable.Length];
        var q = new Queue<int>();
        void Seed(int i) { if (passable[i] && !seen[i]) { seen[i] = true; q.Enqueue(i); } }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (q.Count > 0)
        {
            int p = q.Dequeue(), x = p % w, y = p / w;
            if (x > 0) Seed(p - 1);
            if (x < w - 1) Seed(p + 1);
            if (y > 0) Seed(p - w);
            if (y < h - 1) Seed(p + w);
        }
        return seen;
    }

    private static IEnumerable<List<int>> Components2D(bool[] m, int w, int h)
    {
        var seen = new bool[m.Length];
        var stack = new Stack<int>();
        for (int i = 0; i < m.Length; i++)
        {
            if (!m[i] || seen[i]) continue;
            var c = new List<int>();
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop(); c.Add(p);
                int x = p % w, y = p / w;
                foreach (int q in (ReadOnlySpan<int>)[x > 0 ? p - 1 : -1, x < w - 1 ? p + 1 : -1, y > 0 ? p - w : -1, y < h - 1 ? p + w : -1])
                    if (q >= 0 && m[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
            }
            yield return c;
        }
    }

    private static IEnumerable<List<int>> Components3D(bool[] m, int w, int h, int d)
    {
        int plane = w * h;
        var seen = new bool[m.Length];
        var stack = new Stack<int>();
        for (int i = 0; i < m.Length; i++)
        {
            if (!m[i] || seen[i]) continue;
            var c = new List<int>();
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop(); c.Add(p);
                int x = p % w, y = p / w % h, z = p / plane;
                foreach (int q in (ReadOnlySpan<int>)[x > 0 ? p - 1 : -1, x < w - 1 ? p + 1 : -1, y > 0 ? p - w : -1, y < h - 1 ? p + w : -1,
                                                       z > 0 ? p - plane : -1, z < d - 1 ? p + plane : -1])
                    if (q >= 0 && m[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
            }
            yield return c;
        }
    }
}

/// <summary>Imaging AI results packaged as DICOM SEG + SR (TID 1500), ready to store in PACS next to the study.</summary>
/// <summary>DICOM outputs; <see cref="Segmentation"/> is null when the result has no voxel mask (e.g. detector boxes from the service).</summary>
public sealed record ImagingAiOutput(DicomFile? Segmentation, DicomFile Report, IReadOnlyList<SrFinding> Findings);

public static class ImagingAiReports
{
    /// <summary>SEG + SR for a haemorrhage mask (<see cref="HemorrhageScreener.SegmentVolume"/>); null when the mask is empty.</summary>
    public static ImagingAiOutput? Hemorrhage(DicomVolume v, byte[] mask, DateTimeOffset? at = null)
    {
        int voxels = mask.Count(b => b != 0);
        if (voxels == 0) return null;
        double meanHu = Enumerable.Range(0, mask.Length).Where(i => mask[i] != 0).Average(i => (double)v.Voxels[i]);
        var seg = DicomSegmentation.Create(v,
            [new SegmentDefinition("Suspected haemorrhage", DicomCodes.MorphologicallyAbnormalStructure, DicomCodes.Hemorrhage, mask)
                { AlgorithmName = "Medical.Net HemorrhageScreener", AnatomicRegion = DicomCodes.Brain }],
            "HEMORRHAGE", "AI haemorrhage segmentation", at);
        SrFinding[] findings =
        [
            new("Haemorrhage 1", DicomCodes.Hemorrhage,
                [new(DicomCodes.Volume, Math.Round(voxels * v.VoxelVolumeMm3 / 1000, 2), DicomCodes.Milliliter),
                 new(DicomCodes.MeanAttenuation, Math.Round(meanHu, 1), DicomCodes.HounsfieldUnit)])
            { FindingSite = DicomCodes.Brain, SegmentNumber = 1 },
        ];
        return new ImagingAiOutput(seg, MeasurementReport.Create(v, seg, findings, "Medical.Net HemorrhageScreener", at), findings);
    }

    /// <summary>SEG (one segment per nodule) + SR with long axis, mean diameter, volume and attenuation; null when no nodules.</summary>
    public static ImagingAiOutput? LungNodules(DicomVolume v, LungNoduleReport report, DateTimeOffset? at = null)
    {
        if (report.Nodules.Count == 0) return null;
        bool hasMask = report.Mask.Length == v.Voxels.Length && report.Mask.Any(b => b != 0);
        var segments = report.Nodules.Select(n => new SegmentDefinition($"Nodule {n.Id} (Lung-RADS {n.LungRads})", DicomCodes.MorphologicallyAbnormalStructure, DicomCodes.Nodule,
            hasMask ? report.Mask.Select(b => b == n.Id ? (byte)1 : (byte)0).ToArray() : [])
            { AlgorithmName = "Medical.Net LungNoduleDetector", AnatomicRegion = DicomCodes.Lung, RecommendedCieLab = [53000, 41000, 54000] }).ToList();
        var seg = hasMask ? DicomSegmentation.Create(v, segments, "NODULES", "AI lung nodule segmentation", at) : null;
        var findings = report.Nodules.Select(n => new SrFinding($"Nodule {n.Id}", DicomCodes.Nodule,
            [new(DicomCodes.Diameter, n.LongAxisMm, DicomCodes.Millimeter),
             new(DicomCodes.Volume, n.VolumeMm3, new DicomCode("mm3", "UCUM", "cubic millimeter")),
             new(DicomCodes.MeanAttenuation, n.MeanHu, DicomCodes.HounsfieldUnit)])
            { FindingSite = DicomCodes.Lung, SegmentNumber = hasMask ? n.Id : null }).ToList();
        return new ImagingAiOutput(seg, MeasurementReport.Create(v, seg, findings, "Medical.Net LungNoduleDetector", at), findings);
    }
}
