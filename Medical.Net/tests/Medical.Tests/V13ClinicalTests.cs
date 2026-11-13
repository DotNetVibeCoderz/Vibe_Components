using System.Globalization;
using Medical.Clinical;
using Medical.Core;
using Xunit.Abstractions;

namespace Medical.Tests;

public class CriticalCareScoreTests
{
    [Fact]
    public void SofaScoresEachOrganAndListsMissing()
    {
        var r = ClinicalScores.Sofa(new SofaInputs
        {
            PaO2FiO2 = 150, RespiratorySupport = true, Platelets = 80, BilirubinMgDl = 2.5,
            Vasopressors = new Vasopressors(Norepinephrine: 0.05), Gcs = 13, CreatinineMgDl = 2.5,
        });
        Assert.Equal([3, 2, 2, 3, 1, 2], r.Components.Select(c => c.Points));
        Assert.Equal(13, r.Score);
        Assert.Equal(RiskLevel.Critical, r.Risk);

        // Without ventilation PaO2/FiO2 < 200 caps at 2; urine output < 200 mL/d scores 4; missing values are reported.
        var partial = ClinicalScores.Sofa(new SofaInputs { PaO2FiO2 = 90, UrineOutputMlPerDay = 150 });
        Assert.Equal(2 + 4, partial.Score);
        Assert.Contains("platelets", partial.Interpretation.En);
        Assert.Contains("GCS", partial.Interpretation.En);

        var worst = ClinicalScores.Sofa(new SofaInputs
        {
            PaO2FiO2 = 80, RespiratorySupport = true, Platelets = 10, BilirubinMgDl = 15, Vasopressors = new Vasopressors(Dopamine: 20),
            Gcs = 3, CreatinineMgDl = 6,
        });
        Assert.Equal(24, worst.Score);
    }

    [Fact]
    public void ApacheIIMatchesHandComputedCase()
    {
        var x = new ApacheIIInputs
        {
            TemperatureC = 39.5, MeanArterialPressure = 65, HeartRate = 125, RespiratoryRate = 30, FiO2 = 0.4, PaO2 = 65,
            ArterialPh = 7.30, Sodium = 132, Potassium = 3.2, CreatinineMgDl = 1.8, Hematocrit = 35, Wbc = 18, Gcs = 13, Age = 70,
        };
        var r = ClinicalScores.ApacheII(x);
        Assert.Equal([3, 2, 2, 1, 1, 2, 0, 1, 2, 0, 1, 2, 5, 0], r.Components.Select(c => c.Points));
        Assert.Equal(22, r.Score);
        Assert.Equal(RiskLevel.High, r.Risk);

        // Full equation: logit = −3.517 + 0.146·22 + weight (0 here) → 42.4 %.
        Assert.Contains("42.4 %", ClinicalScores.ApacheII(x, 0).Interpretation.En);
        // Acute renal failure doubles creatinine points; chronic health adds 5 (non-operative) or 2 (elective post-op).
        Assert.Equal(22 + 2 + 5, ClinicalScores.ApacheII(x with { AcuteRenalFailure = true, ChronicHealth = true }).Score);
        Assert.Equal(22 + 2, ClinicalScores.ApacheII(x with { ChronicHealth = true, Admission = ApacheAdmission.ElectivePostoperative }).Score);
        Assert.Throws<ArgumentException>(() => ClinicalScores.ApacheII(x with { FiO2 = 0.6 }));   // needs the A–a gradient
    }

    [Fact]
    public void PewsAndMeowsTriggers()
    {
        Assert.Equal(RiskLevel.Medium, ClinicalScores.Pews(new PewsInputs(1, 2, 1)).Risk);
        Assert.Equal(4, ClinicalScores.Pews(new PewsInputs(1, 2, 1)).Score);
        Assert.Equal(RiskLevel.High, ClinicalScores.Pews(new PewsInputs(3, 0, 0)).Risk);   // any 3 → urgent
        Assert.Equal(6, ClinicalScores.Pews(new PewsInputs(1, 1, 2, true)).Score);

        var normal = ClinicalScores.Meows(16, 98, 36.8, 115, 70, 80, Avpu.Alert);
        Assert.Equal(0, normal.Score);
        Assert.Equal(RiskLevel.Low, normal.Risk);
        var twoYellow = ClinicalScores.Meows(25, 98, 36.8, 115, 70, 110, Avpu.Alert);
        Assert.Equal(2, twoYellow.Score);
        Assert.Equal(RiskLevel.High, twoYellow.Risk);
        var red = ClinicalScores.Meows(16, 92, 36.8, 165, 105, 80, Avpu.Alert);   // SpO2, SBP, DBP red
        Assert.Equal(RiskLevel.Critical, red.Risk);
        Assert.Equal(6, red.Score);
    }

