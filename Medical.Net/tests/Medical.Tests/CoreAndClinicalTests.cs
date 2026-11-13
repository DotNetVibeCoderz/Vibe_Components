using Medical.Clinical;
using Medical.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Medical.Tests;

public class CoreTests
{
    [Fact]
    public void SyntheticDataIsDeterministic()
    {
        var a = SyntheticData.Record(5, 0.4);
        var b = SyntheticData.Record(5, 0.4);
        Assert.Equal(a.Patient, b.Patient);
        Assert.Equal(a.Vitals.Select(v => v.HeartRate), b.Vitals.Select(v => v.HeartRate));
        Assert.Equal(a.Observations.Select(o => o.Id), b.Observations.Select(o => o.Id));
        // pinned value guards against accidental changes to the generator
        Assert.Equal(new SeededRandom(42).NextUInt64(), new SeededRandom(42).NextUInt64());
        Assert.Equal(13679457532755275413UL, new SeededRandom(42).NextUInt64());
    }

    [Fact]
    public void SyntheticPatientsAreAdultsWithStructurallyValidNik()
    {
        foreach (var p in SyntheticData.Cohort(50))
        {
            Assert.InRange(p.AgeAt(new DateOnly(2026, 11, 13)), 22, 87);
            Assert.True(Terminology.CodeValidators.TryParseNik(p.Nik!, out var dob, out var female), p.Nik);
            Assert.Equal(p.BirthDate, dob);
            Assert.Equal(p.Gender == Gender.Female, female);
        }
    }

    [Theory]
    [InlineData(37.0, "Cel", "[degF]", 98.6)]
    [InlineData(70.0, "kg", "[lb_av]", 154.32)]
    [InlineData(120.0, "mm[Hg]", "kPa", 16.0)]
    [InlineData(1.0, "g/dL", "mg/dL", 1000.0)]
    public void UcumConversions(double v, string from, string to, double expected) =>
        Assert.Equal(expected, Ucum.Convert(v, from, to), 1);

    [Fact]
    public void UcumRejectsIncompatibleUnits() => Assert.Throws<InvalidOperationException>(() => Ucum.Convert(1, "kg", "cm"));

    [Fact]
    public void GlucoseMolarConversion() => Assert.Equal(5.55, Ucum.MgPerDlToMmolPerL(100, 180.16), 2);

    [Fact]
    public void EventBusDeliversAndUnsubscribes()
    {
        var bus = new InMemoryMedicalEventBus();
        int got = 0;
        var sub = bus.Subscribe<string>(_ => got++);
        bus.Publish("a");
        sub.Dispose();
        bus.Publish("b");
        Assert.Equal(1, got);
    }

    [Fact]
    public void DiRegistersModulesOnce()
    {
        var services = new ServiceCollection();
        int calls = 0;
        var b = services.AddMedical(m => m.TryAddModule("x", _ => calls++).TryAddModule("x", _ => calls++));
        Assert.Equal(1, calls);
        Assert.Contains("x", b.Modules);
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<IAuditLog>());
    }

    [Fact]
    public void PseudonymsAreStableAndKeyed()
    {
        var a = new Pseudonymizer("0123456789abcdef-secret");
        var b = new Pseudonymizer("another-secret-key-000");
        Assert.Equal(a.Pseudonym("MRN1", "P-"), a.Pseudonym("MRN1", "P-"));
        Assert.NotEqual(a.Pseudonym("MRN1"), b.Pseudonym("MRN1"));
        Assert.InRange(a.DateShiftDays("x", 30), -30, 30);
        Assert.Throws<ArgumentException>(() => new Pseudonymizer("short"));
    }

    [Fact]
    public void PngEncoderWritesValidSignature()
    {
        var png = PngEncoder.EncodeGray8(new byte[16 * 8], 16, 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal("IEND"u8.ToArray(), png[^8..^4]);
    }
}

public class ClinicalScoreTests
{
    private static VitalSigns Normal => new() { RespiratoryRate = 16, SpO2 = 98, SystolicBp = 125, HeartRate = 72, TemperatureC = 36.8 };

    [Fact]
    public void News2ZeroForNormalVitals()
    {
        var s = ClinicalScores.News2(Normal);
        Assert.Equal(0, s.Score);
        Assert.Equal(RiskLevel.Low, s.Risk);
    }

    [Fact]
    public void News2RcpWorkedExample()
    {
        // RR 24 (2), SpO2 93 (2), on O2 (2), SBP 105 (1), HR 115 (2), Alert (0), Temp 38.5 (1) = 10
        var v = new VitalSigns { RespiratoryRate = 24, SpO2 = 93, OnSupplementalOxygen = true, SystolicBp = 105, HeartRate = 115, TemperatureC = 38.5 };
        var s = ClinicalScores.News2(v);
        Assert.Equal(10, s.Score);
        Assert.Equal(RiskLevel.High, s.Risk);
        Assert.Equal([2, 2, 2, 1, 2, 0, 1], s.Components.Select(c => c.Points));
    }

    [Fact]
    public void News2SingleRedParameterIsLowMedium()
    {
        var s = ClinicalScores.News2(Normal with { Consciousness = Consciousness.Confusion });
        Assert.Equal(3, s.Score);
        Assert.Equal(RiskLevel.LowMedium, s.Risk);
    }

