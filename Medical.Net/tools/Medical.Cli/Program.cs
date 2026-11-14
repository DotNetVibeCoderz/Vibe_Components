using System.Globalization;
using System.Net;
using FellowOakDicom;
using Medical.AI;
using Medical.AI.Assistant;
using Medical.Clinical;
using Medical.Cli;
using Medical.Core;
using Medical.Dicom;
using Medical.Visualization;
using Medical.Emr;
using Medical.Fhir;
using Medical.Hl7;
using Medical.Hl7.Conformance;
using Medical.Bpjs.InaCbg;
using Medical.Fhir.SatuSehat;
using Medical.Fhir.Validation;
using Medical.Privacy;
using Medical.Rules;
using Medical.Signals;
using Medical.Terminology;
using Microsoft.Extensions.DependencyInjection;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var a = new Args(args);
string lang = a.Opt("lang") ?? "en";

try
{
    return a.Command switch
    {
        "info" or "" => Info(),
        "help" or "--help" or "-h" => Help(),
        "news2" => News2(),
        "qsofa" => Out(ClinicalScores.QSofa(a.Num("rr"), a.Num("sbp"), a.Flag("altered"))),
        "egfr" => Egfr(),
        "bmi" => Bmi(),
        "dose" => Dose(),
        "code" => await Code(),
        "interactions" when a.Opt("csv") is not null || a.Opt("drugbank") is not null || a.Flag("sources") => await ClinicalIntelligenceCommands.InteractionSources(a, lang),
        "interactions" => Interactions(),
        "score" => ClinicalIntelligenceCommands.Score(a, lang),
        "cvrisk" => ClinicalIntelligenceCommands.CvRisk(a, lang),
        "growth" => await ClinicalIntelligenceCommands.Growth(a, lang),
        "renal" or "organ-dose" => ClinicalIntelligenceCommands.Renal(a, lang),
        "nlp" => ClinicalIntelligenceCommands.Nlp(a, lang),
        "guideline" => await ClinicalIntelligenceCommands.Guideline(a, lang),
        "federated" => ClinicalIntelligenceCommands.Federated(a, lang),
        "drift" => ClinicalIntelligenceCommands.Drift(a, lang),
        "fhir" => Fhir(),
        "hl7" => await Hl7Cmd(),
        "dicom" => await DicomCmd(),
        "ecg" => Ecg(),
        "ecg12" => await Ecg12Cmd(),
        "deid" => Deid(),
        "cds" => Cds(),
        "risk" => Risk(),
        "ask" => await Ask(),
        "emr" => await Emr(),
        "bpjs" => Bpjs(),
        "satusehat" => SatuSehat(),
        "his" => await PlatformCommands.His(a, lang),
        "monitor" => await PlatformCommands.Monitor(a, lang),
        "sync" => await PlatformCommands.Sync(a, lang),
        "worklist" => await InterfaceCommands.Worklist(a, lang),
        _ => Fail($"Unknown command '{a.Command}'. Run 'medical help'."),
    };
}
catch (Exception e) when (e is ArgumentException or KeyNotFoundException or FileNotFoundException or InvalidOperationException or FormatException or NotSupportedException or IOException)
{
    return Fail(e.Message);
}

int Info()
{
    Console.WriteLine($"{MedicalInfo.Name} {MedicalInfo.Version} — {(lang == "id" ? MedicalInfo.TaglineId : MedicalInfo.Tagline)}");
    Console.WriteLine(MedicalInfo.CreditFor(lang));
    Console.WriteLine($"Rust kernels: {(Medical.Native.MedicalKernels.IsNativeAvailable ? "loaded" : "managed fallback")}");
    Console.WriteLine($"Terminology: {string.Join(", ", TerminologyService.Default.Systems.Select(s => $"{s.Name} ({s.Count})"))}");
    Console.WriteLine($"LLM: {LlmOptions.FromEnvironment().Provider}");
    Console.WriteLine();
    Console.WriteLine(MedicalInfo.DisclaimerFor(lang));
    return 0;
}

