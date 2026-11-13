using System.Numerics;
using FellowOakDicom;
using Medical.AI;
using Medical.Core;
using Medical.Dicom;
using Medical.Native;
using Medical.Signals;
using Medical.Visualization;

namespace Medical.Gallery.Shared;

public sealed record PlaneImage(string Label, byte[] Png, string Description);
public sealed record MprView(IReadOnlyList<PlaneImage> Planes, PlaneImage Slab, string Volume, KernelBackend Backend, double Ms);

/// <summary>A mesh ready for a Y-up renderer (x = patient left, y = head, z = anterior), centred on the skull.</summary>
public sealed record DisplayMesh(float[] Positions, float[] Normals, int[] Indices, int Triangles, double VolumeMl, string Color);
public sealed record Volume3DView(DisplayMesh Skull, DisplayMesh Lesion, float Radius, int StlBytes, string Volume, double LesionMl, double ExtractMs);

public sealed record Ecg12View(string Grid, string Medians, Ecg12Analysis Analysis, Ecg12Truth Truth, IReadOnlyList<(string Formula, double Ms)> Qtc);

public sealed record AiFinding(string Label, string Detail, string Category);
public sealed record ImagingAiView(byte[] Overlay, IReadOnlyList<AiFinding> Findings, Localized Impression, string SrTree, int SegBytes, int SrBytes,
    int SegFrames, string Method, bool Chest);

public static partial class Scenarios
{
    private static readonly Lazy<DicomVolume> HeadVolume = new(() => DicomVolume.FromSeries(HeadCt.Value));
    private static readonly Lazy<DicomVolume> ChestVolume = new(() => DicomVolume.FromSeries(ChestCt.Value));
    private static readonly Lazy<Volume3DView> Volume3DCache = new(BuildVolume3D);
    private static readonly Dictionary<bool, ImagingAiView> AiCache = [];

    // ---------------------------------------------------------------- MPR / MIP

    public static MprView Mpr(bool chest = false, double position = 0.5, SlabMode mode = SlabMode.Max, double slabMm = 30)
    {
        var v = chest ? ChestVolume.Value : HeadVolume.Value;
        var w = chest ? WindowPreset.Lung : WindowPreset.Brain;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var planes = new[] { VolumePlane.Axial, VolumePlane.Coronal, VolumePlane.Sagittal }
            .Select(p => { var img = VolumeRendering.Mpr(v, p, position); return new PlaneImage(p.ToString(), img.ToPng(w), img.Description); }).ToList();
        var slabPlane = chest ? VolumePlane.Coronal : VolumePlane.Axial;
        var slab = VolumeRendering.Project(v, slabPlane, mode, position, slabMm);
        var slabWindow = mode switch { SlabMode.Max => chest ? WindowPreset.Bone : WindowPreset.Subdural, SlabMode.Min => WindowPreset.Lung, _ => w };
        double ms = sw.Elapsed.TotalMilliseconds;
        return new MprView(planes, new PlaneImage($"{slabPlane} {mode}", slab.ToPng(slabWindow), $"{slab.Description} · {slabWindow.Name}"), v.ToString(),
            MedicalKernels.SlabBackend, Math.Round(ms, 1));
    }

    // ---------------------------------------------------------------- 3D

    public static Volume3DView Volume3D() => Volume3DCache.Value;

    private static Volume3DView BuildVolume3D()
    {
        var v = HeadVolume.Value;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var skull = IsoSurface.Extract(v, 300, downsample: 2);
        var lesion = IsoSurface.ExtractMask(v, HemorrhageScreener.SegmentVolume(v));
        double ms = sw.Elapsed.TotalMilliseconds;
        var (min, max) = skull.Bounds;
        var center = (min + max) / 2;
        float radius = Vector3.Distance(min, max) / 2;
        return new Volume3DView(ToDisplay(skull.Smooth(14), center, "#C9B994"), ToDisplay(lesion.Smooth(8), center, "#B8261E"), radius, skull.ToStl().Length,
            v.ToString(), Math.Round(lesion.VolumeMl, 2), Math.Round(ms));
    }

    /// <summary>LPS (x left, y posterior, z head) → Y-up display (x left, y head, z anterior): a proper rotation, winding kept.</summary>
    private static DisplayMesh ToDisplay(SurfaceMesh m, Vector3 center, string color)
    {
        var pos = new float[m.Positions.Length * 3];
        var nrm = new float[m.Normals.Length * 3];
        for (int i = 0; i < m.Positions.Length; i++)
        {
            var p = m.Positions[i] - center; var n = m.Normals[i];
            pos[i * 3] = p.X; pos[i * 3 + 1] = p.Z; pos[i * 3 + 2] = -p.Y;
            nrm[i * 3] = n.X; nrm[i * 3 + 1] = n.Z; nrm[i * 3 + 2] = -n.Y;
        }
        return new DisplayMesh(pos, nrm, m.Indices, m.TriangleCount, Math.Round(m.VolumeMl, 2), color);
    }

    /// <summary>Little-endian base64 of a float or int array, for the browser (Float32Array / Uint32Array).</summary>
    public static string Base64(float[] data)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static string Base64(int[] data)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    // ---------------------------------------------------------------- 12-lead ECG

