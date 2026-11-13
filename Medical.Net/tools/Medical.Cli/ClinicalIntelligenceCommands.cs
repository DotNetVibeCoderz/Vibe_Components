using Medical.AI;
using Medical.AI.Guidelines;
using Medical.AI.Learning;
using Medical.AI.Nlp;
using Medical.Clinical;
using Medical.Core;
using Medical.HuggingFace;
using Medical.Rules;

namespace Medical.Cli;

/// <summary>v1.3 commands: critical-care scores, CV risk, WHO growth, organ dosing, drug sources, NLP, guidelines, federated learning, drift.</summary>
internal static class ClinicalIntelligenceCommands
{
    public static int Score(Args a, string lang)
    {
        var kind = a.Positional(0, "score").ToLowerInvariant();
        ScoreResult s = kind switch
        {
            "sofa" => ClinicalScores.Sofa(new SofaInputs
            {
                PaO2FiO2 = a.OptNum("pf"), RespiratorySupport = a.Flag("ventilated"), Platelets = a.OptNum("plt"), BilirubinMgDl = a.OptNum("bili"),
                MeanArterialPressure = a.OptNum("map"), Gcs = a.OptNum("gcs") is { } g ? (int)g : null, CreatinineMgDl = a.OptNum("cr"), UrineOutputMlPerDay = a.OptNum("urine"),
                Vasopressors = new Vasopressors(a.OptNum("dopamine") ?? 0, a.OptNum("dobutamine") ?? 0, a.OptNum("epinephrine") ?? 0, a.OptNum("norepinephrine") ?? 0),
            }),
            "apache2" or "apache" => ClinicalScores.ApacheII(new ApacheIIInputs
            {
                TemperatureC = a.Num("temp"), MeanArterialPressure = a.Num("map"), HeartRate = a.Num("hr"), RespiratoryRate = a.Num("rr"), FiO2 = a.OptNum("fio2") ?? 0.21,
                PaO2 = a.OptNum("pao2"), AaGradient = a.OptNum("aa"), ArterialPh = a.Num("ph"), Sodium = a.Num("na"), Potassium = a.Num("k"), CreatinineMgDl = a.Num("cr"),
                AcuteRenalFailure = a.Flag("arf"), Hematocrit = a.Num("hct"), Wbc = a.Num("wbc"), Gcs = (int)a.Num("gcs"), Age = (int)a.Num("age"), ChronicHealth = a.Flag("chronic"),
                Admission = (a.Opt("admission") ?? "nonop") switch { "emergency" => ApacheAdmission.EmergencyPostoperative, "elective" => ApacheAdmission.ElectivePostoperative, _ => ApacheAdmission.NonOperative },
            }, a.OptNum("weight")),
            "pews" => ClinicalScores.Pews(new PewsInputs((int)a.Num("behaviour"), (int)a.Num("cardio"), (int)a.Num("resp"), a.Flag("nebs"))),
            "meows" => ClinicalScores.Meows(a.Num("rr"), a.Num("spo2"), a.Num("temp"), a.Num("sbp"), a.Num("dbp"), a.Num("hr"),
                (a.Opt("avpu") ?? "A").ToUpperInvariant() switch { "V" => Avpu.Voice, "P" => Avpu.Pain, "U" => Avpu.Unresponsive, _ => Avpu.Alert }),
            "wells-pe" => ClinicalScores.WellsPe(a.Flag("dvt-signs"), a.Flag("pe-likely"), a.OptNum("hr") ?? 80, a.Flag("immobile"), a.Flag("previous"), a.Flag("haemoptysis"), a.Flag("cancer")),
            "wells-dvt" => ClinicalScores.WellsDvt(a.Flag("cancer"), a.Flag("paralysis"), a.Flag("bedridden"), a.Flag("tender"), a.Flag("leg-swollen"), a.Flag("calf"), a.Flag("pitting"), a.Flag("collateral"), a.Flag("previous"), a.Flag("alternative")),
            "perc" => ClinicalScores.Perc((int)a.Num("age"), a.Num("hr"), a.Num("spo2"), a.Flag("leg-swelling"), a.Flag("haemoptysis"), a.Flag("surgery"), a.Flag("previous"), a.Flag("hormone")),
            "child-pugh" => ClinicalScores.ChildPugh(a.Num("bili"), a.Num("albumin"), a.Num("inr"), (int)(a.OptNum("ascites") ?? 0), (int)(a.OptNum("enceph") ?? 0)),
            _ => throw new ArgumentException("score: sofa | apache2 | pews | meows | wells-pe | wells-dvt | perc | child-pugh"),
        };
        if (a.Flag("json")) { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(s)); return 0; }
        Console.WriteLine($"{s.Name}: {(s.ExactScore is { } e ? e.ToString("0.#") : s.Score.ToString())}/{(s.ExactScore is null ? s.MaxScore : s.MaxScore / 2.0)}  [{s.Risk}]");
        foreach (var c in s.Components) Console.WriteLine($"  {c.Parameter,-40} {c.Value,-28} {(c.ExactPoints ?? c.Points):+0.#;-0.#;0}");
        Console.WriteLine(s.Interpretation[lang]);
        return 0;
    }

