using Medical.AI.Assistant;
using Medical.AI.Guidelines;
using Medical.AI.Learning;
using Medical.AI.Nlp;
using Medical.Clinical;
using Medical.Core;
using Medical.HuggingFace;
using Medical.Rules;
using Xunit.Abstractions;

namespace Medical.Tests;

public class DrugSourceTests
{
    [Fact]
    public async Task BuiltInAndLabelSourcesCombine()
    {
        var all = CompositeInteractionSource.Default();
        var simva = await all.FindAsync("simvastatin", "clarithromycin");
        Assert.Equal(InteractionSeverity.Contraindicated, simva[0].Severity);
        Assert.Contains(simva, f => f.Source == "FDA label contraindications" && f.Reference!.Contains("§4"));
        Assert.Equal(InteractionSeverity.Major, await all.WorstAsync("warfarin", "metronidazole"));
        Assert.Null(await all.WorstAsync("paracetamol", "amoxicillin"));
        Assert.Equal(InteractionSeverity.Contraindicated, await all.WorstAsync("Sildenafil", "Isosorbide Mononitrate"));   // case-insensitive

        var list = await all.CheckAsync(["tizanidine", "ciprofloxacin", "warfarin", "paracetamol"]);
        Assert.Equal(InteractionSeverity.Contraindicated, list[0].Severity);
        Assert.Contains(list, f => f.DrugA == "ciprofloxacin" && f.DrugB == "warfarin" || f.DrugA == "warfarin" && f.DrugB == "ciprofloxacin");
    }