    [Fact]
    public void WellsPercAndChildPugh()
    {
        var pe = ClinicalScores.WellsPe(true, false, 110, false, false, false, false);
        Assert.Equal(4.5, pe.ExactScore);
        Assert.Equal(9, pe.Score);
        Assert.Equal(RiskLevel.High, pe.Risk);
        Assert.Equal(4.0, ClinicalScores.WellsPe(false, true, 80, false, false, true, false).ExactScore);
        Assert.Equal(RiskLevel.Low, ClinicalScores.WellsPe(false, true, 80, false, false, true, false).Risk);   // exactly 4 is "unlikely"

        Assert.Equal(1, ClinicalScores.WellsDvt(true, false, false, false, false, true, false, false, false, true).Score + 1);
        Assert.Equal(RiskLevel.High, ClinicalScores.WellsDvt(true, false, true, false, false, false, false, false, false, false).Risk);

        Assert.Equal(0, ClinicalScores.Perc(30, 80, 98, false, false, false, false, false).Score);
        Assert.Equal(3, ClinicalScores.Perc(55, 105, 93, false, false, false, false, false).Score);

        var cp = ClinicalScores.ChildPugh(2.5, 3.0, 1.5, 1, 0);
        Assert.Equal(8, cp.Score);
        Assert.Equal("B", ClinicalScores.ChildPughClass(cp.Score));
        Assert.Equal("A", ClinicalScores.ChildPughClass(ClinicalScores.ChildPugh(1, 4, 1.1, 0, 0).Score));
        Assert.Equal("C", ClinicalScores.ChildPughClass(ClinicalScores.ChildPugh(4, 2.5, 2.5, 2, 1).Score));
    }
}

public class CardiovascularRiskTests
{
    private static CvRiskInputs Example(Gender sex, double sbp = 120) => new(55, sex, 213, 50, sbp, false, false, false);

    [Theory]
    // 2013 ACC/AHA guideline worked examples (age 55, TC 213, HDL 50, SBP 120 untreated, non-smoker, non-diabetic).
    [InlineData(Gender.Female, PceRace.WhiteOrOther, 120, 2.1)]
    [InlineData(Gender.Female, PceRace.AfricanAmerican, 120, 3.0)]
    [InlineData(Gender.Male, PceRace.WhiteOrOther, 120, 5.38)]   // the guideline table prints 5.3 %; its equation and sum (60.69) give 5.38 %
    [InlineData(Gender.Male, PceRace.AfricanAmerican, 120, 6.1)]
    // Same profile with SBP 140, values from the CVrisk R package test-suite.
    [InlineData(Gender.Male, PceRace.AfricanAmerican, 140, 7.95)]
    [InlineData(Gender.Female, PceRace.AfricanAmerican, 140, 5.08)]
    public void PooledCohortEquationsReproducePublishedValues(Gender sex, PceRace race, double sbp, double expected)
    {
        var r = CardiovascularRisk.Ascvd(Example(sex, sbp), race);
        Assert.InRange(r.TenYearRiskPercent, expected - 0.06, expected + 0.06);
    }

    [Fact]
    public void FraminghamAndCategories()
    {
        var r = CardiovascularRisk.Framingham(Example(Gender.Male, 140));
        Assert.InRange(r.TenYearRiskPercent, 13.45, 13.6);   // CVrisk: 13.53 %
        Assert.Equal(RiskLevel.Medium, r.Risk);

        var high = CardiovascularRisk.Ascvd(new CvRiskInputs(70, Gender.Male, 260, 35, 170, true, true, true));
        Assert.True(high.TenYearRiskPercent >= 20);
        Assert.Equal(RiskLevel.High, high.Risk);
        Assert.Contains(CardiovascularRisk.Ascvd(Example(Gender.Male) with { Age = 35 }).Warnings, w => w.Contains("outside the validated range"));
        // Each risk factor raises the estimate.
        double baseline = CardiovascularRisk.Ascvd(Example(Gender.Female)).TenYearRiskPercent;
        Assert.True(CardiovascularRisk.Ascvd(Example(Gender.Female) with { Smoker = true }).TenYearRiskPercent > baseline);
        Assert.True(CardiovascularRisk.Ascvd(Example(Gender.Female) with { Diabetes = true }).TenYearRiskPercent > baseline);
    }
}

public class WhoGrowthTests(ITestOutputHelper output)
{
    private static readonly string Cache = Path.Combine(Path.GetTempPath(), "mednet-who-growth");
    private static WhoGrowthStandards? _who;
    private static bool _offline;

    private static WhoGrowthStandards? Who()
    {
        if (_who is not null || _offline) return _who;
        try { _who = WhoGrowthStandards.DownloadAsync(Cache).GetAwaiter().GetResult(); }
        catch (HttpRequestException) { _offline = true; }
        catch (TaskCanceledException) { _offline = true; }
        return _who;
    }

