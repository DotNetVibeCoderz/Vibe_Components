using System.Globalization;
using System.Net;
using System.Text;
using Hl7.Fhir.Model;
using M = Medical.Core;

namespace Medical.Fhir;

/// <summary>Identifier systems and other knobs used by <see cref="FhirMapper"/>.</summary>
public sealed record FhirMappingOptions
{
    /// <summary>System URI of the facility medical-record number.</summary>
    public string MrnSystem { get; init; } = "urn:medical-net:mrn";
    /// <summary>Organization that authors documents (IPS) — a display string or a reference.</summary>
    public string Organization { get; init; } = "Medical.Net Demo Hospital";
    public string Country { get; init; } = "ID";
}

/// <summary>Bidirectional mapping between the Medical.Net domain model and FHIR R4 resources.</summary>
public sealed class FhirMapper(FhirMappingOptions? options = null)
{
    public const string ObservationCategory = "http://terminology.hl7.org/CodeSystem/observation-category";
    public const string Interpretation = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";
    public const string ActCode = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    public const string ConditionClinical = "http://terminology.hl7.org/CodeSystem/condition-clinical";
    public const string ConditionVerification = "http://terminology.hl7.org/CodeSystem/condition-ver-status";
    public const string ConditionCategory = "http://terminology.hl7.org/CodeSystem/condition-category";
    public const string AllergyClinical = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical";
    public const string AllergyVerification = "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification";

    public FhirMappingOptions Options { get; } = options ?? new FhirMappingOptions();

    // ------------------------------------------------------------------ Patient

