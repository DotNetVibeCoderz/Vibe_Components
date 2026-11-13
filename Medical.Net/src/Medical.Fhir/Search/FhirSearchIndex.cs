using System.Globalization;
using Hl7.Fhir.Model;

namespace Medical.Fhir.Search;

public enum FhirSearchKind { Token, Reference, String, Date }

/// <summary>One indexed value of a search parameter (e.g. <c>code = http://loinc.org|8867-4</c>).</summary>
public sealed record SearchIndexEntry(string Param, FhirSearchKind Type, string? System, string Value, DateTimeOffset? Start = null, DateTimeOffset? End = null);

/// <summary>
/// Extracts the search-parameter values Medical.Net supports from FHIR R4 resources. The same index is used by the
/// in-memory store (computed on the fly) and the EF Core store (persisted in an index table).
/// </summary>
public static class FhirSearchIndex
{
    /// <summary>Supported parameters per resource type (common parameters <c>_id</c> and <c>_lastUpdated</c> apply to all).</summary>
    public static IReadOnlyDictionary<string, FhirSearchKind> ParametersFor(string type) => type switch
    {
        "Patient" => new Dictionary<string, FhirSearchKind>
        {
            ["identifier"] = FhirSearchKind.Token, ["name"] = FhirSearchKind.String, ["family"] = FhirSearchKind.String, ["given"] = FhirSearchKind.String,
            ["gender"] = FhirSearchKind.Token, ["birthdate"] = FhirSearchKind.Date,
        },
        "Condition" or "AllergyIntolerance" => new Dictionary<string, FhirSearchKind>(Common)
        {
            ["clinical-status"] = FhirSearchKind.Token, ["verification-status"] = FhirSearchKind.Token,
        },
        "Appointment" => new Dictionary<string, FhirSearchKind>
        {
            ["identifier"] = FhirSearchKind.Token, ["patient"] = FhirSearchKind.Reference, ["actor"] = FhirSearchKind.Reference, ["status"] = FhirSearchKind.Token,
            ["date"] = FhirSearchKind.Date, ["service-type"] = FhirSearchKind.Token,
        },
        _ => Common,
    };

    private static readonly Dictionary<string, FhirSearchKind> Common = new()
    {
        ["identifier"] = FhirSearchKind.Token, ["patient"] = FhirSearchKind.Reference, ["subject"] = FhirSearchKind.Reference,
        ["encounter"] = FhirSearchKind.Reference, ["code"] = FhirSearchKind.Token, ["category"] = FhirSearchKind.Token,
        ["status"] = FhirSearchKind.Token, ["date"] = FhirSearchKind.Date, ["requester"] = FhirSearchKind.Reference,
        ["performer"] = FhirSearchKind.Reference, ["author"] = FhirSearchKind.Reference, ["type"] = FhirSearchKind.Token,
    };