    public static int CvRisk(Args a, string lang)
    {
        var x = new CvRiskInputs((int)a.Num("age"), a.Sex(), a.Num("tc"), a.Num("hdl"), a.Num("sbp"), a.Flag("treated"), a.Flag("smoker"), a.Flag("diabetes"));
        foreach (var r in new[] { CardiovascularRisk.Ascvd(x, a.Opt("race") is "aa" or "black" ? PceRace.AfricanAmerican : PceRace.WhiteOrOther), CardiovascularRisk.Framingham(x) })
        {
            Console.WriteLine($"{r.Equation}: {r.TenYearRiskPercent:0.0} % — {r.Category[lang]}");
            foreach (var w in r.Warnings) Console.WriteLine($"  ! {w}");
        }
        return 0;
    }

    public static async Task<int> Growth(Args a, string lang)
    {
        string cache = a.Opt("cache") ?? Path.Combine(Path.GetTempPath(), "mednet-who-growth");
        var who = await WhoGrowthStandards.DownloadAsync(cache);
        double days = a.OptNum("age-days") ?? (a.OptNum("age-months") is { } m ? m * 30.4375 : a.Num("age-years") * 365.25);
        var sex = a.Sex();
        LengthMeasure? pos = a.Flag("standing") ? LengthMeasure.Standing : a.Flag("recumbent") ? LengthMeasure.Recumbent : null;
        double? w = a.OptNum("weight"), len = a.OptNum("length") ?? a.OptNum("height"), hc = a.OptNum("hc");
        var results = new List<GrowthResult>();
        void Try(Func<GrowthResult> f) { try { results.Add(f()); } catch (ArgumentOutOfRangeException) { } }
        if (len is { } l) Try(() => who.Assess(GrowthIndicator.LengthHeightForAge, sex, days, l, l, pos));
        if (w is { } wt) Try(() => who.Assess(GrowthIndicator.WeightForAge, sex, days, wt));
        if (w is { } w2 && len is { } l2)
        {
            Try(() => who.Assess(GrowthIndicator.WeightForLengthHeight, sex, days, w2, l2, pos));
            Try(() => who.Assess(GrowthIndicator.BmiForAge, sex, days, w2, l2, pos));
        }
        if (hc is { } h) Try(() => who.Assess(GrowthIndicator.HeadCircumferenceForAge, sex, days, h));
        if (results.Count == 0) throw new ArgumentException("Give --weight, --length/--height and/or --hc for an age within 0–19 years.");
        foreach (var r in results)
            Console.WriteLine($"{r.Indicator,-26} {r.Value,7:0.##}  z {r.ZScore,6:+0.00;-0.00}  P{r.Percentile,-5:0.#} {r.Classification[lang]}{(r.Flagged ? "  [implausible — re-measure]" : "")}");
        Console.WriteLine(lang == "id" ? $"Rujukan: {results[0].Reference} (tabel WHO diunduh ke {cache})" : $"Reference: {results[0].Reference} (WHO tables cached in {cache})");
        return results.Any(r => Math.Abs(r.ZScore) >= 2) ? 3 : 0;
    }

