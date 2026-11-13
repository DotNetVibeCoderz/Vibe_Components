using System.Globalization;
using System.Runtime.InteropServices;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Medical.Core;
using Medical.Native;

namespace Medical.Dicom;

/// <summary>A named VOI window (centre / width in rescaled units, e.g. Hounsfield units for CT).</summary>
public readonly record struct WindowPreset(string Name, double Center, double Width)
{
    public static readonly WindowPreset Brain = new("Brain", 40, 80);
    public static readonly WindowPreset Subdural = new("Subdural", 75, 215);
    public static readonly WindowPreset Stroke = new("Stroke", 40, 40);
    public static readonly WindowPreset SoftTissue = new("Soft tissue", 40, 400);
    public static readonly WindowPreset Mediastinum = new("Mediastinum", 50, 350);
    public static readonly WindowPreset Liver = new("Liver", 30, 150);
    public static readonly WindowPreset Lung = new("Lung", -600, 1500);
    public static readonly WindowPreset Bone = new("Bone", 400, 1800);

    public static IReadOnlyList<WindowPreset> Ct { get; } = [Brain, Subdural, Stroke, SoftTissue, Mediastinum, Liver, Lung, Bone];

    public static WindowPreset? Find(string name) =>
        Ct.Cast<WindowPreset?>().FirstOrDefault(p => p!.Value.Name.Replace(" ", "").Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Key metadata of a DICOM instance.</summary>
public sealed record DicomImageInfo
{
    public string PatientName { get; init; } = "";
    public string PatientId { get; init; } = "";
    public string? PatientSex { get; init; }
    public string? PatientBirthDate { get; init; }
    public string Modality { get; init; } = "";
    public string StudyInstanceUid { get; init; } = "";
    public string SeriesInstanceUid { get; init; } = "";
    public string SopInstanceUid { get; init; } = "";
    public string SopClassUid { get; init; } = "";
    public string? StudyDescription { get; init; }
    public string? SeriesDescription { get; init; }
    public string? BodyPart { get; init; }
    public string? StudyDate { get; init; }
    public int Rows { get; init; }
    public int Columns { get; init; }
    public int Frames { get; init; } = 1;
    public int BitsAllocated { get; init; }
    public int BitsStored { get; init; }
    public bool Signed { get; init; }
    public string Photometric { get; init; } = "MONOCHROME2";
    public double RescaleSlope { get; init; } = 1;
    public double RescaleIntercept { get; init; }
    public double? WindowCenter { get; init; }
    public double? WindowWidth { get; init; }
    public double? PixelSpacingRow { get; init; }
    public double? PixelSpacingColumn { get; init; }
    public double? SliceThickness { get; init; }
    public double? SliceLocation { get; init; }
    public int? InstanceNumber { get; init; }
    public string TransferSyntax { get; init; } = "";
    public bool IsCompressed { get; init; }
}

/// <summary>Grayscale image produced by <see cref="DicomImaging.Render"/>.</summary>
public sealed record RenderedImage(int Width, int Height, byte[] Gray8, WindowPreset Window)
{
    public byte[] ToPng() => PngEncoder.EncodeGray8(Gray8, Width, Height);
}

/// <summary>Reading, rendering and measuring DICOM images (uncompressed transfer syntaxes; compressed frames need fo-dicom codecs).</summary>
public static class DicomImaging
{
    public static DicomFile Open(string path) => DicomFile.Open(path, FileReadOption.ReadAll);

    public static DicomFile Open(Stream stream) => DicomFile.Open(stream, FileReadOption.ReadAll);

    public static DicomImageInfo Describe(DicomDataset ds)
    {
        double[] spacing = ds.TryGetValues<double>(DicomTag.PixelSpacing, out double[]? sp) ? sp : [];
        var ts = ds.InternalTransferSyntax;
        return new DicomImageInfo
        {
            PatientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, ""),
            PatientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, ""),
            PatientSex = ds.GetSingleValueOrDefault<string?>(DicomTag.PatientSex, null),
            PatientBirthDate = ds.GetSingleValueOrDefault<string?>(DicomTag.PatientBirthDate, null),
            Modality = ds.GetSingleValueOrDefault(DicomTag.Modality, ""),
            StudyInstanceUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, ""),
            SeriesInstanceUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, ""),
            SopInstanceUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, ""),
            SopClassUid = ds.GetSingleValueOrDefault(DicomTag.SOPClassUID, ""),
            StudyDescription = ds.GetSingleValueOrDefault<string?>(DicomTag.StudyDescription, null),
            SeriesDescription = ds.GetSingleValueOrDefault<string?>(DicomTag.SeriesDescription, null),
            BodyPart = ds.GetSingleValueOrDefault<string?>(DicomTag.BodyPartExamined, null),
            StudyDate = ds.GetSingleValueOrDefault<string?>(DicomTag.StudyDate, null),
            Rows = ds.GetSingleValueOrDefault(DicomTag.Rows, (ushort)0),
            Columns = ds.GetSingleValueOrDefault(DicomTag.Columns, (ushort)0),
            Frames = ds.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1),
            BitsAllocated = ds.GetSingleValueOrDefault(DicomTag.BitsAllocated, (ushort)16),
            BitsStored = ds.GetSingleValueOrDefault(DicomTag.BitsStored, (ushort)16),
            Signed = ds.GetSingleValueOrDefault(DicomTag.PixelRepresentation, (ushort)0) == 1,
            Photometric = ds.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2"),
            RescaleSlope = ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0),
            RescaleIntercept = ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0),
            WindowCenter = ds.TryGetValue(DicomTag.WindowCenter, 0, out double wc) ? wc : null,
            WindowWidth = ds.TryGetValue(DicomTag.WindowWidth, 0, out double ww) ? ww : null,
            PixelSpacingRow = spacing.Length > 0 ? spacing[0] : null,
            PixelSpacingColumn = spacing.Length > 1 ? spacing[1] : null,
            SliceThickness = ds.TryGetSingleValue(DicomTag.SliceThickness, out double st) ? st : null,
            SliceLocation = ds.TryGetSingleValue(DicomTag.SliceLocation, out double sl) ? sl : null,
            InstanceNumber = ds.TryGetSingleValue(DicomTag.InstanceNumber, out int inum) ? inum : null,
            TransferSyntax = ts.UID.Name,
            IsCompressed = ts.IsEncapsulated,
        };
    }

    /// <summary>Raw stored pixel values of a frame as signed 16-bit (16-bit unsigned data must use <see cref="FrameU16"/>).</summary>
    public static short[] FrameI16(DicomDataset ds, int frame = 0)
    {
        var bytes = FrameBytes(ds, frame);
        return MemoryMarshal.Cast<byte, short>(bytes).ToArray();
    }

    public static ushort[] FrameU16(DicomDataset ds, int frame = 0) => MemoryMarshal.Cast<byte, ushort>(FrameBytes(ds, frame)).ToArray();

    private static ReadOnlySpan<byte> FrameBytes(DicomDataset ds, int frame)
    {
        if (ds.InternalTransferSyntax.IsEncapsulated)
            throw new NotSupportedException($"Compressed transfer syntax '{ds.InternalTransferSyntax.UID.Name}' — transcode to Explicit VR Little Endian first (fo-dicom native codecs).");
        var pd = DicomPixelData.Create(ds);
        if (pd.BitsAllocated != 16 && pd.BitsAllocated != 8) throw new NotSupportedException($"BitsAllocated {pd.BitsAllocated} not supported.");
        return pd.GetFrame(frame).Data;
    }

    /// <summary>
    /// Renders one frame to 8-bit grayscale: Modality LUT (rescale) → linear VOI window → MONOCHROME1 inversion.
    /// Uses the dataset's window when <paramref name="window"/> is null. Runs on the Rust kernel when available.
    /// </summary>
    public static RenderedImage Render(DicomDataset ds, WindowPreset? window = null, int frame = 0)
    {
        var info = Describe(ds);
        var w = window ?? (info.WindowCenter is { } c && info.WindowWidth is { } wd ? new WindowPreset("Default", c, wd) : AutoWindow(ds, info, frame));
        var dst = new byte[info.Rows * info.Columns];
        bool invert = info.Photometric == "MONOCHROME1";
        if (info.BitsAllocated == 8)
        {
            var src = FrameBytes(ds, frame);
            for (int i = 0; i < dst.Length; i++) dst[i] = MedicalKernels.WindowValue(src[i] * info.RescaleSlope + info.RescaleIntercept, w.Center, w.Width, invert);
        }
        else if (info.Signed) MedicalKernels.WindowLevel(MemoryMarshal.Cast<byte, short>(FrameBytes(ds, frame)), dst, info.RescaleSlope, info.RescaleIntercept, w.Center, w.Width, invert);
        else MedicalKernels.WindowLevel(MemoryMarshal.Cast<byte, ushort>(FrameBytes(ds, frame)), dst, info.RescaleSlope, info.RescaleIntercept, w.Center, w.Width, invert);
        return new RenderedImage(info.Columns, info.Rows, dst, w);
    }

    /// <summary>Min/max window when the dataset declares none.</summary>
    private static WindowPreset AutoWindow(DicomDataset ds, DicomImageInfo info, int frame)
    {
        var s = Statistics(ds, frame);
        return new WindowPreset("Auto", (s.Min + s.Max) / 2, Math.Max(1, s.Max - s.Min));
    }

    /// <summary>Statistics of rescaled values (HU for CT) over the frame or a rectangular ROI.</summary>
    public static PixelStats Statistics(DicomDataset ds, int frame = 0, (int X, int Y, int W, int H)? roi = null)
    {
        var info = Describe(ds);
        short[] px = info.Signed ? FrameI16(ds, frame) : FrameU16(ds, frame).Select(v => (short)Math.Min(v, (ushort)short.MaxValue)).ToArray();
        if (roi is not { } r) return MedicalKernels.Stats(px, info.RescaleSlope, info.RescaleIntercept);
        var buf = new short[r.W * r.H];
        for (int y = 0; y < r.H; y++)
            px.AsSpan((r.Y + y) * info.Columns + r.X, r.W).CopyTo(buf.AsSpan(y * r.W));
        return MedicalKernels.Stats(buf, info.RescaleSlope, info.RescaleIntercept);
    }

    /// <summary>Histogram of rescaled values in <paramref name="bins"/> equal bins over [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static int[] Histogram(DicomDataset ds, double min, double max, int bins = 64, int frame = 0)
    {
        var info = Describe(ds);
        var h = new int[bins];
        double scale = bins / (max - min);
        foreach (var raw in FrameI16(ds, frame))
        {
            double v = raw * info.RescaleSlope + info.RescaleIntercept;
            int b = (int)((v - min) * scale);
            if (b >= 0 && b < bins) h[b]++;
        }
        return h;
    }

    /// <summary>Formats a DICOM DA/TM-style value for display.</summary>
    public static string FormatDate(string? da) =>
        da is { Length: 8 } && DateOnly.TryParseExact(da, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : da ?? "";
}
