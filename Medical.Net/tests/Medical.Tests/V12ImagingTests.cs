using FellowOakDicom;
using Medical.Dicom;
using Medical.Native;

namespace Medical.Tests;

public class DicomCodecTests
{
    private static readonly Lazy<IReadOnlyList<DicomFile>> Head = new(() => DicomPhantom.CreateSeries(PhantomKind.Head, 6, 128));

    [Theory]
    [InlineData(DicomCompression.Rle)]
    [InlineData(DicomCompression.JpegLossless)]
    [InlineData(DicomCompression.JpegLsLossless)]
    [InlineData(DicomCompression.Jpeg2000Lossless)]
    [InlineData(DicomCompression.HtJpeg2000Lossless)]
    public void LosslessSyntaxesRoundTripBitExactly(DicomCompression c)
    {
        Assert.True(DicomCodecs.IsAvailable);
        var src = Head.Value[3];
        var enc = DicomCodecs.Transcode(src, c);
        Assert.Equal(DicomCodecs.SyntaxFor(c), enc.Dataset.InternalTransferSyntax);
        Assert.True(enc.Dataset.InternalTransferSyntax.IsEncapsulated);
        Assert.True(DicomCodecs.PixelDataBytes(enc.Dataset) < DicomCodecs.PixelDataBytes(src.Dataset), "compressed should be smaller");
        Assert.Equal(DicomImaging.FrameI16(src.Dataset), DicomImaging.FrameI16(enc.Dataset));      // decoded on demand

        // survives a write / read cycle as a Part 10 file
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        var reread = DicomImaging.Open(ms);
        Assert.True(DicomImaging.Describe(reread.Dataset).IsCompressed);
        Assert.Equal(DicomImaging.Render(src.Dataset, WindowPreset.Brain).Gray8, DicomImaging.Render(reread.Dataset, WindowPreset.Brain).Gray8);
    }

    [Fact]
    public void LossySyntaxesStayWithinTheirErrorBounds()
    {
        var src = Head.Value[3];
        var a = DicomImaging.FrameI16(src.Dataset);
        var nl = DicomImaging.FrameI16(DicomCodecs.Transcode(src, DicomCompression.JpegLsNearLossless, 3).Dataset);
        Assert.True(a.Zip(nl).Max(p => Math.Abs(p.First - p.Second)) <= 3);                    // JPEG-LS NEAR guarantee
        var j2k = DicomCodecs.Transcode(src, DicomCompression.Jpeg2000, 20);
        var b = DicomImaging.FrameI16(j2k.Dataset);
        double rmse = Math.Sqrt(a.Zip(b).Average(p => (double)(p.First - p.Second) * (p.First - p.Second)));
        Assert.InRange(rmse, 0.01, 40);
        Assert.True(DicomCodecs.PixelDataBytes(j2k.Dataset) * 8 < DicomCodecs.PixelDataBytes(src.Dataset));
    }
}

public class VolumeTests
{
    private static readonly Lazy<IReadOnlyList<DicomFile>> HeadFiles = new(() => DicomPhantom.CreateSeries(PhantomKind.Head, 24, 128));
    private static readonly Lazy<DicomVolume> Head = new(() => DicomVolume.FromSeries(HeadFiles.Value));

    [Fact]
    public void VolumeGeometryIsSortedAndInvertible()
    {
        var v = Head.Value;
        var shuffled = DicomVolume.FromSeries(HeadFiles.Value.Reverse().OrderBy(f => f.Dataset.GetString(DicomTag.SOPInstanceUID)));
        Assert.Equal(v.Voxels, shuffled.Voxels);
        Assert.Equal((128, 128, 24), (v.Width, v.Height, v.Depth));
        Assert.Equal(220.0 / 128, v.Spacing.X, 3);
        Assert.InRange(v.Spacing.Z, 4.9, 5.3);
        Assert.Equal(1, v.SliceDirection.Z, 6);
        var p = v.ToPatient(10.5, 20.25, 3.5);
        var back = v.ToVoxel(p);
        Assert.Equal(10.5, back.X, 9); Assert.Equal(20.25, back.Y, 9); Assert.Equal(3.5, back.Z, 9);
        Assert.NotNull(v.FrameOfReferenceUid);
        Assert.Equal(24, v.SliceSopInstanceUids.Distinct().Count());
        // centre voxel is brain (≈ 28–38 HU), corner is air
        Assert.InRange((int)v[64, 64, 12], 10, 60);
        Assert.InRange((int)v[0, 0, 0], -1030, -970);
    }

