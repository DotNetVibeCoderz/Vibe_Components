using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Medical.Native;

namespace Medical.Dicom;

/// <summary>A coded concept (code value, coding scheme designator, meaning) as used in DICOM code sequences.</summary>
public readonly record struct DicomCode(string Value, string Scheme, string Meaning)
{
    public DicomDataset ToDataset() => new()
    {
        { DicomTag.CodeValue, Value }, { DicomTag.CodingSchemeDesignator, Scheme }, { DicomTag.CodeMeaning, Meaning },
    };

    public static DicomCode From(DicomDataset item) => new(
        item.GetSingleValueOrDefault(DicomTag.CodeValue, ""), item.GetSingleValueOrDefault(DicomTag.CodingSchemeDesignator, ""),
        item.GetSingleValueOrDefault(DicomTag.CodeMeaning, ""));

    public override string ToString() => $"({Value}, {Scheme}, \"{Meaning}\")";
}

/// <summary>Codes used by Medical.Net derived objects (SNOMED CT, DICOM DCM, UCUM).</summary>
public static class DicomCodes
{
    public static readonly DicomCode MorphologicallyAbnormalStructure = new("49755003", "SCT", "Morphologically abnormal structure");
    public static readonly DicomCode Tissue = new("85756007", "SCT", "Tissue");
    public static readonly DicomCode Hemorrhage = new("50960005", "SCT", "Hemorrhage");
    public static readonly DicomCode Nodule = new("27925004", "SCT", "Nodule");
    public static readonly DicomCode Bone = new("272673000", "SCT", "Bone");
    public static readonly DicomCode Brain = new("12738006", "SCT", "Brain");
    public static readonly DicomCode Lung = new("39607008", "SCT", "Lung");
    public static readonly DicomCode Volume = new("118565006", "SCT", "Volume");
    public static readonly DicomCode Diameter = new("81827009", "SCT", "Diameter");
    public static readonly DicomCode MeanAttenuation = new("112031", "DCM", "Attenuation Coefficient");
    public static readonly DicomCode FindingSite = new("363698007", "SCT", "Finding Site");
    public static readonly DicomCode ComputedTomography = new("77477000", "SCT", "Computed Tomography");
    public static readonly DicomCode SourceImage = new("121322", "DCM", "Source image for image processing operation");
    public static readonly DicomCode SegmentationDerivation = new("113076", "DCM", "Segmentation");
    public static readonly DicomCode Milliliter = new("ml", "UCUM", "milliliter");
    public static readonly DicomCode Millimeter = new("mm", "UCUM", "millimeter");
    public static readonly DicomCode HounsfieldUnit = new("[hnsf'U]", "UCUM", "Hounsfield unit");
}

/// <summary>One segment of a segmentation: what it is, how it was made, and its voxels.</summary>
public sealed record SegmentDefinition(string Label, DicomCode Category, DicomCode Type, byte[] Mask)
{
    public string AlgorithmName { get; init; } = "Medical.Net";
    public string AlgorithmVersion { get; init; } = Medical.Core.MedicalInfo.Version;
    /// <summary>AUTOMATIC, SEMIAUTOMATIC or MANUAL.</summary>
    public string AlgorithmType { get; init; } = "AUTOMATIC";
    public DicomCode? AnatomicRegion { get; init; }
    /// <summary>Display colour as CIELab (DICOM scaled 0–65535); defaults to red.</summary>
    public ushort[] RecommendedCieLab { get; init; } = [34885, 53484, 50171];
}

/// <summary>A segmentation read back from a DICOM SEG instance.</summary>
public sealed record SegmentationResult(string SopInstanceUid, string SeriesInstanceUid, IReadOnlyList<(int Number, string Label, DicomCode Type, byte[] Mask)> Segments);

/// <summary>
/// DICOM Segmentation objects (Segmentation IOD, PS3.3 A.51): BINARY segmentation of a <see cref="DicomVolume"/>, one
/// frame per segment per slice that contains it, with per-frame functional groups referencing the source slices so any
/// PACS / viewer (OHIF, 3D Slicer, highdicom) overlays it on the original series.
/// </summary>
public static class DicomSegmentation
{
    public static readonly DicomUID SegmentationStorage = DicomUID.SegmentationStorage;