int Help()
{
    Console.WriteLine("""
        medical <command> [options]   (add --lang id for Bahasa Indonesia, --json for JSON where supported)

        Scores & calculators
          news2 --rr 24 --spo2 93 [--o2] --sbp 105 --hr 115 --temp 38.5 [--avpu A|C|V|P|U] [--scale2]
          qsofa --rr 22 --sbp 98 [--altered]
          egfr  --cr 1.2 --age 60 --sex f|m          bmi --weight 70 --height 172
          dose  <drug> --weight 18 [--crcl 40]
          score sofa --pf 150 --ventilated --plt 80 --bili 2.5 --norepinephrine 0.05 --gcs 13 --cr 2.5
          score apache2 --temp 39.5 --map 65 --hr 125 --rr 30 --pao2 65 --ph 7.3 --na 132 --k 3.2 --cr 1.8 --hct 35 --wbc 18 --gcs 13 --age 70
          score pews|meows|wells-pe|wells-dvt|perc|child-pugh ...   (see docs/en/clinical-intelligence.md)
          cvrisk --age 55 --sex m --tc 213 --hdl 50 --sbp 140 [--treated] [--smoker] [--diabetes] [--race aa]
          growth --sex f --age-months 18 --weight 8.9 --length 76 [--standing] [--hc 45]   (WHO tables downloaded once)
          renal <drug> [--crcl 40 | --egfr 40 | --cr 1.6 --age 80 --weight 55 --sex f] [--dialysis] [--child-pugh B]
        Terminology & drugs
          code search <text> [--system icd10|snomed|loinc|rxnorm]
          code validate <icd10|snomed|loinc|rxnorm> <code>       code map <sctid>
          interactions <drug> <drug> ... [--allergy penicillin]
          interactions <drug> ... --sources [--csv formulary.csv] [--drugbank full_database.xml]   (label table + licensed sources)
        Interoperability
          fhir patient|record|ips [--index 3] [--out file.json]   fhir validate <file.json> [--profile <url>]
          hl7 sample [--type oru|adt|orm|oml|siu|mdm] [--out file.hl7]   hl7 parse|to-fhir|conform <file.hl7>
          hl7 send <host:port> <file.hl7>   hl7 listen [--port 2575]
          satusehat bundle [--index 3]   (Encounter -> Condition -> Observation -> MedicationRequest transaction)
          bpjs claim [--index 3] [--sep 0301R0011126V000123]   (INA-CBG claim data + coding validation)
        Imaging
          dicom phantom [--kind head|chest] [--slices 24] [--size 256] --out <dir>
          dicom info|render <file.dcm> [--window brain|lung|bone...] [--out file.png]
          dicom anonymize <in.dcm> <out.dcm> --secret <key>    dicom hemorrhage <dir>
          dicom echo <host> <port> [--aet ANY-SCP]
          dicom transcode <in.dcm> <out.dcm> --ts jpegls|jpegls-near|j2k|j2k-lossy|htj2k|jpeg-lossless|rle|none [--quality n]
          dicom mpr <dir> [--plane axial|coronal|sagittal] [--pos 0.5] [--window brain] --out mpr.png
          dicom mip <dir> [--plane coronal] [--slab 20] [--mode max|min|mean] [--window bone] --out mip.png
          dicom mesh <dir> [--iso 300] [--downsample 2] --out skull.stl|skull.obj
          dicom ai <dir> [--out-dir results]   (haemorrhage on head CT / lung nodules on chest CT → DICOM SEG + SR)
        Signals, privacy, decision support, AI
          ecg [--rhythm sinus|af|ectopy|tachy|brady] [--hr 72] [--seconds 30]
          ecg12 [--pattern inferior|anterior|lateral|diffuse] [--hr 72] [--qtc 400] [--rhythm af] [--sex f --age 50] [--out ecg.svg|.dcm|.scp]
          ecg12 analyze <file.dcm|file.scp> [--sex m --age 60]
          deid <text> | --file note.txt [--pseudonyms --secret <key>]
          cds [--index 3] [--deterioration 0.9]     risk [--index 7]
          ask "<question>" [--patient 7]   (MEDNET_LLM_PROVIDER / ANTHROPIC_API_KEY / HF_TOKEN; offline otherwise)
          emr seed [--db medical.db] [--count 50]   emr show <patient-id> [--db medical.db]
        Clinical intelligence
          nlp "<note>" | --file note.txt [--hf [model]]   (entities, negation, ICD-10 suggestions; --hf = d4data/biomedical-ner-all)
          guideline ask "<question>" [--corpus dir] [--hf-embed] [--offline]   (cited answers)
          federated [--noise 1.0 --clip 0.5] [--no-secure] [--card card.md]     drift [--shift 0.8]
        Hospital platform (v2.0)
          his seed [--db his.db] [--patients 40]          (two demo hospitals: rs-nusantara, rs-bahari)
          his dashboard [--db his.db] [--tenant rs-nusantara]   (census, BOR, ALOS, queues, wards, NEWS2 watch list, revenue)
          his patient <mrn> [--db his.db] [--tenant rs-nusantara] [--fhir summary.json]
          monitor [--minutes 120] [--step 5]   (simulated ICU monitors → HL7/MLLP → NEWS2 alerts; exit 3 on a high alert)
          sync demo                            (offline-first ward rounds: two tablets, conflicts, convergence)
        Interfaces (v2.1)
          worklist query <host> <port> [--modality CT] [--date 20261116[-20261120]] [--aet MEDICALNET-MWL] [--name "RAHAYU*"]   (DICOM MWL C-FIND)
          worklist serve [--port 11112] [--date 20261116]   (demo Modality Worklist SCP)
        """);
    return 0;
}

int News2()
{
    var v = new VitalSigns
    {
        RespiratoryRate = a.Num("rr"), SpO2 = a.Num("spo2"), OnSupplementalOxygen = a.Flag("o2"), SystolicBp = a.Num("sbp"),
        HeartRate = a.Num("hr"), TemperatureC = a.Num("temp"),
        Consciousness = (a.Opt("avpu") ?? "A").ToUpperInvariant() switch
        {
            "C" => Consciousness.Confusion, "V" => Consciousness.Voice, "P" => Consciousness.Pain, "U" => Consciousness.Unresponsive, _ => Consciousness.Alert,
        },
    };
    return Out(ClinicalScores.News2(v, a.Flag("scale2")));
}