    [Theory]
    [InlineData(SlabMode.Mean, 0.0)]
    [InlineData(SlabMode.Max, 30.0)]
    [InlineData(SlabMode.Min, 12.0)]
    [InlineData(SlabMode.Mean, double.PositiveInfinity)]
    public void RustAndManagedSlabsAreBitIdentical(SlabMode mode, double thickness)
    {
        if (!MedicalKernels.IsNativeAvailable) return;   // Rust kernels not built on this machine
        var v = Head.Value;
        var before = MedicalKernels.PreferredBackend;
        try
        {
            foreach (var plane in Enum.GetValues<VolumePlane>())
            {
                MedicalKernels.PreferredBackend = KernelBackend.Rust;
                var rust = VolumeRendering.Project(v, plane, mode, 0.43, thickness);
                Assert.Equal(KernelBackend.Rust, rust.Backend);
                MedicalKernels.PreferredBackend = KernelBackend.Managed;
                var managed = VolumeRendering.Project(v, plane, mode, 0.43, thickness);
                Assert.Equal(rust.Values, managed.Values);
            }
            MedicalKernels.PreferredBackend = KernelBackend.Rust;
            var obl = VolumeRendering.Oblique(v, new(1, 0.3, 0.2), new(0, 0.2, -1), 0.5, 10, SlabMode.Max);
            MedicalKernels.PreferredBackend = KernelBackend.Managed;
            Assert.Equal(obl.Values, VolumeRendering.Oblique(v, new(1, 0.3, 0.2), new(0, 0.2, -1), 0.5, 10, SlabMode.Max).Values);
        }
        finally { MedicalKernels.PreferredBackend = before; }
    }

    [Fact]
    public void ReformatsHaveCorrectProportionsAndProjectionsBoundTheirSlices()
    {
        var v = Head.Value;
        var axial = VolumeRendering.Mpr(v, VolumePlane.Axial, 0.5);
        Assert.Equal((128, 128), (axial.Width, axial.Height));
        var coronal = VolumeRendering.Mpr(v, VolumePlane.Coronal, 0.5);
        Assert.Equal(128, coronal.Width);
        double heightMm = (v.Depth - 1) * v.Spacing.Z;
        Assert.InRange(coronal.Height, heightMm / coronal.PixelSpacingMm - 1, heightMm / coronal.PixelSpacingMm + 2);   // not squashed

        // axial MPR at a slice position reproduces the slice exactly
        int k = 12;
        var exact = VolumeRendering.Mpr(v, VolumePlane.Axial, k / (double)(v.Depth - 1));
        Assert.Equal(v.Voxels.AsSpan(k * 128 * 128, 128 * 128).ToArray(), exact.Values);

        var mip = VolumeRendering.Project(v, VolumePlane.Axial, SlabMode.Max);
        var minip = VolumeRendering.Project(v, VolumePlane.Axial, SlabMode.Min);
        for (int z = 0; z < v.Depth; z += 5)
        {
            var slice = VolumeRendering.Mpr(v, VolumePlane.Axial, z / (double)(v.Depth - 1)).Values;
            for (int i = 0; i < slice.Length; i += 97)
            {
                Assert.True(mip.Values[i] >= slice[i]);
                Assert.True(minip.Values[i] <= slice[i]);
            }
        }
        // the skull ring dominates the MIP, the frontal sinus air shows on the MinIP
        Assert.True(mip.Values.Max() >= 950);
        Assert.Contains("MIP", mip.Description);
        Assert.Equal(128 * 128 * 4 > 0, mip.Render(WindowPreset.Bone).ToPng().Length > 100);
    }