    public static int Renal(Args a, string lang)
    {
        string drug = a.Positional(0, "drug");
        if (drug.Equals("apixaban", StringComparison.OrdinalIgnoreCase) && a.Opt("age") is not null)
        {
            var ap = OrganDosing.ApixabanAf((int)a.Num("age"), a.Num("weight"), a.Num("cr"));
            Console.WriteLine($"apixaban (AF): {ap.Advice[lang]}  [{ap.Basis}; {ap.Source}]");
        }
        double? crcl = a.OptNum("crcl");
        if (crcl is null && a.Opt("cr") is not null && a.Opt("age") is not null && a.Opt("weight") is not null)
            crcl = Calculators.CockcroftGault(a.Num("cr"), (int)a.Num("age"), a.Num("weight"), a.Sex());
        var advice = OrganDosing.Default.Renal(drug, crcl, a.OptNum("egfr"), a.Flag("dialysis"), a.Opt("regimen"));
        foreach (var r in advice) Console.WriteLine($"{r.Generic} — {r.Regimen}: [{r.Action}] {r.Advice[lang]}  ({r.Basis}; {r.Source})");
        if (a.Opt("child-pugh") is { } cls && OrganDosing.Default.Hepatic(drug, cls) is { } h)
            Console.WriteLine($"{h.Generic} — hepatic: [{h.Action}] {h.Advice[lang]}  ({h.Basis}; {h.Source})");
        if (advice.Count == 0 && a.Opt("child-pugh") is null)
            Console.WriteLine(lang == "id" ? $"Tidak ada tabel penyesuaian dosis untuk {drug}." : $"No organ-dosing table for {drug}.");
        return advice.Any(r => r.Action is DoseAction.Avoid or DoseAction.Contraindicated) ? 3 : 0;
    }

    /// <summary>Extra sources for the interaction check: --csv file, --drugbank file.xml (licensed), always with the label table.</summary>
    public static async Task<int> InteractionSources(Args a, string lang)
    {
        var sources = new List<IDrugInteractionSource> { new BuiltInInteractionSource(), LabelContraindications.Source };
        foreach (var csv in a.All("csv")) sources.Add(CsvInteractionSource.Load(csv));
        foreach (var xml in a.All("drugbank")) sources.Add(DrugBankInteractionSource.Load(xml, a.Positionals));
        var composite = new CompositeInteractionSource([.. sources]);
        var found = await composite.CheckAsync(a.Positionals);
        if (found.Count == 0) Console.WriteLine(lang == "id" ? "Tidak ada interaksi yang ditemukan." : "No interactions found.");
        foreach (var f in found) Console.WriteLine($"[{f.Severity}] {f.DrugA} + {f.DrugB}: {f.Effect[lang]}  — {f.Source}{(f.Reference is null ? "" : $", {f.Reference}")}");
        foreach (var e in composite.Errors) Console.WriteLine($"  ! {e}");
        return found.Count == 0 ? 0 : 3;
    }

    public static int Nlp(Args a, string lang)
    {
        string text = a.Opt("file") is { } f ? File.ReadAllText(f) : string.Join(' ', a.Positionals);
        if (text.Length == 0) throw new ArgumentException("nlp \"<clinical text>\" | --file note.txt");
        IEntityRecognizer rec = new TerminologyEntityRecognizer();
        HuggingFaceEntityRecognizer? hf = null;
        if (a.Flag("hf") || a.Opt("hf") is not null)
        {
            Console.WriteLine($"Loading {a.Opt("hf") ?? HuggingFaceEntityRecognizer.DefaultModel} …");
            hf = HuggingFaceEntityRecognizer.Load(a.Opt("hf") ?? HuggingFaceEntityRecognizer.DefaultModel);
            rec = new CompositeEntityRecognizer(rec, hf);
        }
        using var _ = hf;
        var coder = new Icd10Coder(rec);
        var codes = coder.Suggest(text);
        Console.WriteLine(lang == "id" ? "Entitas:" : "Entities:");
        foreach (var e in coder.LastEntities) Console.WriteLine($"  {e}");
        Console.WriteLine(lang == "id" ? "Saran kode ICD-10 (konfirmasi oleh koder):" : "ICD-10 suggestions (for a coder to confirm):");
        foreach (var s in codes) Console.WriteLine($"  {s.Code,-7} {s.DisplayFor(lang)}  ({s.Score:0.00}) ← {string.Join("; ", s.Evidence)} — {s.Note[lang]}");
        return 0;
    }

