using System.Globalization;
using Medical.Core;
using Medical.Fhir;

namespace Medical.Hl7;

/// <summary>Maps HL7 v2 PID / PV1 / OBX content to the Medical.Net domain model and on to FHIR R4.</summary>
public static class Hl7Mapping
{
    /// <summary>Patient from the first PID segment.</summary>
    public static Patient ToPatient(Hl7Message m)
    {
        var pid = m.Segments("PID").FirstOrDefault() ?? throw new InvalidDataException("Message has no PID segment.");
        string mrn = Hl7Message.Field(pid, 3), nik = "";
        for (int rep = 0; rep < 4; rep++)
        {
            string id = Hl7Message.Field(pid, 3, 1, rep), type = Hl7Message.Field(pid, 3, 5, rep);
            if (id.Length == 0) break;
            if (type == "MR") mrn = id;
            if (type == "NI") nik = id;
        }
        string sex = Hl7Message.Field(pid, 8);
        string dob = Hl7Message.Field(pid, 7);
        return new Patient
        {
            Id = $"pat-{mrn}",
            Mrn = mrn,
            Nik = nik.Length > 0 ? nik : null,
            Name = new HumanName(Hl7Message.Field(pid, 5, 2), Hl7Message.Field(pid, 5, 1)),
            Gender = sex switch { "M" => Gender.Male, "F" => Gender.Female, "O" or "A" => Gender.Other, _ => Gender.Unknown },
            BirthDate = dob.Length >= 8 && DateOnly.TryParseExact(dob[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : default,
            Address = NullIfEmpty(Hl7Message.Field(pid, 11, 1)),
            City = NullIfEmpty(Hl7Message.Field(pid, 11, 3)),
            Phone = NullIfEmpty(Hl7Message.Field(pid, 13, 1)),
        };
    }

    /// <summary>Encounter from PV1 (ADT messages).</summary>
    public static Encounter? ToEncounter(Hl7Message m, string patientId)
    {
        var pv1 = m.Segments("PV1").FirstOrDefault();
        if (pv1 is null) return null;
        string visit = Hl7Message.Field(pv1, 19);
        return new Encounter
        {
            Id = visit.Length > 0 ? visit : $"enc-{m.ControlId}",
            PatientId = patientId,
            Class = Hl7Message.Field(pv1, 2) switch { "I" => EncounterClass.Inpatient, "E" => EncounterClass.Emergency, _ => EncounterClass.Ambulatory },
            Location = NullIfEmpty(Hl7Message.Field(pv1, 3)),
            Practitioner = NullIfEmpty(string.Join(' ', Hl7Message.Field(pv1, 7, 2), Hl7Message.Field(pv1, 7, 1)).Trim()),
            Start = ParseTs(Hl7Message.Field(pv1, 44)) ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Every OBX as a coded observation (NM → numeric value, others → text). OBX-3 coding system "LN" maps to LOINC.</summary>
    public static IReadOnlyList<ClinicalObservation> ToObservations(Hl7Message m, string patientId)
    {
        var list = new List<ClinicalObservation>();
        foreach (var obx in m.Segments("OBX"))
        {
            string type = Hl7Message.Field(obx, 2), raw = Hl7Message.Field(obx, 5), sys = Hl7Message.Field(obx, 3, 3);
            string range = Hl7Message.Field(obx, 7);
            double? lo = null, hi = null;
            var parts = range.Split('-', 2);
            if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var l) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var h)) { lo = l; hi = h; }
            double? value = type == "NM" && double.TryParse(raw, CultureInfo.InvariantCulture, out var v) ? v : null;
            list.Add(new ClinicalObservation
            {
                Id = $"obx-{m.ControlId}-{Hl7Message.Field(obx, 1)}",
                PatientId = patientId,
                Code = new ClinicalCode(sys == "LN" ? CodeSystems.Loinc : $"urn:hl7v2:{sys}", Hl7Message.Field(obx, 3, 1), NullIfEmpty(Hl7Message.Field(obx, 3, 2))),
                Value = value,
                ValueText = value is null ? raw : null,
                Unit = NullIfEmpty(Hl7Message.Field(obx, 6)),
                ReferenceLow = lo,
                ReferenceHigh = hi,
                Flag = Hl7Message.Field(obx, 8) switch
                {
                    "L" => ResultFlag.Low, "H" => ResultFlag.High, "LL" => ResultFlag.CriticalLow, "HH" => ResultFlag.CriticalHigh, "A" => ResultFlag.Abnormal, _ => ResultFlag.Normal,
                },
                Status = Hl7Message.Field(obx, 11) == "P" ? ObservationStatus.Preliminary : ObservationStatus.Final,
                Effective = ParseTs(Hl7Message.Field(obx, 14)) ?? DateTimeOffset.UtcNow,
            });
        }
        return list;
    }

    /// <summary>Converts an ADT or ORU message to a FHIR transaction bundle (Patient, Encounter, Observations).</summary>
    public static global::Hl7.Fhir.Model.Bundle ToFhirTransaction(Hl7Message m, FhirMapper? mapper = null)
    {
        mapper ??= new FhirMapper();
        var p = ToPatient(m);
        var enc = ToEncounter(m, p.Id);
        var record = new PatientRecord
        {
            Patient = p,
            Encounters = enc is null ? [] : [enc],
            Observations = ToObservations(m, p.Id),
        };
        return mapper.ToTransaction(record);
    }

    /// <summary>Parses HL7 TS (YYYYMMDD[HHMM[SS]][+/-ZZZZ]).</summary>
    public static DateTimeOffset? ParseTs(string ts)
    {
        if (ts.Length < 8) return null;
        string[] formats = ["yyyyMMddHHmmsszzz", "yyyyMMddHHmmzzz", "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd"];
        string s = ts.Length > 14 && (ts[^5] is '+' or '-') ? ts[..^5] + ts[^5..^2] + ":" + ts[^2..] : ts;
        if (s.Contains('.')) s = s[..s.IndexOf('.')] + (ts.Length > 5 && ts[^5] is '+' or '-' ? s[^6..] : "");
        return DateTimeOffset.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