    public static DicomFile Create(DicomVolume source, IReadOnlyList<SegmentDefinition> segments, string contentLabel = "SEGMENTATION", string? seriesDescription = null, DateTimeOffset? at = null)
    {
        if (segments.Count == 0) throw new ArgumentException("At least one segment is required.", nameof(segments));
        if (source.Reference is null) throw new ArgumentException("The volume has no reference dataset (build it with DicomVolume.FromSeries).", nameof(source));
        int plane = source.Width * source.Height;
        foreach (var s in segments)
            if (s.Mask.Length != source.Voxels.Length) throw new ArgumentException($"Segment '{s.Label}' mask size does not match the volume.");

        var now = (at ?? DateTimeOffset.Now).ToLocalTime();
        string series = DicomPhantom.Uid($"seg-series/{source.SeriesInstanceUid}/{contentLabel}/{string.Join("|", segments.Select(s => s.Label))}");
        string sop = DicomPhantom.Uid($"seg/{series}/{now.ToUnixTimeMilliseconds()}");
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
        CopyPatientStudy(source.Reference, ds);
        ds.AddOrUpdate(DicomTag.SOPClassUID, SegmentationStorage);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, sop);
        ds.AddOrUpdate(DicomTag.Modality, "SEG");
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, series);
        ds.AddOrUpdate(DicomTag.SeriesNumber, "300");
        ds.AddOrUpdate(DicomTag.SeriesDescription, seriesDescription ?? $"{contentLabel} (Medical.Net)");
        ds.AddOrUpdate(DicomTag.InstanceNumber, "1");
        ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, source.FrameOfReferenceUid ?? DicomPhantom.Uid($"for/{source.SeriesInstanceUid}"));
        ds.AddOrUpdate(DicomTag.PositionReferenceIndicator, "");
        AddEquipment(ds);
        ds.AddOrUpdate(DicomTag.ContentDate, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        ds.AddOrUpdate(DicomTag.ContentTime, now.ToString("HHmmss", CultureInfo.InvariantCulture));
        ds.AddOrUpdate(DicomTag.ImageType, "DERIVED", "PRIMARY");
        ds.AddOrUpdate(DicomTag.ContentLabel, contentLabel.Length > 16 ? contentLabel[..16].ToUpperInvariant() : contentLabel.ToUpperInvariant());
        ds.AddOrUpdate(DicomTag.ContentDescription, "Automatic segmentation");
        ds.AddOrUpdate(DicomTag.ContentCreatorName, "MedicalNet^Algorithm");
        ds.AddOrUpdate(DicomTag.SegmentationType, "BINARY");
        ds.AddOrUpdate(DicomTag.LossyImageCompression, "00");

        // Segment Sequence
        var segSeq = new DicomSequence(DicomTag.SegmentSequence);
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var item = new DicomDataset
            {
                { DicomTag.SegmentNumber, (ushort)(i + 1) },
                { DicomTag.SegmentLabel, s.Label },
                { DicomTag.SegmentAlgorithmType, s.AlgorithmType },
                { DicomTag.RecommendedDisplayCIELabValue, s.RecommendedCieLab },
                new DicomSequence(DicomTag.SegmentedPropertyCategoryCodeSequence, s.Category.ToDataset()),
                new DicomSequence(DicomTag.SegmentedPropertyTypeCodeSequence, s.Type.ToDataset()),
            };
            if (s.AlgorithmType != "MANUAL") item.Add(DicomTag.SegmentAlgorithmName, s.AlgorithmName);   // Type 1C
            if (s.AnatomicRegion is { } ar) item.Add(new DicomSequence(DicomTag.AnatomicRegionSequence, ar.ToDataset()));
            segSeq.Items.Add(item);
        }
        ds.Add(segSeq);

        // Dimensions: segment number, then position along the stack.
        string dimOrg = DicomPhantom.Uid($"dim/{sop}");
        ds.Add(new DicomSequence(DicomTag.DimensionOrganizationSequence, new DicomDataset { { DicomTag.DimensionOrganizationUID, dimOrg } }));
        ds.Add(new DicomSequence(DicomTag.DimensionIndexSequence,
            new DicomDataset
            {
                { DicomTag.DimensionOrganizationUID, dimOrg },
                { DicomTag.DimensionIndexPointer, DicomTag.ReferencedSegmentNumber },
                { DicomTag.FunctionalGroupPointer, DicomTag.SegmentIdentificationSequence },
                { DicomTag.DimensionDescriptionLabel, "ReferencedSegmentNumber" },
            },
            new DicomDataset
            {
                { DicomTag.DimensionOrganizationUID, dimOrg },
                { DicomTag.DimensionIndexPointer, DicomTag.ImagePositionPatient },
                { DicomTag.FunctionalGroupPointer, DicomTag.PlanePositionSequence },
                { DicomTag.DimensionDescriptionLabel, "ImagePositionPatient" },
            }));

        // Shared functional groups: pixel measures and orientation.
        string Ds(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        ds.Add(new DicomSequence(DicomTag.SharedFunctionalGroupsSequence, new DicomDataset
        {
            new DicomSequence(DicomTag.PixelMeasuresSequence, new DicomDataset
            {
                new DicomDecimalString(DicomTag.PixelSpacing, Ds(source.Spacing.Y), Ds(source.Spacing.X)),
                new DicomDecimalString(DicomTag.SliceThickness, Ds(source.Spacing.Z)),
                new DicomDecimalString(DicomTag.SpacingBetweenSlices, Ds(source.Spacing.Z)),
            }),
            new DicomSequence(DicomTag.PlaneOrientationSequence, new DicomDataset
            {
                new DicomDecimalString(DicomTag.ImageOrientationPatient, Ds(source.RowDirection.X), Ds(source.RowDirection.Y), Ds(source.RowDirection.Z),
                    Ds(source.ColumnDirection.X), Ds(source.ColumnDirection.Y), Ds(source.ColumnDirection.Z)),
            }),
        }));

        // Frames: every slice where the segment has voxels, segment-major.
        var perFrame = new DicomSequence(DicomTag.PerFrameFunctionalGroupsSequence);
        var frames = new List<(int Segment, int Slice)>();
        for (int s = 0; s < segments.Count; s++)
            for (int k = 0; k < source.Depth; k++)
                if (segments[s].Mask.AsSpan(k * plane, plane).ContainsAnyExcept((byte)0)) frames.Add((s, k));
        if (frames.Count == 0) frames.Add((0, 0));   // an empty segmentation still needs one frame
        var refs = source.SliceSopInstanceUids;
        foreach (var (s, k) in frames)
        {
            var pos = source.ToPatient(0, 0, k);
            var fg = new DicomDataset
            {
                new DicomSequence(DicomTag.FrameContentSequence, new DicomDataset { { DicomTag.DimensionIndexValues, (uint)(s + 1), (uint)(k + 1) } }),
                new DicomSequence(DicomTag.PlanePositionSequence, new DicomDataset { new DicomDecimalString(DicomTag.ImagePositionPatient, Ds(pos.X), Ds(pos.Y), Ds(pos.Z)) }),
                new DicomSequence(DicomTag.SegmentIdentificationSequence, new DicomDataset { { DicomTag.ReferencedSegmentNumber, (ushort)(s + 1) } }),
            };
            if (k < refs.Count)
                fg.Add(new DicomSequence(DicomTag.DerivationImageSequence, new DicomDataset
                {
                    new DicomSequence(DicomTag.DerivationCodeSequence, DicomCodes.SegmentationDerivation.ToDataset()),
                    new DicomSequence(DicomTag.SourceImageSequence, new DicomDataset
                    {
                        { DicomTag.ReferencedSOPClassUID, source.SopClassUid }, { DicomTag.ReferencedSOPInstanceUID, refs[k] },
                        new DicomSequence(DicomTag.PurposeOfReferenceCodeSequence, DicomCodes.SourceImage.ToDataset()),
                    }),
                }));
            perFrame.Items.Add(fg);
        }
        ds.Add(perFrame);
        ds.Add(new DicomSequence(DicomTag.ReferencedSeriesSequence, new DicomDataset
        {
            { DicomTag.SeriesInstanceUID, source.SeriesInstanceUid },
            new DicomSequence(DicomTag.ReferencedInstanceSequence, refs.Select(r => new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, source.SopClassUid }, { DicomTag.ReferencedSOPInstanceUID, r },
            }).ToArray()),
        }));

        // Image pixel: 1 bit per pixel, frames concatenated at bit level, LSB first.
        ds.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
        ds.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");
        ds.AddOrUpdate(DicomTag.Rows, (ushort)source.Height);
        ds.AddOrUpdate(DicomTag.Columns, (ushort)source.Width);
        ds.AddOrUpdate(DicomTag.BitsAllocated, (ushort)1);
        ds.AddOrUpdate(DicomTag.BitsStored, (ushort)1);
        ds.AddOrUpdate(DicomTag.HighBit, (ushort)0);
        ds.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);
        ds.AddOrUpdate(DicomTag.NumberOfFrames, frames.Count.ToString(CultureInfo.InvariantCulture));
        long bits = (long)frames.Count * plane;
        var packed = new byte[(bits + 7) / 8 + ((bits + 7) / 8 % 2)];
        long bit = 0;
        foreach (var (s, k) in frames)
        {
            var m = segments[s].Mask.AsSpan(k * plane, plane);
            for (int i = 0; i < plane; i++, bit++)
                if (m[i] != 0) packed[bit >> 3] |= (byte)(1 << (int)(bit & 7));
        }
        ds.AddOrUpdate(new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer(packed)));
        return new DicomFile(ds);
    }

    /// <summary>Reads a BINARY segmentation back onto the grid of <paramref name="volume"/> (frames matched by source SOP instance, else by position).</summary>
    public static SegmentationResult Read(DicomFile seg, DicomVolume volume)
    {
        var ds = seg.Dataset;
        if (ds.GetSingleValueOrDefault(DicomTag.SegmentationType, "") != "BINARY") throw new NotSupportedException("Only BINARY segmentations are supported.");
        int rows = ds.GetSingleValue<ushort>(DicomTag.Rows), cols = ds.GetSingleValue<ushort>(DicomTag.Columns);
        if (rows != volume.Height || cols != volume.Width) throw new InvalidOperationException("Segmentation size differs from the volume.");
        int plane = rows * cols;
        var bytes = ds.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data;
        var segments = ds.GetSequence(DicomTag.SegmentSequence).Items
            .Select(i => (Number: (int)i.GetSingleValue<ushort>(DicomTag.SegmentNumber), Label: i.GetSingleValueOrDefault(DicomTag.SegmentLabel, ""),
                          Type: DicomCode.From(i.GetSequence(DicomTag.SegmentedPropertyTypeCodeSequence).Items[0]), Mask: new byte[volume.Voxels.Length]))
            .ToList();
        var sliceByUid = volume.SliceSopInstanceUids.Select((u, k) => (u, k)).ToDictionary(x => x.u, x => x.k);
        var frames = ds.GetSequence(DicomTag.PerFrameFunctionalGroupsSequence).Items;
        for (int f = 0; f < frames.Count; f++)
        {
            var fg = frames[f];
            int number = fg.GetSequence(DicomTag.SegmentIdentificationSequence).Items[0].GetSingleValue<ushort>(DicomTag.ReferencedSegmentNumber);
            int k = -1;
            if (fg.TryGetSequence(DicomTag.DerivationImageSequence, out var deriv) && deriv.Items.Count > 0
                && deriv.Items[0].TryGetSequence(DicomTag.SourceImageSequence, out var src) && src.Items.Count > 0
                && sliceByUid.TryGetValue(src.Items[0].GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, ""), out int byUid))
                k = byUid;
            else if (fg.TryGetSequence(DicomTag.PlanePositionSequence, out var pp) && pp.Items[0].TryGetValues<double>(DicomTag.ImagePositionPatient, out double[]? ipp))
                k = (int)Math.Round(volume.ToVoxel(new Vector3d(ipp[0], ipp[1], ipp[2])).Z);
            if (k < 0 || k >= volume.Depth) continue;
            var target = segments.First(s => s.Number == number).Mask.AsSpan(k * plane, plane);
            long bit = (long)f * plane;
            for (int i = 0; i < plane; i++, bit++)
                if ((bytes[bit >> 3] & (1 << (int)(bit & 7))) != 0) target[i] = 1;
        }
        return new SegmentationResult(ds.GetSingleValue<string>(DicomTag.SOPInstanceUID), ds.GetSingleValue<string>(DicomTag.SeriesInstanceUID),
            segments.Select(s => (s.Number, s.Label, s.Type, s.Mask)).ToList());
    }

    internal static void CopyPatientStudy(DicomDataset from, DicomDataset to)
    {
        foreach (var tag in new[]
        {
            DicomTag.PatientName, DicomTag.PatientID, DicomTag.PatientBirthDate, DicomTag.PatientSex,
            DicomTag.StudyInstanceUID, DicomTag.StudyDate, DicomTag.StudyTime, DicomTag.StudyID, DicomTag.AccessionNumber,
            DicomTag.ReferringPhysicianName, DicomTag.StudyDescription, DicomTag.SpecificCharacterSet,
        })
            if (from.TryGetString(tag, out string? v)) to.AddOrUpdate(tag, v);
            else if (tag != DicomTag.SpecificCharacterSet) to.AddOrUpdate(tag, "");
    }

    internal static void AddEquipment(DicomDataset ds)
    {
        ds.AddOrUpdate(DicomTag.Manufacturer, "Gravicode Studios");
        ds.AddOrUpdate(DicomTag.ManufacturerModelName, "Medical.Net");
        ds.AddOrUpdate(DicomTag.DeviceSerialNumber, "MEDNET-1");
        ds.AddOrUpdate(DicomTag.SoftwareVersions, Medical.Core.MedicalInfo.Version);
    }
}