int Out(ScoreResult s)
{
    if (a.Flag("json")) { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(s)); return 0; }
    Console.WriteLine($"{s.Name}: {s.Score}/{s.MaxScore}  [{s.Risk}]");
    foreach (var c in s.Components) Console.WriteLine($"  {c.Parameter,-34} {c.Value,-14} {(c.Points > 0 ? "+" + c.Points : "0")}");
    Console.WriteLine(s.Interpretation[lang]);
    return 0;
}

int Egfr()
{
    var r = Calculators.EgfrCkdEpi2021(a.Num("cr"), (int)a.Num("age"), a.Sex());
    Console.WriteLine($"eGFR (CKD-EPI 2021): {r.Egfr} mL/min/1.73m²  stage {r.Stage} — {r.Label[lang]}");
    if (a.Opt("weight") is not null) Console.WriteLine($"CrCl (Cockcroft-Gault): {Calculators.CockcroftGault(a.Num("cr"), (int)a.Num("age"), a.Num("weight"), a.Sex())} mL/min");
    return 0;
}

int Bmi()
{
    var who = Calculators.Bmi(a.Num("weight"), a.Num("height"));
    var ap = Calculators.Bmi(a.Num("weight"), a.Num("height"), BmiStandard.AsiaPacific);
    Console.WriteLine($"BMI {who.Bmi} kg/m² — WHO: {who.Label[lang]}; Asia-Pacific: {ap.Label[lang]}; BSA {Calculators.BsaMosteller(a.Num("weight"), a.Num("height"))} m²");
    return 0;
}

int Dose()
{
    var r = DoseCalculator.Calculate(a.Positional(0, "drug"), a.Num("weight"), a.Opt("crcl") is null ? null : a.Num("crcl"));
    Console.WriteLine($"{r.Generic}: {r.SingleDoseMg} mg × {r.DosesPerDay}/day = {r.DailyDoseMg} mg/day{(r.Capped ? " (capped at maximum)" : "")}{(r.RenalAdjusted ? " (renally adjusted)" : "")}");
    Console.WriteLine(r.Note[lang]);
    return 0;
}

async Task<int> Code()
{
    var t = TerminologyService.Default;
    switch (a.Positional(0, "sub-command"))
    {
        case "search":
            foreach (var h in t.Search(string.Join(' ', a.Positionals.Skip(1)), SystemUri(a.Opt("system")), (int)(a.OptNum("limit") ?? 15)))
                Console.WriteLine($"{CodeSystems.ShortName(h.Concept.System),-9} {h.Concept.Code,-12} {h.Concept.DisplayFor(lang)}");
            return 0;
        case "validate":
            var v = t.Validate(SystemUri(a.Positional(1, "system")) ?? throw new ArgumentException("Unknown system."), a.Positional(2, "code"));
            Console.WriteLine($"{(v.IsValid ? "valid" : "INVALID")}{(v.Known ? ", known" : "")}: {v.Message}");
            return v.IsValid ? 0 : 2;
        case "map":
            var m = t.SnomedToIcd10(a.Positional(1, "sctid"));
            Console.WriteLine(m is null ? "No ICD-10 map." : $"SNOMED {m.Source.Code} {m.Source.Display} → ICD-10 {m.Target.Code} {m.Target.DisplayFor(lang)} ({m.Equivalence})");
            return m is null ? 2 : 0;
        default:
            return await Task.FromResult(Fail("code search|validate|map"));
    }
}

string? SystemUri(string? s) => s?.ToLowerInvariant() switch
{
    "icd10" or "icd-10" => CodeSystems.Icd10, "snomed" => CodeSystems.Snomed, "loinc" => CodeSystems.Loinc, "rxnorm" => CodeSystems.RxNorm, _ => null,
};

int Interactions()
{
    var tools = new ClinicalTools();
    var input = new System.Text.Json.Nodes.JsonObject
    {
        ["drugs"] = new System.Text.Json.Nodes.JsonArray(a.Positionals.Select(d => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(d)!).ToArray()),
        ["allergies"] = new System.Text.Json.Nodes.JsonArray(a.All("allergy").Select(d => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(d)!).ToArray()),
    };
    var res = System.Text.Json.Nodes.JsonNode.Parse(tools.Execute("check_drug_safety", input.ToJsonString(), null).Output)!;
    var findings = res["findings"]!.AsArray();
    if (findings.Count == 0) Console.WriteLine(lang == "id" ? "Tidak ada interaksi yang ditemukan." : "No interactions found.");
    foreach (var f in findings)
        Console.WriteLine($"[{f!["severity"] ?? "duplicate"}] {f["drugs"] ?? $"{f["allergy"]} → {f["drug"]}"}: {f[lang == "id" ? "effect_id" : "effect_en"] ?? $"same class: {f["class"]}"}");
    return findings.Count == 0 ? 0 : 3;
}