    public static IReadOnlyList<SearchIndexEntry> Extract(Resource r)
    {
        var list = new List<SearchIndexEntry>();
        void Token(string p, string? system, string? code) { if (!string.IsNullOrEmpty(code)) list.Add(new(p, FhirSearchKind.Token, system, code)); }
        void Concept(string p, CodeableConcept? cc) { foreach (var c in cc?.Coding ?? []) Token(p, c.System, c.Code); }
        void Ref(string p, ResourceReference? rr) { if (rr?.Reference is { Length: > 0 } v) list.Add(new(p, FhirSearchKind.Reference, null, Normalize(v))); }
        void Str(string p, string? s) { if (!string.IsNullOrWhiteSpace(s)) list.Add(new(p, FhirSearchKind.String, null, s)); }
        void Date(string p, DataType? d)
        {
            if (DateRange(d) is { } range) list.Add(new(p, FhirSearchKind.Date, null, range.Start.ToString("O", CultureInfo.InvariantCulture), range.Start, range.End));
        }
        void Subject(ResourceReference? rr)
        {
            Ref("subject", rr);
            if (rr?.Reference is { } v && Normalize(v).StartsWith("Patient/", StringComparison.Ordinal)) Ref("patient", rr);
        }

        switch (r)
        {
            case Patient p:
                foreach (var i in p.Identifier) Token("identifier", i.System, i.Value);
                foreach (var n in p.Name)
                {
                    Str("family", n.Family);
                    foreach (var g in n.Given) { Str("given", g); Str("name", g); }
                    Str("name", n.Family);
                    Str("name", n.Text);
                }
                Token("gender", "http://hl7.org/fhir/administrative-gender", p.Gender?.ToString().ToLowerInvariant());
                if (p.BirthDate is { } bd) Date("birthdate", new FhirDateTime(bd));
                break;
            case Observation o:
                foreach (var i in o.Identifier) Token("identifier", i.System, i.Value);
                Subject(o.Subject); Ref("encounter", o.Encounter); Concept("code", o.Code);
                foreach (var c in o.Category) Concept("category", c);
                Token("status", null, Code(o.Status)); Date("date", o.Effective);
                foreach (var pf in o.Performer) Ref("performer", pf);
                break;
            case Condition c:
                foreach (var i in c.Identifier) Token("identifier", i.System, i.Value);
                Subject(c.Subject); Ref("encounter", c.Encounter); Concept("code", c.Code);
                foreach (var cat in c.Category) Concept("category", cat);
                Concept("status", c.ClinicalStatus); Concept("clinical-status", c.ClinicalStatus); Concept("verification-status", c.VerificationStatus);
                Date("date", c.Onset ?? (c.RecordedDate is { } rd ? new FhirDateTime(rd) : null));
                break;
            case Encounter e:
                foreach (var i in e.Identifier) Token("identifier", i.System, i.Value);
                Subject(e.Subject); Token("status", null, Code(e.Status)); Token("category", e.Class?.System, e.Class?.Code);
                foreach (var t in e.Type) Concept("type", t);
                Date("date", e.Period);
                foreach (var pa in e.Participant) Ref("performer", pa.Individual);
                break;
            case MedicationRequest m:
                foreach (var i in m.Identifier) Token("identifier", i.System, i.Value);
                Subject(m.Subject); Ref("encounter", m.Encounter); Concept("code", m.Medication as CodeableConcept);
                Token("status", null, Code(m.Status)); Ref("requester", m.Requester);
                if (m.AuthoredOn is { } ao) Date("date", new FhirDateTime(ao));
                break;
            case AllergyIntolerance a:
                foreach (var i in a.Identifier) Token("identifier", i.System, i.Value);
                Ref("patient", a.Patient); Ref("subject", a.Patient); Ref("encounter", a.Encounter); Concept("code", a.Code);
                Concept("status", a.ClinicalStatus); Concept("clinical-status", a.ClinicalStatus);
                break;
            case ServiceRequest s:
                foreach (var i in s.Identifier) Token("identifier", i.System, i.Value);
                Subject(s.Subject); Ref("encounter", s.Encounter); Concept("code", s.Code);
                foreach (var cat in s.Category) Concept("category", cat);
                Token("status", null, Code(s.Status)); Ref("requester", s.Requester);
                if (s.AuthoredOn is { } sa) Date("date", new FhirDateTime(sa));
                foreach (var pf in s.Performer) Ref("performer", pf);
                break;
            case Appointment ap:
                foreach (var i in ap.Identifier) Token("identifier", i.System, i.Value);
                Token("status", null, Code(ap.Status));
                foreach (var st in ap.ServiceType) Concept("service-type", st);
                foreach (var pa in ap.Participant)
                {
                    Ref("actor", pa.Actor);
                    if (pa.Actor?.Reference is { } ar && Normalize(ar).StartsWith("Patient/", StringComparison.Ordinal)) Ref("patient", pa.Actor);
                }
                if (ap.Start is { } start) Date("date", new FhirDateTime(start));
                break;
            case DocumentReference d:
                foreach (var i in d.Identifier) Token("identifier", i.System, i.Value);
                Subject(d.Subject); Concept("type", d.Type);
                foreach (var cat in d.Category) Concept("category", cat);
                Token("status", null, Code(d.Status));
                foreach (var au in d.Author) Ref("author", au);
                if (d.Date is { } dd) Date("date", new FhirDateTime(dd));
                foreach (var enc in d.Context?.Encounter ?? []) Ref("encounter", enc);
                break;
            case DiagnosticReport dr:
                foreach (var i in dr.Identifier) Token("identifier", i.System, i.Value);
                Subject(dr.Subject); Ref("encounter", dr.Encounter); Concept("code", dr.Code);
                foreach (var cat in dr.Category) Concept("category", cat);
                Token("status", null, Code(dr.Status)); Date("date", dr.Effective);
                break;
            case ImagingStudy im:
                foreach (var i in im.Identifier) Token("identifier", i.System, i.Value);
                Subject(im.Subject); Ref("encounter", im.Encounter); Token("status", null, Code(im.Status));
                if (im.Started is { } ims) Date("date", new FhirDateTime(ims));
                break;
            case Composition cp:
                Subject(cp.Subject); Concept("type", cp.Type); Token("status", null, Code(cp.Status));
                if (cp.Date is { } cd) Date("date", new FhirDateTime(cd));
                foreach (var au in cp.Author) Ref("author", au);
                break;
        }
        return list;
    }