    [Fact]
    public async Task CsvAndDrugBankAdaptersAndFailingSources()
    {
        var csv = CsvInteractionSource.Parse(new StringReader("""
            drug_a,drug_b,severity,effect_en,effect_id,reference
            "lithium",ibuprofen|naproxen,Major,"NSAIDs raise lithium levels, risk of toxicity",NSAID menaikkan kadar litium,Formulary 2026
            amiodarone,simvastatin,moderate,Limit simvastatin to 20 mg,,
            """), "Hospital formulary");
        Assert.Equal(2, csv.Count);
        var li = await csv.FindAsync("Naproxen", "lithium");
        Assert.Equal(InteractionSeverity.Major, li[0].Severity);
        Assert.Equal("NSAIDs raise lithium levels, risk of toxicity", li[0].Effect.En);
        Assert.Equal("NSAID menaikkan kadar litium", li[0].Effect.Id);
        Assert.Throws<InvalidDataException>(() => CsvInteractionSource.Parse(new StringReader("drug_a,drug_b,severity,effect_en\na,b,whatever,x"), "bad"));

        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <drugbank xmlns="http://www.drugbank.ca" version="5.1">
              <drug type="small molecule"><drugbank-id primary="true">DB00682</drugbank-id><name>Warfarin</name>
                <salts><salt><name>Warfarin sodium</name></salt></salts>
                <drug-interactions>
                  <drug-interaction><drugbank-id>DB00916</drugbank-id><name>Metronidazole</name><description>The risk or severity of bleeding and hemorrhage can be increased when Metronidazole is combined with Warfarin.</description></drug-interaction>
                  <drug-interaction><drugbank-id>DB00316</drugbank-id><name>Acetaminophen</name><description>Acetaminophen may increase the anticoagulant activities of Warfarin.</description></drug-interaction>
                </drug-interactions>
              </drug>
            </drugbank>
            """;
        var db = DrugBankInteractionSource.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));
        Assert.Equal(2, db.Count);
        Assert.Equal(InteractionSeverity.Major, (await db.FindAsync("metronidazole", "warfarin"))[0].Severity);
        Assert.Equal(InteractionSeverity.Moderate, (await db.FindAsync("warfarin", "acetaminophen"))[0].Severity);
        Assert.Empty(await db.FindAsync("warfarin sodium", "metronidazole"));   // salt names are not drug names

        var composite = new CompositeInteractionSource(new Failing(), csv, LabelContraindications.Source);
        var f = await composite.FindAsync("simvastatin", "amiodarone");
        Assert.Single(f);
        Assert.Contains("offline", composite.Errors.Single());
    }

    private sealed class Failing : IDrugInteractionSource
    {
        public string Name => "Remote DB";
        public ValueTask<IReadOnlyList<InteractionFinding>> FindAsync(string a, string b, CancellationToken ct = default) => throw new HttpRequestException("offline");
    }
}

public class OrganDosingTests
{
    private static OrganDoseAdvice One(string drug, double? crcl = null, double? egfr = null, string? regimen = null, bool dialysis = false) =>
        OrganDosing.Default.Renal(drug, crcl, egfr, dialysis, regimen).Single();

    [Fact]
    public void RenalBandsFollowTheLabels()
    {
        Assert.Equal(DoseAction.Contraindicated, One("metformin", egfr: 25).Action);
        Assert.Equal(DoseAction.Reduce, One("metformin", egfr: 40).Action);
        Assert.Equal(DoseAction.NoChange, One("metformin", egfr: 45).Action);
        Assert.Contains("15 mg", One("rivaroxaban", 50, regimen: "AF stroke prevention").Advice.En);   // 15–50 inclusive
        Assert.Contains("20 mg", One("rivaroxaban", 51, regimen: "AF stroke prevention").Advice.En);
        Assert.Equal(DoseAction.Avoid, One("rivaroxaban", 10, regimen: "AF stroke prevention").Action);
        Assert.Equal(2, OrganDosing.Default.Renal("rivaroxaban", 40).Count);   // both regimens without a filter
        Assert.Contains("75 mg", One("dabigatran", 30).Advice.En);
        Assert.Contains("150 mg", One("dabigatran", 31).Advice.En);
        Assert.Contains("200–700", One("gabapentin", 20).Advice.En);
        Assert.Contains("supplemental", One("gabapentin", 5, dialysis: true).Advice.En);
        Assert.Contains("every 48 h", One("levofloxacin", 30).Advice.En);
        Assert.Contains("18 h", One("ciprofloxacin", 20).Advice.En);
        Assert.Contains("needed", One("metformin", crcl: 50).Advice.En);   // metformin uses eGFR

        // Integration with the Cockcroft-Gault calculator.
        double crcl = Calculators.CockcroftGault(2.0, 80, 60, Gender.Female);
        Assert.Equal(DoseAction.Reduce, One("enoxaparin", crcl, regimen: "VTE prophylaxis").Action);
    }

    [Fact]
    public void ApixabanRuleAndHepaticAdvice()
    {
        Assert.Equal(DoseAction.Reduce, OrganDosing.ApixabanAf(82, 58, 1.0).Action);
        Assert.Equal(DoseAction.NoChange, OrganDosing.ApixabanAf(82, 70, 1.0).Action);
        Assert.Equal(DoseAction.Reduce, OrganDosing.ApixabanAf(70, 55, 1.6).Action);
        Assert.Equal(DoseAction.Avoid, OrganDosing.Default.Hepatic("rivaroxaban", "B")!.Action);
        Assert.Equal(DoseAction.NoChange, OrganDosing.Default.Hepatic("apixaban", "a")!.Action);
        string cls = ClinicalScores.ChildPughClass(ClinicalScores.ChildPugh(4, 2.5, 2.5, 2, 1).Score);
        Assert.Equal(DoseAction.Avoid, OrganDosing.Default.Hepatic("apixaban", cls)!.Action);
        Assert.Null(OrganDosing.Default.Hepatic("unknown-drug", "A"));
        Assert.Throws<ArgumentException>(() => new OrganDosing().AddRenal(new RenalDosingTable("x", "r", RenalMeasure.CrCl,
            [new(0, 30, DoseAction.Reduce, new("a", "a")), new(40, 100, DoseAction.NoChange, new("b", "b"))], "test")));
    }
}

public class ClinicalNlpTests(ITestOutputHelper output)
{
    [Fact]
    public void IndonesianNoteWithNegationAndFamilyHistory()
    {
        var rec = new TerminologyEntityRecognizer();
        const string note = "Pasien dengan demam berdarah dan darah tinggi, tidak ada sesak napas. Riwayat keluarga diabetes.";
        var ents = rec.Recognize(note);
        foreach (var e in ents) output.WriteLine(e.ToString());
        var dbd = ents.Single(e => e.Text == "demam berdarah");
        Assert.Equal("A91", dbd.Codes[0].Code);
        Assert.False(dbd.Negated);
        Assert.Equal("I10", ents.Single(e => e.Text == "darah tinggi").Codes[0].Code);
        Assert.True(ents.Single(e => e.Text == "sesak napas").Negated);
        Assert.True(ents.Single(e => e.Text == "diabetes").FamilyHistory);

        var coder = new Icd10Coder(rec);
        var codes = coder.Suggest(note).Select(s => s.Code).ToList();
        Assert.Equal(["A91", "I10"], codes.Order().ToList());
    }

    [Fact]
    public void EnglishNoteRanksDiagnosesAboveSymptoms()
    {
        const string note = "Patient denies chest pain but reports cough. Known COPD and atrial fibrillation on warfarin.";
        var coder = new Icd10Coder();
        var s = coder.Suggest(note);
        foreach (var x in s) output.WriteLine(x.ToString());
        Assert.DoesNotContain(s, x => x.Code == "R07.4");                      // negated
        var cough = s.Single(x => x.Code == "R05");                            // "but" ends the negation scope
        Assert.Contains("Symptom code", cough.Note.En);
        Assert.True(s.First(x => x.Code == "J44.9").Score > cough.Score);
        Assert.Contains(s, x => x.Code == "I48");
        Assert.Contains(coder.LastEntities, e => e.Type == ClinicalEntityType.Drug && e.Text == "warfarin");
    }

    [Fact]
    public void CompositeMergesOverlappingSpans()
    {
        var a = new Fixed("a", new ClinicalEntity(ClinicalEntityType.Problem, "pneumonia", 12, 21, 0.9, [], "a") { Label = "Disease_disorder" });
        var composite = new CompositeEntityRecognizer(new TerminologyEntityRecognizer(), a);
        var e = composite.Recognize("Patient has pneumonia.").Single();
        Assert.Equal("J18.9", e.Codes[0].Code);
        Assert.Equal("Disease_disorder", e.Label);
        Assert.Equal(ClinicalEntityType.Symptom, HuggingFaceEntityRecognizer.MapLabel("Sign_symptom"));
        Assert.Null(HuggingFaceEntityRecognizer.MapLabel("Age"));
    }

    private sealed class Fixed(string name, params ClinicalEntity[] entities) : IEntityRecognizer
    {
        public string Name => name;
        public IReadOnlyList<ClinicalEntity> Recognize(string text) => entities;
    }

    [Fact]
    public void HuggingFaceModelsWhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("MEDNET_HF_TESTS") != "1") { output.WriteLine("Set MEDNET_HF_TESTS=1 to download and run the Hugging Face models."); return; }
        using var ner = HuggingFaceEntityRecognizer.Load();
        var ents = ner.Recognize("A 64-year-old man with fever and productive cough was diagnosed with pneumonia and started on amoxicillin.");
        foreach (var e in ents) output.WriteLine(e.ToString());
        Assert.Contains(ents, e => e.Type is ClinicalEntityType.Symptom && e.Text.Contains("fever"));
        Assert.Contains(ents, e => e.Type is ClinicalEntityType.Problem && e.Text.Contains("pneumonia"));
        Assert.Contains(ents, e => e.Type is ClinicalEntityType.Drug && e.Text.Contains("amoxicillin"));

        using var emb = HuggingFaceEmbedder.Load();
        var retriever = new GuidelineRetriever(GuidelineCorpus.Sample(), emb);
        Assert.True(retriever.HasDense);
        var hit = retriever.Search("Berapa lama pengobatan tuberkulosis paru?", 3);
        output.WriteLine(string.Join(" | ", hit.Select(h => $"{h.Chunk.Source} lex {h.LexicalRank} dense {h.DenseRank}")));
        Assert.Equal("WHO-TB-2022", hit[0].Chunk.Source);
    }
}

public class GuidelineRagTests
{
    private static readonly GuidelineRetriever Retriever = new(GuidelineCorpus.Sample());

    [Theory]
    [InlineData("How long is treatment for drug-susceptible pulmonary TB in adults?", "WHO-TB-2022")]
    [InlineData("Berapa lama pengobatan TB paru?", "WHO-TB-2022")]
    [InlineData("Obat lini pertama diabetes melitus tipe 2", "KMK-PNPK-DM")]
    [InlineData("What fluid rate for dengue with warning signs?", "WHO-DENGUE-2009")]
    [InlineData("first-line vasopressor in septic shock", "WHO-SEPSIS-SSC-2021")]
    [InlineData("pengobatan malaria vivaks primakuin", "KMK-PNPK-MALARIA-2020")]
    public void RetrievesTheRightGuideline(string question, string source) => Assert.Equal(source, Retriever.Search(question, 3)[0].Chunk.Source);

    [Fact]
    public async Task ExtractiveAnswerHasValidCitations()
    {
        var a = await new GuidelineAnswerer(Retriever).AskAsync("How long is treatment for drug-susceptible pulmonary TB?");
        Assert.Equal("extractive", a.Mode);
        Assert.Contains("6 months", a.Text);
        Assert.Contains("[1]", a.Text);
        Assert.All(a.Citations, c => Assert.Contains($"[{c.Number}]", a.Text));
    }

    [Fact]
    public async Task LlmCitationsAreValidated()
    {
        var good = await new GuidelineAnswerer(Retriever, new FakeLlm("Treat for 6 months: 2HRZE then 4HR [1]. A 4-month regimen exists for age 12 and over [1][9].")).AskAsync("TB treatment duration?");
        Assert.StartsWith("llm:", good.Mode);
        Assert.DoesNotContain("[9]", good.Text);
        Assert.Single(good.Citations);
        Assert.Contains(good.Warnings, w => w.Contains("Removed 1"));

        var uncited = await new GuidelineAnswerer(Retriever, new FakeLlm("Six months.")).AskAsync("TB treatment duration?");
        Assert.Equal("extractive", uncited.Mode);
        Assert.Contains(uncited.Warnings, w => w.Contains("no valid citation"));

        var (text, used, invalid) = GuidelineAnswerer.Validate("A [1, 2]. B [5]. C [2-3].", 3);
        Assert.Equal("A [1, 2]. B. C [2, 3].", text);
        Assert.Equal([1, 2, 3], used.Order().ToArray());
        Assert.Equal(1, invalid);
    }

    [Fact]
    public void LoadsAFolderCorpusWithHeaders()
    {
        var dir = Directory.CreateTempSubdirectory("mednet-guidelines").FullName;
        File.WriteAllText(Path.Combine(dir, "asma.md"), "title: Pedoman Asma RS\nsource: RS-ASMA-2026\nlanguage: id\n\n# Serangan akut\nBerikan salbutamol inhalasi 2,5–5 mg tiap 20 menit pada jam pertama.\n# Pulang\nEdukasi teknik inhaler.");
        var corpus = GuidelineCorpus.LoadDirectory(dir);
        Assert.Equal(2, corpus.Chunks.Count);
        Assert.Equal("Pedoman Asma RS — Serangan akut", corpus.Chunks[0].Citation);
        Assert.Equal("RS-ASMA-2026", new GuidelineRetriever(corpus).Search("salbutamol serangan asma")[0].Chunk.Source);
    }

    [Fact]
    public async Task ClaudeAnswersWithValidCitationsWhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("MEDNET_LLM_TESTS") != "1" || Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is not { Length: > 0 } key) return;
        var llm = new AnthropicClinicalLlm(key, workspaceId: Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID"));
        var a = await new GuidelineAnswerer(Retriever, llm).AskAsync("Berapa lama pengobatan TB paru sensitif obat pada dewasa?", "id");
        Assert.StartsWith("llm:", a.Mode);
        Assert.Contains(a.Citations, c => c.Chunk.Source == "WHO-TB-2022");
        Assert.Matches(@"\[\d\]", a.Text);
        Assert.Contains("6", a.Text);
    }

    private sealed class FakeLlm(string answer) : IClinicalLlm
    {
        public string Provider => "fake";
        public string Model => "test";
        public Task<AssistantReply> RunAsync(string system, IReadOnlyList<ChatMessage> history, ClinicalTools tools, PatientRecord? patient, CancellationToken ct = default) =>
            Task.FromResult(new AssistantReply(answer, [], Provider, Model));
    }
}

public class LearningTests
{
    private static readonly string[] Names = ["lactate", "age", "sbp"];

    private static List<(double[] X, bool Y)> Cohort(int n, int seed, double shift)
    {
        var r = new Random(seed);
        double G() => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
        var rows = new List<(double[], bool)>();
        for (int i = 0; i < n; i++)
        {
            double lac = 2 + shift + G(), age = 55 + 10 * shift + 15 * G(), sbp = 120 - 10 * shift + 18 * G();
            double z = -1 + 1.3 * (lac - 2) + 0.04 * (age - 55) - 0.03 * (sbp - 120);
            rows.Add((new[] { lac, age, sbp }, r.NextDouble() < 1 / (1 + Math.Exp(-z))));
        }
        return rows;
    }

    private static readonly FederatedSite[] Sites =
    [
        new("RS Jakarta", Cohort(900, 1, 0.3)), new("RS Surabaya", Cohort(500, 2, -0.2)), new("Puskesmas Bandung", Cohort(250, 3, 0.0)),
    ];

    [Fact]
    public void FederatedMatchesCentralisedAndMasksCancel()
    {
        var test = Cohort(2000, 99, 0.1);
        var pooled = FederatedLogisticRegression.TrainCentralised(Names, Sites.SelectMany(s => Cohort(0, 0, 0)).Concat(Cohort(900, 1, 0.3)).Concat(Cohort(500, 2, -0.2)).Concat(Cohort(250, 3, 0.0)).ToList());
        var fed = FederatedLogisticRegression.Train(Names, Sites, new FederatedOptions { Rounds = 30, LocalEpochs = 5 });
        var plain = FederatedLogisticRegression.Train(Names, Sites, new FederatedOptions { Rounds = 30, LocalEpochs = 5, SecureAggregation = false });
        double aucPooled = pooled.Evaluate(test).Auc, aucFed = fed.Model.Evaluate(test).Auc;
        Assert.True(aucFed > 0.75, $"federated AUC {aucFed}");
        Assert.InRange(aucFed, aucPooled - 0.01, aucPooled + 0.01);
        for (int j = 0; j < Names.Length; j++) Assert.Equal(plain.Model.Weights[j], fed.Model.Weights[j], 8);   // masks cancel exactly enough
        Assert.Equal(double.PositiveInfinity, fed.Epsilon());
        Assert.True(fed.History[^1].MeanLoss < fed.History[0].MeanLoss);
    }

    [Fact]
    public void DifferentialPrivacyTradesAccuracyForEpsilon()
    {
        var test = Cohort(2000, 99, 0.1);
        var low = FederatedLogisticRegression.Train(Names, Sites, new FederatedOptions { ClipNorm = 0.5, NoiseMultiplier = 0.5 });
        var high = FederatedLogisticRegression.Train(Names, Sites, new FederatedOptions { ClipNorm = 0.5, NoiseMultiplier = 2.0 });
        Assert.True(high.Epsilon() < low.Epsilon());
        Assert.True(double.IsFinite(high.Epsilon()));
        Assert.True(high.Model.Evaluate(test).Auc > 0.65);
        Assert.Contains(low.History, h => h.ClippedSites > 0);
    }

    [Fact]
    public void ModelCardReportsSubgroups()
    {
        var model = FederatedLogisticRegression.Train(Names, Sites).Model;
        var rows = Sites.SelectMany(s => s.Name == "RS Jakarta" ? Cohort(400, 7, 0.3).Select(r => (s.Name, r)) : Cohort(200, 8, -0.2).Select(r => (s.Name, r))).ToList();
        var p = rows.Select(r => model.Predict(r.r.X)).ToList();
        var y = rows.Select(r => r.r.Y).ToList();
        var card = ModelCard.Create("Sepsis deterioration (federated)", "Flag adult inpatients at risk of deterioration within 48 h for clinician review.", p, y,
            new Dictionary<string, IReadOnlyList<string>> { ["site"] = rows.Select(r => r.Name).ToList(), ["age"] = rows.Select(r => r.r.X[1] >= 65 ? "≥ 65" : "< 65").ToList() })
            with { Limitations = ["Synthetic cohorts."], Features = Names };
        Assert.Equal(5, card.Subgroups.Count);
        Assert.True(card.AucGaps.ContainsKey("site"));
        var md = card.ToMarkdown();
        Assert.Contains("| **Overall** |", md);
        Assert.Contains("site = Puskesmas Bandung", md);
        Assert.Contains("\"intendedUse\"", card.ToJson());
    }

    [Fact]
    public void DriftMonitorFlagsShiftAndCalibrationLoss()
    {
        var model = FederatedLogisticRegression.Train(Names, Sites).Model;
        var reference = Cohort(1500, 21, 0);
        var monitor = new DriftMonitor(Names, reference.Select(r => r.X).ToList(), reference.Select(r => model.Predict(r.X)).ToList(), reference.Select(r => r.Y).ToList());

        var same = Cohort(1500, 22, 0);
        var ok = monitor.Check(same.Select(r => r.X).ToList(), same.Select(r => model.Predict(r.X)).ToList(), same.Select(r => r.Y).ToList());
        Assert.Equal(DriftLevel.Stable, ok.Worst);
        Assert.Empty(ok.Alerts);
        Assert.True(ok.Features[0].KsPValue > 0.01);

        var shifted = Cohort(1500, 23, 1.0);
        var bad = monitor.Check(shifted.Select(r => r.X).ToList(), shifted.Select(r => model.Predict(r.X)).ToList());
        Assert.Equal(DriftLevel.Major, bad.Features.Single(f => f.Feature == "lactate").Level);
        Assert.True(bad.Features[0].KsPValue < 1e-6);
        Assert.NotEqual(DriftLevel.Stable, bad.Prediction!.Level);

        // Labels from a different mechanism: calibration and discrimination alerts.
        var r = new Random(5);
        var flipped = same.Select(x => r.NextDouble() < 0.5).ToList();
        var labelled = monitor.Check(same.Select(x => x.X).ToList(), same.Select(x => model.Predict(x.X)).ToList(), flipped);
        Assert.Contains(labelled.Alerts, a => a.Contains("AUC fell"));
    }

    [Fact]
    public void EvaluationMetricsAreCorrect()
    {
        Assert.Equal(1.0, ModelEvaluation.Auc([0.1, 0.4, 0.35, 0.8], [false, false, true, true]) + 0.25);   // 3 of 4 pairs ordered
        Assert.Equal(0.5, ModelEvaluation.Auc([0.5, 0.5], [false, true]));
        var m = ModelEvaluation.Evaluate([0.9, 0.8, 0.2, 0.1], [true, true, false, false]);
        Assert.Equal(1.0, m.Auc);
        Assert.Equal(1.0, m.Sensitivity);
        Assert.Equal(0.025, m.Brier, 6);
    }
}