    public Patient ToFhir(M.Patient p)
    {
        var r = new Patient
        {
            Id = p.Id,
            Active = true,
            Identifier = [new Identifier(Options.MrnSystem, p.Mrn) { Use = Identifier.IdentifierUse.Usual }],
            Name = [new HumanName { Use = HumanName.NameUse.Official, Family = p.Name.Family, Given = [p.Name.Given], Text = p.Name.Full }],
            Gender = p.Gender switch
            {
                M.Gender.Male => AdministrativeGender.Male,
                M.Gender.Female => AdministrativeGender.Female,
                M.Gender.Other => AdministrativeGender.Other,
                _ => AdministrativeGender.Unknown,
            },
            BirthDate = p.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (p.Nik is { } nik) r.Identifier.Add(new Identifier(M.CodeSystems.Nik, nik) { Use = Identifier.IdentifierUse.Official });
        if (p.Phone is { } phone) r.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Phone, ContactPoint.ContactPointUse.Mobile, phone));
        if (p.Email is { } email) r.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Email, ContactPoint.ContactPointUse.Home, email));
        if (p.Address is not null || p.City is not null)
            r.Address.Add(new Address { Use = Address.AddressUse.Home, Line = p.Address is null ? [] : [p.Address], City = p.City, Country = Options.Country });
        return r;
    }

    public M.Patient FromFhir(Patient r)
    {
        var name = r.Name.FirstOrDefault(n => n.Use == HumanName.NameUse.Official) ?? r.Name.FirstOrDefault();
        return new M.Patient
        {
            Id = r.Id ?? Guid.NewGuid().ToString("n"),
            Mrn = r.Identifier.FirstOrDefault(i => i.System == Options.MrnSystem)?.Value ?? r.Identifier.FirstOrDefault()?.Value ?? r.Id ?? "",
            Nik = r.Identifier.FirstOrDefault(i => i.System == M.CodeSystems.Nik)?.Value,
            Name = new M.HumanName(string.Join(' ', name?.Given ?? []), name?.Family ?? ""),
            Gender = r.Gender switch
            {
                AdministrativeGender.Male => M.Gender.Male,
                AdministrativeGender.Female => M.Gender.Female,
                AdministrativeGender.Other => M.Gender.Other,
                _ => M.Gender.Unknown,
            },
            BirthDate = DateOnly.TryParse(r.BirthDate, CultureInfo.InvariantCulture, out var bd) ? bd : default,
            Phone = r.Telecom.FirstOrDefault(t => t.System == ContactPoint.ContactPointSystem.Phone)?.Value,
            Email = r.Telecom.FirstOrDefault(t => t.System == ContactPoint.ContactPointSystem.Email)?.Value,
            Address = r.Address.FirstOrDefault()?.Line.FirstOrDefault(),
            City = r.Address.FirstOrDefault()?.City,
        };
    }

    // ------------------------------------------------------------------ Encounter

    public Encounter ToFhir(M.Encounter e)
    {
        var (code, display) = e.Class switch
        {
            M.EncounterClass.Emergency => ("EMER", "emergency"),
            M.EncounterClass.Inpatient => ("IMP", "inpatient encounter"),
            M.EncounterClass.Virtual => ("VR", "virtual"),
            M.EncounterClass.Home => ("HH", "home health"),
            _ => ("AMB", "ambulatory"),
        };
        var r = new Encounter
        {
            Id = e.Id,
            Status = e.Status switch
            {
                M.EncounterStatus.Planned => Encounter.EncounterStatus.Planned,
                M.EncounterStatus.Arrived => Encounter.EncounterStatus.Arrived,
                M.EncounterStatus.Finished => Encounter.EncounterStatus.Finished,
                M.EncounterStatus.Cancelled => Encounter.EncounterStatus.Cancelled,
                _ => Encounter.EncounterStatus.InProgress,
            },
            Class = new Coding(ActCode, code, display),
            Subject = Ref("Patient", e.PatientId),
            Period = new Period { Start = Dt(e.Start), End = e.End is { } end ? Dt(end) : null },
        };
        if (e.Practitioner is { } pr) r.Participant.Add(new Encounter.ParticipantComponent { Individual = new ResourceReference { Display = pr } });
        if (e.Location is { } loc) r.Location.Add(new Encounter.LocationComponent { Location = new ResourceReference { Display = loc } });
        if (e.Reason is { } reason) r.ReasonCode.Add(Concept(reason));
        return r;
    }

    // ------------------------------------------------------------------ Observations

    /// <summary>
    /// Maps one vital-signs set to FHIR vital-sign Observations conforming to the base vital-signs profiles
    /// (heart rate, respiratory rate, BP panel with systolic/diastolic components, temperature, SpO₂).
    /// </summary>
    public IReadOnlyList<Observation> ToFhir(M.VitalSigns v, string patientId, string? encounterId = null)
    {
        string stamp = v.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Observation Vital(string slug, M.ClinicalCode code, string profile, double? value, string unit, string display)
        {
            var o = new Observation
            {
                Id = $"{patientId}-{slug}-{stamp}",
                Meta = new Meta { Profile = [$"http://hl7.org/fhir/StructureDefinition/{profile}"] },
                Status = ObservationStatus.Final,
                Category = [new CodeableConcept(ObservationCategory, "vital-signs", "Vital Signs")],
                Code = Concept(code),
                Subject = Ref("Patient", patientId),
                Encounter = encounterId is null ? null : Ref("Encounter", encounterId),
                Effective = new FhirDateTime(v.Timestamp),
            };
            if (value is { } x) o.Value = Qty(x, unit, display);
            return o;
        }

        var bp = Vital("bp", M.Loinc.BloodPressurePanel, "bp", null, "", "");
        bp.Component.Add(new Observation.ComponentComponent { Code = Concept(M.Loinc.SystolicBp), Value = Qty(v.SystolicBp, "mm[Hg]", "mmHg") });
        bp.Component.Add(new Observation.ComponentComponent { Code = Concept(M.Loinc.DiastolicBp), Value = Qty(v.DiastolicBp, "mm[Hg]", "mmHg") });
        return
        [
            Vital("hr", M.Loinc.HeartRate, "heartrate", v.HeartRate, "/min", "beats/minute"),
            Vital("rr", M.Loinc.RespiratoryRate, "resprate", v.RespiratoryRate, "/min", "breaths/minute"),
            bp,
            Vital("temp", M.Loinc.BodyTemperature, "bodytemp", v.TemperatureC, "Cel", "°C"),
            Vital("spo2", M.Loinc.OxygenSaturation, "oxygensat", v.SpO2, "%", "%"),
        ];
    }

    /// <summary>Maps a lab result (with reference range and interpretation) to a laboratory Observation.</summary>
    public Observation ToFhir(M.ClinicalObservation o)
    {
        var r = new Observation
        {
            Id = o.Id,
            Status = o.Status switch
            {
                M.ObservationStatus.Registered => ObservationStatus.Registered,
                M.ObservationStatus.Preliminary => ObservationStatus.Preliminary,
                M.ObservationStatus.Amended => ObservationStatus.Amended,
                M.ObservationStatus.Cancelled => ObservationStatus.Cancelled,
                _ => ObservationStatus.Final,
            },
            Category = [new CodeableConcept(ObservationCategory, "laboratory", "Laboratory")],
            Code = Concept(o.Code),
            Subject = Ref("Patient", o.PatientId),
            Encounter = o.EncounterId is null ? null : Ref("Encounter", o.EncounterId),
            Effective = new FhirDateTime(o.Effective),
        };
        if (o.Value is { } v) r.Value = Qty(v, o.Unit ?? "1", M.Ucum.Display(o.Unit ?? ""));
        else if (o.ValueText is { } t) r.Value = new FhirString(t);
        if (o.ReferenceLow is not null || o.ReferenceHigh is not null)
            r.ReferenceRange.Add(new Observation.ReferenceRangeComponent
            {
                Low = o.ReferenceLow is { } lo ? Qty(lo, o.Unit ?? "1", M.Ucum.Display(o.Unit ?? "")) : null,
                High = o.ReferenceHigh is { } hi ? Qty(hi, o.Unit ?? "1", M.Ucum.Display(o.Unit ?? "")) : null,
            });
        var (ic, id) = o.Flag switch
        {
            M.ResultFlag.Low => ("L", "Low"),
            M.ResultFlag.High => ("H", "High"),
            M.ResultFlag.CriticalLow => ("LL", "Critical low"),
            M.ResultFlag.CriticalHigh => ("HH", "Critical high"),
            M.ResultFlag.Abnormal => ("A", "Abnormal"),
            _ => ("N", "Normal"),
        };
        r.Interpretation.Add(new CodeableConcept(Interpretation, ic, id));
        return r;
    }

    public M.ClinicalObservation FromFhir(Observation r)
    {
        var coding = r.Code.Coding.FirstOrDefault() ?? new Coding("urn:unknown", r.Code.Text ?? "unknown");
        var q = r.Value as Quantity;
        var range = r.ReferenceRange.FirstOrDefault();
        string? interp = r.Interpretation.FirstOrDefault()?.Coding.FirstOrDefault()?.Code;
        return new M.ClinicalObservation
        {
            Id = r.Id ?? Guid.NewGuid().ToString("n"),
            PatientId = r.Subject?.Reference?.Split('/').Last() ?? "",
            EncounterId = r.Encounter?.Reference?.Split('/').Last(),
            Code = new M.ClinicalCode(coding.System ?? "urn:unknown", coding.Code ?? "", coding.Display),
            Value = q?.Value is { } d ? (double)d : null,
            Unit = q?.Code ?? q?.Unit,
            ValueText = (r.Value as FhirString)?.Value,
            ReferenceLow = range?.Low?.Value is { } lo ? (double)lo : null,
            ReferenceHigh = range?.High?.Value is { } hi ? (double)hi : null,
            Flag = interp switch { "L" => M.ResultFlag.Low, "H" => M.ResultFlag.High, "LL" => M.ResultFlag.CriticalLow, "HH" => M.ResultFlag.CriticalHigh, "A" => M.ResultFlag.Abnormal, _ => M.ResultFlag.Normal },
            Effective = (r.Effective as FhirDateTime)?.TryToDateTimeOffset(out var dto) == true ? dto : DateTimeOffset.MinValue,
        };
    }

    // ------------------------------------------------------------------ Condition / medication / allergy

    public Condition ToFhir(M.Condition c)
    {
        string status = c.Status switch
        {
            M.ConditionStatus.Recurrence => "recurrence",
            M.ConditionStatus.Remission => "remission",
            M.ConditionStatus.Resolved => "resolved",
            _ => "active",
        };
        return new Condition
        {
            Id = c.Id,
            ClinicalStatus = new CodeableConcept(ConditionClinical, status),
            VerificationStatus = new CodeableConcept(ConditionVerification, "confirmed"),
            Category = [new CodeableConcept(ConditionCategory, "problem-list-item", "Problem List Item")],
            Code = Concept(c.Code),
            Subject = Ref("Patient", c.PatientId),
            Onset = c.Onset is { } d ? new FhirDateTime(d.Year, d.Month, d.Day) : null,
        };
    }

    public MedicationRequest ToFhir(M.MedicationOrder m) => new()
    {
        Id = m.Id,
        Status = MedicationRequest.MedicationrequestStatus.Active,
        Intent = MedicationRequest.MedicationRequestIntent.Order,
        Medication = Concept(m.Medication),
        Subject = Ref("Patient", m.PatientId),
        AuthoredOn = m.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DosageInstruction =
        [
            new Dosage
            {
                Text = m.Instructions ?? $"{m.DoseMg:0.##} mg {m.Route} {m.FrequencyPerDay:0}× daily",
                Timing = new Timing { Repeat = new Timing.RepeatComponent { Frequency = (int)Math.Round(m.FrequencyPerDay), Period = 1, PeriodUnit = Timing.UnitsOfTime.D } },
                Route = new CodeableConcept { Text = m.Route },
                DoseAndRate = [new Dosage.DoseAndRateComponent { Dose = Qty(m.DoseMg, "mg", "mg") }],
            },
        ],
    };

    public AllergyIntolerance ToFhir(M.Allergy a)
    {
        var code = new CodeableConcept { Text = a.Substance };
        if (a.Substance.Equals("penicillin", StringComparison.OrdinalIgnoreCase)) code.Coding.Add(new Coding(M.CodeSystems.Snomed, "764146007", "Penicillin"));
        var r = new AllergyIntolerance
        {
            Id = a.Id,
            ClinicalStatus = new CodeableConcept(AllergyClinical, "active"),
            VerificationStatus = new CodeableConcept(AllergyVerification, "confirmed"),
            Code = code,
            Patient = Ref("Patient", a.PatientId),
        };
        r.Reaction.Add(new AllergyIntolerance.ReactionComponent
        {
            Manifestation = [new CodeableConcept { Text = a.Reaction ?? "Unknown reaction" }],
            Severity = a.Severity switch
            {
                M.AllergySeverity.Mild => AllergyIntolerance.AllergyIntoleranceSeverity.Mild,
                M.AllergySeverity.Severe => AllergyIntolerance.AllergyIntoleranceSeverity.Severe,
                _ => AllergyIntolerance.AllergyIntoleranceSeverity.Moderate,
            },
        });
        return r;
    }

    // ------------------------------------------------------------------ Bundles

    /// <summary>All resources of a patient record (patient first).</summary>
    public IReadOnlyList<Resource> ToResources(M.PatientRecord record)
    {
        var list = new List<Resource> { ToFhir(record.Patient) };
        string? enc = record.Encounters.FirstOrDefault()?.Id;
        list.AddRange(record.Encounters.Select(ToFhir));
        list.AddRange(record.Conditions.Select(ToFhir));
        list.AddRange(record.Allergies.Select(ToFhir));
        list.AddRange(record.Medications.Select(ToFhir));
        list.AddRange(record.Observations.Select(ToFhir));
        if (record.LatestVitals is { } v) list.AddRange(ToFhir(v, record.Patient.Id, enc));
        return list;
    }

    /// <summary>Transaction bundle (PUT by id → idempotent upload to any FHIR server, including SATUSEHAT-style endpoints).</summary>
    public Bundle ToTransaction(M.PatientRecord record)
    {
        var b = new Bundle { Type = Bundle.BundleType.Transaction, Timestamp = DateTimeOffset.UtcNow };
        foreach (var r in ToResources(record))
            b.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"{r.TypeName}/{r.Id}",
                Resource = r,
                Request = new Bundle.RequestComponent { Method = Bundle.HTTPVerb.PUT, Url = $"{r.TypeName}/{r.Id}" },
            });
        return b;
    }

    /// <summary>
    /// International Patient Summary (HL7 IPS) style document bundle: a Composition with problem, medication, allergy,
    /// result and vital-sign sections, each with generated narrative, followed by the referenced resources.
    /// </summary>
    public Bundle ToPatientSummary(M.PatientRecord record, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        var resources = ToResources(record);
        string Url(Resource r) => $"urn:uuid:{DeterministicGuid($"{r.TypeName}/{r.Id}")}";
        ResourceReference RefTo(Resource r) => new(Url(r));

        Composition.SectionComponent Section(string title, M.ClinicalCode code, IEnumerable<Resource> items, Func<Resource, string> line)
        {
            var arr = items.ToArray();
            var sb = new StringBuilder("<div xmlns=\"http://www.w3.org/1999/xhtml\">");
            if (arr.Length == 0) sb.Append("<p>No information available.</p>");
            else
            {
                sb.Append("<ul>");
                foreach (var r in arr) sb.Append("<li>").Append(WebUtility.HtmlEncode(line(r))).Append("</li>");
                sb.Append("</ul>");
            }
            sb.Append("</div>");
            var s = new Composition.SectionComponent
            {
                Title = title,
                Code = Concept(code),
                Text = new Narrative { Status = Narrative.NarrativeStatus.Generated, Div = sb.ToString() },
                Entry = arr.Select(RefTo).ToList(),
            };
            if (arr.Length == 0) s.EmptyReason = new CodeableConcept("http://terminology.hl7.org/CodeSystem/list-empty-reason", "unavailable", "Unavailable");
            return s;
        }

        var patient = resources.OfType<Patient>().First();
        var comp = new Composition
        {
            Id = $"ips-{record.Patient.Id}",
            Status = CompositionStatus.Final,
            Type = new CodeableConcept(M.CodeSystems.Loinc, "60591-5", "Patient summary Document"),
            Subject = RefTo(patient),
            Date = when.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            Author = [new ResourceReference { Display = Options.Organization }],
            Title = $"Patient Summary — {record.Patient.Name.Full}",
            Section =
            [
                Section("Problems", M.ClinicalCode.Loinc("11450-4", "Problem list - Reported"), resources.OfType<Condition>(),
                    r => ((Condition)r).Code?.Coding.FirstOrDefault() is { } c ? $"{c.Display} ({c.Code})" : ""),
                Section("Medications", M.ClinicalCode.Loinc("10160-0", "History of Medication use Narrative"), resources.OfType<MedicationRequest>(),
                    r => $"{(((MedicationRequest)r).Medication as CodeableConcept)?.Coding.FirstOrDefault()?.Display} — {((MedicationRequest)r).DosageInstruction.First().Text}"),
                Section("Allergies and Intolerances", M.ClinicalCode.Loinc("48765-2", "Allergies and adverse reactions Document"), resources.OfType<AllergyIntolerance>(),
                    r => $"{((AllergyIntolerance)r).Code?.Text}: {((AllergyIntolerance)r).Reaction.First().Manifestation.First().Text}"),
                Section("Results", M.ClinicalCode.Loinc("30954-2", "Relevant diagnostic tests/laboratory data Narrative"),
                    resources.OfType<Observation>().Where(o => o.Category.Any(c => c.Coding.Any(x => x.Code == "laboratory"))), Line),
                Section("Vital Signs", M.ClinicalCode.Loinc("8716-3", "Vital signs"),
                    resources.OfType<Observation>().Where(o => o.Category.Any(c => c.Coding.Any(x => x.Code == "vital-signs"))), Line),
            ],
        };

        var bundle = new Bundle
        {
            Id = $"ips-bundle-{record.Patient.Id}",
            Identifier = new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{DeterministicGuid($"ips/{record.Patient.Id}/{when:O}")}"),
            Type = Bundle.BundleType.Document,
            Timestamp = when,
        };
        bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = Url(comp), Resource = comp });
        foreach (var r in resources) bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = Url(r), Resource = r });
        return bundle;
    }

    private static string Line(Resource r)
    {
        var o = (Observation)r;
        string name = o.Code.Coding.FirstOrDefault()?.Display ?? o.Code.Text ?? "";
        string value = o.Value is Quantity q ? $"{q.Value?.ToString("0.##", CultureInfo.InvariantCulture)} {q.Unit}" :
            o.Component.Count > 0 ? string.Join(" / ", o.Component.Select(c => (c.Value as Quantity)?.Value?.ToString("0", CultureInfo.InvariantCulture))) + " mmHg" : "";
        string flag = o.Interpretation.FirstOrDefault()?.Coding.FirstOrDefault()?.Code is { } f and not "N" ? $" [{f}]" : "";
        return $"{name}: {value}{flag}";
    }

    // ------------------------------------------------------------------ helpers

    public static CodeableConcept Concept(M.ClinicalCode c) => new(c.System, c.Code, c.Display) { Text = c.Display };

    public static ResourceReference Ref(string type, string id) => new($"{type}/{id}");

    private static Quantity Qty(double value, string ucum, string display) => new()
    {
        Value = (decimal)Math.Round(value, 3),
        Unit = display.Length > 0 ? display : ucum,
        System = M.CodeSystems.Ucum,
        Code = ucum,
    };

    private static string Dt(DateTimeOffset d) => d.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>Name-based (SHA-1, RFC 4122 v5-style) GUID so document entries get stable urn:uuid identifiers.</summary>
    public static Guid DeterministicGuid(string name)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(name));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