    [Fact]
    public void LmsRestrictedZBeyondThreeSd()
    {
        var lms = new Lms(-0.3521, 10.0, 0.11);
        Assert.Equal(1.5, lms.Z(lms.ValueAt(1.5)), 9);
        double sd3 = lms.ValueAt(3), sd2 = lms.ValueAt(2);
        Assert.Equal(4, lms.ZRestricted(sd3 + (sd3 - sd2)), 9);   // one 2–3 SD interval above +3 SD
        Assert.Equal(0.5, WhoGrowthStandards.NormalCdf(0), 6);
        Assert.Equal(0.97725, WhoGrowthStandards.NormalCdf(2), 4);
    }

    [Fact]
    public void ReproducesWhoAnthroExamples()
    {
        var who = Who();
        if (who is null) { output.WriteLine("WHO tables not reachable — skipped."); return; }
        // Expected values from the WHO anthro package's own tests.
        var r = who.Assess(GrowthIndicator.LengthHeightForAge, Gender.Male, 987, 0, 72.86, LengthMeasure.Recumbent);
        Assert.Equal(-6.09, r.ZScore);
        Assert.True(r.Flagged);
        // Standing below 9 months is ignored; at 9 months it adds 0.7 cm.
        Assert.Equal(-4.81, who.Assess(GrowthIndicator.LengthHeightForAge, Gender.Male, 8 * 30.4375, 0, 60, LengthMeasure.Standing).ZScore, 2);
        var nine = who.Assess(GrowthIndicator.LengthHeightForAge, Gender.Male, 9 * 30.4375, 0, 60, LengthMeasure.Standing);
        Assert.Equal(60.7, nine.Value, 6);
        Assert.Equal(-5.02, nine.ZScore, 2);
        Assert.Equal(-2.55, who.Assess(GrowthIndicator.LengthHeightForAge, Gender.Female, 24 * 30.4375, 0, 77.5).ZScore);
        Assert.Contains("Stunted", who.Assess(GrowthIndicator.LengthHeightForAge, Gender.Female, 24 * 30.4375, 0, 77.5).Classification.En);
        // Weight-for-length interpolates at 0.1 cm and classifies wasting.
        var wfl = who.Assess(GrowthIndicator.WeightForLengthHeight, Gender.Male, 300, 6.5, 70.25);
        Assert.True(wfl.ZScore < -2);
        Assert.Contains("wasted", wfl.Classification.En, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, who.SdLines(GrowthIndicator.WeightForAge, Gender.Female, 365).Count);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1031", Justification = "Cached one-off download.")]
    public void MatchesWhoAnthroPlusSurveyZScores()
    {
        var who = Who();
        if (who is null) { output.WriteLine("WHO tables not reachable — skipped."); return; }
        string csv = Path.Combine(Cache, "survey_who2007_z.csv");
        if (!File.Exists(csv))
        {
            try
            {
                using var http = new HttpClient();
                File.WriteAllText(csv, http.GetStringAsync("https://raw.githubusercontent.com/WorldHealthOrganization/anthroplus/7cfcdb39026e9a55de55732bc3cf14c82261bcf7/data-raw/survey_who2007_z.csv").GetAwaiter().GetResult());
            }
            catch (HttpRequestException) { output.WriteLine("Survey not reachable — skipped."); return; }
        }
        var lines = File.ReadAllLines(csv);
        var head = lines[0].Split(',').Select(h => h.Trim('"')).ToList();
        int Col(string n) => head.IndexOf(n);
        int compared = 0, mismatched = 0;
        double maxDiff = 0;
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',').Select(v => v.Trim('"')).ToArray();
            double? D(string n) => double.TryParse(f[Col(n)], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            if (D("agemons") is not { } months || D("sex") is not { } sexCode) continue;
            var sex = sexCode == 1 ? Gender.Male : Gender.Female;
            double days = months * 30.4375;
            void Check(double? expected, Func<GrowthResult> compute)
            {
                if (expected is not { } e) return;
                double z = compute().ZScore;
                compared++;
                double d = Math.Abs(z - e);
                maxDiff = Math.Max(maxDiff, d);
                if (d > 0.0100001) mismatched++;
            }
            Check(D("zhfa"), () => who.Assess(GrowthIndicator.LengthHeightForAge, sex, days, D("height")!.Value));
            Check(D("zwfa"), () => who.Assess(GrowthIndicator.WeightForAge, sex, days, D("weight")!.Value));
            Check(D("zbfa"), () => who.Assess(GrowthIndicator.BmiForAge, sex, days, D("weight")!.Value, D("height")!.Value));
        }
        output.WriteLine($"compared {compared} z-scores, max |diff| {maxDiff:0.###}, mismatches {mismatched}");
        Assert.True(compared > 1500);
        Assert.Equal(0, mismatched);
    }
}
