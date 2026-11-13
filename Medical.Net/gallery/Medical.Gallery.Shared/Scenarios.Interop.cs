using Medical.Bpjs.Crypto;
using Medical.Bpjs.InaCbg;
using Medical.Core;
using Medical.Fhir;
using Medical.Fhir.SatuSehat;
using Medical.Fhir.Smart;
using Medical.Fhir.Validation;
using Medical.Fhir.Search;
using Medical.Hl7;
using Medical.Hl7.Conformance;
using F = Hl7.Fhir.Model;

namespace Medical.Gallery.Shared;

public sealed record QueryRow(string Query, int Total, int Included, string Note);
public sealed record HistoryRow(string Version, string Method, string LastUpdated, string Change);
public sealed record ValidationRow(string Resource, string Profile, bool Ok, IReadOnlyList<string> Issues);
public sealed record FhirServerView(IReadOnlyList<QueryRow> Queries, IReadOnlyList<HistoryRow> History, IReadOnlyList<ValidationRow> Validation, IReadOnlyList<(string Scope, Localized Meaning, string? Normalised)> Scopes, int Stored);

public sealed record Hl7OrderView(string Type, string Er7, ConformanceResult Conformance, ConformanceResult Broken, string BrokenEr7, IReadOnlyList<(string Resource, string Summary)> Fhir);

public sealed record BundleRow(string Resource, string FullUrl, string Summary, string Links);
public sealed record SatuSehatView(IReadOnlyList<BundleRow> Entries, IReadOnlyList<string> Warnings, string Json, int Medications);

public sealed record BpjsView(InaCbgClaim Claim, IReadOnlyList<ClaimIssue> Issues, IReadOnlyList<(string Field, string Value)> Data, IReadOnlyList<(string Component, decimal Amount)> Tariff,
    IReadOnlyList<(string Header, string Value)> Headers, string Compressed, int PlainLength);

public static partial class Scenarios
{
    private static readonly DateTimeOffset VisitAt = new(2026, 11, 13, 8, 0, 0, TimeSpan.FromHours(7));
    private static readonly Lazy<FhirServerView> Server = new(BuildFhirServer);

    // ---------------------------------------------------------------- FHIR server

    /// <summary>Search, history and profile validation against a seeded repository (computed once; validation loads the R4 spec).</summary>
    public static FhirServerView FhirServer() => Server.Value;