    public static Ecg12View Ecg12(StPattern pattern = StPattern.InferiorStemi, double heartRate = 72, double qtc = 400, bool af = false, Gender sex = Gender.Male, int age = 58, string lang = "en")
    {
        var (ecg, truth) = Ecg12Simulator.Generate(new Ecg12Options
        {
            StPattern = pattern, HeartRate = heartRate, QtcMs = qtc, Rhythm = af ? Rhythm.AtrialFibrillation : Rhythm.NormalSinus,
        });
        var a = Ecg12Analyzer.Analyze(ecg, new EcgPatient(sex, age));
        var grid = Charts.Ecg12Grid(ecg, 2.9, a.MeetsStemiCriteria ? a.Leads.Where(l => l.StJMv >= 0.1).Select(l => l.Lead) : []);
        (string, double)[] qtcs = [("Bazett", a.QtcBazettMs), ("Fridericia", a.QtcFridericiaMs), ("Framingham", a.QtcFraminghamMs), ("Hodges", a.QtcHodgesMs)];
        return new Ecg12View(grid, Charts.MedianBeats(a, 2.6, lang), a, truth, qtcs);
    }

    // ---------------------------------------------------------------- Imaging AI → DICOM SEG + SR

    public static ImagingAiView ImagingAi(bool chest = false)
    {
        lock (AiCache)
        {
            if (AiCache.TryGetValue(chest, out var cached)) return cached;
            var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(7));
            var v = chest ? ChestVolume.Value : HeadVolume.Value;
            ImagingAiOutput output;
            var findings = new List<AiFinding>();
            Localized impression;
            string method;
            if (chest)
            {
                var r = LungNoduleDetector.Detect(v);
                output = ImagingAiReports.LungNodules(v, r, at)!;
                findings.AddRange(r.Nodules.Select(n => new AiFinding($"Nodule {n.Id}",
                    $"{n.LongAxisMm} × {n.ShortAxisMm} mm · {n.VolumeMm3:0} mm³ · {n.MeanHu} HU", $"Lung-RADS {n.LungRads}")));
                impression = r.Impression; method = r.Method;
            }
            else
            {
                var mask = HemorrhageScreener.SegmentVolume(v);
                output = ImagingAiReports.Hemorrhage(v, mask, at)!;
                var f = output.Findings[0];
                findings.Add(new AiFinding("Haemorrhage 1", string.Join(" · ", f.Measurements.Select(m => $"{m.Concept.Meaning} {m.Value} {m.Unit.Value}")), "SNOMED 50960005"));
                impression = HemorrhageScreener.Analyze(HeadCt.Value).Impression; method = "HU 50–90 + bone suppression + components";
            }
            // Round trip: overlay what the SEG says, read back from its DICOM bytes.
            using var ms = new MemoryStream();
            output.Segmentation!.Save(ms);
            ms.Position = 0;
            var seg = DicomSegmentation.Read(DicomFile.Open(ms), v);
            using var srMs = new MemoryStream();
            output.Report.Save(srMs);
            int plane = v.Width * v.Height;
            int k = Enumerable.Range(0, v.Depth).MaxBy(z => seg.Segments.Sum(s => s.Mask.AsSpan(z * plane, plane).Count((byte)1)));
            var gray = new byte[plane];
            var w = chest ? WindowPreset.Lung : WindowPreset.Brain;
            MedicalKernels.WindowLevel(v.Voxels.AsSpan(k * plane, plane), gray, 1, 0, w.Center, w.Width);
            var rgba = new byte[plane * 4];
            for (int i = 0; i < plane; i++)
            {
                bool hit = seg.Segments.Any(s => s.Mask[k * plane + i] != 0);
                byte g = gray[i];
                rgba[i * 4] = hit ? (byte)Math.Min(255, 150 + g / 3) : g;
                rgba[i * 4 + 1] = hit ? (byte)(g / 4) : g;
                rgba[i * 4 + 2] = hit ? (byte)(g / 4) : g;
                rgba[i * 4 + 3] = 255;
            }
            var view = new ImagingAiView(PngEncoder.EncodeRgba(rgba, v.Width, v.Height), findings, impression, MeasurementReport.Read(output.Report).Text,
                (int)ms.Length, (int)srMs.Length, int.Parse(output.Segmentation.Dataset.GetString(DicomTag.NumberOfFrames)), method, chest);
            AiCache[chest] = view;
            return view;
        }
    }

    // ---------------------------------------------------------------- transfer syntaxes

    public sealed record CodecResult(string Name, string Uid, long Bytes, double Ratio, int MaxError, bool Lossless);

    /// <summary>Encodes one slice with every supported transfer syntax and decodes it back.</summary>
    public static IReadOnlyList<CodecResult> Codecs(int slice = 14)
    {
        var src = HeadCt.Value[Math.Clamp(slice, 0, HeadCt.Value.Count - 1)];
        var original = DicomImaging.FrameI16(src.Dataset);
        long raw = DicomCodecs.PixelDataBytes(src.Dataset);
        var list = new List<CodecResult>();
        foreach (var c in new[] { DicomCompression.Rle, DicomCompression.JpegLossless, DicomCompression.JpegLsLossless, DicomCompression.Jpeg2000Lossless,
                     DicomCompression.HtJpeg2000Lossless, DicomCompression.JpegLsNearLossless, DicomCompression.Jpeg2000 })
        {
            var enc = DicomCodecs.Transcode(src, c, c == DicomCompression.Jpeg2000 ? 20 : null);
            var back = DicomImaging.FrameI16(enc.Dataset);
            int err = 0;
            for (int i = 0; i < back.Length; i++) err = Math.Max(err, Math.Abs(back[i] - original[i]));
            long bytes = DicomCodecs.PixelDataBytes(enc.Dataset);
            list.Add(new CodecResult(enc.Dataset.InternalTransferSyntax.UID.Name, enc.Dataset.InternalTransferSyntax.UID.UID, bytes, Math.Round((double)raw / bytes, 1), err, err == 0));
        }
        return list;
    }
}
