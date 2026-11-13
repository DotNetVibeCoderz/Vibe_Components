using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Medical.AI.Assistant;
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
            default:
                return Text(T("Pick a use case from the list.", "Pilih kasus dari daftar."));
        }
    }
}