    private static FhirServerView BuildFhirServer()
    {
        var mapper = new FhirMapper();
        var store = new InMemoryFhirStore(new FixedClock(VisitAt));
        for (int i = 1; i <= 6; i++) store.Process(mapper.ToTransaction(SyntheticData.Record(i, i * 0.15)));

        QueryRow Q(string type, string query, string note)
        {
            var b = store.Search(type, FhirRepositoryBase.ParseQueryString(query));
            return new($"GET /{type}?{query}", b.Total ?? 0, b.Entry.Count(e => e.Search?.Mode == F.Bundle.SearchEntryMode.Include), note);
        }
        var queries = new[]
        {
            Q("Patient", "gender=female&_sort=-birthdate", "token + sort"),
            Q("Observation", "patient=pat-00003&category=laboratory&_include=Observation:patient", "_include"),
            Q("Patient", "_id=pat-00004&_revinclude=Condition:patient&_revinclude=MedicationRequest:patient", "_revinclude"),
            Q("Observation", "code=http://loinc.org|2345-7&date=ge2026-11-01&_sort=-date", "system|code, date prefix"),
            Q("Condition", "code:not=I10&clinical-status=active", ":not modifier"),
            Q("Encounter", "_summary=count", "_summary=count"),
        };

        var p = (F.Patient)store.Read("Patient", "pat-00002")!;
        p.Telecom.Add(new F.ContactPoint(F.ContactPoint.ContactPointSystem.Phone, F.ContactPoint.ContactPointUse.Mobile, "+62 812-0000-0002"));
        store.Update(p, "1");
        p.Active = false;
        store.Update(p, "W/\"2\"");
        string[] changes = ["inactive", "phone added", "created (transaction)"];
        var history = store.History("Patient", "pat-00002").Entry.Select((e, i) => new HistoryRow(
            e.Resource?.Meta?.VersionId ?? "?", e.Request?.Method?.ToString() ?? "", e.Resource?.Meta?.LastUpdated?.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss") ?? "", changes[Math.Min(i, 2)])).ToList();

        var validator = new FhirProfileValidator();
        var validation = new List<ValidationRow>();
        foreach (var o in mapper.ToFhir(SyntheticData.Record(1).LatestVitals!, "pat-00001"))
        {
            var oo = validator.Validate(o);
            validation.Add(new($"{o.Code.Coding[0].Display} · {o.Code.Coding[0].Code}", o.Meta?.Profile.FirstOrDefault()?.Split('/')[^1] ?? "—", FhirProfileValidator.IsSuccess(oo), Issues(oo)));
        }
        var bad = new F.Observation { Status = F.ObservationStatus.Final, Code = new F.CodeableConcept("http://loinc.org", "8867-4", "Heart rate"), Value = new F.Quantity(72, "/min") };
        var badOo = validator.Validate(bad, "http://hl7.org/fhir/StructureDefinition/heartrate");
        validation.Add(new("Heart rate (hand-written)", "heartrate", FhirProfileValidator.IsSuccess(badOo), Issues(badOo)));

        (string Scope, Localized Meaning)[] scopes =
        [
            ("patient/Observation.rs", new("read + search the launch patient's observations", "baca + cari observasi pasien yang diluncurkan")),
            ("patient/*.read", new("SMART v1 scope, normalised to v2", "scope SMART v1, dinormalisasi ke v2")),
            ("user/Encounter.cruds", new("full access within the user's permissions", "akses penuh sesuai hak pengguna")),
            ("system/*.rs", new("backend service (client_credentials)", "layanan backend (client_credentials)")),
            ("launch/patient openid fhirUser", new("standalone launch with patient picker", "peluncuran mandiri dengan pemilih pasien")),
        ];
        return new(queries, history, validation, scopes.Select(s => (s.Scope, s.Meaning, SmartScope.Parse(s.Scope)?.ToString())).ToList(), store.Count);

        static IReadOnlyList<string> Issues(F.OperationOutcome oo) => oo.Issue
            .Where(i => i.Severity is F.OperationOutcome.IssueSeverity.Error or F.OperationOutcome.IssueSeverity.Fatal)
            .Select(i => System.Text.RegularExpressions.Regex.Replace(i.Diagnostics ?? "", @"\s*\(ElementDefinition trace:.*\)$", "")).ToList();
    }

    private sealed class FixedClock(DateTimeOffset start) : TimeProvider
    {
        private int _ticks;
        public override DateTimeOffset GetUtcNow() => start.ToUniversalTime().AddSeconds(Interlocked.Increment(ref _ticks));
    }

    // ---------------------------------------------------------------- HL7 orders

    public static Hl7OrderView Hl7Orders(string type = "ORM", int patient = 5)
    {
        var rec = SyntheticData.Record(patient, 0.6);
        var p = rec.Patient;
        ServiceOrder[] orders =
        [
            new() { PatientId = p.Id, PlacerOrderNumber = "ORD-2026-0101", Code = Loinc.Glucose, Priority = OrderPriority.Stat, OrderedAt = VisitAt, Requester = "dr. Ratna Sari", Specimen = "SER", ClinicalInfo = "Polidipsia" },
            new() { PatientId = p.Id, PlacerOrderNumber = "ORD-2026-0102", Code = Loinc.HbA1c, OrderedAt = VisitAt, Requester = "dr. Ratna Sari", Specimen = "BLD" },
            new() { PatientId = p.Id, PlacerOrderNumber = "ORD-2026-0103", Code = ClinicalCode.Loinc("36643-5", "XR Chest 2 Views"), Category = OrderCategory.Radiology, OrderedAt = VisitAt, Requester = "dr. Ratna Sari" },
        ];
        Hl7Endpoint emr = new("EMR", "RSUD"), lis = new("LIS", "LAB-RSUD");
        NHapi.Base.Model.IMessage msg = type switch
        {
            "OML" => Hl7V2.BuildLabOrder(p, orders[..2], emr, lis, rec.Encounters[0], "MSG00101", VisitAt),
            "SIU" => Hl7V2.BuildAppointment(p, new Appointment
            {
                PatientId = p.Id, Start = VisitAt.AddDays(7), End = VisitAt.AddDays(7).AddMinutes(20), ServiceType = new ClinicalCode("urn:rsud:poli", "PD", "Poli Penyakit Dalam"),
                Practitioner = "dr. Ratna Sari", Location = "Poli PD · R. 3", Reason = "Kontrol DM tipe 2",
            }, new("SIMRS", "RSUD"), emr, "MSG00102", VisitAt),
            "MDM" => Hl7V2.BuildDocument(p, new ClinicalDocument
            {
                PatientId = p.Id, Type = ClinicalCode.Loinc("11506-3", "Progress note"), Title = "CPPT Poli Penyakit Dalam", Author = "dr. Ratna Sari", Created = VisitAt,
                Text = "S: poliuria, polidipsia 2 minggu\nO: TD 138/86, GDS 247 mg/dL\nA: DM tipe 2 tidak terkontrol\nP: metformin 500 mg 2x1, edukasi diet, kontrol 1 minggu",
            }, emr, new("DMS", "RSUD"), rec.Encounters[0], "MSG00103", VisitAt),
            _ => Hl7V2.BuildOrder(p, orders, emr, lis, rec.Encounters[0], controlId: "MSG00100", at: VisitAt),
        };
        string er7 = Hl7V2.Encode(msg);
        var parsed = Hl7V2.Parse(er7);
        var validator = new Hl7ConformanceValidator();
        // A deliberately broken variant: no PID-3 (MRN) and an unknown table value.
        string broken = string.Join('\r', er7.Split('\r').Select(seg => seg.StartsWith("PID|", StringComparison.Ordinal) ? BlankField(seg, 3) : seg)
            .Select(seg => seg.StartsWith("ORC|", StringComparison.Ordinal) || seg.StartsWith("SCH|", StringComparison.Ordinal) ? ReplaceField(seg, 1, "ZZ") : seg));
        var tx = Hl7Mapping.ToFhirTransaction(parsed);
        var fhir = tx.Entry.Select(e => (e.Resource!.TypeName, Summary(e.Resource))).ToList();
        return new(type, er7.Replace("\r", "\n").TrimEnd(), validator.Validate(parsed), validator.Validate(broken), broken.Replace("\r", "\n").TrimEnd(), fhir);

        static string BlankField(string seg, int index) => ReplaceField(seg, index, "");
        static string ReplaceField(string seg, int index, string value)
        {
            var f = seg.Split('|');
            if (f.Length > index) f[index] = value;
            return string.Join('|', f);
        }
        static string Summary(F.Resource r) => r switch
        {
            F.Patient pt => $"{pt.Name.FirstOrDefault()?.ToString()} · {pt.Identifier.FirstOrDefault()?.Value}",
            F.ServiceRequest sr => $"{sr.Code?.Coding.FirstOrDefault()?.Display ?? sr.Code?.Text} · {sr.Priority?.ToString().ToLowerInvariant() ?? "routine"} · {sr.Identifier.FirstOrDefault()?.Value}",
            F.Appointment ap => $"{ap.Start:yyyy-MM-dd HH:mm} – {ap.End:HH:mm} · {ap.Description}",
            F.DocumentReference dr => $"{dr.Type?.Coding.FirstOrDefault()?.Display} · {dr.Description}",
            F.Encounter en => $"{en.Class?.Code} · {en.Status}",
            _ => r.Id ?? "",
        };
    }

    // ---------------------------------------------------------------- SATUSEHAT

    public static SatuSehatView SatuSehat(int patient = 3, bool withKfa = true)
    {
        var rec = SyntheticData.Record(patient, 0.3);
        var ctx = new SatuSehatContext
        {
            PatientIhs = "P02478375538", PatientName = rec.Patient.Name.Full, PractitionerIhs = "N10000001", PractitionerName = "dr. Ratna Sari",
            LocationId = "b017aa54-f1df-4ec2-9d84-8823815d7228", LocationName = "Poli Penyakit Dalam", OrganizationId = "10000004",
        };
        KfaCatalog? kfa = null;
        if (withKfa)
        {
            // Illustrative placeholders: production codes come from the KFA API (KfaClient) or the official catalogue.
            kfa = new KfaCatalog();
            foreach (var m in rec.Medications.Skip(1)) kfa.Add(m.Generic, new KfaProduct($"9300000{m.Generic.Sum(ch => ch) % 1000:000}", $"{m.Generic} {m.DoseMg:0} mg Tablet", "Tablet"));
        }
        var sub = SatuSehatWorkflow.BuildOutpatientVisit(rec, ctx, kfa, VisitAt.AddMinutes(45));
        var rows = sub.Bundle.Entry.Select(e => new BundleRow(e.Resource!.TypeName, (e.FullUrl ?? "")[..Math.Min(e.FullUrl?.Length ?? 0, 22)] + "…", Describe(e.Resource), Links(e.Resource))).ToList();
        return new(rows, sub.Warnings, FhirJson.Serialize(sub.Bundle), sub.Bundle.Entry.Count(e => e.Resource is F.Medication));

        static string Describe(F.Resource r) => r switch
        {
            F.Encounter en => $"{en.Class?.Code} · {en.Status} · {en.StatusHistory.Count} status · {en.Diagnosis.Count} dx",
            F.Condition c => $"{c.Code?.Coding.FirstOrDefault()?.Code} {c.Code?.Coding.FirstOrDefault()?.Display}",
            F.Observation o => $"{o.Code.Coding.FirstOrDefault()?.Display} = {(o.Value as F.Quantity)?.Value} {(o.Value as F.Quantity)?.Unit}{(o.Component.Count > 0 ? string.Join("/", o.Component.Select(c => (c.Value as F.Quantity)?.Value)) + " mmHg" : "")}",
            F.Medication m => $"KFA {m.Code?.Coding.FirstOrDefault()?.Code} · {m.Code?.Coding.FirstOrDefault()?.Display}",
            F.MedicationRequest mr => mr.DosageInstruction.FirstOrDefault()?.Text ?? "",
            _ => r.TypeName,
        };
        string Links(F.Resource r)
        {
            var refs = new List<string>();
            void Walk(F.Base b)
            {
                if (b is F.ResourceReference rr && rr.Reference is { } v) refs.Add(v.StartsWith("urn:uuid:", StringComparison.Ordinal) ? "→" + v[9..15] : v.Split('/')[0]);
                foreach (var (_, val) in b.EnumerateElements())
                {
                    if (val is F.Base one) Walk(one);
                    else if (val is IEnumerable<F.Base> many) foreach (var m in many) Walk(m);
                }
            }
            Walk(r);
            return string.Join(", ", refs.Distinct().Take(4));
        }
    }

    // ---------------------------------------------------------------- BPJS

    public static BpjsView Bpjs(int patient = 7, bool broken = false)
    {
        var rec = SyntheticData.Record(patient, 0.9);
        var tariff = new HospitalTariff
        {
            ProsedurNonBedah = 350_000, Konsultasi = 300_000, TenagaAhli = 250_000, Keperawatan = 600_000, Laboratorium = 485_000, Radiologi = 220_000,
            Kamar = 1_500_000, Obat = 812_500, Bmhp = 95_000, SewaAlat = 120_000,
        };
        var claim = InaCbgClaimBuilder.FromRecord(rec, "0301R0011126V000123", "0001234567890", tariff, "3201234505900001", ["87.44", "99.04"]);
        if (broken) claim = claim with { NomorSep = "0301R00111", Diagnoses = ["R50.9", .. claim.Diagnoses.Where(d => d != "R50.9")], Procedures = ["8744"] };
        var data = claim.ClaimData();
        string[] keys = ["nomor_sep", "nomor_kartu", "tgl_masuk", "tgl_pulang", "jenis_rawat", "kelas_rawat", "discharge_status", "diagnosa", "procedure", "tarif_poli_eks", "nama_dokter", "kode_tarif", "payor_id", "cob_cd", "coder_nik"];
        var rows = keys.Where(k => data[k] is not null).Select(k => (k, data[k]!.ToString())).ToList();
        var parts = new (string, decimal)[]
        {
            ("prosedur_non_bedah", tariff.ProsedurNonBedah), ("konsultasi", tariff.Konsultasi), ("tenaga_ahli", tariff.TenagaAhli), ("keperawatan", tariff.Keperawatan),
            ("laboratorium", tariff.Laboratorium), ("radiologi", tariff.Radiologi), ("kamar", tariff.Kamar), ("obat", tariff.Obat), ("bmhp", tariff.Bmhp), ("sewa_alat", tariff.SewaAlat),
        };

        // VClaim request signing with demo credentials (never real ones in samples).
        var cred = new BpjsCredentials("1234", "demo-secret", "demo-user-key");
        string ts = BpjsCrypto.Timestamp(VisitAt);
        // A typical list response (referensi diagnosa): repetitive JSON is where LZ-String pays off.
        var list = new System.Text.Json.Nodes.JsonArray();
        foreach (var (code, name) in new[] { ("E11", "Type 2 diabetes mellitus"), ("E11.9", "Type 2 diabetes mellitus without complications"), ("E11.6", "Type 2 diabetes mellitus with other specified complications"),
                     ("E11.5", "Type 2 diabetes mellitus with peripheral circulatory complications"), ("E11.4", "Type 2 diabetes mellitus with neurological complications"), ("E11.2", "Type 2 diabetes mellitus with renal complications") })
            list.Add(new System.Text.Json.Nodes.JsonObject { ["kode"] = code, ["nama"] = $"{code} - {name}" });
        string sample = new System.Text.Json.Nodes.JsonObject { ["diagnosa"] = list }.ToJsonString();
        string compressed = LzString.CompressToEncodedURIComponent(sample);
        (string, string)[] headers = [("X-cons-id", cred.ConsId), ("X-timestamp", ts), ("X-signature", BpjsCrypto.Signature(cred, ts)), ("user_key", cred.UserKey)];
        return new(claim, InaCbgClaimBuilder.Validate(claim), rows, parts, headers, compressed, sample.Length);
    }
}