    [Fact]
    public void SurfaceNetsMeshIsClosedAndMeasuresTheLesion()
    {
        var v = Head.Value;
        // Exact lesion mask from the analytic phantom (no noise), on the volume grid.
        var mask = new byte[v.Voxels.Length];
        for (int z = 0; z < v.Depth; z++)
        {
            double zn = -0.95 + 1.9 * z / (v.Depth - 1);
            for (int y = 0; y < v.Height; y++)
                for (int x = 0; x < v.Width; x++)
                    if (DicomPhantom.HuAt(PhantomKind.Head, (x + 0.5) / v.Width * 2 - 1, (y + 0.5) / v.Height * 2 - 1, zn) == 72)
                        mask[(z * v.Height + y) * v.Width + x] = 1;
        }
        double voxelMl = mask.Count(b => b != 0) * v.VoxelVolumeMm3 / 1000;
        var mesh = IsoSurface.ExtractMask(v, mask);
        double analytic = 4.0 / 3 * Math.PI * 9.9 * 7.7 * 10.8 / 1000;     // 3.45 mL
        Assert.InRange(voxelMl, analytic * 0.85, analytic * 1.15);
        Assert.InRange(mesh.VolumeMl, analytic * 0.75, analytic * 1.1);    // surface nets shrink corners slightly
        AssertClosed(mesh);
        Assert.Equal(84 + mesh.TriangleCount * 50, mesh.ToStl().Length);
        Assert.StartsWith("# Medical.Net", mesh.ToObj());
        Assert.All(mesh.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));

        var smooth = mesh.Smooth(10);                                   // Taubin: no shrinkage, topology unchanged
        Assert.Equal(mesh.Indices, smooth.Indices);
        Assert.InRange(smooth.VolumeMl / mesh.VolumeMl, 0.95, 1.05);
        AssertClosed(smooth);

        var skull = IsoSurface.Extract(v, 300, downsample: 2);
        Assert.True(skull.TriangleCount > 2000);
        AssertClosed(skull);
        var (min, max) = skull.Bounds;
        Assert.InRange(max.X - min.X, 140, 165);           // 0.72 × 220 mm wide
    }

    private static void AssertClosed(SurfaceMesh m)
    {
        // Watertight and consistently wound: every directed edge is matched by its reverse equally often
        // (naive surface nets may share an edge between four faces where voxels touch only diagonally).
        var count = new Dictionary<(int, int), int>();
        for (int t = 0; t < m.Indices.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                var key = (m.Indices[t + e], m.Indices[t + (e + 1) % 3]);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        Assert.All(count, kv => Assert.Equal(kv.Value, count.GetValueOrDefault((kv.Key.Item2, kv.Key.Item1))));
    }
}

public class ImagingAiOutputTests
{
    private static readonly Lazy<IReadOnlyList<DicomFile>> HeadFiles = new(() => DicomPhantom.CreateSeries(PhantomKind.Head, 24, 256));
    private static readonly Lazy<IReadOnlyList<DicomFile>> ChestFiles = new(() => DicomPhantom.CreateSeries(PhantomKind.Chest, 16, 256));
    public static readonly string ExportDir = Path.Combine(Path.GetTempPath(), "mednet-v12");

    [Fact]
    public void HaemorrhageSegAndSrRoundTrip()
    {
        var v = DicomVolume.FromSeries(HeadFiles.Value);
        var mask = Medical.AI.HemorrhageScreener.SegmentVolume(v);
        double maskMl = mask.Count(b => b != 0) * v.VoxelVolumeMm3 / 1000;
        // Same rule as the 2D report, which multiplies by SliceThickness (5 mm) instead of the true spacing (≈ 4.96 mm).
        Assert.InRange(maskMl / Medical.AI.HemorrhageScreener.Analyze(HeadFiles.Value).VolumeMl, 0.98, 1.0);
        var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(7));
        var output = Medical.AI.ImagingAiReports.Hemorrhage(v, mask, at)!;

        var seg = Reload(output.Segmentation!, "hemorrhage-seg.dcm");
        Assert.Equal("SEG", seg.Dataset.GetString(DicomTag.Modality));
        Assert.Equal(v.FrameOfReferenceUid, seg.Dataset.GetString(DicomTag.FrameOfReferenceUID));
        var back = DicomSegmentation.Read(seg, v);
        var only = Assert.Single(back.Segments);
        Assert.Equal(DicomCodes.Hemorrhage, only.Type);
        Assert.Equal(mask, only.Mask);
        int framesWithLesion = Enumerable.Range(0, v.Depth).Count(k => mask.AsSpan(k * v.Width * v.Height, v.Width * v.Height).ContainsAnyExcept((byte)0));
        Assert.Equal(framesWithLesion.ToString(), seg.Dataset.GetString(DicomTag.NumberOfFrames));

