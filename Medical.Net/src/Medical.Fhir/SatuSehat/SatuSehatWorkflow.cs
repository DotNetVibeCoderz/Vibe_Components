using System.Globalization;
using Hl7.Fhir.Model;
using M = Medical.Core;

namespace Medical.Fhir.SatuSehat;

/// <summary>Identities registered in SATUSEHAT that one encounter submission refers to.</summary>
public sealed record SatuSehatContext
{
    /// <summary>Patient IHS number (from <see cref="SatuSehatClient.SearchPatientByNikAsync"/>).</summary>
    public required string PatientIhs { get; init; }
    public required string PatientName { get; init; }
    public required string PractitionerIhs { get; init; }
    public required string PractitionerName { get; init; }
    public required string LocationId { get; init; }
    public required string LocationName { get; init; }
    public required string OrganizationId { get; init; }
}

/// <summary>A prepared submission: the transaction bundle plus anything that could not be included.</summary>
public sealed record SatuSehatSubmission(Bundle Bundle, IReadOnlyList<string> Warnings)
{
    public int ResourceCount => Bundle.Entry.Count;
}

/// <summary>
/// Builds the SATUSEHAT "rawat jalan" (outpatient) interoperability flow for one encounter as a single FHIR transaction:
/// Encounter (arrived → in-progress → finished with status history and diagnoses) → Condition (ICD-10) →
/// Observation (vital signs, one LOINC code each) → Medication (KFA) + MedicationRequest. Entries reference each other
/// through <c>urn:uuid</c> full URLs, so the whole visit is accepted or rejected atomically.
/// </summary>
public static class SatuSehatWorkflow
{
    public const string MedicationTypeExtension = "https://fhir.kemkes.go.id/r4/StructureDefinition/MedicationType";
    public const string MedicationTypeSystem = "http://terminology.kemkes.go.id/CodeSystem/medication-type";

