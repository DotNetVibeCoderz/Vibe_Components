using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Medical.Core;

namespace Medical.AI.Assistant;

/// <summary>
/// Deterministic, network-free backend: routes bilingual keywords to the same tools an LLM would call and renders a
/// structured answer. Used when no API key is configured, in tests, and as a safe fallback.
/// </summary>
public sealed partial class OfflineClinicalLlm : IClinicalLlm
{
    public string Provider => "offline";
    public string Model => "rule-based";

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*kg", RegexOptions.IgnoreCase)] private static partial Regex WeightRx();
    [GeneratedRegex(@"\b([A-Z]\d{2}(?:\.\d{1,2})?)\b")] private static partial Regex IcdRx();

    private static readonly string[] IdWords = ["berapa", "apa", "bagaimana", "pasien", "dosis", "ginjal", "obat", "interaksi", "ringkas", "skor", "kode", "untuk", "yang", "ini"];

    public Task<AssistantReply> RunAsync(string system, IReadOnlyList<ChatMessage> history, ClinicalTools tools, PatientRecord? patient, CancellationToken ct = default)
    {
        string q = history.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        string lower = q.ToLowerInvariant();
        string lang = IdWords.Count(w => Regex.IsMatch(lower, $@"\b{w}\b")) >= 1 ? "id" : "en";
        var calls = new List<ToolCallRecord>();
        var sb = new StringBuilder();

        bool Has(params string[] words) => words.Any(w => lower.Contains(w, StringComparison.Ordinal));
        JsonNode Call(string name, JsonObject input)
        {
            var r = tools.Execute(name, input.ToJsonString(), patient);
            calls.Add(r);
            return JsonNode.Parse(r.Output)!;
        }

        if (Has("interact", "interaksi", "aman", "safe", "combine", "kombinasi"))
        {
            var drugs = DrugsIn(lower, tools, patient);
            var res = Call("check_drug_safety", new JsonObject
            {
                ["drugs"] = new JsonArray(drugs.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
                ["allergies"] = new JsonArray((patient?.Allergies.Select(a => a.Substance) ?? []).Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            });
            var findings = res["findings"]!.AsArray();
            sb.AppendLine(lang == "id" ? $"**Telaah keamanan obat** ({string.Join(", ", drugs)}):" : $"**Medication safety review** ({string.Join(", ", drugs)}):");
            if (findings.Count == 0) sb.AppendLine(lang == "id" ? "- Tidak ditemukan interaksi pada basis pengetahuan." : "- No interactions found in the knowledge base.");
            foreach (var f in findings)
                sb.AppendLine($"- [{f!["severity"] ?? f["type"]}] {f["drugs"] ?? $"{f["allergy"]} → {f["drug"]}"}: {f[lang == "id" ? "effect_id" : "effect_en"] ?? (lang == "id" ? $"golongan sama ({f["class"]})" : $"same class ({f["class"]})")}");
        }
        else if (Has("dose", "dosis", "dosage") || WeightRx().IsMatch(q))
        {
            string? drug = Medical.Clinical.DoseCalculator.Rules.Keys.FirstOrDefault(k => lower.Contains(k)) ?? (lower.Contains("parasetamol") ? "paracetamol" : lower.Contains("amoksisilin") ? "amoxicillin" : null);
            double? weight = WeightRx().Match(q) is { Success: true } m ? double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : patient?.WeightKg;
            if (drug is null || weight is null)
                sb.AppendLine(lang == "id" ? "Sebutkan nama obat dan berat badan (mis. \"dosis parasetamol 18 kg\")." : "Please give the drug and body weight (e.g. \"paracetamol dose 18 kg\").");
            else
            {
                var r = Call("weight_based_dose", new JsonObject { ["drug"] = drug, ["weight_kg"] = weight });
                sb.AppendLine(lang == "id"
                    ? $"**Dosis {drug}** untuk {weight:0.#} kg: {r["single_dose_mg"]} mg × {r["doses_per_day"]}/hari (total {r["daily_dose_mg"]} mg/hari){(r["capped"]!.GetValue<bool>() ? ", dibatasi dosis maksimum" : "")}. {r["note_id"]}"
                    : $"**{drug} dose** for {weight:0.#} kg: {r["single_dose_mg"]} mg × {r["doses_per_day"]}/day (total {r["daily_dose_mg"]} mg/day){(r["capped"]!.GetValue<bool>() ? ", capped at the maximum" : "")}. {r["note_en"]}");
            }
        }
        else if (Has("egfr", "ginjal", "kidney", "renal", "kreatinin", "creatinine"))
        {
            double? cr = patient?.Latest(Loinc.Creatinine)?.Value;
            if (cr is null || patient is null) sb.AppendLine(lang == "id" ? "Data kreatinin pasien tidak tersedia." : "No creatinine result available for this patient.");
            else
            {
                int age = patient.Patient.AgeAt(DateOnly.FromDateTime(patient.LatestVitals?.Timestamp.UtcDateTime ?? DateTime.UtcNow));
                var r = Call("calculate_egfr", new JsonObject { ["creatinine_mg_dl"] = cr, ["age"] = age, ["sex"] = patient.Patient.Gender == Gender.Female ? "female" : "male" });
                sb.AppendLine(lang == "id"
                    ? $"**eGFR (CKD-EPI 2021)**: {r["egfr_ml_min_1_73m2"]} mL/menit/1,73 m² — stadium {r["stage"]} ({r["label_id"]}). Kreatinin {cr:0.00} mg/dL, usia {age}."
                    : $"**eGFR (CKD-EPI 2021)**: {r["egfr_ml_min_1_73m2"]} mL/min/1.73 m² — stage {r["stage"]} ({r["label_en"]}). Creatinine {cr:0.00} mg/dL, age {age}.");
            }
        }
        else if (Has("icd", "snomed", "loinc", "kode", "code"))
        {
            string term = IcdRx().Match(q) is { Success: true } m ? m.Value : Regex.Replace(lower, @"\b(kode|code|icd-?10|snomed|loinc|untuk|for|apa|what|is|the|of|cari|search)\b", " ").Trim();
            var hits = Call("lookup_terminology", new JsonObject { ["query"] = term, ["system"] = Has("snomed") ? "snomed" : Has("loinc") ? "loinc" : Has("icd") ? "icd10" : "any" }).AsArray();
            sb.AppendLine(lang == "id" ? $"**Hasil terminologi** untuk \"{term}\":" : $"**Terminology results** for \"{term}\":");
            foreach (var h in hits.Take(5)) sb.AppendLine($"- {h!["system"]} `{h["code"]}` — {(lang == "id" ? h["display_id"] ?? h["display"] : h["display"])}");
            if (hits.Count == 0) sb.AppendLine(lang == "id" ? "- Tidak ditemukan." : "- No match.");
        }
        else if (patient is not null && Has("news", "early warning", "peringatan", "vital", "summary", "ringkas", "pasien", "patient", "status", "kondisi", "sepsis", "plan", "rencana"))
        {
            var s = Call("get_patient_summary", []);
            var v = s["latest_vitals"];
            sb.AppendLine(lang == "id"
                ? $"**Ringkasan {s["patient"]!["first_name"]}** ({s["patient"]!["age"]} th, {s["patient"]!["sex"]})"
                : $"**Summary for {s["patient"]!["first_name"]}** ({s["patient"]!["age"]} y, {s["patient"]!["sex"]})");
            if (v is not null)
                sb.AppendLine(lang == "id"
                    ? $"- Tanda vital: HR {v["hr"]}, RR {v["rr"]}, TD {v["sbp"]}/{v["dbp"]}, suhu {v["temp_c"]} °C, SpO₂ {v["spo2"]}% — **NEWS2 {v["news2"]} ({v["news2_risk"]})**"
                    : $"- Vitals: HR {v["hr"]}, RR {v["rr"]}, BP {v["sbp"]}/{v["dbp"]}, temp {v["temp_c"]} °C, SpO₂ {v["spo2"]}% — **NEWS2 {v["news2"]} ({v["news2_risk"]})**");
            sb.AppendLine((lang == "id" ? "- Masalah: " : "- Problems: ") + string.Join("; ", s["problems"]!.AsArray().Select(x => x!.ToString())));
            var labs = s["abnormal_labs"]!.AsArray();
            if (labs.Count > 0) sb.AppendLine((lang == "id" ? "- Lab abnormal: " : "- Abnormal labs: ") + string.Join("; ", labs.Take(6).Select(x => x!.ToString())));
            var alerts = s["alerts"]!.AsArray();
            if (alerts.Count > 0)
            {
                sb.AppendLine(lang == "id" ? "- **Peringatan CDS:**" : "- **Decision-support alerts:**");
                foreach (var a in alerts.Take(6)) sb.AppendLine($"  - [{a!["severity"]}] {a["title"]}");
            }
        }
        else
        {
            sb.AppendLine(lang == "id"
                ? "Saya dapat menghitung NEWS2/qSOFA/eGFR/IMT, dosis berbasis berat badan, memeriksa interaksi obat, mencari kode ICD-10/SNOMED/LOINC, dan meringkas pasien."
                : "I can calculate NEWS2/qSOFA/eGFR/BMI, weight-based doses, check drug interactions, look up ICD-10/SNOMED/LOINC codes and summarise the patient.");
        }
        sb.AppendLine().Append(lang == "id" ? "_Mode offline · " : "_Offline mode · ").Append(MedicalInfo.DisclaimerFor(lang)).Append('_');
        return Task.FromResult(new AssistantReply(sb.ToString().Trim(), calls, Provider, Model));
    }

    private static string[] DrugsIn(string lower, ClinicalTools tools, PatientRecord? patient)
    {
        var known = Terminology.TerminologyService.Default.RxNorm.Concepts;
        var found = known.Where(c => Regex.IsMatch(lower, $@"\b{Regex.Escape(c.Display)}\b") || (c.DisplayId is { } id && Regex.IsMatch(lower, $@"\b{Regex.Escape(id.Split(' ')[0])}\b")))
            .Select(c => c.Display).ToList();
        if (lower.Contains("paracetamol")) found.Add("acetaminophen");
        if (found.Count < 2 && patient is not null) found.AddRange(patient.Medications.Select(m => m.Generic));
        return found.Distinct().ToArray();
    }
}