        var sr = Reload(output.Report, "hemorrhage-sr.dcm");
        var content = MeasurementReport.Read(sr);
        Assert.Equal("Imaging Measurement Report", content.Title);
        var f = Assert.Single(content.Findings);
        Assert.Equal(DicomCodes.Hemorrhage, f.Finding);
        Assert.Equal(DicomCodes.Brain, f.FindingSite);
        Assert.Equal(1, f.SegmentNumber);
        Assert.Equal(Math.Round(maskMl, 2), f.Measurements.Single(m => m.Concept == DicomCodes.Volume).Value, 2);
        Assert.Contains("Measurement Group", content.Text);
        Assert.Contains("SEG segment 1", content.Text);
    }

    [Fact]
    public void LungNoduleIsFoundSizedAndCategorised()
    {
        var v = DicomVolume.FromSeries(ChestFiles.Value);
        var r = Medical.AI.LungNoduleDetector.Detect(v);
        var n = Assert.Single(r.Nodules);
        // phantom nodule: semi-axes 0.06 × 0.05 of a 380 mm field → 22.8 × 19.0 mm
        Assert.InRange(n.LongAxisMm, 20.5, 24.5);
        Assert.InRange(n.ShortAxisMm, 17, 21);
        Assert.Equal("4B", n.LungRads);
        Assert.InRange(n.MeanHu, 15, 45);
        Assert.True(n.CenterMm.X < 0);                    // right lung (patient right = −X in LPS)
        Assert.InRange(r.LungVolumeMl, 1500, 9000);
        Assert.Equal("4B", r.Category);
        Assert.Empty(Medical.AI.LungNoduleDetector.Detect(DicomVolume.FromSeries(HeadFiles.Value)).Nodules);

        var output = Medical.AI.ImagingAiReports.LungNodules(v, r, new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero))!;
        var seg = Reload(output.Segmentation!, "nodules-seg.dcm");
        Assert.Equal(DicomCodes.Nodule, DicomSegmentation.Read(seg, v).Segments[0].Type);
        var sr = MeasurementReport.Read(Reload(output.Report, "nodules-sr.dcm"));
        Assert.Equal(n.LongAxisMm, sr.Findings[0].Measurements.Single(m => m.Concept == DicomCodes.Diameter).Value, 1);
        Assert.Equal(DicomCodes.Lung, sr.Findings[0].FindingSite);

        // the source series for independent validation (highdicom) next to the derived objects
        Directory.CreateDirectory(Path.Combine(ExportDir, "chest"));
        foreach (var f in ChestFiles.Value) f.Save(Path.Combine(ExportDir, "chest", f.Dataset.GetString(DicomTag.InstanceNumber).PadLeft(3, '0') + ".dcm"));
        Directory.CreateDirectory(Path.Combine(ExportDir, "head"));
        foreach (var f in HeadFiles.Value) f.Save(Path.Combine(ExportDir, "head", f.Dataset.GetString(DicomTag.InstanceNumber).PadLeft(3, '0') + ".dcm"));
    }

    [Fact]
    public void DicomCodesHaveValidSnomedCheckDigits()
    {
        foreach (var c in new[] { DicomCodes.MorphologicallyAbnormalStructure, DicomCodes.Tissue, DicomCodes.Hemorrhage, DicomCodes.Nodule, DicomCodes.Bone,
                     DicomCodes.Brain, DicomCodes.Lung, DicomCodes.Volume, DicomCodes.Diameter, DicomCodes.FindingSite, DicomCodes.ComputedTomography })
            Assert.True(Medical.Terminology.CodeValidators.IsValidSctid(c.Value), $"{c} has an invalid SCTID");
    }

    private static DicomFile Reload(DicomFile f, string name)
    {
        Directory.CreateDirectory(ExportDir);
        string path = Path.Combine(ExportDir, name);
        f.Save(path);
        return DicomFile.Open(path);
    }
}