    /// <summary>"http://server/fhir/Patient/1/_history/2" → "Patient/1".</summary>
    public static string Normalize(string reference)
    {
        var parts = reference.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int h = Array.IndexOf(parts, "_history");
        if (h >= 2) parts = parts[..h];
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : reference;
    }

    private static string? Code<T>(T? e) where T : struct, Enum =>
        e is { } v ? Hl7.Fhir.Utility.EnumUtility.GetLiteral(v) : null;

    /// <summary>Start/end instant covered by a FHIR date, dateTime, instant or Period (partial dates cover their whole span).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? DateRange(DataType? d)
    {
        switch (d)
        {
            case FhirDateTime dt: return Range(dt.Value);
            case Date date: return Range(date.Value);
            case Instant inst when inst.Value is { } iv: return (iv, iv);
            case Period p:
                var s = p.Start is { } ps ? Range(ps)?.Start : null;
                var e = p.End is { } pe ? Range(pe)?.End : null;
                if (s is null && e is null) return null;
                return (s ?? DateTimeOffset.MinValue, e ?? DateTimeOffset.MaxValue);
            default: return null;
        }
    }

    /// <summary>Parses a FHIR date/dateTime literal of any precision into the instant range it covers.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? Range(string? literal)
    {
        if (string.IsNullOrWhiteSpace(literal)) return null;
        var s = literal.Trim();
        var inv = CultureInfo.InvariantCulture;
        if (s.Length == 4 && int.TryParse(s, out int y)) { var a = new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero); return (a, a.AddYears(1).AddTicks(-1)); }
        if (s.Length == 7 && DateTime.TryParseExact(s, "yyyy-MM", inv, DateTimeStyles.None, out var ym)) { var a = new DateTimeOffset(ym, TimeSpan.Zero); return (a, a.AddMonths(1).AddTicks(-1)); }
        if (s.Length == 10 && DateTime.TryParseExact(s, "yyyy-MM-dd", inv, DateTimeStyles.None, out var ymd)) { var a = new DateTimeOffset(ymd, TimeSpan.Zero); return (a, a.AddDays(1).AddTicks(-1)); }
        if (DateTimeOffset.TryParse(s, inv, DateTimeStyles.AssumeUniversal, out var full)) return (full, full);
        return null;
    }
}