    [Theory]
    [InlineData(88, false, 0)]  // scale 2 target range on air
    [InlineData(95, true, 2)]
    [InlineData(83, false, 3)]
    public void News2HypercapnicScale(double spo2, bool o2, int expected) =>
        Assert.Equal(expected, ClinicalScores.News2(Normal with { SpO2 = spo2, OnSupplementalOxygen = o2 }, hypercapnicScale: true).Components[1].Points);

    [Fact]
    public void QSofaThreshold()
    {
        Assert.Equal(2, ClinicalScores.QSofa(22, 100, false).Score);
        Assert.Equal(RiskLevel.High, ClinicalScores.QSofa(22, 100, false).Risk);
        Assert.Equal(0, ClinicalScores.QSofa(18, 120, false).Score);
    }

    [Fact]
    public void Curb65AndCha2Ds2Vasc()
    {
        Assert.Equal(5, ClinicalScores.Curb65(true, 9, 32, 85, 55, 80).Score);
        // 76-year-old woman with hypertension and diabetes: age 2 + HTN 1 + DM 1 + female 1 = 5
        Assert.Equal(5, ClinicalScores.Cha2Ds2Vasc(76, Gender.Female, false, true, true, false, false).Score);
        Assert.Equal(RiskLevel.Low, ClinicalScores.Cha2Ds2Vasc(50, Gender.Female, false, false, false, false, false).Risk);
    }

    [Fact]
    public void GcsApgarMeld()
    {
        Assert.Equal(RiskLevel.Critical, ClinicalScores.Gcs(2, 2, 4).Risk);
        Assert.Throws<ArgumentOutOfRangeException>(() => ClinicalScores.Gcs(5, 5, 6));
        Assert.Equal(10, ClinicalScores.Apgar(2, 2, 2, 2, 2).Score);
        // creatinine 1.9, bilirubin 4.2, INR 1.8 → MELD 25 (UNOS formula)
        Assert.Equal(25, ClinicalScores.Meld(1.9, 4.2, 1.8).Score);
        Assert.Equal(6, ClinicalScores.Meld(0.5, 0.5, 0.9).Score);
    }

    [Theory]
    [InlineData(1.0, 50, Gender.Male, 92)]   // NKF CKD-EPI 2021 calculator reference values
    [InlineData(1.0, 50, Gender.Female, 69)]
    [InlineData(2.5, 70, Gender.Male, 27)]
    public void EgfrCkdEpi2021(double cr, int age, Gender g, double expected) =>
        Assert.Equal(expected, Calculators.EgfrCkdEpi2021(cr, age, g).Egfr);

    [Fact]
    public void BmiStandardsDiffer()
    {
        Assert.Equal("Normal", Calculators.Bmi(70, 172).Category);           // 23.7
        Assert.Equal("Overweight", Calculators.Bmi(70, 172, BmiStandard.AsiaPacific).Category);
        Assert.Equal(1.82, Calculators.BsaMosteller(70, 170), 2);
    }

    [Fact]
    public void CockcroftGaultAndFluids()
    {
        Assert.Equal(68.0, Calculators.CockcroftGault(1.0, 60, 72, Gender.Female), 1); // (140-60)×72/(72×1.0)×0.85
        Assert.Equal(85, Calculators.MaintenanceFluidMlPerHour(45)); // 10×4 + 10×2 + 25×1
    }

    [Fact]
    public void DosingCapsAndRenalAdjustment()
    {
        var child = DoseCalculator.Calculate("paracetamol", 18);
        Assert.Equal(270, child.SingleDoseMg);
        Assert.False(child.Capped);
        var adult = DoseCalculator.Calculate("paracetamol", 90);
        Assert.Equal(1000, adult.SingleDoseMg);
        Assert.True(adult.Capped);
        var renal = DoseCalculator.Calculate("amoxicillin", 60, crClMlMin: 20);
        Assert.True(renal.RenalAdjusted);
        Assert.Equal(500, renal.SingleDoseMg);
        Assert.Throws<KeyNotFoundException>(() => DoseCalculator.Calculate("unobtainium", 70));
    }

    [Fact]
    public void LabInterpreterFlagsCriticalAndSexSpecificRanges()
    {
        var li = new LabInterpreter();
        var k = new ClinicalObservation { PatientId = "p", Code = Loinc.Potassium, Value = 6.8, Unit = "mmol/L" };
        Assert.Equal(ResultFlag.CriticalHigh, li.Interpret(k).Flag);
        var hb = new ClinicalObservation { PatientId = "p", Code = Loinc.Hemoglobin, Value = 12.5, Unit = "g/dL" };
        Assert.Equal(ResultFlag.Low, li.Interpret(hb, Gender.Male).Flag);
        Assert.Equal(ResultFlag.Normal, li.Interpret(hb, Gender.Female).Flag);
        var g = new ClinicalObservation { PatientId = "p", Code = Loinc.Glucose, Value = 1.0, Unit = "g/L" }; // = 100 mg/dL
        Assert.Equal(ResultFlag.Normal, li.Interpret(g).Flag);
    }
}
