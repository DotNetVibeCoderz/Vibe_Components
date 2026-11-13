using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Medical.Clinical;
using Medical.Core;
using Medical.Rules;
using Medical.Terminology;

namespace Medical.AI.Assistant;

/// <summary>A tool the assistant may call: JSON-Schema input and a deterministic handler returning JSON.</summary>
public sealed record ClinicalTool(string Name, string Description, JsonObject InputSchema, Func<JsonElement, PatientRecord?, JsonNode> Handler);

/// <summary>One executed tool call (for transparency in the UI and the audit trail).</summary>
public sealed record ToolCallRecord(string Name, string Input, string Output, bool IsError);

/// <summary>
/// The validated calculators and knowledge services exposed to LLMs as tools. Every number in an assistant answer
/// should come from here, never from the model's memory.
/// </summary>
public sealed class ClinicalTools
{
    private readonly Dictionary<string, ClinicalTool> _tools = new(StringComparer.Ordinal);

    public ClinicalTools(TerminologyService? terms = null, DrugKnowledge? drugs = null)
    {
        var t = terms ?? TerminologyService.Default;
        var d = drugs ?? DrugKnowledge.Default;

        Add(new("calculate_news2", "National Early Warning Score 2 from bedside vital signs. Returns the score, risk band, per-parameter points and recommended response.",
            Schema(("respiratory_rate", "number", "breaths/min"), ("spo2", "number", "%"), ("supplemental_oxygen", "boolean", "on oxygen?"),
                ("systolic_bp", "number", "mmHg"), ("heart_rate", "number", "beats/min"), ("temperature_c", "number", "°C"),
                ("consciousness", "string", "alert | confusion | voice | pain | unresponsive")),
            (j, _) =>
            {
                var v = new VitalSigns
                {
                    RespiratoryRate = Num(j, "respiratory_rate"), SpO2 = Num(j, "spo2"), OnSupplementalOxygen = Bool(j, "supplemental_oxygen"),
                    SystolicBp = Num(j, "systolic_bp"), HeartRate = Num(j, "heart_rate"), TemperatureC = Num(j, "temperature_c"),
                    Consciousness = Enum.TryParse<Consciousness>(Str(j, "consciousness"), true, out var c) ? c : Consciousness.Alert,
                };
                return Score(ClinicalScores.News2(v));
            }));

        Add(new("calculate_qsofa", "quick SOFA (Sepsis-3) from respiratory rate, systolic BP and mentation.",
            Schema(("respiratory_rate", "number", "breaths/min"), ("systolic_bp", "number", "mmHg"), ("altered_mentation", "boolean", "GCS < 15?")),
            (j, _) => Score(ClinicalScores.QSofa(Num(j, "respiratory_rate"), Num(j, "systolic_bp"), Bool(j, "altered_mentation")))));

        Add(new("calculate_egfr", "eGFR by the race-free CKD-EPI 2021 creatinine equation, with KDIGO CKD stage.",
            Schema(("creatinine_mg_dl", "number", "serum creatinine mg/dL"), ("age", "integer", "years"), ("sex", "string", "male | female")),
            (j, _) =>
            {
                var r = Calculators.EgfrCkdEpi2021(Num(j, "creatinine_mg_dl"), (int)Num(j, "age"), Sex(j));
                return new JsonObject { ["egfr_ml_min_1_73m2"] = r.Egfr, ["stage"] = r.Stage.ToString(), ["label_en"] = r.Label.En, ["label_id"] = r.Label.Id };
            }));

        Add(new("calculate_bmi", "Body-mass index with WHO and Asia-Pacific categories.",
            Schema(("weight_kg", "number", "kg"), ("height_cm", "number", "cm")),
            (j, _) =>
            {
                var who = Calculators.Bmi(Num(j, "weight_kg"), Num(j, "height_cm"));
                var ap = Calculators.Bmi(Num(j, "weight_kg"), Num(j, "height_cm"), BmiStandard.AsiaPacific);
                return new JsonObject { ["bmi"] = who.Bmi, ["who_category"] = who.Category, ["asia_pacific_category"] = ap.Category };
            }));

        Add(new("weight_based_dose", "Weight-based dose with single/daily caps and renal adjustment. Supported: " + string.Join(", ", DoseCalculator.Rules.Keys) + ".",
            Schema(("drug", "string", "generic name"), ("weight_kg", "number", "kg"), ("crcl_ml_min", "number", "creatinine clearance, optional")),
            (j, _) =>
            {
                double? crcl = j.TryGetProperty("crcl_ml_min", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
                var r = DoseCalculator.Calculate(Str(j, "drug"), Num(j, "weight_kg"), crcl);
                return new JsonObject
                {
                    ["drug"] = r.Generic, ["single_dose_mg"] = r.SingleDoseMg, ["doses_per_day"] = r.DosesPerDay, ["daily_dose_mg"] = r.DailyDoseMg,
                    ["capped"] = r.Capped, ["renal_adjusted"] = r.RenalAdjusted, ["note_en"] = r.Note.En, ["note_id"] = r.Note.Id,
                };
            }));

        Add(new("check_drug_safety", "Checks a medication list (and optional allergies) for drug-drug interactions, duplicate therapy and allergy conflicts.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["drugs"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "generic names (English or Indonesian)" },
                    ["allergies"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                },
                ["required"] = new JsonArray("drugs"),
            },
            (j, _) =>
            {
                var drugsIn = j.GetProperty("drugs").EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0)
                    .Select(s => t.Drug(s)?.Display ?? s.ToLowerInvariant()).Distinct().ToArray();
                var allergies = j.TryGetProperty("allergies", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
                var findings = new JsonArray();
                for (int x = 0; x < drugsIn.Length; x++)
                    for (int y = x + 1; y < drugsIn.Length; y++)
                    {
                        if (d.Interaction(drugsIn[x], drugsIn[y]) is { } i)
                            findings.Add((JsonNode)new JsonObject { ["type"] = "interaction", ["drugs"] = $"{drugsIn[x]} + {drugsIn[y]}", ["severity"] = i.Severity.ToString(), ["effect_en"] = i.Effect.En, ["effect_id"] = i.Effect.Id });
                        if (d.DuplicateClass(drugsIn[x], drugsIn[y]) is { } cls)
                            findings.Add((JsonNode)new JsonObject { ["type"] = "duplicate", ["drugs"] = $"{drugsIn[x]} + {drugsIn[y]}", ["class"] = cls });
                    }
                foreach (var al in allergies)
                    foreach (var dr in drugsIn)
                        if (d.AllergyConflict(al, dr) is { } c)
                            findings.Add((JsonNode)new JsonObject { ["type"] = "allergy", ["allergy"] = al, ["drug"] = dr, ["severity"] = c.Severity.ToString(), ["effect_en"] = c.Effect.En, ["effect_id"] = c.Effect.Id });
                return new JsonObject { ["normalized_drugs"] = new JsonArray(drugsIn.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()), ["findings"] = findings, ["count"] = findings.Count };
            }));

        Add(new("lookup_terminology", "Searches ICD-10, SNOMED CT, LOINC and RxNorm (English or Bahasa Indonesia).",
            Schema(("query", "string", "text or code"), ("system", "string", "icd10 | snomed | loinc | rxnorm | any")),
            (j, _) =>
            {
                string? sys = Str(j, "system").ToLowerInvariant() switch
                {
                    "icd10" or "icd-10" => CodeSystems.Icd10, "snomed" => CodeSystems.Snomed, "loinc" => CodeSystems.Loinc, "rxnorm" => CodeSystems.RxNorm, _ => null,
                };
                var hits = t.Search(Str(j, "query"), sys, 8);
                return new JsonArray(hits.Select(h => (JsonNode)new JsonObject
                {
                    ["system"] = CodeSystems.ShortName(h.Concept.System), ["code"] = h.Concept.Code, ["display"] = h.Concept.Display, ["display_id"] = h.Concept.DisplayId,
                }).ToArray());
            }));

        Add(new("get_patient_summary", "Returns the current patient's demographics, problems, medications, allergies, latest vitals with NEWS2, abnormal labs and active decision-support alerts. Use it before answering any question about 'this patient' / 'pasien ini'.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            (_, record) => record is null ? new JsonObject { ["error"] = "No patient in context." } : PatientSummary.ToJson(record)));
    }

    public IReadOnlyCollection<ClinicalTool> All => _tools.Values;

    public void Add(ClinicalTool tool) => _tools[tool.Name] = tool;

    /// <summary>Runs a tool; exceptions become an error result the model can recover from.</summary>
    public ToolCallRecord Execute(string name, JsonElement input, PatientRecord? context)
    {
        string raw = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();
        if (!_tools.TryGetValue(name, out var tool)) return new(name, raw, $"{{\"error\":\"Unknown tool '{name}'.\"}}", true);
        try { return new(name, raw, tool.Handler(input, context).ToJsonString(), false); }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {
            return new(name, raw, new JsonObject { ["error"] = e.Message }.ToJsonString(), true);
        }
    }

    public ToolCallRecord Execute(string name, string jsonInput, PatientRecord? context)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonInput) ? "{}" : jsonInput);
        return Execute(name, doc.RootElement.Clone(), context);
    }

    private static JsonObject Schema(params (string Name, string Type, string Description)[] props)
    {
        var p = new JsonObject();
        foreach (var (n, ty, desc) in props) p[n] = new JsonObject { ["type"] = ty, ["description"] = desc };
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = new JsonArray(props.Where(x => !x.Description.Contains("optional")).Select(x => (JsonNode)JsonValue.Create(x.Name)!).ToArray()) };
    }

    private static JsonObject Score(ScoreResult s) => new()
    {
        ["name"] = s.Name, ["score"] = s.Score, ["max"] = s.MaxScore, ["risk"] = s.Risk.ToString(),
        ["interpretation_en"] = s.Interpretation.En, ["interpretation_id"] = s.Interpretation.Id,
        ["components"] = new JsonArray(s.Components.Select(c => (JsonNode)new JsonObject { ["parameter"] = c.Parameter, ["value"] = c.Value, ["points"] = c.Points }).ToArray()),
    };

    private static double Num(JsonElement j, string name) => j.TryGetProperty(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new ArgumentException($"'{name}' must be a number."),
    } : throw new ArgumentException($"Missing '{name}'.");

    private static bool Bool(JsonElement j, string name) => j.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() is "true" or "yes" or "ya"));

    private static string Str(JsonElement j, string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static Gender Sex(JsonElement j) => Str(j, "sex").ToLowerInvariant() switch { "female" or "f" or "perempuan" or "wanita" => Gender.Female, _ => Gender.Male };
}

