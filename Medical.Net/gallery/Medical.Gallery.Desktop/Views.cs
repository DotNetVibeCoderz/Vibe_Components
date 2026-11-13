using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Medical.AI.Assistant;
using Medical.Clinical;
using Medical.Core;
using Medical.Dicom;
using Medical.Gallery.Shared;
using Medical.Signals;
using static Medical.Gallery.Desktop.Ui;

namespace Medical.Gallery.Desktop;

/// <summary>Desktop renderings of the shared gallery scenarios. Interactive controls update a small state object and re-render.</summary>
internal static class Views
{
    private static readonly Dictionary<string, object> State = [];

    private static T S<T>() where T : new()
    {
        string key = typeof(T).Name;
        if (!State.TryGetValue(key, out var v)) State[key] = v = new T();
        return (T)v;
    }

    private sealed class NewsState { public double Det = 0.85; }
    private sealed class DicomState { public int Slice = 14; public string Window = "Brain"; public bool Chest; }
    private sealed class SliceState { public int Slice = 14; }
    private sealed class EcgState { public Rhythm Rhythm = Rhythm.AtrialFibrillation; public double Hr = 88; }
    private sealed class TermState { public string Query = "gagal jantung"; }
    private sealed class RiskState { public double Det = 0.7; }
    private sealed class PrivacyState { public bool Pseudo; }
    private sealed class OrderState { public string Type = "ORM"; public bool Broken; }
    private sealed class ClaimState { public bool Broken; }
    private sealed class MprState { public bool Chest; public double Pos = 0.5; public Medical.Native.SlabMode Mode = Medical.Native.SlabMode.Max; public double Slab = 30; }
    private sealed class AiState { public bool Chest; }
    private sealed class Ecg12State { public Medical.Signals.StPattern Pattern = Medical.Signals.StPattern.InferiorStemi; public double Hr = 72, Qtc = 400; public bool Af, Female; }
    private sealed class IcuState { public double Sev = 0.6; }
    private sealed class CvState { public int Age = 58; public double Sbp = 142, Tc = 220, Hdl = 42; public bool Female, Smoker = true, Diabetes; }
    private sealed class GrowthState { public GrowthIndicator Indicator = GrowthIndicator.LengthHeightForAge; }
    private sealed class SafetyState { public double Cr = 1.6; public string ChildPugh = "A"; }
    private sealed class NlpState { public string? Lang; }
    private sealed class GuideState
    {
        public string Question = Scenarios.GuidelineQuestions[0];
        public GuidelineView? View;
        public bool Busy;
        public IClinicalLlm Llm = LlmOptions.FromEnvironment().Create();
    }
    private sealed class FedState { public double Noise, Shift = 0.8; public bool Secure = true; }
    private sealed class ChatState
    {
        public ClinicalAssistant Assistant = Scenarios.Assistant();
        public List<(string Q, AssistantReply R)> Turns = [];
        public bool Busy;
    }