int Fhir()
{
    var mapper = new FhirMapper();
    var rec = SyntheticData.Record((int)(a.OptNum("index") ?? 1), a.OptNum("deterioration") ?? 0.3);
    string json;
    switch (a.Positional(0, "sub-command"))
    {
        case "patient": json = FhirJson.Serialize(mapper.ToFhir(rec.Patient)); break;
        case "record": json = FhirJson.Serialize(mapper.ToTransaction(rec)); break;
        case "ips": json = FhirJson.Serialize(mapper.ToPatientSummary(rec)); break;
        case "validate":
            var res = FhirJson.Parse(File.ReadAllText(a.Positional(1, "file")));
            var oo = a.Opt("profile") is not null || res.Meta?.Profile.Any() == true
                ? new FhirProfileValidator().Validate(res, a.Opt("profile"))
                : FhirJson.Validate(res);
            oo.Issue.RemoveAll(i => i.Severity is Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Information);
            Console.WriteLine(oo.Issue.Count == 0 ? $"{res.TypeName}/{res.Id}: valid" : string.Join(Environment.NewLine, oo.Issue.Select(i => $"{i.Severity}: {i.Diagnostics}")));
            return oo.Issue.Count == 0 ? 0 : 2;
        default: return Fail("fhir patient|record|ips|validate");
    }
    return Write(json);
}

int Write(string text)
{
    if (a.Opt("out") is { } path) { File.WriteAllText(path, text); Console.WriteLine($"Wrote {path} ({text.Length:N0} chars)"); }
    else Console.WriteLine(text);
    return 0;
}

async Task<int> Hl7Cmd()
{
    switch (a.Positional(0, "sub-command"))
    {
        case "sample":
            var rec = SyntheticData.Record((int)(a.OptNum("index") ?? 1), 0.6);
            var now = DateTimeOffset.Now;
            var lab = new ServiceOrder { PatientId = rec.Patient.Id, PlacerOrderNumber = "PO-" + now.ToString("HHmmss"), Code = Loinc.Glucose, Priority = OrderPriority.Stat, OrderedAt = now, Requester = "dr. Ratna", Specimen = "SER" };
            NHapi.Base.Model.IMessage msg = (a.Opt("type") ?? "oru").ToLowerInvariant() switch
            {
                "oru" => Hl7V2.BuildResults(rec.Patient, rec.Observations, new("LIS", "LAB"), new("EMR", "RS")),
                "adt" => Hl7V2.BuildAdmit(rec.Patient, rec.Encounters[0], new("ADT", "RS"), new("EMR", "RS")),
                "orm" => Hl7V2.BuildOrder(rec.Patient, [lab], new("EMR", "RS"), new("LIS", "LAB")),
                "oml" => Hl7V2.BuildLabOrder(rec.Patient, [lab], new("EMR", "RS"), new("LIS", "LAB")),
                "siu" => Hl7V2.BuildAppointment(rec.Patient, new Appointment { PatientId = rec.Patient.Id, Start = now.AddDays(1), End = now.AddDays(1).AddMinutes(15), Practitioner = "dr. Ratna", Location = "Poli Penyakit Dalam", Reason = "Kontrol" }, new("SCH", "RS"), new("EMR", "RS")),
                "mdm" => Hl7V2.BuildDocument(rec.Patient, new ClinicalDocument { PatientId = rec.Patient.Id, Type = ClinicalCode.Loinc("11506-3", "Progress note"), Title = "CPPT", Author = "dr. Ratna", Created = now, Text = "S: sesak napas 2 hari\nO: RR 24, SpO2 93%\nA: pneumonia\nP: ceftriaxone 1 g IV" }, new("EMR", "RS"), new("DMS", "RS"), rec.Encounters[0]),
                var t => throw new ArgumentException($"Unknown HL7 sample type '{t}' (oru|adt|orm|oml|siu|mdm)."),
            };
            return Write(Hl7V2.Encode(msg).Replace("\r", Environment.NewLine));
        case "conform":
            var result = new Hl7ConformanceValidator().Validate(File.ReadAllText(a.Positional(1, "file")).Replace("\r\n", "\r").Replace('\n', '\r'));
            Console.WriteLine($"Profile {result.Profile}: {(result.IsValid ? "conformant" : "NOT conformant")}");
            foreach (var issue in result.Issues) Console.WriteLine($"  {issue}");
            return result.IsValid ? 0 : 2;
        case "parse":
            var m = Hl7V2.Parse(File.ReadAllText(a.Positional(1, "file")));
            Console.WriteLine($"{m}  from {m.SendingApplication}@{m.SendingFacility}");
            var p = Hl7Mapping.ToPatient(m);
            Console.WriteLine($"Patient {p.Name} MRN {p.Mrn} DOB {p.BirthDate:yyyy-MM-dd} {p.Gender}");
            foreach (var o in Hl7Mapping.ToObservations(m, p.Id)) Console.WriteLine($"  {o}");
            return 0;
        case "to-fhir":
            return Write(FhirJson.Serialize(Hl7Mapping.ToFhirTransaction(Hl7V2.Parse(File.ReadAllText(a.Positional(1, "file"))))));
        case "send":
            var hp = a.Positional(1, "host:port").Split(':');
            await using (var client = new MllpClient(hp[0], int.Parse(hp[1], CultureInfo.InvariantCulture)))
                Console.WriteLine((await client.SendAsync(File.ReadAllText(a.Positional(2, "file")))).Replace("\r", Environment.NewLine));
            return 0;
        case "listen":
            int port = (int)(a.OptNum("port") ?? 2575);
            await using (var server = new MllpServer(new IPEndPoint(IPAddress.Any, port), (msg, _) =>
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg} from {msg.SendingApplication}");
                return ValueTask.FromResult(Hl7V2.BuildAck(msg));
            }))
            {
                server.Start();
                Console.WriteLine($"Listening for HL7 over MLLP on port {port}. Ctrl+C to stop.");
                var done = new TaskCompletionSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                await done.Task;
            }
            return 0;
        default: return Fail("hl7 sample|parse|to-fhir|conform|send|listen");
    }
}