    public static async Task<int> Guideline(Args a, string lang)
    {
        string q = string.Join(' ', a.Positionals.Skip(a.Positionals.Count > 0 && a.Positionals[0] == "ask" ? 1 : 0));
        if (q.Length == 0) throw new ArgumentException("guideline ask \"<question>\" [--corpus dir] [--hf-embed] [--k 4]");
        var corpus = a.Opt("corpus") is { } dir ? GuidelineCorpus.LoadDirectory(dir) : GuidelineCorpus.Sample();
        using var emb = a.Flag("hf-embed") ? HuggingFaceEmbedder.Load() : null;
        var retriever = new GuidelineRetriever(corpus, emb);
        var answer = await new GuidelineAnswerer(retriever, a.Flag("offline") ? null : Medical.AI.Assistant.LlmOptions.FromEnvironment().Create())
            .AskAsync(q, lang, (int)(a.OptNum("k") ?? 4));
        Console.WriteLine(answer.Text);
        Console.WriteLine();
        foreach (var c in answer.Citations) Console.WriteLine($"[{c.Number}] {c.Chunk.Citation} ({c.Chunk.Source}){(c.Chunk.Url is null ? "" : " " + c.Chunk.Url)}");
        Console.WriteLine($"({answer.Mode}{(retriever.HasDense ? ", hybrid BM25 + embeddings" : ", BM25")})");
        foreach (var w in answer.Warnings) Console.WriteLine($"  ! {w}");
        return answer.Grounded ? 0 : 3;
    }

    /// <summary>Federated training across three synthetic hospitals, with a model card and drift check.</summary>
    public static int Federated(Args a, string lang)
    {
        var (names, sites, test) = SyntheticFederation.Create();
        var opts = new FederatedOptions
        {
            Rounds = (int)(a.OptNum("rounds") ?? 30), SecureAggregation = !a.Flag("no-secure"),
            ClipNorm = a.OptNum("noise") is not null ? a.OptNum("clip") ?? 0.5 : a.OptNum("clip"), NoiseMultiplier = a.OptNum("noise") ?? 0,
        };
        var fed = FederatedLogisticRegression.Train(names, sites, opts);
        var pooled = FederatedLogisticRegression.TrainCentralised(names, sites.SelectMany(s => SyntheticFederation.Rows(s)).ToList());
        Console.WriteLine($"Sites: {string.Join(", ", sites.Select(s => $"{s.Name} ({s.Count})"))}; secure aggregation {(opts.SecureAggregation ? "on" : "off")}");
        Console.WriteLine($"Federated AUC {fed.Model.Evaluate(test).Auc:0.###} vs pooled {pooled.Evaluate(test).Auc:0.###}; loss {fed.History[0].MeanLoss:0.###} → {fed.History[^1].MeanLoss:0.###}");
        Console.WriteLine(double.IsInfinity(fed.Epsilon()) ? "No differential privacy (add --noise 1.0 [--clip 0.5])." : $"Site-level DP: ε = {fed.Epsilon():0.##} at δ = 1e-5 (σ {opts.NoiseMultiplier}, C {opts.ClipNorm})");
        var p = test.Select(r => fed.Model.Predict(r.X)).ToList();
        var card = ModelCard.Create("Deterioration risk (federated)", "Flag adult inpatients at risk of deterioration within 48 h for clinician review.", p, test.Select(r => r.Y).ToList(),
            new Dictionary<string, IReadOnlyList<string>> { ["age"] = test.Select(r => r.X[1] >= 65 ? "≥ 65" : "< 65").ToList() })
            with { Features = names, TrainingData = "Synthetic cohorts at three simulated sites (rows never pooled).", Limitations = ["Synthetic data — retrain and validate locally."] };
        if (a.Opt("card") is { } path) { File.WriteAllText(path, card.ToMarkdown()); Console.WriteLine($"Model card → {path}"); }
        else Console.WriteLine(card.ToMarkdown());
        return 0;
    }

    public static int Drift(Args a, string lang)
    {
        var (names, sites, test) = SyntheticFederation.Create();
        var model = FederatedLogisticRegression.Train(names, sites).Model;
        var monitor = new DriftMonitor(names, test.Select(r => r.X).ToList(), test.Select(r => model.Predict(r.X)).ToList(), test.Select(r => r.Y).ToList());
        var current = SyntheticFederation.Cohort(1500, 77, a.OptNum("shift") ?? 0.8);
        var rep = monitor.Check(current.Select(r => r.X).ToList(), current.Select(r => model.Predict(r.X)).ToList(), current.Select(r => r.Y).ToList());
        foreach (var f in rep.Features) Console.WriteLine($"  {f}");
        if (rep.Prediction is { } pd) Console.WriteLine($"  {pd}");
        Console.WriteLine($"AUC {rep.ReferencePerformance?.Auc:0.###} → {rep.CurrentPerformance?.Auc:0.###}; ECE {rep.ReferencePerformance?.Ece:0.###} → {rep.CurrentPerformance?.Ece:0.###}");
        foreach (var al in rep.Alerts) Console.WriteLine($"  ! {al}");
        return rep.Worst == DriftLevel.Stable ? 0 : 3;
    }
}
