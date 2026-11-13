using System.Text.Json;
using Medical.Core;
using Medical.Signals;
using Xunit.Abstractions;

namespace Medical.Tests;

public class Ecg12Tests(ITestOutputHelper output)
{
    private static Ecg12Analysis Run(Ecg12Options o, out Ecg12Truth truth, EcgPatient? p = null)
    {
        var (ecg, t) = Ecg12Simulator.Generate(o);
        truth = t;
        return Ecg12Analyzer.Analyze(ecg, p ?? new EcgPatient(Gender.Male, 55));
    }

    [Fact]
    public void LimbLeadsObeyEinthovenAndGoldberger()
    {
        var (ecg, _) = Ecg12Simulator.Generate();
        for (int i = 0; i < ecg.Samples; i += 37)
        {
            float I = ecg[EcgLead.I][i], II = ecg[EcgLead.II][i];
            Assert.Equal(II - I, ecg[EcgLead.III][i], 5);
            Assert.Equal(-(I + II) / 2, ecg[EcgLead.aVR][i], 5);
            Assert.Equal(0f, ecg[EcgLead.aVR][i] + ecg[EcgLead.aVL][i] + ecg[EcgLead.aVF][i], 4);   // augmented leads sum to zero
        }
        Assert.Equal(5000, ecg.Samples);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(72)]
    [InlineData(110)]
    public void IntervalsMatchGroundTruthAcrossRates(double hr)
    {
        var a = Run(new Ecg12Options { HeartRate = hr }, out var t);
        Assert.InRange(a.HeartRateBpm, hr - 2, hr + 2);
        Assert.InRange(a.QtMs, t.QtMs - 20, t.QtMs + 20);
        Assert.InRange(a.QrsMs, t.QrsMs - 10, t.QrsMs + 10);
        Assert.InRange(a.PrMs!.Value, t.PrMs - 12, t.PrMs + 12);
        Assert.InRange(a.QrsAxisDeg, t.QrsAxisDeg - 15, t.QrsAxisDeg + 15);
        // Fridericia QTc is rate-independent in the simulator (QT ∝ RR^⅓); Bazett over-corrects at high rates.
        Assert.InRange(a.QtcFridericiaMs, 380, 420);
        if (hr > 100) Assert.True(a.QtcBazettMs > a.QtcFridericiaMs + 20);
        Assert.Empty(a.StemiTerritories);
        Assert.True(a.Qtc is QtcCategory.Normal or QtcCategory.Borderline, $"QTc {a.QtcBazettMs} → {a.Qtc}");
    }

    [Theory]
    [InlineData(StPattern.InferiorStemi, "Inferior", new[] { EcgLead.I, EcgLead.aVL })]
    [InlineData(StPattern.AnteriorStemi, "Anterior", new EcgLead[0])]
    [InlineData(StPattern.LateralStemi, "High lateral", new[] { EcgLead.III, EcgLead.aVF })]
    public void StemiPatternsAreLocalised(StPattern pattern, string territory, EcgLead[] reciprocal)
    {
        var a = Run(new Ecg12Options { StPattern = pattern }, out var t);
        Assert.Contains(territory, a.StemiTerritories);
        foreach (var l in reciprocal) Assert.Contains(l, a.StDepressionLeads);
        foreach (var m in a.Leads) Assert.InRange(m.StJMv, t.StAtJMv[m.Lead] - 0.06, t.StAtJMv[m.Lead] + 0.04);
        Assert.InRange(a.QtMs, t.QtMs - 25, t.QtMs + 25);
        Assert.Contains("STEMI", a.Interpretation.En);
        Assert.Contains("STEMI", a.Interpretation.Id);
    }

    [Fact]
    public void V2V3ThresholdsDependOnSexAndAge()
    {
        // ≈ 0.18 mV measured at J in V2–V3: below the 2 mm threshold for men ≥ 40 y, above 1.5 mm for women.
        var (ecg, t) = Ecg12Simulator.Generate(new Ecg12Options { StPattern = StPattern.AnteriorStemi, StMagnitudeMv = 0.21 });
        Assert.InRange(t.StAtJMv[EcgLead.V2], 0.18, 0.21);
        var man = Ecg12Analyzer.Analyze(ecg, new EcgPatient(Gender.Male, 60));
        Assert.All(new[] { EcgLead.V2, EcgLead.V3 }, l => Assert.InRange(man[l].StJMv, 0.155, 0.199));
        var woman = Ecg12Analyzer.Analyze(ecg, new EcgPatient(Gender.Female, 60));
        Assert.DoesNotContain("Anterior", man.StemiTerritories);
        Assert.Contains("Anterior", woman.StemiTerritories);
    }