async Task<int> DicomCmd()
{
    switch (a.Positional(0, "sub-command"))
    {
        case "phantom":
            var kind = a.Opt("kind") == "chest" ? PhantomKind.Chest : PhantomKind.Head;
            var series = DicomPhantom.CreateSeries(kind, (int)(a.OptNum("slices") ?? 24), (int)(a.OptNum("size") ?? 256));
            string dir = a.Opt("out") ?? $"phantom-{kind.ToString().ToLowerInvariant()}";
            Directory.CreateDirectory(dir);
            for (int i = 0; i < series.Count; i++) await series[i].SaveAsync(Path.Combine(dir, $"IM{i + 1:000}.dcm"));
            Console.WriteLine($"Wrote {series.Count} CT instances to {dir}");
            return 0;
        case "info":
            var info = DicomImaging.Describe(DicomImaging.Open(a.Positional(1, "file")).Dataset);
            foreach (var prop in typeof(DicomImageInfo).GetProperties()) Console.WriteLine($"{prop.Name,-20} {prop.GetValue(info)}");
            return 0;
        case "render":
            var file = DicomImaging.Open(a.Positional(1, "file"));
            var img = DicomImaging.Render(file.Dataset, a.Opt("window") is { } w ? WindowPreset.Find(w) ?? throw new ArgumentException($"Unknown window '{w}'.") : null);
            string png = a.Opt("out") ?? Path.ChangeExtension(a.Positional(1, "file"), ".png");
            await File.WriteAllBytesAsync(png, img.ToPng());
            Console.WriteLine($"{img.Width}×{img.Height} {img.Window.Name} (C {img.Window.Center} / W {img.Window.Width}) → {png}  [{Medical.Native.MedicalKernels.ActiveBackend}]");
            return 0;
        case "anonymize":
            var src = DicomImaging.Open(a.Positional(1, "input"));
            var secret = a.Opt("secret") ?? Environment.GetEnvironmentVariable("MEDNET_PSEUDONYM_SECRET") ?? throw new ArgumentException("--secret (≥ 16 chars) or MEDNET_PSEUDONYM_SECRET is required.");
            var anon = new DicomDeidentifier(secret).Deidentify(src, out var report);
            await anon.SaveAsync(a.Positional(2, "output"));
            Console.WriteLine($"{report.OriginalPatientId} → {report.Pseudonym}; {report.ElementsBefore} → {report.ElementsAfter} elements");
            return 0;
        case "hemorrhage":
            var files = Directory.GetFiles(a.Positional(1, "dir"), "*.dcm").Select(DicomImaging.Open).ToArray();
            var rep = HemorrhageScreener.Analyze(files);
            Console.WriteLine($"{rep.Impression[lang]}  ({rep.Method}; {rep.SlicesAnalyzed} slices)");
            foreach (var f in rep.Findings) Console.WriteLine($"  slice {f.Slice}: {f.AreaMm2} mm², mean {f.MeanHu} HU at ({f.CentroidX}, {f.CentroidY})");
            return rep.Suspected ? 3 : 0;
        case "echo":
            var (ok, elapsed) = await DicomScu.EchoAsync(a.Positional(1, "host"), int.Parse(a.Positional(2, "port"), CultureInfo.InvariantCulture), calledAe: a.Opt("aet") ?? "ANY-SCP");
            Console.WriteLine(ok ? $"C-ECHO success in {elapsed.TotalMilliseconds:0} ms" : "C-ECHO failed");
            return ok ? 0 : 2;
        case "transcode":
            var input = DicomImaging.Open(a.Positional(1, "input"));
            var target = (a.Opt("ts") ?? "jpegls") switch
            {
                "jpegls" => DicomCompression.JpegLsLossless, "jpegls-near" => DicomCompression.JpegLsNearLossless,
                "j2k" => DicomCompression.Jpeg2000Lossless, "j2k-lossy" => DicomCompression.Jpeg2000, "htj2k" => DicomCompression.HtJpeg2000Lossless,
                "jpeg-lossless" => DicomCompression.JpegLossless, "rle" => DicomCompression.Rle, "none" or "explicit" => DicomCompression.None,
                var t => throw new ArgumentException($"Unknown transfer syntax '{t}'."),
            };
            var output = DicomCodecs.Transcode(input, target, a.OptNum("quality") is { } q ? (int)q : null);
            await output.SaveAsync(a.Positional(2, "output"));
            long before = DicomCodecs.PixelDataBytes(input.Dataset), after = DicomCodecs.PixelDataBytes(output.Dataset);
            Console.WriteLine($"{input.Dataset.InternalTransferSyntax.UID.Name} → {output.Dataset.InternalTransferSyntax.UID.Name}: pixel data {before:N0} → {after:N0} bytes ({(after > 0 ? (double)before / after : 0):0.0}:1)");
            return 0;
        case "mpr" or "mip":
            var vol = LoadVolume(a.Positional(1, "dir"));
            var plane = Enum.Parse<VolumePlane>(a.Opt("plane") ?? (a.Positional(0, "") == "mip" ? "coronal" : "axial"), ignoreCase: true);
            var mode = (a.Opt("mode") ?? "max") switch { "min" => Medical.Native.SlabMode.Min, "mean" or "avg" => Medical.Native.SlabMode.Mean, _ => Medical.Native.SlabMode.Max };
            var image = a.Positional(0, "") == "mpr"
                ? VolumeRendering.Mpr(vol, plane, a.OptNum("pos") ?? 0.5)
                : VolumeRendering.Project(vol, plane, mode, a.OptNum("pos") ?? 0.5, a.OptNum("slab") ?? double.PositiveInfinity);
            var win = a.Opt("window") is { } wn ? WindowPreset.Find(wn) ?? throw new ArgumentException($"Unknown window '{wn}'.") : mode == Medical.Native.SlabMode.Min ? WindowPreset.Lung : WindowPreset.Bone;
            string outPng = a.Opt("out") ?? $"{a.Positional(0, "")}-{plane.ToString().ToLowerInvariant()}.png";
            await File.WriteAllBytesAsync(outPng, image.ToPng(win));
            Console.WriteLine($"{image.Description} · {win.Name} → {outPng}  [{image.Backend}]");
            return 0;
        case "mesh":
            var mv = LoadVolume(a.Positional(1, "dir"));
            var mesh = IsoSurface.Extract(mv, a.OptNum("iso") ?? 300, (int)(a.OptNum("downsample") ?? 2));
            string meshOut = a.Opt("out") ?? "surface.stl";
            if (meshOut.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) await File.WriteAllTextAsync(meshOut, mesh.ToObj());
            else await File.WriteAllBytesAsync(meshOut, mesh.ToStl());
            var (bmin, bmax) = mesh.Bounds;
            Console.WriteLine($"{mesh.TriangleCount:N0} triangles, {mesh.VolumeMl:0.0} mL enclosed, {bmax.X - bmin.X:0} × {bmax.Y - bmin.Y:0} × {bmax.Z - bmin.Z:0} mm → {meshOut}");
            return 0;
        case "ai":
            var aiFiles = Directory.GetFiles(a.Positional(1, "dir"), "*.dcm").Select(DicomImaging.Open).ToArray();
            var av = DicomVolume.FromSeries(aiFiles);
            string outDir = a.Opt("out-dir") ?? "ai-results";
            Directory.CreateDirectory(outDir);
            ImagingAiOutput? result;
            if (HemorrhageScreener.IsHeadCt(DicomImaging.Describe(aiFiles[0].Dataset)))
            {
                result = ImagingAiReports.Hemorrhage(av, HemorrhageScreener.SegmentVolume(av));
                Console.WriteLine(HemorrhageScreener.Analyze(aiFiles).Impression[lang]);
            }
            else
            {
                var nod = LungNoduleDetector.Detect(av);
                foreach (var n in nod.Nodules)
                    Console.WriteLine($"  nodule {n.Id}: {n.LongAxisMm} × {n.ShortAxisMm} mm (mean {n.MeanDiameterMm} mm), {n.VolumeMm3:0} mm³, {n.MeanHu} HU, Lung-RADS {n.LungRads}");
                Console.WriteLine(nod.Impression[lang]);
                result = ImagingAiReports.LungNodules(av, nod);
            }
            if (result is null) { Console.WriteLine(lang == "id" ? "Tidak ada temuan untuk disimpan." : "No findings to store."); return 0; }
            if (result.Segmentation is { } segFile) await segFile.SaveAsync(Path.Combine(outDir, "seg.dcm"));
            await result.Report.SaveAsync(Path.Combine(outDir, "sr.dcm"));
            Console.WriteLine($"DICOM SEG + SR (TID 1500) → {outDir}");
            Console.Write(MeasurementReport.Read(result.Report).Text);
            return 2;
        default: return Fail("dicom phantom|info|render|anonymize|hemorrhage|echo|transcode|mpr|mip|mesh|ai");
    }
}

