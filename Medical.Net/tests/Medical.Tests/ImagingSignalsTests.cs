using FellowOakDicom;
using Medical.AI;
using Medical.Core;
using Medical.Dicom;
using Medical.Native;
using Medical.Signals;

namespace Medical.Tests;

[Collection("kernels")]
public class NativeKernelTests
{
    [Fact]
    public void RustKernelIsLoadedInTests() => Assert.True(MedicalKernels.IsNativeAvailable, "medical_native was not found — run build/build-native.ps1");

    [Fact]
    public void WindowLevelMatchesDicomFormula()
    {
        Assert.Equal(0, MedicalKernels.WindowValue(-1000, 40, 400, false));
        Assert.Equal(255, MedicalKernels.WindowValue(1000, 40, 400, false));
        Assert.Equal(255, MedicalKernels.WindowValue(-1000, 40, 400, true));
        Assert.InRange(MedicalKernels.WindowValue(40, 40, 400, false), (byte)127, (byte)128);
    }

    [Fact]
    public void RustAndManagedBackendsAgree()
    {
        var r = new SeededRandom(9);
        var px = Enumerable.Range(0, 10_000).Select(_ => (short)r.Next(-1024, 3000)).ToArray();
        var ecg = EcgSimulator.Generate(20, 80, Rhythm.AtrialFibrillation, seed: 3).Samples;
        try
        {
            MedicalKernels.PreferredBackend = KernelBackend.Managed;
            var m1 = new byte[px.Length]; MedicalKernels.WindowLevel(px, m1, 1, -1024, 40, 400);
            var s1 = MedicalKernels.Stats(px, 1, -1024);
            var p1 = MedicalKernels.DetectRPeaks(ecg, 250);
            MedicalKernels.PreferredBackend = KernelBackend.Rust;
            var m2 = new byte[px.Length]; MedicalKernels.WindowLevel(px, m2, 1, -1024, 40, 400);
            var s2 = MedicalKernels.Stats(px, 1, -1024);
            var p2 = MedicalKernels.DetectRPeaks(ecg, 250);
            Assert.Equal(m1, m2);
            Assert.Equal(s1.Mean, s2.Mean, 9);
            Assert.Equal(s1.StdDev, s2.StdDev, 9);
            Assert.Equal(p1, p2);
        }
        finally { MedicalKernels.PreferredBackend = KernelBackend.Rust; }
    }

    [Fact]
    public void InvalidArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MedicalKernels.WindowLevel(new short[4], new byte[4], 1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => MedicalKernels.WindowLevel(new short[4], new byte[2], 1, 0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => MedicalKernels.DetectRPeaks(new float[10], 250, 1.5));
    }
}

public class DicomTests
{
    private static readonly IReadOnlyList<DicomFile> Head = DicomPhantom.CreateSeries(PhantomKind.Head, 24, 128);

    [Fact]
    public void PhantomIsValidCtWithDeterministicUids()
    {
        var info = DicomImaging.Describe(Head[0].Dataset);
        Assert.Equal("CT", info.Modality);
        Assert.Equal(128, info.Rows);
        Assert.Equal(-1024, info.RescaleIntercept);
        Assert.StartsWith("2.25.", info.SopInstanceUid);
        Assert.Equal(info.SopInstanceUid, DicomImaging.Describe(DicomPhantom.CreateSeries(PhantomKind.Head, 24, 128)[0].Dataset).SopInstanceUid);
        Assert.Single(Head.Select(f => DicomImaging.Describe(f.Dataset).SeriesInstanceUid).Distinct());
    }

    [Fact]
    public void SaveAndReopenPreservesPixels()
    {
        using var ms = new MemoryStream();
        Head[5].Save(ms);
        ms.Position = 0;
        var re = DicomImaging.Open(ms);
        Assert.Equal(DicomImaging.FrameI16(Head[5].Dataset), DicomImaging.FrameI16(re.Dataset));
    }

    [Fact]
    public void RenderAndStatistics()
    {
        var img = DicomImaging.Render(Head[12].Dataset, WindowPreset.Brain);
        Assert.Equal(128 * 128, img.Gray8.Length);
        Assert.Contains((byte)0, img.Gray8);    // air
        Assert.Contains((byte)255, img.Gray8);  // skull
        var stats = DicomImaging.Statistics(Head[12].Dataset, roi: (54, 54, 20, 20));
        Assert.InRange(stats.Mean, 15, 45);      // brain parenchyma in HU
        Assert.True(img.ToPng().Length > 100);
        Assert.Equal(WindowPreset.Lung, WindowPreset.Find("lung"));
    }