    [Fact]
    public void DiffuseDepressionWithAvrElevation()
    {
        var a = Run(new Ecg12Options { StPattern = StPattern.DiffuseDepression, StMagnitudeMv = 0.2 }, out _);
        Assert.Empty(a.StemiTerritories);
        Assert.True(a.StDepressionLeads.Count >= 6);
        Assert.True(a[EcgLead.aVR].StJMv >= 0.1);
        Assert.Contains("aVR", a.Interpretation.En);
    }

    [Fact]
    public void LongQtAtrialFibrillationAndAxis()
    {
        var lqt = Run(new Ecg12Options { QtcMs = 520 }, out var t);
        Assert.Equal(QtcCategory.MarkedlyProlonged, lqt.Qtc);
        Assert.InRange(lqt.QtMs, t.QtMs - 20, t.QtMs + 20);
        Assert.Contains("torsades", lqt.Interpretation.En);

        var af = Run(new Ecg12Options { Rhythm = Rhythm.AtrialFibrillation, HeartRate = 95 }, out _);
        Assert.Equal(Rhythm.AtrialFibrillation, af.Rhythm);
        Assert.Null(af.PrMs);

        var lad = Run(new Ecg12Options { QrsAxisDeg = -45 }, out var tl);
        Assert.InRange(lad.QrsAxisDeg, tl.QrsAxisDeg - 15, tl.QrsAxisDeg + 15);
        Assert.StartsWith("Left axis deviation", lad.Axis.En);

        var woman = Ecg12Analyzer.Analyze(Ecg12Simulator.Generate(new Ecg12Options { QtcMs = 455 }).Ecg, new EcgPatient(Gender.Female, 50));
        Assert.True(woman.Qtc <= QtcCategory.Prolonged);
    }

    [Fact]
    public void TangentMethodMatchesItsOwnConvention()
    {
        // The simulator defines T end with the tangent convention (peak + 2σ), so the tangent method is near-exact.
        var (ecg, t) = Ecg12Simulator.Generate();
        var a = Ecg12Analyzer.Analyze(ecg, method: TEndMethod.Tangent);
        Assert.InRange(a.QtMs, t.QtMs - 12, t.QtMs + 5);
    }

    [Fact]
    public void LeadAgnosticIntervalsOnTwoLeads()
    {
        var (ecg, t) = Ecg12Simulator.Generate();
        var r = Ecg12Analyzer.MeasureIntervals([ecg[EcgLead.II], ecg[EcgLead.V5]], ecg.SampleRate);
        Assert.InRange(r.QtMs, t.QtMs - 25, t.QtMs + 25);
        Assert.InRange(r.PrMs!.Value, t.PrMs - 15, t.PrMs + 15);
        Assert.Equal(2, r.MedianBeats.Length);
    }