int Ecg()
{
    var rhythm = (a.Opt("rhythm") ?? "sinus") switch
    {
        "af" => Rhythm.AtrialFibrillation, "ectopy" or "pvc" => Rhythm.Ectopy, "tachy" => Rhythm.SinusTachycardia, "brady" => Rhythm.SinusBradycardia, _ => Rhythm.NormalSinus,
    };
    double hr = a.OptNum("hr") ?? rhythm switch { Rhythm.SinusTachycardia => 125, Rhythm.SinusBradycardia => 48, _ => 72 };
    var sig = EcgSimulator.Generate(a.OptNum("seconds") ?? 30, hr, rhythm == Rhythm.SinusTachycardia || rhythm == Rhythm.SinusBradycardia ? Rhythm.NormalSinus : rhythm);
    var r = EcgAnalyzer.Analyze(sig);
    Console.WriteLine($"Beats {r.RPeaks.Length} · HR {r.HeartRateBpm} bpm · SDNN {r.SdnnMs} ms · RMSSD {r.RmssdMs} ms · pNN50 {r.Pnn50:P0} · RR CV {r.RrCv:P1}  [{r.Backend}]");
    Console.WriteLine(r.Interpretation[lang]);
    return 0;
}

int Deid()
{
    string text = a.Opt("file") is { } f ? File.ReadAllText(f) : string.Join(' ', a.Positionals);
    var d = new TextDeidentifier(a.Opt("secret"));
    var res = d.Deidentify(text, a.Flag("pseudonyms") ? ReplacementStyle.Pseudonyms : ReplacementStyle.Tags);
    Console.WriteLine(res.Text);
    Console.Error.WriteLine(string.Join(", ", res.Counts.Select(kv => $"{kv.Key}: {kv.Value}")));
    return 0;
}