    public static Control Build(string slug, string lang, Action rerender)
    {
        string T(string en, string id) => lang == "id" ? id : en;
        switch (slug)
        {
            case "news2":
            {
                var st = S<NewsState>();
                var v = Scenarios.News(st.Det, lang);
                return Stack(16,
                    Controls(Slider(T("Deterioration over 12 hours", "Perburukan selama 12 jam"), 0, 1, st.Det, 0.05, x => { st.Det = x; rerender(); }, x => $"{x * 100:0} %")),
                    Panel(T("Observation chart", "Lembar observasi"), SvgView(v.ObservationChart), $"{v.Patient.Name} · {v.Patient.Mrn} · {v.Patient.Location}"),
                    Columns([2, 3],
                        Panel("NEWS2", Stack(8, SvgView(v.Gauge, 240).Also(c => c.HorizontalAlignment = HorizontalAlignment.Center),
                            Pill(Scenarios.RiskName(v.News2.Risk)[lang], v.News2.Risk.ToString().ToLowerInvariant()), Text(v.News2.Interpretation[lang]),
                            Mono($"qSOFA {v.QSofa.Score}/3 — {v.QSofa.Interpretation[lang]}", 12))),
                        Panel(T("Trend", "Tren"), SvgView(v.Trend))));
            }
            case "cds":
            {
                var v = Scenarios.Cds().GetAwaiter().GetResult();
                var alerts = new StackPanel();
                foreach (var a in v.Alerts) alerts.Children.Add(Alert(a.Severity.ToString().ToLowerInvariant(), $"{a.Severity} · {a.Category} · {a.Rule}", a.Title[lang], a.Detail[lang]));
                return Columns([2, 3],
                    Panel(v.Patient.Name, Stack(4,
                        Eyebrow(T("Problems", "Masalah")), Text(string.Join("\n", v.Patient.Problems)),
                        Eyebrow(T("Medications", "Obat")), Mono(string.Join("\n", v.Medications)),
                        Eyebrow(T("Allergies", "Alergi")), Text(string.Join(", ", v.Allergies)),
                        Eyebrow(T("Triage workflow (JSON rules)", "Alur triase (aturan JSON)")), Text(v.Triage.FirstOrDefault(t => t.Matched)?.Event ?? "—", weight: FontWeight.Bold)),
                        $"{v.Patient.Age} {T("y", "th")} · {v.Patient.Sex} · {v.Patient.Location}"),
                    Panel(T($"{v.Alerts.Count} alerts", $"{v.Alerts.Count} peringatan"), alerts, T($"{v.Rules.Count} NRules rules", $"{v.Rules.Count} aturan NRules")));
            }
            case "calculators":
            {
                var v = Scenarios.Calculators();
                return Stack(16,
                    Text(T("Woman, 67 y, 58 kg, 155 cm, creatinine 1.4 mg/dL", "Perempuan, 67 th, 58 kg, 155 cm, kreatinin 1,4 mg/dL"), 15, "muted"),
                    Columns([1, 1, 1, 1],
                        Readout("eGFR · CKD-EPI 2021", $"{v.Egfr.Egfr}", "mL/min/1.73 m²", $"{v.Egfr.Stage} — {v.Egfr.Label[lang]}"),
                        Readout("CrCl · Cockcroft-Gault", Scenarios.Fmt(v.CrCl), "mL/min", T("used for dosing", "untuk dosis")),
                        Readout("BMI", $"{v.BmiWho.Bmi}", "kg/m²", $"WHO {v.BmiWho.Label[lang]} · AP {v.BmiAsia.Label[lang]}"),
                        Readout("BSA", $"{v.Bsa}", "m²", $"{T("fluids", "cairan")} {v.FluidsMlH} mL/h")),
                    Panel(v.Dose.Generic, Stack(4, Mono($"{Scenarios.Fmt(v.Dose.SingleDoseMg)} mg × {v.Dose.DosesPerDay}/{T("day", "hari")} = {Scenarios.Fmt(v.Dose.DailyDoseMg)} mg", 26), Text(v.Dose.Note[lang], 14, "muted"))));
            }
            case "dicom":
            {
                var st = S<DicomState>();
                var v = Scenarios.Dicom(st.Slice, st.Window, st.Chest);
                var film = new Border { Background = Brush("Film"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = new Grid { Children = {
                    Png(v.Png, 520),
                    Mono($"{v.Info.PatientName}\n{v.Info.StudyDescription}", 11.5).Also(t => { t.Foreground = new SolidColorBrush(Color.Parse("#C9D6DF")); t.VerticalAlignment = VerticalAlignment.Top; t.HorizontalAlignment = HorizontalAlignment.Left; }),
                    Mono($"C {v.Window.Center} · W {v.Window.Width}\n{v.Info.SliceLocation} mm", 11.5).Also(t => { t.Foreground = new SolidColorBrush(Color.Parse("#C9D6DF")); t.VerticalAlignment = VerticalAlignment.Top; t.HorizontalAlignment = HorizontalAlignment.Right; t.TextAlignment = TextAlignment.Right; }) } } };
                return Stack(16,
                    Controls(
                        Segmented([(false, T("Head CT", "CT kepala")), (true, T("Chest CT", "CT toraks"))], st.Chest, x => { st.Chest = x; st.Window = x ? "Lung" : "Brain"; st.Slice = x ? 8 : 14; rerender(); }),
                        Slider(T("Slice", "Irisan"), 0, v.Slices - 1, v.Slice, 1, x => { st.Slice = (int)x; rerender(); }, x => $"{x + 1:0}/{v.Slices}"),
                        Segmented(WindowPreset.Ct.Select(p => (p.Name, p.Name)), v.Window.Name, n => { st.Window = n; rerender(); })),
                    Columns([3, 2], film,
                        Panel($"{v.Info.Modality} · {v.Info.SeriesDescription}", Stack(4,
                            Table([T("Field", "Kolom"), T("Value", "Nilai")],
                            [
                                [T("Matrix", "Matriks"), $"{v.Info.Columns} × {v.Info.Rows} · {v.Info.BitsStored}-bit"],
                                [T("Spacing", "Jarak"), $"{v.Info.PixelSpacingRow} mm · {T("slice", "irisan")} {v.Info.SliceThickness} mm"],
                                ["HU", $"{T("mean", "rerata")} {v.Stats.Mean:0} · SD {v.Stats.StdDev:0} · {v.Stats.Min}…{v.Stats.Max}"],
                                [T("Render", "Render"), $"{v.Backend} · {v.RenderMs} ms"],
                            ]),
                            Eyebrow(T("Hounsfield histogram (log)", "Histogram Hounsfield (log)")), SvgView(v.Histogram)))));
            }
            case "hemorrhage":
            {
                var st = S<SliceState>();
                var v = Scenarios.Hemorrhage(st.Slice);
                var r = v.Report;
                return Stack(16,
                    Controls(Slider(T("Slice", "Irisan"), 0, v.Slices - 1, v.Slice, 1, x => { st.Slice = (int)x; rerender(); }, x => $"{x + 1:0}/{v.Slices}")),
                    Columns([3, 2],
                        new Border { Background = Brush("Film"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = Png(v.Overlay, 520) },
                        Panel(null, Stack(8,
                            Pill(r.Suspected ? T("Prioritise for review", "Prioritaskan untuk dibaca") : T("No collection found", "Tidak ditemukan"), r.Suspected ? "high" : "low"),
                            Mono($"{r.VolumeMl:0.0} mL", 34), Text(r.Impression[lang]),
                            Table([T("Slice", "Irisan"), "mm²", T("mean HU", "rerata HU")], r.Findings.Select(f => new[] { $"{f.Slice}", $"{f.AreaMm2}", $"{f.MeanHu}" }), 1, 2),
                            Text($"{r.Method}. {T("A triage aid — not a diagnosis.", "Alat bantu triase — bukan diagnosis.")}", 12.5, "muted")))));
            }
            case "ecg":
            {
                var st = S<EcgState>();
                var v = Scenarios.Ecg(st.Rhythm, st.Hr);
                var a = v.Analysis;
                return Stack(16,
                    Controls(
                        Segmented([(Rhythm.NormalSinus, "Sinus"), (Rhythm.AtrialFibrillation, T("Atrial fibrillation", "Fibrilasi atrium")), (Rhythm.Ectopy, T("Ectopic beats", "Denyut ektopik"))], st.Rhythm, x => { st.Rhythm = x; rerender(); }),
                        Slider(T("Rate", "Frekuensi"), 40, 150, st.Hr, 2, x => { st.Hr = x; rerender(); }, x => $"{x:0} bpm")),
                    new Border { Classes = { "panel" }, Padding = new Thickness(0), ClipToBounds = true, Child = SvgView(v.Strip) },
                    Columns([1, 1, 1, 1.4],
                        Readout(T("Heart rate", "Denyut"), Scenarios.Fmt(a.HeartRateBpm, "0"), "bpm", $"{a.RPeaks.Length} {T("beats in 30 s", "denyut / 30 dtk")}"),
                        Readout("SDNN", $"{a.SdnnMs}", "ms", $"RMSSD {a.RmssdMs} ms"),
                        Readout("RR CV", Scenarios.Fmt(a.RrCv * 100), "%", $"{T("detector", "detektor")}: {a.Backend}"),
                        Panel(Scenarios.RhythmName(a.Rhythm)[lang], Text(a.Interpretation[lang], 13.5, "muted"))));
            }
            case "fhir":
            {
                var v = Scenarios.Fhir();
                return Columns([2, 3],
                    Panel(T("International Patient Summary", "Ringkasan Pasien Internasional"), Stack(6,
                        Text($"document · {v.Entries} {T("entries", "entri")} · {v.Issues} {T("validation issues", "masalah validasi")}", 14, "muted"),
                        Table([T("Section", "Bagian"), T("Entries", "Entri")], v.Sections.Select(s => new[] { s.Section, $"{s.Items}" }), 1),
                        Eyebrow("FHIRPath"), Mono(string.Join("\n", v.FhirPath), 11.5))),
                    Panel("FHIR R4 JSON", Mono(v.Json[..Math.Min(3200, v.Json.Length)] + "\n…", 11.5)));
            }
            case "hl7":
            {
                var v = Scenarios.Hl7();
                return Columns([1, 1],
                    Panel("ORU^R01 · HL7 v2.5.1", Stack(8, Mono(v.Er7, 11), Eyebrow("ACK"), Mono(v.Ack, 11)), "LIS → EMR"),
                    Panel(T("Mapped results", "Hasil terpetakan"), Table(["LOINC", T("Test", "Pemeriksaan"), T("Value", "Nilai"), ""],
                        v.Observations.Select(o => new[] { o.Code.Code, o.Code.Display ?? "", $"{o.Value} {Ucum.Display(o.Unit ?? "")}", o.Flag == ResultFlag.Normal ? "" : o.Flag.ToString() }), 0, 2), v.Patient));
            }
            case "terminology":
            {
                var st = S<TermState>();
                var v = Scenarios.Terminology(st.Query);
                var box = new TextBox { Text = st.Query, Width = 420 };
                box.KeyUp += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { st.Query = box.Text ?? ""; rerender(); } };
                var detail = v.Selected is { } c
                    ? Panel(c.Display, Stack(6,
                        Eyebrow(T("Is-a hierarchy", "Hierarki is-a")), Text(string.Join("\n", v.Ancestors.Reverse().Select(a => $"↑ {a.Display}  {a.Code}"))),
                        Eyebrow(T("Map to ICD-10", "Peta ke ICD-10")), Text(v.Map is { } m ? $"{m.Target.Code} {m.Target.DisplayFor(lang)} ({m.Equivalence})" : "—")), $"{CodeSystems.ShortName(c.System)} {c.Code}")
                    : Panel(null, Text(T("No concept selected.", "Tidak ada konsep dipilih."), 14, "muted"));
                return Stack(16,
                    Controls(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Text(T("Search (Enter)", "Cari (Enter)"), 13.5, "muted").Also(t => t.VerticalAlignment = VerticalAlignment.Center), box } }),
                    Columns([3, 2],
                        Panel(null, Table([T("System", "Sistem"), T("Code", "Kode"), T("Display", "Tampilan")], v.Hits.Select(h => new[] { CodeSystems.ShortName(h.Concept.System), h.Concept.Code, h.Concept.DisplayFor(lang) }), 1)),
                        detail));
            }
            case "assistant":
            {
                var st = S<ChatState>();
                if (st.Turns.Count == 0 && st.Assistant.Llm.Provider == "offline")
                    st.Turns.Add((Scenarios.SampleQuestions[0], st.Assistant.AskAsync(Scenarios.SampleQuestions[0]).GetAwaiter().GetResult()));
                var chat = new StackPanel { Spacing = 10 };
                foreach (var (q, r) in st.Turns)
                {
                    chat.Children.Add(new Border { Background = Brush("Ink"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), HorizontalAlignment = HorizontalAlignment.Right, Child = Text(q, 14.5, fg: Brush("Paper")) });
                    chat.Children.Add(new Border { Background = Brush("Paper"), BorderBrush = Brush("Rule"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8),
                        Child = Stack(6, Text(System.Text.RegularExpressions.Regex.Replace(r.Text.Replace("**", ""), @"(^|
)_|_($|
)", "$1$2"), 14), Mono($"{T("Tools", "Alat")}: {string.Join(", ", r.ToolCalls.Select(t => t.Name))}", 11.5).Also(t => t.Foreground = Brush("Teal"))) });
                }
                if (st.Busy) chat.Children.Add(Text(T("Working through the tools…", "Sedang memakai alat…"), 14, "muted"));
                var input = new TextBox { PlaceholderText = T("Ask about this patient…", "Tanyakan tentang pasien ini…"), MinWidth = 420 };
                var ask = new Button { Content = T("Ask", "Tanya"), Classes = { "primary" } };
                async void Ask(string q)
                {
                    if (string.IsNullOrWhiteSpace(q) || st.Busy) return;
                    st.Busy = true; rerender();
                    try { st.Turns.Add((q, await st.Assistant.AskAsync(q))); }
                    catch (Exception e) { st.Turns.Add((q, new AssistantReply(e.Message, [], st.Assistant.Llm.Provider, st.Assistant.Llm.Model))); }
                    finally { st.Busy = false; rerender(); }
                }
                ask.Click += (_, _) => Ask(input.Text ?? "");
                var chips = new StackPanel();
                foreach (var q in Scenarios.SampleQuestions) { var b = new Button { Content = q, Classes = { "chip" } }; b.Click += (_, _) => Ask(q); chips.Children.Add(b); }
                return Columns([2, 3],
                    Panel(st.Assistant.Patient!.Patient.Name.Full, Stack(8,
                        Text(T("Set ANTHROPIC_API_KEY, HF_TOKEN or MEDNET_LLM_* to use a live model; otherwise the offline engine answers through the same tools.",
                               "Atur ANTHROPIC_API_KEY, HF_TOKEN atau MEDNET_LLM_* untuk model langsung; tanpa itu mesin offline menjawab melalui alat yang sama."), 13, "muted"),
                        Eyebrow(T("Try", "Coba")), chips), $"{st.Assistant.Llm.Provider} · {st.Assistant.Llm.Model}"),
                    Panel(null, Stack(12, chat, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { input, ask } })));
            }
            case "risk":
            {
                var st = S<RiskState>();
                var v = Scenarios.Risk(st.Det);
                return Stack(16,
                    Controls(Slider(T("Clinical picture", "Gambaran klinis"), 0, 1, st.Det, 0.05, x => { st.Det = x; rerender(); }, x => $"{x * 100:0} %")),
                    Columns([1, 1],
                        Panel(T("48-hour deterioration", "Perburukan 48 jam"), Stack(6, Mono($"{v.Deterioration.Probability * 100:0.0} %", 34),
                            Pill(v.Deterioration.Band, v.Deterioration.Band.ToLowerInvariant()), Text(v.Deterioration.Interpretation[lang]),
                            Eyebrow(T("Drivers (log-odds)", "Faktor penentu (log-odds)")), SvgView(v.Drivers)), $"AUC {v.Metrics.Auc}"),
                        Panel(T("Undiagnosed type 2 diabetes", "DM tipe 2 belum terdiagnosis"), Stack(6, Mono($"{v.Diabetes.Probability * 100:0.0} %", 34),
                            Pill(v.Diabetes.Band, v.Diabetes.Band.ToLowerInvariant()), Text(v.Diabetes.Interpretation[lang]), SvgView(v.DiabetesDrivers)), $"AUC {v.DiabetesMetrics.Auc}")));
            }
            case "privacy":
            {
                var st = S<PrivacyState>();
                var v = Scenarios.Privacy(pseudonyms: st.Pseudo);
                return Stack(16,
                    Controls(Segmented([(false, T("Redact", "Sensor")), (true, T("Pseudonymise", "Pseudonim"))], st.Pseudo, x => { st.Pseudo = x; rerender(); }),
                        Text(string.Join(" · ", v.Counts.Select(kv => $"{kv.Key} {kv.Value}")), 13, "muted").Also(t => t.VerticalAlignment = VerticalAlignment.Center)),
                    Columns([1, 1], Panel(T("Clinical note", "Catatan klinis"), Text(v.Original)), Panel(T("Safe to share", "Aman dibagikan"), Text(v.Redacted))),
                    Panel(T("Display masking", "Penyamaran tampilan"), Table([T("Field", "Kolom"), T("Masked", "Disamarkan")], v.Masks.Select(m => new[] { m.Label, m.Value }), 1)));
            }
            case "emr":
            {
                var v = Scenarios.Emr().GetAwaiter().GetResult();
                return Columns([1, 1],
                    Panel(T($"{v.Search.Count} of {v.Patients} patients", $"{v.Search.Count} dari {v.Patients} pasien"),
                        Table(["MRN", T("Name", "Nama"), T("City", "Kota")], v.Search.Select(p => new[] { p.Mrn, p.Name.Full, p.City ?? "" }), 0), v.Provider),
                    Panel(T("Audit trail", "Jejak audit"), Table([T("User", "Pengguna"), T("Action", "Aksi"), T("Resource", "Sumber")],
                        v.Audit.Select(a => new[] { a.User, a.Action.ToString(), $"{a.Resource} {a.PatientId}" }))));
            }
            case "fhir-server":
            {
                var v = Scenarios.FhirServer();
                var checks = Stack(8, v.Validation.Select(r => Alert(r.Ok ? "info" : "critical", r.Profile, r.Resource,
                    r.Ok ? T("Conforms to the profile.", "Sesuai profil.") : string.Join("\n", r.Issues.Take(3)))).ToArray());
                return Columns([1, 1],
                    Panel(T("Search", "Pencarian"), Stack(10,
                        Table([T("Request", "Permintaan"), T("Match", "Cocok"), "Incl."], v.Queries.Select(q => new[] { $"{q.Query.Replace("&", "\n      &")}\n  → {q.Note}", $"{q.Total}", q.Included == 0 ? "—" : $"{q.Included}" }), 0, 1, 2),
                        Eyebrow("_history · Patient/pat-00002"),
                        Table(["vid", T("Method", "Metode"), "lastUpdated", T("Change", "Perubahan")], v.History.Select(h => new[] { h.Version, h.Method, h.LastUpdated, h.Change }), 0, 1, 2)),
                        $"{v.Stored} {T("resources stored", "resource tersimpan")}"),
                    Panel(T("Profile validation", "Validasi profil"), Stack(10, checks, Eyebrow("SMART on FHIR · scopes"),
                        Table(["scope", T("Meaning", "Arti")], v.Scopes.Select(sc => new[] { sc.Scope, sc.Normalised is { } n && n != sc.Scope ? $"{sc.Meaning[lang]}  → {n}" : sc.Meaning[lang] }), 0)), "Firely · hl7.fhir.r4.core"));
            }
            case "hl7-orders":
            {
                var st = S<OrderState>();
                var v = Scenarios.Hl7Orders(st.Type);
                var c = st.Broken ? v.Broken : v.Conformance;
                var issues = c.Issues.Count == 0
                    ? Alert("info", "OK", T("All segments and fields satisfy the profile.", "Semua segmen dan field memenuhi profil."), T("Usage, cardinality, length and HL7 tables checked.", "Usage, kardinalitas, panjang dan tabel HL7 diperiksa."))
                    : Stack(6, c.Issues.Select(i => Alert(i.Severity == Medical.Hl7.Conformance.ConformanceSeverity.Error ? "critical" : "warning", i.Location, i.Message, "")).ToArray());
                return Stack(16,
                    Controls(Segmented([("ORM", "ORM^O01"), ("OML", "OML^O21"), ("SIU", "SIU^S12"), ("MDM", "MDM^T02")], st.Type, x => { st.Type = x; rerender(); }),
                        Segmented([(false, T("As built", "Sesuai bentukan")), (true, T("Broken", "Dirusak"))], st.Broken, x => { st.Broken = x; rerender(); })),
                    Columns([1, 1],
                        Panel($"{c.Profile} · HL7 v2.5.1", Mono(st.Broken ? v.BrokenEr7 : v.Er7, 11)),
                        Panel(T("Conformance", "Kesesuaian"), Stack(10, issues, Eyebrow(T("Mapped to FHIR", "Dipetakan ke FHIR")),
                            Table(["Resource", T("Summary", "Ringkasan")], v.Fhir.Select(f => new[] { f.Resource, f.Summary }), 0)),
                            c.IsValid ? T("conformant", "sesuai") : T("not conformant", "tidak sesuai"))));
            }
            case "satusehat":
            {
                var v = Scenarios.SatuSehat();
                return Columns([3, 2],
                    Panel(T("Transaction bundle", "Bundle transaksi"), Stack(10,
                        Table(["Resource", T("References", "Referensi"), T("Content", "Isi")], v.Entries.Select(e => new[] { e.Resource, e.Links.Replace(", ", "\n"), e.Summary }), 0, 1),
                        Stack(6, v.Warnings.Select(w => Alert("warning", "KFA", w, "")).ToArray())),
                        $"{v.Entries.Count} {T("entries", "entri")} · {v.Medications} {T("KFA medications", "obat KFA")}"),
                    Panel("POST /fhir-r4/v1", Mono(v.Json[..Math.Min(2600, v.Json.Length)] + "\n…", 11), "Bearer · RetryHandler"));
            }
            case "bpjs":
            {
                var st = S<ClaimState>();
                var v = Scenarios.Bpjs(broken: st.Broken);
                var idr = System.Globalization.CultureInfo.GetCultureInfo("id-ID");
                int errors = v.Issues.Count(i => i.Severity == Medical.Bpjs.InaCbg.ClaimIssueSeverity.Error);
                var checks = v.Issues.Count == 0
                    ? Alert("info", "OK", T("Ready for the INA-CBG grouper.", "Siap untuk grouper INA-CBG."), "SEP, ICD-10 / ICD-9-CM, LOS, tarif")
                    : Stack(6, v.Issues.Select(i => Alert(i.Severity == Medical.Bpjs.InaCbg.ClaimIssueSeverity.Error ? "critical" : "warning", i.Field, i.Message[lang], "")).ToArray());
                return Stack(16,
                    Controls(Segmented([(false, T("Clean claim", "Klaim bersih")), (true, T("Coding errors", "Kesalahan koding"))], st.Broken, x => { st.Broken = x; rerender(); })),
                    Columns([1, 1],
                        Panel("E-Klaim · set_claim_data", Stack(10,
                            Table([T("Field", "Kolom"), T("Value", "Nilai")], v.Data.Select(d => new[] { d.Field, d.Value }), 0, 1),
                            Eyebrow($"tarif_rs · Rp {v.Claim.Tariff.Total.ToString("N0", idr)}"),
                            Table([T("Component", "Komponen"), "Rp"], v.Tariff.Select(t => new[] { t.Component, t.Amount.ToString("N0", idr) }), 0, 1)),
                            $"LOS {v.Claim.LengthOfStay} {T("days", "hari")}"),
                        Panel(T("Pre-grouping checks", "Pemeriksaan sebelum grouping"), Stack(10, checks,
                            Eyebrow(T("VClaim request headers (demo credentials)", "Header VClaim (kredensial demo)")),
                            Table(["Header", T("Value", "Nilai")], v.Headers.Select(h => new[] { h.Header, h.Value }), 0, 1),
                            Eyebrow($"LZ-String · {v.PlainLength} → {v.Compressed.Length}"), Mono(v.Compressed, 11)),
                            $"{errors} {T("errors", "galat")}")));
            }
            case "mpr":
            {
                var st = S<MprState>();
                var v = Scenarios.Mpr(st.Chest, st.Pos, st.Mode, st.Slab);
                string Plane(string p) => p switch { "Axial" => T("Axial", "Aksial"), "Coronal" => T("Coronal", "Koronal"), _ => T("Sagittal", "Sagital") };
                Control Img(PlaneImage p, double w) => new Image { Source = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(p.Png)), Stretch = Stretch.Uniform, MaxWidth = w, MaxHeight = w };
                return Stack(16,
                    Controls(Segmented([(false, T("Head CT", "CT kepala")), (true, T("Chest CT", "CT toraks"))], st.Chest, x => { st.Chest = x; rerender(); }),
                        Slider(T("Position", "Posisi"), 0.05, 0.95, st.Pos, 0.05, x => { st.Pos = x; rerender(); }, x => $"{x * 100:0} %"),
                        Segmented([(Medical.Native.SlabMode.Max, "MIP"), (Medical.Native.SlabMode.Min, "MinIP"), (Medical.Native.SlabMode.Mean, "AvgIP")], st.Mode, x => { st.Mode = x; rerender(); }),
                        Slider(T("Slab", "Slab"), 5, 120, st.Slab, 5, x => { st.Slab = x; rerender(); }, x => $"{x:0} mm")),
                    Columns([1, 1, 1], v.Planes.Select(p => (Control)Panel(Plane(p.Label), Stack(6, Img(p, 330), Mono(p.Description, 11)))).ToArray()),
                    Columns([3, 2],
                        Panel(v.Slab.Label, Stack(6, Img(v.Slab, 520), Mono(v.Slab.Description, 11)), T("slab projection", "proyeksi slab")),
                        Panel(T("Volume", "Volume"), Stack(8,
                            Table([T("Item", "Butir"), T("Value", "Nilai")], [["Grid", v.Volume], ["Kernel", $"{v.Backend} · mdn_slab_i16"], [T("Compute", "Komputasi"), $"{v.Ms} ms"]], 1),
                            Text(T("Square output pixels keep thick-slice reformats in proportion; projections along an acquisition axis sample each voxel layer once.",
                                   "Piksel keluaran persegi menjaga proporsi rekonstruksi irisan tebal; proyeksi sepanjang sumbu akuisisi mengambil setiap lapis voksel sekali."), 13, "muted")))));
            }
            case "volume3d":
            {
                var v = Scenarios.Volume3D();
                Control view = Program.Headless
                    ? (Volume3D.RenderPng(v) is { } png ? new Image { Source = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(png)), Stretch = Stretch.Uniform, MaxHeight = 460 } : Text(T("No GPU adapter for ThreeNet.", "Tidak ada adaptor GPU untuk ThreeNet."), 14, "muted"))
                    : Volume3D.CreateView(v) ?? Text(T("ThreeNet needs a GPU adapter (Vulkan / Metal / DirectX 12).", "ThreeNet memerlukan adaptor GPU (Vulkan / Metal / DirectX 12)."), 14, "muted");
                return Columns([3, 2],
                    Panel(T("Skull and haemorrhage", "Tengkorak dan perdarahan"), view, $"ThreeNet · {T("drag to orbit", "seret untuk memutar")}"),
                    Panel(T("Meshes", "Mesh"), Stack(10,
                        Table([T("Surface", "Permukaan"), T("Triangles", "Segitiga"), "mL"],
                            [[T("Bone (300 HU)", "Tulang (300 HU)"), v.Skull.Triangles.ToString("N0"), $"{v.Skull.VolumeMl}"], [T("Haemorrhage", "Perdarahan"), v.Lesion.Triangles.ToString("N0"), $"{v.LesionMl}"]], 1, 2),
                        Mono($"{v.Volume}\nsurface nets · {v.ExtractMs} ms · STL {v.StlBytes / 1024} KB", 11.5),
                        Text(T("One vertex per boundary cell, no case tables, a closed mesh; Taubin smoothing for display. The web gallery shows the same meshes with three.js.",
                               "Satu verteks per sel batas, tanpa tabel kasus, mesh tertutup; penghalusan Taubin untuk tampilan. Galeri web menampilkan mesh yang sama dengan three.js."), 13, "muted"))));
            }
            case "codecs":
            {
                var rows = Scenarios.Codecs(14);
                return Columns([3, 2],
                    Panel(T("Transfer syntaxes", "Sintaks transfer"), Table([T("Syntax", "Sintaks"), T("Bytes", "Byte"), T("Ratio", "Rasio"), T("Max error", "Galat maks")],
                        new[] { new[] { "Explicit VR Little Endian", "131,072", "1.0 : 1", "—" } }.Concat(rows.Select(r => new[] { r.Name, r.Bytes.ToString("N0"), $"{r.Ratio} : 1", r.Lossless ? T("0 (bit-exact)", "0 (identik)") : $"{r.MaxError} HU" })), 1, 2, 3),
                        T("head CT slice 15, 256 × 256 × 16 bit", "CT kepala irisan 15, 256 × 256 × 16 bit")),
                    Panel(T("How it works", "Cara kerja"), Text(T("fo-dicom.Codecs (libijg, CharLS, OpenJPEG, OpenJPH) for Windows / Linux / macOS on x64 and Arm64. Compressed pixel data is decoded on demand, so rendering, volumes, MPR and AI read compressed studies directly.",
                        "fo-dicom.Codecs (libijg, CharLS, OpenJPEG, OpenJPH) untuk Windows / Linux / macOS di x64 dan Arm64. Piksel terkompresi didekode sesuai kebutuhan, sehingga render, volume, MPR dan AI membaca studi terkompresi langsung."), 13.5)));
            }
            case "imaging-ai":
            {
                var st = S<AiState>();
                var v = Scenarios.ImagingAi(st.Chest);
                return Stack(16,
                    Controls(Segmented([(false, T("Head CT · haemorrhage", "CT kepala · perdarahan")), (true, T("Chest CT · lung nodules", "CT toraks · nodul paru"))], st.Chest, x => { st.Chest = x; rerender(); })),
                    Columns([1, 1],
                        Panel(T("Read back from the DICOM SEG", "Dibaca ulang dari DICOM SEG"), Stack(8, Png(v.Overlay, 440), Text(v.Impression[lang], 13.5)), $"{v.SegFrames} frames · {v.SegBytes / 1024} KB"),
                        Panel(T("Findings", "Temuan"), Stack(8,
                            Stack(6, v.Findings.Select(f => Alert(f.Category.Contains('4') ? "critical" : "warning", f.Category, f.Label, f.Detail)).ToArray()),
                            Eyebrow($"TID 1500 Measurement Report · {v.SrBytes / 1024} KB"), Mono(v.SrTree, 10.5)), v.Method)));
            }
            case "ecg12":
            {
                var st = S<Ecg12State>();
                var v = Scenarios.Ecg12(st.Pattern, st.Hr, st.Qtc, st.Af, st.Female ? Gender.Female : Gender.Male, 58, lang);
                var a = v.Analysis;
                return Stack(16,
                    Controls(Segmented([(Medical.Signals.StPattern.None, "Normal"), (Medical.Signals.StPattern.InferiorStemi, T("Inferior STEMI", "STEMI inferior")),
                            (Medical.Signals.StPattern.AnteriorStemi, T("Anterior STEMI", "STEMI anterior")), (Medical.Signals.StPattern.LateralStemi, T("Lateral STEMI", "STEMI lateral")),
                            (Medical.Signals.StPattern.DiffuseDepression, T("Diffuse ST↓", "ST↓ difus"))], st.Pattern, x => { st.Pattern = x; rerender(); }),
                        Slider(T("Rate", "Frekuensi"), 45, 130, st.Hr, 5, x => { st.Hr = x; rerender(); }, x => $"{x:0} bpm"),
                        Slider("QTc", 360, 540, st.Qtc, 10, x => { st.Qtc = x; rerender(); }, x => $"{x:0} ms"),
                        Segmented([(false, T("Sinus", "Sinus")), (true, T("AF", "FA"))], st.Af, x => { st.Af = x; rerender(); }),
                        Segmented([(false, T("Male", "Laki-laki")), (true, T("Female", "Perempuan"))], st.Female, x => { st.Female = x; rerender(); })),
                    new Border { Classes = { "panel" }, Padding = new Thickness(0), ClipToBounds = true, Child = SvgView(v.Grid) },
                    Columns([2, 3],
                        Panel(T("Measurements", "Pengukuran"), Stack(8,
                            Table([T("Interval", "Interval"), "ms"], [[T("Rate", "Frekuensi"), $"{a.HeartRateBpm} bpm"], ["PR", a.PrMs is { } pr ? $"{pr}" : "—"], ["QRS", $"{a.QrsMs}"], ["QT", $"{a.QtMs}"],
                                ["QTc Bazett", $"{a.QtcBazettMs}"], ["QTc Fridericia", $"{a.QtcFridericiaMs}"], ["QTc Framingham", $"{a.QtcFraminghamMs}"], ["QTc Hodges", $"{a.QtcHodgesMs}"],
                                [T("Axis", "Aksis"), $"{a.QrsAxisDeg}°"]], 1),
                            Alert(a.MeetsStemiCriteria || a.Qtc >= Medical.Signals.QtcCategory.MarkedlyProlonged ? "critical" : a.StDepressionLeads.Count > 0 ? "warning" : "info",
                                a.MeetsStemiCriteria ? "STEMI · " + string.Join(", ", a.StemiTerritories) : Scenarios.RhythmName(a.Rhythm)[lang], T("Interpretation", "Interpretasi"), a.Interpretation[lang])), "median beat"),
                        Panel(T("Median beats and fiducials", "Denyut median dan titik acuan"), SvgView(v.Medians))));
            }
            case "icu-scores":
            {
                var st = S<IcuState>();
                var v = Scenarios.Icu(st.Sev);
                Control Small(ScoreResult s) => Panel(s.Name, Stack(6, Mono($"{(s.ExactScore is { } e ? e.ToString("0.#") : s.Score.ToString())} / {(s.ExactScore is null ? s.MaxScore : s.MaxScore / 2.0)}", 22),
                    Pill(Scenarios.RiskName(s.Risk)[lang], s.Risk.ToString().ToLowerInvariant()), Text(s.Interpretation[lang], 13.5)));
                return Stack(16,
                    Controls(Slider(T("Severity of sepsis", "Keparahan sepsis"), 0, 1, st.Sev, 0.05, x => { st.Sev = x; rerender(); }, x => $"{x * 100:0} %")),
                    Columns([2, 3],
                        Panel($"SOFA · {v.Sofa.Score}/24", Stack(8, SvgView(v.SofaBars), Pill(Scenarios.RiskName(v.Sofa.Risk)[lang], v.Sofa.Risk.ToString().ToLowerInvariant()), Text(v.Sofa.Interpretation[lang]))),
                        Panel($"APACHE II · {v.Apache.Score}/71", Stack(8, Text(v.Apache.Interpretation[lang]),
                            Table([T("Variable", "Variabel"), T("Worst value", "Nilai terburuk"), T("Points", "Poin")], v.Apache.Components.Where(c => c.Points > 0).Select(c => new[] { c.Parameter, c.Value, $"+{c.Points}" }), 1, 2)))),
                    Columns([1, 1, 1, 1], Small(v.Pews), Small(v.Meows), Small(v.WellsPe), Small(v.Perc)));
            }
            case "cvrisk":
            {
                var st = S<CvState>();
                var v = Scenarios.CvRisk(st.Age, st.Female, st.Sbp, st.Tc, st.Hdl, false, st.Smoker, st.Diabetes);
                return Stack(16,
                    Controls(Slider(T("Age", "Usia"), 40, 79, st.Age, 1, x => { st.Age = (int)x; rerender(); }, x => $"{x:0}"),
                        Slider(T("Systolic BP", "TD sistolik"), 100, 190, st.Sbp, 2, x => { st.Sbp = x; rerender(); }, x => $"{x:0}"),
                        Slider(T("Total chol.", "Kolesterol"), 140, 300, st.Tc, 5, x => { st.Tc = x; rerender(); }, x => $"{x:0}"),
                        Segmented([(false, T("Male", "Laki-laki")), (true, T("Female", "Perempuan"))], st.Female, x => { st.Female = x; rerender(); }),
                        Segmented([(false, T("Non-smoker", "Bukan perokok")), (true, T("Smoker", "Perokok"))], st.Smoker, x => { st.Smoker = x; rerender(); }),
                        Segmented([(false, T("No diabetes", "Tanpa diabetes")), (true, T("Diabetes", "Diabetes"))], st.Diabetes, x => { st.Diabetes = x; rerender(); })),
                    Columns([1, 1],
                        Readout("ASCVD · Pooled Cohort Equations", $"{v.Pce.TenYearRiskPercent:0.0}", "%", v.Pce.Category[lang]),
                        Readout("CVD · Framingham 2008", $"{v.Framingham.TenYearRiskPercent:0.0}", "%", v.Framingham.Category[lang])),
                    Panel(T("Risk across age for this profile", "Risiko menurut usia untuk profil ini"), SvgView(v.ByAge), string.Join(" ", v.Pce.Warnings.Take(1))));
            }
            case "growth":
            {
                var st = S<GrowthState>();
                var v = Scenarios.Growth(st.Indicator, lang).GetAwaiter().GetResult();
                if (v.Error is { } err) return Alert("warning", "WHO", T("WHO tables unavailable", "Tabel WHO tidak tersedia"), err);
                var last = v.Visits[^1];
                return Stack(16,
                    Controls(Segmented([(GrowthIndicator.LengthHeightForAge, T("Length-for-age", "PB/U")), (GrowthIndicator.WeightForAge, T("Weight-for-age", "BB/U"))], st.Indicator, x => { st.Indicator = x; rerender(); })),
                    Columns([3, 2],
                        Panel($"Siti · {T("girl, 0–24 months", "perempuan, 0–24 bulan")}", SvgView(v.Chart!), "WHO Child Growth Standards 2006"),
                        Panel(T("Visits", "Kunjungan"), Stack(8,
                            Table([T("Mo", "Bln"), T("Length", "PB"), "z", T("Weight", "BB"), "z", "BB/PB z"], v.Visits.Select(x => new[] { $"{x.Months}", $"{x.LengthCm:0.0}", $"{x.LengthForAge.ZScore:+0.00;-0.00}", $"{x.WeightKg:0.0}", $"{x.WeightForAge.ZScore:+0.00;-0.00}", $"{x.WeightForLength.ZScore:+0.00;-0.00}" }), 0, 1, 2, 3, 4, 5),
                            Pill(last.LengthForAge.Classification[lang], last.LengthForAge.ZScore < -2 ? "high" : "low"),
                            Text(T("Tables are downloaded from WHO at first use and cached, not redistributed.", "Tabel diunduh dari WHO saat pertama dipakai dan di-cache, tidak didistribusikan ulang."), 13, "muted")))));
            }
            case "drug-safety":
            {
                var st = S<SafetyState>();
                var v = Scenarios.DrugSafety(st.Cr, st.ChildPugh).GetAwaiter().GetResult();
                var alerts = Stack(0, v.Interactions.Select(f => Alert(f.Severity == Medical.Rules.InteractionSeverity.Moderate ? "warning" : "critical", $"{f.Severity} · {f.Source}", $"{f.DrugA} + {f.DrugB}", f.Effect[lang])).ToArray());
                return Stack(16,
                    Controls(Slider(T("Creatinine", "Kreatinin"), 0.6, 4, st.Cr, 0.1, x => { st.Cr = x; rerender(); }, x => $"{x:0.0} mg/dL"),
                        Segmented([("A", "Child-Pugh A"), ("B", "B"), ("C", "C")], st.ChildPugh, x => { st.ChildPugh = x; rerender(); })),
                    Columns([1, 1, 1],
                        Readout("CrCl · Cockcroft-Gault", $"{v.CrCl:0}", "mL/min", T("80 y, 55 kg, female", "80 th, 55 kg, perempuan")),
                        Readout("eGFR · CKD-EPI 2021", $"{v.Egfr:0}", "mL/min/1.73 m²", T("metformin uses eGFR", "metformin memakai eGFR")),
                        Readout("apixaban (AF)", v.Apixaban.Action == Medical.Clinical.DoseAction.Reduce ? "2.5" : "5", "mg × 2", v.Apixaban.Basis)),
                    Columns([1, 1],
                        Panel(T($"{v.Interactions.Count} interaction findings", $"{v.Interactions.Count} temuan interaksi"), alerts, string.Join(" · ", v.Medications)),
                        Panel(T("Renal & hepatic dosing", "Dosis ginjal & hati"), Stack(10,
                            Table([T("Drug", "Obat"), T("Action", "Tindakan"), T("Advice", "Anjuran")], v.Renal.Select(r => new[] { $"{r.Generic}\n{r.Basis}", r.Action.ToString(), r.Advice[lang] }), 1),
                            Eyebrow($"Child-Pugh {v.ChildPugh}"),
                            Table([T("Drug", "Obat"), T("Action", "Tindakan"), T("Advice", "Anjuran")], v.Hepatic.Select(h => new[] { h.Generic, h.Action.ToString(), h.Advice[lang] }), 1)))));
            }
            case "clinical-nlp":
            {
                var st = S<NlpState>();
                var v = Scenarios.Nlp(Scenarios.SampleNotes[st.Lang ?? lang]);
                var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15.5, LineHeight = 28 };
                foreach (var s in v.Segments)
                {
                    var run = new Avalonia.Controls.Documents.Run(s.Text);
                    if (s.Entity is { } e)
                    {
                        run.Background = e.Negated || e.FamilyHistory ? null : new SolidColorBrush(Color.Parse(e.Type switch
                        {
                            Medical.AI.Nlp.ClinicalEntityType.Problem => "#F2C2BD", Medical.AI.Nlp.ClinicalEntityType.Symptom => "#FBE8B4", _ => "#CFE5E3",
                        }));
                        if (e.Negated) { run.TextDecorations = TextDecorations.Strikethrough; run.Foreground = Brush("Muted"); }
                        if (e.FamilyHistory) { run.TextDecorations = TextDecorations.Underline; run.Foreground = Brush("Amber"); }
                    }
                    text.Inlines!.Add(run);
                }
                var codes = Stack(0, v.Codes.Select(c => Alert(c.Code.StartsWith('R') ? "warning" : "info", $"{c.Code} · {c.Score:0.00}", c.DisplayFor(lang), $"{c.Note[lang]}  ← {string.Join("; ", c.Evidence)}")).ToArray());
                return Stack(16,
                    Controls(Segmented([("id", T("Indonesian note", "Catatan Indonesia")), ("en", T("English note", "Catatan Inggris"))], st.Lang ?? lang, x => { st.Lang = x; rerender(); }),
                        Text(T("highlight: problem · symptom · drug — struck through = negated, underlined = family history", "sorotan: masalah · gejala · obat — dicoret = negasi, garis bawah = riwayat keluarga"), 13, "muted")),
                    Columns([3, 2],
                        Panel(T("Clinical note", "Catatan klinis"), Stack(10, text,
                            Table([T("Entity", "Entitas"), T("Type", "Jenis"), T("Code", "Kode"), T("Context", "Konteks")], v.Entities.Select(e => new[] { e.Text, e.Type.ToString(), e.Codes.Count > 0 ? e.Codes[0].Code : "—", e.Negated ? T("negated", "negasi") : e.FamilyHistory ? T("family", "keluarga") : T("affirmed", "positif") }), 2))),
                        Panel(T("ICD-10 suggestions", "Saran ICD-10"), codes, T("for a coder to confirm", "untuk dikonfirmasi koder"))));
            }
            case "guidelines":
            {
                var st = S<GuideState>();
                if (st.View is null && !st.Busy) st.View = Scenarios.Guidelines(st.Question, lang, st.Llm).GetAwaiter().GetResult();
                async void Ask(string q)
                {
                    st.Question = q; st.Busy = true; rerender();
                    try { st.View = await Scenarios.Guidelines(q, lang, st.Llm); }
                    finally { st.Busy = false; rerender(); }
                }
                var chips = new StackPanel();
                foreach (var q in Scenarios.GuidelineQuestions) { var b = new Button { Content = q, Classes = { "chip" } }; b.Click += (_, _) => Ask(q); chips.Children.Add(b); }
                Control right = st.Busy || st.View is null ? Text(T("Retrieving…", "Mencari…"), 14, "muted") : Stack(10,
                    Text(st.View.Answer.Text, 15.5),
                    Eyebrow(T("Citations", "Sitasi")),
                    Text(string.Join("\n", st.View.Answer.Citations.Select(c => $"[{c.Number}] {c.Chunk.Citation} ({c.Chunk.Source})")), 13.5),
                    Eyebrow(T("Retrieved passages (reciprocal rank fusion)", "Pasase yang ditemukan (reciprocal rank fusion)")),
                    Table(["#", T("Passage", "Pasase"), "BM25", T("Score", "Skor")], st.View.Passages.Select((p, i) => new[] { $"{i + 1}", $"{(p.Chunk.Citation.Length > 70 ? p.Chunk.Citation[..70] + "…" : p.Chunk.Citation)} [{p.Chunk.Language}]", p.LexicalRank?.ToString() ?? "—", $"{p.Score:0.0000}" }), 0, 2, 3));
                return Columns([2, 3],
                    Panel(T("Ask the guidelines", "Tanya pedoman"), Stack(8, chips,
                        Text(T("Set ANTHROPIC_API_KEY or MEDNET_LLM_* for generated answers; offline, the best sentences are quoted.", "Atur ANTHROPIC_API_KEY atau MEDNET_LLM_* untuk jawaban generatif; saat offline, kalimat terbaik dikutip."), 13, "muted"))),
                    Panel(st.Question, right, st.View?.Answer.Mode));
            }
            case "federated":
            {
                var st = S<FedState>();
                var v = Scenarios.Federated(st.Noise, st.Shift, st.Secure);
                return Stack(16,
                    Controls(Slider(T("DP noise σ", "Derau DP σ"), 0, 3, st.Noise, 0.25, x => { st.Noise = x; rerender(); }, x => $"{x:0.00}"),
                        Slider(T("Population shift", "Pergeseran populasi"), 0, 1.5, st.Shift, 0.1, x => { st.Shift = x; rerender(); }, x => $"{x:0.0}"),
                        Segmented([(true, T("Secure aggregation", "Agregasi aman")), (false, T("Plain", "Biasa"))], st.Secure, x => { st.Secure = x; rerender(); })),
                    Columns([1, 1, 1],
                        Readout(T("Federated AUC", "AUC federated"), $"{v.Federated.Auc:0.000}", null, $"{T("pooled", "gabungan")} {v.Pooled.Auc:0.000}"),
                        Readout(T("Privacy budget", "Anggaran privasi"), double.IsInfinity(v.Result.Epsilon()) ? "ε = ∞" : $"ε = {v.Result.Epsilon():0.0}", null, T("site-level, δ = 10⁻⁵", "tingkat situs, δ = 10⁻⁵")),
                        Readout(T("Calibration", "Kalibrasi"), $"{v.Federated.Ece:0.000}", "ECE", $"Brier {v.Federated.Brier:0.000}")),
                    Columns([1, 1],
                        Panel(T("Sites", "Situs"), Stack(8,
                            Table([T("Site", "Situs"), T("Rows (local)", "Baris (lokal)"), T("Event rate", "Laju kejadian")], v.Sites.Select(s => new[] { s.Site, $"{s.Rows}", $"{s.Prevalence * 100:0.0} %" }), 1, 2),
                            Eyebrow(T("Training loss by round", "Loss per putaran")), SvgView(v.LossChart),
                            Eyebrow(T("Privacy–utility trade-off", "Kompromi privasi–utilitas")), SvgView(v.PrivacyChart))),
                        Panel(T("Model card & drift", "Model card & drift"), Stack(8,
                            Table([T("Group", "Grup"), "n", "AUC", "ECE"], new[] { new[] { T("Overall", "Keseluruhan"), $"{v.Card.Overall.N}", $"{v.Card.Overall.Auc:0.000}", $"{v.Card.Overall.Ece:0.000}" } }
                                .Concat(v.Card.Subgroups.Select(s => new[] { $"{s.Attribute} = {s.Group}", $"{s.Metrics.N}", $"{s.Metrics.Auc:0.000}", $"{s.Metrics.Ece:0.000}" })), 1, 2, 3),
                            Eyebrow(T("Drift since deployment", "Drift sejak penerapan")),
                            Table([T("Feature", "Fitur"), "PSI", "KS", T("Level", "Tingkat")], v.Drift.Features.Append(v.Drift.Prediction!).Select(f => new[] { f.Feature, $"{f.Psi:0.000}", $"{f.KsStatistic:0.000}", f.Level.ToString() }), 1, 2),
                            Stack(0, v.Drift.Alerts.Select(a => Alert("warning", "drift", a, "")).ToArray())))));
            }
            default:
                return Text(T("Pick a use case from the list.", "Pilih kasus dari daftar."));
        }
    }
}
