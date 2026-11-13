using System.Net;
using System.Net.Http.Json;
using System.Text;
using Medical.AI;
using Medical.AI.Assistant;
using Medical.AspNetCore;
using Medical.Core;
using Medical.Emr;
using Medical.Privacy;
using Medical.Rules;
using Medical.Visualization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Medical.Tests;

public class RulesTests
{
    private static PatientRecord Septic() => SyntheticData.Record(3, 0.9) with
    {
        Medications =
        [
            new MedicationOrder { PatientId = "pat-00003", Medication = ClinicalCode.RxNorm("11289", "warfarin"), Generic = "warfarin", DoseMg = 5 },
            new MedicationOrder { PatientId = "pat-00003", Medication = ClinicalCode.RxNorm("5640", "ibuprofen"), Generic = "ibuprofen", DoseMg = 400 },
            new MedicationOrder { PatientId = "pat-00003", Medication = ClinicalCode.RxNorm("723", "amoxicillin"), Generic = "amoxicillin", DoseMg = 500 },
        ],
        Allergies = [new Allergy { PatientId = "pat-00003", Substance = "penicillin", Reaction = "Anaphylaxis" }],
    };

    [Fact]
    public void CdsRaisesExpectedAlertsInSeverityOrder()
    {
        var alerts = new ClinicalDecisionSupport().Evaluate(Septic());
        string[] rules = [.. alerts.Select(a => a.Rule)];
        Assert.Contains("NEWS2.High", rules);
        Assert.Contains("Sepsis.Screen", rules);
        Assert.Contains("Drug.Allergy", rules);
        Assert.Contains(alerts, a => a.Rule == "Drug.Interaction" && a.Evidence == "ibuprofen+warfarin");
        Assert.Equal(alerts.Select(a => a.Severity).OrderDescending(), alerts.Select(a => a.Severity));
        Assert.Equal(alerts.Count, alerts.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void HealthyPatientHasNoCriticalAlerts() =>
        Assert.DoesNotContain(new ClinicalDecisionSupport().Evaluate(SyntheticData.Record(8, 0)), a => a.Severity == AlertSeverity.Critical);

    [Fact]
    public void MetforminRenalRuleUsesEgfr()
    {
        var r = SyntheticData.Record(10) with
        {
            Observations = [new ClinicalObservation { PatientId = "pat-00010", Code = Loinc.Creatinine, Value = 3.2, Unit = "mg/dL" }],
            Medications = [new MedicationOrder { PatientId = "pat-00010", Medication = ClinicalCode.RxNorm("6809", "metformin"), Generic = "metformin", DoseMg = 500 }],
        };
        var a = Assert.Single(new ClinicalDecisionSupport().Evaluate(r), x => x.Rule == "Renal.Metformin");
        Assert.Equal(AlertSeverity.Critical, a.Severity);
    }

    [Theory]
    [InlineData("simvastatin", "clarithromycin", InteractionSeverity.Contraindicated)]
    [InlineData("lisinopril", "spironolactone", InteractionSeverity.Major)]
    [InlineData("sertraline", "tramadol", InteractionSeverity.Major)]
    [InlineData("clopidogrel", "omeprazole", InteractionSeverity.Moderate)]
    public void DrugKnowledgeInteractions(string a, string b, InteractionSeverity expected)
    {
        Assert.Equal(expected, DrugKnowledge.Default.Interaction(a, b)!.Severity);
        Assert.Equal(expected, DrugKnowledge.Default.Interaction(b, a)!.Severity);
    }

    [Fact]
    public void NoInteractionForUnrelatedDrugs() => Assert.Null(DrugKnowledge.Default.Interaction("paracetamol", "amlodipine"));

    [Fact]
    public async System.Threading.Tasks.Task JsonWorkflowsEvaluate()
    {
        var rules = new ConfigurableRules();
        Assert.Contains("Triage", rules.WorkflowNames);
        var triage = await rules.EvaluateAsync("Triage", ClinicalSnapshot.From(Septic()));
        Assert.True(triage.First(r => r.Rule == "Resuscitation").Matched);
        var calm = await rules.EvaluateAsync("Triage", new ClinicalSnapshot { News2 = 0, SpO2 = 98, SystolicBp = 120, HeartRate = 70, TemperatureC = 36.8 });
        Assert.Equal(["Standard"], calm.Where(r => r.Matched).Select(r => r.Rule));
        var dengue = await rules.EvaluateAsync("DengueWarningSigns", new ClinicalSnapshot { Dengue = true, Platelets = 15, SystolicBp = 85, HeartRate = 130, Alert = true });
        Assert.Equal(3, dengue.Count(r => r.Matched));
        Assert.All(dengue, r => Assert.Null(r.Error));
    }
}

public class PrivacyTests
{
    private const string Note = "Tn. Budi Santoso, 67 tahun, NIK 3201234567890001, No. RM: MRN100245, alamat Jl. Merdeka No. 12 RT 03/RW 05 Bandung, " +
                                "HP 0812-3456-7890, email budi.s@example.com. Masuk 12 November 2026 (kontrol 2026-11-20). Budi mengeluh sesak. Ayah 92 tahun.";

    [Fact]
    public void RemovesAllIdentifierClasses()
    {
        var d = new TextDeidentifier().Deidentify(Note);
        foreach (var leaked in new[] { "Budi", "Santoso", "3201234567890001", "MRN100245", "Merdeka", "0812", "budi.s@", "12 November 2026", "2026-11-20", "92 tahun" })
            Assert.DoesNotContain(leaked, d.Text);
        Assert.Contains("67 tahun", d.Text); // ages ≤ 89 are not identifiers under Safe Harbor
        Assert.Contains("mengeluh sesak", d.Text);
        foreach (var t in new[] { PhiType.Name, PhiType.Nik, PhiType.Mrn, PhiType.Address, PhiType.Phone, PhiType.Email, PhiType.Date, PhiType.Age })
            Assert.True(d.Counts.ContainsKey(t), t.ToString());
    }

    [Fact]
    public void PseudonymsAndDateShiftAreConsistent()
    {
        var deid = new TextDeidentifier("privacy-test-secret-key");
        var a = deid.Deidentify(Note, ReplacementStyle.Pseudonyms, "pat-1");
        var b = deid.Deidentify(Note, ReplacementStyle.Pseudonyms, "pat-1");
        Assert.Equal(a.Text, b.Text);
        Assert.DoesNotContain("Budi", a.Text);
        Assert.Contains("90+", a.Text);
    }

    [Fact]
    public void MaskingAndConsent()
    {
        Assert.Equal("3201********0001", Masking.Nik("3201234567890001"));
        Assert.Equal("b***s@example.com", Masking.Email("budi.s@example.com"));
        var audit = new InMemoryAuditLog();
        var reg = new ConsentRegistry(audit);
        Assert.True(reg.IsPermitted("p1", ConsentPurpose.Treatment));
        Assert.False(reg.IsPermitted("p1", ConsentPurpose.Research));
        reg.Record(new ConsentRecord("p1", ConsentPurpose.Research, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1)));
        Assert.True(reg.IsPermitted("p1", ConsentPurpose.Research));
        reg.Record(new ConsentRecord("p1", ConsentPurpose.Research, false, DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.False(reg.IsPermitted("p1", ConsentPurpose.Research));
        Assert.True(audit.Query("p1").Count >= 5);
    }
}

public class EmrTests
{
    [Fact]
    public async System.Threading.Tasks.Task SqliteRepositoryRoundTripWithCacheAndAudit()
    {
        string db = Path.Combine(Path.GetTempPath(), $"mednet-{Guid.NewGuid():n}.db");
        var services = new ServiceCollection();
        services.AddMedical(m => m.AddEmr(o => { o.Provider = EmrProvider.Sqlite; o.ConnectionString = $"Data Source={db}"; }));
        await using var sp = services.BuildServiceProvider();
        await sp.EnsureEmrDatabaseAsync();
        using (var scope = sp.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<PatientRepository>();
            Assert.Equal(12, await repo.SeedSyntheticAsync(12));
            Assert.Equal(0, await repo.SeedSyntheticAsync(12));
            var original = SyntheticData.Record(7, 0.85);
            var loaded = await repo.GetRecordAsync("pat-00007", "treatment");
            Assert.NotNull(loaded);
            Assert.Equal(original.Patient, loaded.Patient);
            Assert.Equal(original.Observations.Count, loaded.Observations.Count);
            Assert.Equal(original.Vitals.Select(v => v.HeartRate), loaded.Vitals.Select(v => v.HeartRate));
            Assert.Equal(original.Medications.Select(m => m.Generic).Order(), loaded.Medications.Select(m => m.Generic));
            Assert.Equal(loaded, await repo.GetRecordAsync("pat-00007") with { }, new RecordComparer()); // served from cache
            await repo.AddVitalsAsync("pat-00007", original.Vitals[0] with { Timestamp = original.Vitals[^1].Timestamp.AddHours(1), HeartRate = 140 });
            Assert.Equal(140, (await repo.GetRecordAsync("pat-00007"))!.LatestVitals!.HeartRate);
            Assert.NotEmpty(await repo.SearchAsync(original.Patient.Name.Family));
            Assert.Single(await repo.SearchAsync(original.Patient.Mrn));
            Assert.Equal(3, (await repo.VitalsAsync("pat-00007", original.Vitals[^2].Timestamp, DateTimeOffset.MaxValue)).Count); // two originals + the new one
        }
        var audit = sp.GetRequiredService<IAuditLog>();
        Assert.IsType<EfAuditLog>(audit);
        Assert.Contains(audit.Query("pat-00007"), e => e.Action == AuditAction.Read && e.Purpose == "treatment");
    }

    private sealed class RecordComparer : IEqualityComparer<PatientRecord?>
    {
        public bool Equals(PatientRecord? x, PatientRecord? y) => x?.Patient == y?.Patient && x?.Observations.Count == y?.Observations.Count;
        public int GetHashCode(PatientRecord? obj) => 0;
    }
}

public class AiTests
{
    [Fact]
    public void RiskModelsTrainWellAndExplainPredictions()
    {
        var m = new DeteriorationRiskModel();
        var metrics = m.Train(3000);
        Assert.True(metrics.Auc > 0.9, $"AUC {metrics.Auc}");
        Assert.True(metrics.TestRows > 400);
        var sick = m.Predict(SyntheticData.Record(7, 0.9));
        var well = m.Predict(SyntheticData.Record(8, 0.0));
        Assert.True(sick.Probability > 0.8 && well.Probability < 0.1, $"{sick.Probability} / {well.Probability}");
        Assert.Equal("news2", sick.Drivers[0].Feature);
        var d = new DiabetesRiskModel();
        Assert.True(d.Train(3000).Auc > 0.75);
        Assert.True(d.Predict([58, 31, 104, 150, 0, 0, 1, 1]).Probability > d.Predict([30, 21, 76, 112, 1, 1, 0, 0]).Probability);
    }

    [Theory]
    [InlineData("Bagaimana kondisi pasien ini?", "get_patient_summary", "NEWS2")]
    [InlineData("dosis parasetamol untuk anak 18 kg", "weight_based_dose", "270 mg")]
    [InlineData("Is warfarin safe with ibuprofen?", "check_drug_safety", "Major")]
    [InlineData("kode ICD-10 untuk demam berdarah", "lookup_terminology", "A91")]
    public async System.Threading.Tasks.Task OfflineAssistantIsGroundedInTools(string question, string tool, string expected)
    {
        var a = new ClinicalAssistant(new OfflineClinicalLlm()) { Patient = SyntheticData.Record(7, 0.9) };
        var reply = await a.AskAsync(question);
        Assert.Contains(reply.ToolCalls, t => t.Name == tool && !t.IsError);
        Assert.Contains(expected, reply.Text);
        Assert.Equal(2, a.History.Count);
    }

    [Fact]
    public void ToolsReportErrorsInsteadOfThrowing()
    {
        var tools = new ClinicalTools();
        Assert.True(tools.Execute("weight_based_dose", """{"drug":"unobtainium","weight_kg":70}""", null).IsError);
        Assert.True(tools.Execute("nope", "{}", null).IsError);
        Assert.Contains("\"score\":0", tools.Execute("calculate_news2",
            """{"respiratory_rate":16,"spo2":98,"supplemental_oxygen":false,"systolic_bp":125,"heart_rate":72,"temperature_c":36.8,"consciousness":"alert"}""", null).Output);
        Assert.Equal(8, tools.All.Count);
    }

    [Fact]
    public void LlmOptionsDefaultToOfflineWithoutKeys()
    {
        var o = new LlmOptions();
        Assert.IsType<OfflineClinicalLlm>(o.Create());
        Assert.IsType<AnthropicClinicalLlm>(new LlmOptions { Provider = LlmProvider.Anthropic, ApiKey = "sk-test" }.Create());
        Assert.Equal("claude-opus-5-5", ((AnthropicClinicalLlm)new LlmOptions { Provider = LlmProvider.Anthropic, ApiKey = "x" }.Create()).Model);
    }
}

public class VisualizationTests
{
    [Fact]
    public void ChartsProduceSvgWithThemeVariables()
    {
        var rec = SyntheticData.Record(3, 0.9);
        var ecg = Signals.EcgSimulator.Generate(6);
        string[] svgs =
        [
            Charts.EcgStrip(ecg.Samples, ecg.SampleRate, Native.MedicalKernels.DetectRPeaks(ecg.Samples, ecg.SampleRate)),
            Charts.ObservationChart(rec.Vitals, "id"),
            Charts.Trend([new Series("HR", rec.Vitals.Select(v => (v.Timestamp, v.HeartRate)).ToArray(), SvgTheme.Red)], 40, 160, (51, 90), "/min"),
            Charts.DivergingBars([("news2", 2.1), ("egfr", -0.4)]),
            Charts.Histogram([1, 5, 20, 3], -1000, 1000, [(40, "brain")]),
            Charts.Gauge(7, 20, [(4, SvgTheme.Teal), (6, SvgTheme.Amber), (20, SvgTheme.Red)], "NEWS2"),
        ];
        Assert.All(svgs, s => { Assert.StartsWith("<svg", s); Assert.EndsWith("</svg>", s); Assert.Contains("var(--", s); });
        Assert.DoesNotContain("var(--", SvgTheme.Resolve(svgs[0]));
        Assert.Contains("Frek. napas", svgs[1]);
    }
}

public class AspNetCoreTests
{
    private static async System.Threading.Tasks.Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Services.AddMedical(m => m.AddMedicalApi());
        var app = b.Build();
        app.MapMedicalFhir();
        app.MapMedicalApi();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async System.Threading.Tasks.Task FhirRestRoundTrip()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;
        var meta = await http.GetStringAsync("/fhir/metadata");
        Assert.Contains("CapabilityStatement", meta);
        var mapper = new Fhir.FhirMapper();
        var rec = SyntheticData.Record(2);
        var tx = await http.PostAsync("/fhir", new StringContent(Fhir.FhirJson.Serialize(mapper.ToTransaction(rec)), Encoding.UTF8, "application/fhir+json"));
        Assert.Equal(HttpStatusCode.OK, tx.StatusCode);
        var p = await http.GetAsync($"/fhir/Patient/{rec.Patient.Id}");
        Assert.Equal("application/fhir+json; charset=utf-8", p.Content.Headers.ContentType!.ToString());
        var search = Fhir.FhirJson.Parse<global::Hl7.Fhir.Model.Bundle>(await http.GetStringAsync($"/fhir/Observation?patient={rec.Patient.Id}&category=laboratory"));
        Assert.Equal(rec.Observations.Count, search.Total);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/fhir/Patient/nope")).StatusCode);
        var bad = await http.PostAsync("/fhir/Patient", new StringContent("{\"resourceType\":\"Patient\",\"gender\":\"robot\"}", Encoding.UTF8, "application/fhir+json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("OperationOutcome", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task ClinicalApi()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;
        var news = await http.PostAsJsonAsync("/api/scores/news2", new VitalSigns { RespiratoryRate = 24, SpO2 = 93, OnSupplementalOxygen = true, SystolicBp = 105, HeartRate = 115, TemperatureC = 38.5 });
        Assert.Contains("\"score\":10", await news.Content.ReadAsStringAsync());
        Assert.Contains("I10", await http.GetStringAsync("/api/terminology/search?q=hipertensi"));
        Assert.Contains("92", await http.GetStringAsync("/api/calc/egfr?creatinine=1&age=50&sex=Male"));
        var hl7 = await http.PostAsync("/api/hl7/to-fhir", new StringContent("MSH|^~\\&|A|B|C|D|20260101||ADT^A01^ADT_A01|1|P|2.5.1\rPID|1||M1^^^X^MR||Doe^Jane||19800101|F\r"));
        Assert.Contains("\"transaction\"", await hl7.Content.ReadAsStringAsync());
        var deid = await http.PostAsync("/api/privacy/deidentify", new StringContent("Ny. Siti Rahma, HP 081234567890"));
        Assert.DoesNotContain("Siti", await deid.Content.ReadAsStringAsync());
        Assert.Contains("Sepsis.Screen", await http.GetStringAsync("/api/cds/synthetic/3?deterioration=0.9"));
    }
}