int Cds()
{
    var rec = SyntheticData.Record((int)(a.OptNum("index") ?? 3), a.OptNum("deterioration") ?? 0.9);
    Console.WriteLine($"{rec.Patient.Name} ({rec.Patient.Mrn}) — {rec.Conditions.Count} problems, {rec.Medications.Count} medications");
    foreach (var al in new ClinicalDecisionSupport().Evaluate(rec)) Console.WriteLine($"[{al.Severity,-8}] {al.Title[lang]} — {al.Detail[lang]}");
    return 0;
}

int Risk()
{
    var rec = SyntheticData.Record((int)(a.OptNum("index") ?? 7), a.OptNum("deterioration") ?? 0.9);
    var m = new DeteriorationRiskModel();
    var metrics = m.Train();
    var p = m.Predict(rec);
    Console.WriteLine($"{p.Model}: p = {p.Probability:P1} [{p.Band}] — {p.Interpretation[lang]}");
    foreach (var d in p.Drivers.Take(5)) Console.WriteLine($"  {d.Feature,-14} {d.Value,8:0.##}  {d.Contribution:+0.00;-0.00}");
    Console.WriteLine($"(synthetic validation AUC {metrics.Auc}, accuracy {metrics.Accuracy})");
    return 0;
}

async Task<int> Ask()
{
    var llm = LlmOptions.FromEnvironment().Create();
    var assistant = new ClinicalAssistant(llm) { Patient = a.OptNum("patient") is { } i ? SyntheticData.Record((int)i, 0.85) : null };
    var reply = await assistant.AskAsync(string.Join(' ', a.Positionals));
    Console.WriteLine(reply.Text);
    Console.Error.WriteLine($"— {reply.Provider}/{reply.Model}; tools: {string.Join(", ", reply.ToolCalls.Select(t => t.Name))}");
    return 0;
}