/// <summary>Compact JSON view of a patient record for LLM grounding (no direct identifiers beyond first name).</summary>
public static class PatientSummary
{
    public static JsonObject ToJson(PatientRecord r, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(r.LatestVitals?.Timestamp.UtcDateTime ?? DateTime.UtcNow);
        var o = new JsonObject
        {
            ["patient"] = new JsonObject { ["first_name"] = r.Patient.Name.Given, ["age"] = r.Patient.AgeAt(day), ["sex"] = r.Patient.Gender.ToString(), ["weight_kg"] = r.WeightKg, ["height_cm"] = r.HeightCm },
            ["problems"] = new JsonArray(r.Conditions.Select(c => (JsonNode)JsonValue.Create($"{c.Code.Code} {c.Code.Display}")!).ToArray()),
            ["medications"] = new JsonArray(r.Medications.Select(m => (JsonNode)JsonValue.Create($"{m.Generic} {m.DoseMg:0.##} mg × {m.FrequencyPerDay:0}/day")!).ToArray()),
            ["allergies"] = new JsonArray(r.Allergies.Select(a => (JsonNode)JsonValue.Create($"{a.Substance} ({a.Reaction})")!).ToArray()),
        };
        if (r.LatestVitals is { } v)
        {
            var news = ClinicalScores.News2(v);
            o["latest_vitals"] = new JsonObject
            {
                ["time"] = v.Timestamp.ToString("O"), ["hr"] = v.HeartRate, ["rr"] = v.RespiratoryRate, ["sbp"] = v.SystolicBp, ["dbp"] = v.DiastolicBp,
                ["temp_c"] = v.TemperatureC, ["spo2"] = v.SpO2, ["on_oxygen"] = v.OnSupplementalOxygen, ["consciousness"] = v.Consciousness.ToString(),
                ["news2"] = news.Score, ["news2_risk"] = news.Risk.ToString(),
            };
        }
        var latest = r.Observations.GroupBy(x => x.Code.Code).Select(g => g.MaxBy(x => x.Effective)!).ToArray();
        o["labs"] = new JsonArray(latest.Select(x => (JsonNode)new JsonObject
        {
            ["loinc"] = x.Code.Code, ["name"] = x.Code.Display, ["value"] = x.Value, ["unit"] = x.Unit, ["flag"] = x.Flag.ToString(),
            ["reference"] = x.ReferenceLow is null ? null : $"{x.ReferenceLow}-{x.ReferenceHigh}",
        }).ToArray());
        o["abnormal_labs"] = new JsonArray(latest.Where(x => x.Flag != ResultFlag.Normal)
            .Select(x => (JsonNode)JsonValue.Create($"{x.Code.Display}: {x.Value:0.##} {x.Unit} [{x.Flag}]")!).ToArray());
        o["alerts"] = new JsonArray(new ClinicalDecisionSupport().Evaluate(r, day)
            .Select(a => (JsonNode)new JsonObject { ["severity"] = a.Severity.ToString(), ["title"] = a.Title.En, ["detail"] = a.Detail.En }).ToArray());
        return o;
    }
}