    /// <summary>
    /// Agreement with cardiologist annotations of the PhysioNet QT Database (Laguna et al., 1997; ODC-By).
    /// Runs only when segments were exported to %TEMP%/mednet-qtdb (scripts in docs: tools.md → "QT validation").
    /// </summary>
    [Fact]
    public void QtDatabaseAgreement()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mednet-qtdb");
        if (!Directory.Exists(dir)) return;
        foreach (var method in Enum.GetValues<TEndMethod>()) QtDatabase(dir, method);
    }

    private void QtDatabase(string dir, TEndMethod method)
    {
        var diffs = new List<double>();
        string? split = Environment.GetEnvironmentVariable("MEDNET_QTDB_SPLIT");     // "a" / "b": alternate records (tuning vs held-out)
        var files = Directory.GetFiles(dir, "*.json").Order().Where((_, i) => split is null || (split == "a") == (i % 2 == 0));
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            double fs = root.GetProperty("fs").GetDouble();
            var leads = root.GetProperty("signals").EnumerateArray().Select(l => l.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()).ToArray();
            double annotated = root.GetProperty("annotated_qt_ms").EnumerateArray().Average(v => v.GetDouble());
            try
            {
                var r = Ecg12Analyzer.MeasureIntervals(leads, fs, method);
                diffs.Add(r.QtMs - annotated);
                output.WriteLine($"{root.GetProperty("record").GetString(),-10} annotated {annotated,5:0} measured {r.QtMs,5:0} diff {r.QtMs - annotated,5:0}  HR {r.HeartRateBpm,5:0} QRS {r.QrsMs,4:0} Tend [{string.Join(", ", r.TEndMs.Select(t => t?.ToString() ?? "-"))}] T [{string.Join(", ", r.TPeakMv.Select(t => t.ToString("0.00")))}]");
            }
            catch (InvalidOperationException e) { output.WriteLine($"{Path.GetFileName(file)}: {e.Message}"); }
        }
        if (diffs.Count == 0) return;
        double mean = diffs.Average(), sd = Math.Sqrt(diffs.Sum(d => (d - mean) * (d - mean)) / Math.Max(1, diffs.Count - 1));
        var abs = diffs.Select(Math.Abs).Order().ToArray();
        output.WriteLine($"QTDB [{method}]: n={diffs.Count} records, mean diff {mean:0.0} ms, SD {sd:0.0} ms, median |diff| {abs[abs.Length / 2]:0} ms, within 30 ms: {diffs.Count(d => Math.Abs(d) <= 30) * 100.0 / diffs.Count:0}%");
    }
}

public class EcgFileFormatTests
{
    public static readonly string ExportDir = Path.Combine(Path.GetTempPath(), "mednet-v12");
    private static readonly (Ecg12 Ecg, Ecg12Truth Truth) Sample = Ecg12Simulator.Generate(new Ecg12Options { StPattern = StPattern.InferiorStemi });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScpEcgRoundTrips(bool huffman)
    {
        var ecg = Sample.Ecg;
        var at = new DateTime(2026, 11, 13, 9, 30, 15);
        var bytes = ScpEcg.Write(ecg, "MRN100005", "Harahap", "Fajar", Gender.Male, at, huffman);
        Directory.CreateDirectory(ExportDir);
        File.WriteAllBytes(Path.Combine(ExportDir, huffman ? "ecg-huffman.scp" : "ecg-raw.scp"), bytes);
        var r = ScpEcg.Read(bytes);
        Assert.True(r.CrcValid, string.Join("; ", r.Notes));
        Assert.Equal(("MRN100005", "Harahap", "Fajar", Gender.Male, at), (r.PatientId, r.LastName, r.FirstName, r.Sex, r.Acquired));
        Assert.Equal(500, r.Ecg.SampleRate);
        foreach (var lead in Ecg12.Order)
            for (int i = 0; i < ecg.Samples; i += 7)
                Assert.InRange(r.Ecg[lead][i] - ecg[lead][i], -0.0026f, 0.0026f);       // 5 µV quantisation
        if (huffman) Assert.True(bytes.Length * 2 < ecg.Samples * 12 * 2, $"{bytes.Length} bytes");   // > 2× smaller than raw (noisy input)

        // a corrupted byte is detected by the CRC, not silently accepted
        var bad = (byte[])bytes.Clone();
        bad[^5] ^= 0x10;
        Assert.False(ScpEcg.Read(bad).CrcValid);
    }

    [Fact]
    public void DicomTwelveLeadWaveformRoundTrips()
    {
        var ecg = Sample.Ecg;
        var analysis = Ecg12Analyzer.Analyze(ecg);
        var file = Medical.Dicom.DicomEcg.Create(ecg, SyntheticData.Patient(5), analysis, acquired: new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.FromHours(7)));
        Directory.CreateDirectory(ExportDir);
        string path = Path.Combine(ExportDir, "ecg-12lead.dcm");
        file.Save(path);
        var back = Medical.Dicom.DicomEcg.Read(FellowOakDicom.DicomFile.Open(path));
        Assert.Equal(ecg.SampleRate, back.SampleRate);
        foreach (var lead in Ecg12.Order)
            for (int i = 0; i < ecg.Samples; i += 11)
                Assert.InRange(back[lead][i] - ecg[lead][i], -0.0006f, 0.0006f);         // 1 µV resolution
        var again = Ecg12Analyzer.Analyze(back);
        Assert.Equal(analysis.StemiTerritories, again.StemiTerritories);
        Assert.InRange(again.QtMs, analysis.QtMs - 4, analysis.QtMs + 4);
    }
}