    public static SatuSehatSubmission BuildOutpatientVisit(M.PatientRecord record, SatuSehatContext ctx, KfaCatalog? kfa = null, DateTimeOffset? end = null)
    {
        var warnings = new List<string>();
        var enc = record.Encounters.FirstOrDefault() ?? throw new InvalidOperationException("The record has no encounter.");
        var start = enc.Start;
        var finish = end ?? enc.End ?? start.AddMinutes(45);
        var inProgress = start.AddMinutes(10);
        string Dt(DateTimeOffset d) => d.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        string Uuid(string key) => $"urn:uuid:{FhirMapper.DeterministicGuid($"satusehat/{record.Patient.Id}/{enc.Id}/{key}")}";
        var subject = new ResourceReference($"Patient/{ctx.PatientIhs}", ctx.PatientName);
        var bundle = new Bundle { Type = Bundle.BundleType.Transaction };
        void Add(string fullUrl, Resource r) => bundle.Entry.Add(new Bundle.EntryComponent
        {
            FullUrl = fullUrl, Resource = r, Request = new Bundle.RequestComponent { Method = Bundle.HTTPVerb.POST, Url = r.TypeName },
        });

        // ---- Condition(s)
        var conditionUrls = new List<(string Url, M.Condition C)>();
        foreach (var c in record.Conditions.Where(c => c.Code.System is M.CodeSystems.Icd10 or M.CodeSystems.Icd10Cm))
            conditionUrls.Add((Uuid($"condition/{c.Code.Code}"), c));
        if (conditionUrls.Count == 0) warnings.Add("No ICD-10 diagnosis: SATUSEHAT expects at least one Condition per encounter.");

        // ---- Encounter
        string encUrl = Uuid("encounter");
        var encounter = new Encounter
        {
            Identifier = [new Identifier(SatuSehatSystems.Encounter(ctx.OrganizationId), enc.Id)],
            Status = Encounter.EncounterStatus.Finished,
            Class = enc.Class == M.EncounterClass.Inpatient
                ? new Coding(FhirMapper.ActCode, "IMP", "inpatient encounter")
                : new Coding(FhirMapper.ActCode, "AMB", "ambulatory"),
            Subject = subject,
            Participant =
            [
                new Encounter.ParticipantComponent
                {
                    Type = [FhirMapper.Cc("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", "ATND", "attender")],
                    Individual = new ResourceReference($"Practitioner/{ctx.PractitionerIhs}", ctx.PractitionerName),
                },
            ],
            Period = new Period { Start = Dt(start), End = Dt(finish) },
            Location = [new Encounter.LocationComponent { Location = new ResourceReference($"Location/{ctx.LocationId}", ctx.LocationName) }],
            StatusHistory =
            [
                new Encounter.StatusHistoryComponent { Status = Encounter.EncounterStatus.Arrived, Period = new Period { Start = Dt(start), End = Dt(inProgress) } },
                new Encounter.StatusHistoryComponent { Status = Encounter.EncounterStatus.InProgress, Period = new Period { Start = Dt(inProgress), End = Dt(finish) } },
                new Encounter.StatusHistoryComponent { Status = Encounter.EncounterStatus.Finished, Period = new Period { Start = Dt(finish), End = Dt(finish) } },
            ],
            ServiceProvider = new ResourceReference($"Organization/{ctx.OrganizationId}"),
        };
        int rank = 1;
        foreach (var (url, c) in conditionUrls)
            encounter.Diagnosis.Add(new Encounter.DiagnosisComponent
            {
                Condition = new ResourceReference(url, c.Code.Display),
                Use = FhirMapper.Cc("http://terminology.hl7.org/CodeSystem/diagnosis-role", "DD", "Discharge diagnosis"),
                Rank = rank++,
            });
        Add(encUrl, encounter);

        foreach (var (url, c) in conditionUrls)
            Add(url, new Condition
            {
                ClinicalStatus = FhirMapper.Cc(FhirMapper.ConditionClinical, "active", "Active"),
                Category = [FhirMapper.Cc(FhirMapper.ConditionCategory, "encounter-diagnosis", "Encounter Diagnosis")],
                Code = FhirMapper.Cc(M.CodeSystems.Icd10, c.Code.Code, c.Code.Display),
                Subject = subject,
                Encounter = new ResourceReference(encUrl),
            });

        // ---- Observations (one per vital sign, as the SATUSEHAT playbook models them)
        if (record.LatestVitals is { } v)
        {
            (M.ClinicalCode Code, double Value, string Unit, string Display)[] vitals =
            [
                (M.Loinc.HeartRate, v.HeartRate, "/min", "beats/minute"),
                (M.Loinc.RespiratoryRate, v.RespiratoryRate, "/min", "breaths/minute"),
                (M.Loinc.SystolicBp, v.SystolicBp, "mm[Hg]", "mm[Hg]"),
                (M.Loinc.DiastolicBp, v.DiastolicBp, "mm[Hg]", "mm[Hg]"),
                (M.Loinc.BodyTemperature, v.TemperatureC, "Cel", "C"),
                (M.Loinc.OxygenSaturation, v.SpO2, "%", "%"),
            ];
            foreach (var (code, value, unit, display) in vitals)
                Add(Uuid($"observation/{code.Code}"), new Observation
                {
                    Status = ObservationStatus.Final,
                    Category = [FhirMapper.Cc(FhirMapper.ObservationCategory, "vital-signs", "Vital Signs")],
                    Code = FhirMapper.Cc(M.CodeSystems.Loinc, code.Code, code.Display),
                    Subject = subject,
                    Performer = [new ResourceReference($"Practitioner/{ctx.PractitionerIhs}", ctx.PractitionerName)],
                    Encounter = new ResourceReference(encUrl),
                    Effective = new FhirDateTime(v.Timestamp),
                    Issued = v.Timestamp,
                    Value = new Quantity { Value = (decimal)Math.Round(value, 1), Unit = display, System = M.CodeSystems.Ucum, Code = unit },
                });
        }
        else warnings.Add("No vital signs recorded.");

        // ---- Medication (KFA) + MedicationRequest
        int rx = 1;
        foreach (var m in record.Medications)
        {
            if (kfa?.Find(m.Generic) is not { } product)
            {
                warnings.Add($"No KFA code for '{m.Generic}': add it to the KfaCatalog (or resolve with KfaClient) — skipped.");
                continue;
            }
            string medUrl = Uuid($"medication/{m.Id}");
            var medication = new Medication
            {
                Meta = new Meta { Profile = ["https://fhir.kemkes.go.id/r4/StructureDefinition/Medication"] },
                Identifier = [new Identifier($"http://sys-ids.kemkes.go.id/medication/{ctx.OrganizationId}", m.Id)],
                Code = FhirMapper.Cc(SatuSehatSystems.Kfa, product.Code, product.Name),
                Status = Medication.MedicationStatusCodes.Active,
            };
            medication.Extension.Add(new Extension(MedicationTypeExtension, FhirMapper.Cc(MedicationTypeSystem, "NC", "Non-compound")));
            Add(medUrl, medication);
            Add(Uuid($"medicationrequest/{m.Id}"), new MedicationRequest
            {
                Identifier =
                [
                    new Identifier($"http://sys-ids.kemkes.go.id/prescription/{ctx.OrganizationId}", enc.Id),
                    new Identifier($"http://sys-ids.kemkes.go.id/prescription-item/{ctx.OrganizationId}", $"{enc.Id}-{rx++}"),
                ],
                Status = MedicationRequest.MedicationrequestStatus.Completed,
                Intent = MedicationRequest.MedicationRequestIntent.Order,
                Category = [FhirMapper.Cc("http://terminology.hl7.org/CodeSystem/medicationrequest-category", "outpatient", "Outpatient")],
                Medication = new ResourceReference(medUrl, product.Name),
                Subject = subject,
                Encounter = new ResourceReference(encUrl),
                AuthoredOn = Dt(start.AddMinutes(30)),
                Requester = new ResourceReference($"Practitioner/{ctx.PractitionerIhs}", ctx.PractitionerName),
                DosageInstruction =
                [
                    new Dosage
                    {
                        Sequence = 1,
                        Text = m.Instructions ?? $"{m.DoseMg:0.##} mg {m.FrequencyPerDay:0}× sehari",
                        Timing = new Timing { Repeat = new Timing.RepeatComponent { Frequency = (int)Math.Round(m.FrequencyPerDay), Period = 1, PeriodUnit = Timing.UnitsOfTime.D } },
                        Route = FhirMapper.Cc("http://www.whocc.no/ddd", m.Route == "oral" ? "O" : "P", m.Route == "oral" ? "Oral" : "Parenteral"),
                        DoseAndRate = [new Dosage.DoseAndRateComponent { Dose = new Quantity { Value = (decimal)m.DoseMg, Unit = "mg", System = M.CodeSystems.Ucum, Code = "mg" } }],
                    },
                ],
            });
        }
        return new SatuSehatSubmission(bundle, warnings);
    }
}