    [Fact]
    public void DeidentificationRemovesIdentifiersConsistently()
    {
        var d = new DicomDeidentifier("unit-test-secret-0001");
        var a = d.Deidentify(Head[0], out var ra);
        var b = d.Deidentify(Head[1], out var rb);
        Assert.Equal(ra.Pseudonym, rb.Pseudonym);
        Assert.NotEqual(DicomImaging.Describe(Head[0].Dataset).PatientName, DicomImaging.Describe(a.Dataset).PatientName);
        Assert.Equal("YES", a.Dataset.GetSingleValue<string>(DicomTag.PatientIdentityRemoved));
        Assert.False(a.Dataset.Contains(DicomTag.ReferringPhysicianName) && a.Dataset.GetString(DicomTag.ReferringPhysicianName).Length > 0);
        // same study after de-identification
        Assert.Equal(DicomImaging.Describe(a.Dataset).StudyInstanceUid, DicomImaging.Describe(b.Dataset).StudyInstanceUid);
        Assert.NotEqual(DicomImaging.Describe(Head[0].Dataset).StudyInstanceUid, DicomImaging.Describe(a.Dataset).StudyInstanceUid);
    }

    [Fact]
    public async System.Threading.Tasks.Task StoreScpLoopback()
    {
        var inbox = new DicomInbox();
        int port = 11000 + Random.Shared.Next(2000);
        using var server = StoreScp.StartServer(port, inbox);
        var (ok, _) = await DicomScu.EchoAsync("127.0.0.1", port);
        Assert.True(ok);
        int stored = await DicomScu.StoreAsync("127.0.0.1", port, Head.Take(3));
        Assert.Equal(3, stored);
        Assert.Equal(3, inbox.Count);
    }

    [Fact]
    public void HemorrhageScreenFindsPhantomLesionOnlyOnHeadCt()
    {
        var r = HemorrhageScreener.Analyze(Head);
        Assert.True(r.Suspected);
        Assert.InRange(r.VolumeMl, 1, 8);
        Assert.All(r.Findings, f => Assert.InRange(f.MeanHu, 55, 90));
        Assert.All(r.Findings, f => Assert.True(f.CentroidX > 64 && f.CentroidY < 64, $"lesion at {f.CentroidX},{f.CentroidY}")); // right-anterior quadrant
        Assert.False(HemorrhageScreener.Analyze(DicomPhantom.CreateSeries(PhantomKind.Chest, 4, 96)).Suspected);
        Assert.Equal(new byte[] { 0x89, 0x50 }, HemorrhageScreener.Overlay(Head[14])[..2]);
    }
}

public class SignalTests
{
    [Theory]
    [InlineData(65, Rhythm.NormalSinus, Rhythm.NormalSinus)]
    [InlineData(48, Rhythm.NormalSinus, Rhythm.SinusBradycardia)]
    [InlineData(130, Rhythm.NormalSinus, Rhythm.SinusTachycardia)]
    [InlineData(85, Rhythm.AtrialFibrillation, Rhythm.AtrialFibrillation)]
    [InlineData(72, Rhythm.Ectopy, Rhythm.Ectopy)]
    public void RhythmScreening(double hr, Rhythm simulated, Rhythm expected)
    {
        var a = EcgAnalyzer.Analyze(EcgSimulator.Generate(30, hr, simulated, seed: 11));
        Assert.Equal(expected, a.Rhythm);
        if (simulated != Rhythm.Ectopy) Assert.InRange(a.HeartRateBpm, hr * 0.9, hr * 1.1);
    }

    [Fact]
    public void HrvIsHigherInAf()
    {
        var sinus = EcgAnalyzer.Analyze(EcgSimulator.Generate(30, 75, seed: 4));
        var af = EcgAnalyzer.Analyze(EcgSimulator.Generate(30, 75, Rhythm.AtrialFibrillation, seed: 4));
        Assert.True(af.RmssdMs > 3 * sinus.RmssdMs);
    }

    [Fact]
    public void FiltersAreShapePreserving()
    {
        var x = Enumerable.Range(0, 100).Select(i => (float)i).ToArray();
        var y = Filters.MovingAverage(x, 5);
        Assert.Equal(50, y[50], 3);
        Assert.Equal(25, Filters.Decimate(x, 4).Length);
        Assert.Equal(1000, EcgSimulator.Ppg(10, 70, 100).Samples.Length);
    }
}