async Task<int> Emr()
{
    var services = new ServiceCollection();
    services.AddMedical(m => m.AddEmr(o => o.ConnectionString = $"Data Source={a.Opt("db") ?? "medical.db"}"));
    await using var sp = services.BuildServiceProvider();
    await sp.EnsureEmrDatabaseAsync();
    using var scope = sp.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<PatientRepository>();
    repo.User = Environment.UserName;
    switch (a.Positional(0, "sub-command"))
    {
        case "seed":
            Console.WriteLine($"Added {await repo.SeedSyntheticAsync((int)(a.OptNum("count") ?? 50))} patients; total {await repo.CountAsync()}.");
            return 0;
        case "show":
            var r = await repo.GetRecordAsync(a.Positional(1, "patient-id"), "cli");
            if (r is null) return Fail("Not found.");
            Console.WriteLine($"{r.Patient.Name} · {r.Patient.Mrn} · {r.Patient.Gender} · {r.Patient.BirthDate:yyyy-MM-dd}");
            foreach (var c in r.Conditions) Console.WriteLine($"  problem   {c.Code}");
            foreach (var m in r.Medications) Console.WriteLine($"  med       {m.Generic} {m.DoseMg} mg × {m.FrequencyPerDay}/d");
            foreach (var o in r.Observations) Console.WriteLine($"  lab       {o}");
            if (r.LatestVitals is { } v) Console.WriteLine($"  NEWS2     {ClinicalScores.News2(v)}");
            return 0;
        default: return Fail("emr seed|show");
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}

int SatuSehat()
{
    if (a.Positional(0, "sub-command") != "bundle") return Fail("satusehat bundle [--index 3]");
    var rec = SyntheticData.Record((int)(a.OptNum("index") ?? 1), 0.3);
    var ctx = new SatuSehatContext
    {
        PatientIhs = a.Opt("patient-ihs") ?? "P00000000000", PatientName = rec.Patient.Name.ToString(),
        PractitionerIhs = a.Opt("practitioner-ihs") ?? "N00000000", PractitionerName = "dr. Ratna",
        LocationId = a.Opt("location") ?? "00000000-0000-0000-0000-000000000000", LocationName = "Poli Penyakit Dalam",
        OrganizationId = a.Opt("org") ?? Environment.GetEnvironmentVariable("SATUSEHAT_ORG_ID") ?? "10000000",
    };
    var sub = SatuSehatWorkflow.BuildOutpatientVisit(rec, ctx);
    foreach (var w in sub.Warnings) Console.Error.WriteLine($"warning: {w}");
    return Write(FhirJson.Serialize(sub.Bundle));
}

int Bpjs()
{
    if (a.Positional(0, "sub-command") != "claim") return Fail("bpjs claim [--index 3] [--sep <19 chars>]");
    var rec = SyntheticData.Record((int)(a.OptNum("index") ?? 1), 0.6);
    var claim = InaCbgClaimBuilder.FromRecord(rec, a.Opt("sep") ?? "0301R0011126V000123", a.Opt("card") ?? "0001234567890",
        new HospitalTariff { Kamar = 1_500_000, Obat = 650_000, Laboratorium = 420_000, Radiologi = 250_000 }, a.Opt("coder") ?? "3201234505900001");
    var issues = InaCbgClaimBuilder.Validate(claim);
    foreach (var i in issues) Console.Error.WriteLine($"{i.Severity}: {i.Field} - {i.Message[lang]}");
    Write(claim.ClaimData().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return InaCbgClaimBuilder.IsValid(issues) ? 0 : 2;
}

static DicomVolume LoadVolume(string dir) =>
    DicomVolume.FromSeries(Directory.GetFiles(dir, "*.dcm").Select(DicomImaging.Open));

async Task<int> Ecg12Cmd()
{
    var patient = new EcgPatient(a.Opt("sex") is "f" or "female" ? Gender.Female : a.Opt("sex") is "m" or "male" ? Gender.Male : Gender.Unknown,
        a.OptNum("age") is { } age ? (int)age : null);
    Ecg12 ecg;
    if (a.Positionals.Count > 0 && a.Positional(0, "") == "analyze")
    {
        string path = a.Positional(1, "file");
        ecg = path.EndsWith(".scp", StringComparison.OrdinalIgnoreCase)
            ? ScpEcg.Read(await File.ReadAllBytesAsync(path)).Ecg
            : DicomEcg.Read(DicomImaging.Open(path));
    }
    else
    {
        var pattern = (a.Opt("pattern") ?? "none") switch
        {
            "inferior" => StPattern.InferiorStemi, "anterior" => StPattern.AnteriorStemi, "lateral" => StPattern.LateralStemi,
            "diffuse" => StPattern.DiffuseDepression, _ => StPattern.None,
        };
        ecg = Ecg12Simulator.Generate(new Ecg12Options
        {
            HeartRate = a.OptNum("hr") ?? 72, QtcMs = a.OptNum("qtc") ?? 400, StPattern = pattern,
            Rhythm = a.Opt("rhythm") == "af" ? Rhythm.AtrialFibrillation : Rhythm.NormalSinus,
        }).Ecg;
    }
    var r = Ecg12Analyzer.Analyze(ecg, patient);
    Console.WriteLine($"HR {r.HeartRateBpm} bpm · PR {(r.PrMs is { } pr ? $"{pr} ms" : "—")} · QRS {r.QrsMs} ms · QT {r.QtMs} ms · QTc Bazett {r.QtcBazettMs} / Fridericia {r.QtcFridericiaMs} / Framingham {r.QtcFraminghamMs} / Hodges {r.QtcHodgesMs} ms · axis {r.QrsAxisDeg}°");
    Console.WriteLine("ST at J (mm): " + string.Join("  ", r.Leads.Select(l => $"{l.Lead} {l.StJMv * 10:+0.0;-0.0;0.0}")));
    Console.WriteLine(r.Interpretation[lang]);
    if (a.Opt("out") is { } outPath)
    {
        var ext = Path.GetExtension(outPath).ToLowerInvariant();
        if (ext == ".svg") await File.WriteAllTextAsync(outPath, Charts.Ecg12Grid(ecg, highlight: r.Leads.Where(l => l.StJMv >= 0.1).Select(l => l.Lead)));
        else if (ext == ".scp") await File.WriteAllBytesAsync(outPath, ScpEcg.Write(ecg, "SYNTH-0001", "Synthetic", "Patient", patient.Sex));
        else await DicomEcg.Create(ecg, SyntheticData.Patient(1), r).SaveAsync(outPath);
        Console.WriteLine($"Wrote {outPath}");
    }
    return r.MeetsStemiCriteria || r.Qtc >= QtcCategory.Prolonged ? 3 : 0;
}

namespace Medical.Cli
{
    /// <summary>Tiny argument parser: command, positionals, --key value, --flag.</summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, List<string>> _opts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        public Args(string[] argv)
        {
            Command = argv.Length > 0 ? argv[0].ToLowerInvariant() : "";
            var pos = new List<string>();
            for (int i = 1; i < argv.Length; i++)
            {
                if (argv[i].StartsWith("--", StringComparison.Ordinal))
                {
                    string key = argv[i][2..];
                    if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        if (!_opts.TryGetValue(key, out var l)) _opts[key] = l = [];
                        l.Add(argv[++i]);
                    }
                    else _flags.Add(key);
                }
                else pos.Add(argv[i]);
            }
            Positionals = pos;
        }

        public string Command { get; }
        public IReadOnlyList<string> Positionals { get; }
        public string? Opt(string key) => _opts.TryGetValue(key, out var l) ? l[^1] : null;
        public IEnumerable<string> All(string key) => _opts.TryGetValue(key, out var l) ? l : [];
        public bool Flag(string key) => _flags.Contains(key) || (Opt(key) is { } v && v is "true" or "yes");
        public double? OptNum(string key) => Opt(key) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : null;
        public double Num(string key) => OptNum(key) ?? throw new ArgumentException($"--{key} is required.");
        public string Positional(int i, string name) => i < Positionals.Count ? Positionals[i] : throw new ArgumentException($"<{name}> is required.");
        public Gender Sex() => (Opt("sex") ?? "m").ToLowerInvariant() is "f" or "female" or "p" or "perempuan" ? Gender.Female : Gender.Male;
    }
}
