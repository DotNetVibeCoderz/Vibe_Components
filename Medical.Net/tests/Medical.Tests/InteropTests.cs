using System.Net;
using Hl7.Fhir.Model;
using Medical.Core;
using Medical.Fhir;
using Medical.Hl7;
using Medical.Terminology;
using Task = System.Threading.Tasks.Task;

namespace Medical.Tests;

public class TerminologyTests
{
    private static readonly TerminologyService T = TerminologyService.Default;

    [Fact]
    public void EmbeddedSystemsLoaded()
    {
        Assert.True(T.Icd10.Count > 150);
        Assert.True(T.Loinc.Count > 60);
        Assert.True(T.Snomed.Count > 70);
        Assert.True(T.RxNorm.Count > 70);
    }

    [Fact]
    public void EveryEmbeddedCodeHasAValidFormatAndCheckDigit()
    {
        Assert.All(T.Icd10.Concepts, c => Assert.True(CodeValidators.IsIcd10Format(c.Code), c.Code));
        Assert.All(T.Loinc.Concepts, c => Assert.True(CodeValidators.IsValidLoinc(c.Code), c.Code));
        Assert.All(T.Snomed.Concepts, c => Assert.True(CodeValidators.IsConceptSctid(c.Code), c.Code));
        // every SNOMED map target exists in ICD-10
        Assert.All(T.Snomed.Concepts.Where(c => c.MapTarget is not null), c => Assert.NotNull(T.Icd10.Lookup(c.MapTarget!)));
        // every LOINC constant used in code is in the embedded set
        foreach (var f in typeof(Loinc).GetFields()) Assert.NotNull(T.Loinc.Lookup(((ClinicalCode)f.GetValue(null)!).Code));
    }

    [Theory]
    [InlineData("2345-7", true)]
    [InlineData("2345-8", false)]
    [InlineData("8867-4", true)]
    public void LoincCheckDigit(string code, bool valid) => Assert.Equal(valid, CodeValidators.IsValidLoinc(code));

    [Theory]
    [InlineData("44054006", true)]
    [InlineData("44054007", false)]
    [InlineData("22298006", true)]
    public void SctidVerhoeff(string id, bool valid) => Assert.Equal(valid, CodeValidators.IsValidSctid(id));

    [Fact]
    public void BilingualSearchRanksSensibly()
    {
        Assert.Equal("I10", T.Search("hipertensi", CodeSystems.Icd10)[0].Concept.Code);
        Assert.Equal("A91", T.Search("demam berdarah", CodeSystems.Icd10)[0].Concept.Code);
        Assert.Equal("E11.9", T.Search("E11.9")[0].Concept.Code);
        Assert.Contains(T.Search("gagal jan"), h => h.Concept.Code == "84114007");
        Assert.Empty(T.Search("zzzzqqq"));
    }

    [Fact]
    public void SubsumptionExpansionAndMaps()
    {
        Assert.True(T.Subsumes("73211009", "44054006"));   // DM ⊒ T2DM
        Assert.True(T.Subsumes("64572001", "44054006"));   // disease ⊒ T2DM
        Assert.False(T.Subsumes("44054006", "73211009"));
        var diabetes = T.Expand("73211009").Select(c => c.Code).ToArray();
        Assert.Contains("46635009", diabetes);
        Assert.Equal("E11.9", T.SnomedToIcd10("44054006")!.Target.Code);
        Assert.Contains(T.Icd10ToSnomed("I10"), m => m.Target.Code == "38341003");
    }

    [Fact]
    public void DrugLookupHandlesInnAndIndonesianNames()
    {
        Assert.Equal("161", T.Drug("paracetamol")!.Code);
        Assert.Equal("723", T.Drug("amoksisilin")!.Code);
        Assert.Contains("anticoagulant", T.Drug("warfarin")!.Tags);
    }

    [Fact]
    public void NikParsing()
    {
        // province 32, regency 01, district 23, born 05-05-1990 (male), serial 0001
        Assert.True(CodeValidators.TryParseNik("3201230505900001", out var dob, out var female));
        Assert.Equal(new DateOnly(1990, 5, 5), dob);
        Assert.False(female);
        Assert.True(CodeValidators.TryParseNik("3201234505900001", out _, out female)); // day + 40 → female
        Assert.True(female);
        Assert.False(CodeValidators.TryParseNik("12345", out _, out _));
    }

    [Fact]
    public void LoadersReadReleaseFormats()
    {
        string dir = Directory.CreateTempSubdirectory("mednet").FullName;
        File.WriteAllLines(Path.Combine(dir, "desc.txt"),
        [
            "id\teffectiveTime\tactive\tmoduleId\tconceptId\tlanguageCode\ttypeId\tterm\tcaseSignificanceId",
            "1\t20250101\t1\t9\t44054006\ten\t900000000000003001\tDiabetes mellitus type 2 (disorder)\t9",
            "2\t20250101\t1\t9\t73211009\ten\t900000000000003001\tDiabetes mellitus (disorder)\t9",
        ]);
        File.WriteAllLines(Path.Combine(dir, "rel.txt"),
        [
            "id\teffectiveTime\tactive\tmoduleId\tsourceId\tdestinationId\trelationshipGroup\ttypeId\tcharacteristicTypeId\tmodifierId",
            "9\t20250101\t1\t9\t44054006\t73211009\t0\t116680003\t9\t9",
        ]);
        var snomed = ReleaseLoaders.LoadSnomedRf2(Path.Combine(dir, "desc.txt"), Path.Combine(dir, "rel.txt"));
        Assert.Equal("disorder", snomed.Lookup("44054006")!.Category);
        Assert.True(snomed.Subsumes("73211009", "44054006"));

        File.WriteAllLines(Path.Combine(dir, "Loinc.csv"), ["\"LOINC_NUM\",\"CLASS\",\"LONG_COMMON_NAME\",\"STATUS\"", "\"2345-7\",\"CHEM\",\"Glucose [Mass/volume] in Serum or Plasma\",\"ACTIVE\""]);
        Assert.Equal("CHEM", ReleaseLoaders.LoadLoincCsv(Path.Combine(dir, "Loinc.csv")).Lookup("2345-7")!.Category);

        File.WriteAllLines(Path.Combine(dir, "rx.rrf"), ["6809|ENG||||||||||RXNORM|IN|6809|metformin||N||"]);
        Assert.Equal("metformin", ReleaseLoaders.LoadRxNormRrf(Path.Combine(dir, "rx.rrf")).Lookup("6809")!.Display);
    }
}

public class FhirTests
{
    private readonly FhirMapper _m = new();

    [Fact]
    public void PatientRoundTrip()
    {
        var p = SyntheticData.Patient(3);
        var fhir = _m.ToFhir(p);
        Assert.Contains(fhir.Identifier, i => i.System == CodeSystems.Nik && i.Value == p.Nik);
        var json = FhirJson.Serialize(fhir);
        var back = _m.FromFhir(FhirJson.Parse<global::Hl7.Fhir.Model.Patient>(json));
        Assert.Equal(p.Mrn, back.Mrn);
        Assert.Equal(p.Name, back.Name);
        Assert.Equal(p.BirthDate, back.BirthDate);
        Assert.Equal(p.Gender, back.Gender);
    }

    [Fact]
    public void VitalSignsUseProfilesAndBpComponents()
    {
        var v = SyntheticData.VitalsSeries(1, 0, DateTimeOffset.UnixEpoch)[0];
        var obs = _m.ToFhir(v, "p1");
        Assert.Equal(5, obs.Count);
        var bp = obs.Single(o => o.Code.Coding[0].Code == "85354-9");
        Assert.Equal(2, bp.Component.Count);
        Assert.All(obs, o => Assert.Equal("vital-signs", o.Category[0].Coding[0].Code));
        Assert.All(obs, o => Assert.True(FhirJson.IsValid(o)));
    }

    [Fact]
    public void LabObservationRoundTripKeepsRangeAndFlag()
    {
        var lab = SyntheticData.Record(2).Observations[0];
        var back = _m.FromFhir(FhirJson.Parse<Observation>(FhirJson.Serialize(_m.ToFhir(lab))));
        Assert.Equal(lab.Value, back.Value);
        Assert.Equal(lab.ReferenceHigh, back.ReferenceHigh);
        Assert.Equal(lab.Flag, back.Flag);
        Assert.Equal(lab.Code.Code, back.Code.Code);
    }

    [Fact]
    public void PatientSummaryDocumentIsValid()
    {
        var rec = SyntheticData.Record(4, 0.6);
        var ips = _m.ToPatientSummary(rec, new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(Bundle.BundleType.Document, ips.Type);
        var comp = Assert.IsType<Composition>(ips.Entry[0].Resource);
        Assert.Equal(5, comp.Section.Count);
        Assert.Empty(FhirJson.Validate(ips).Issue);
        Assert.All(ips.Entry, e => Assert.StartsWith("urn:uuid:", e.FullUrl));
        // deterministic document identifiers
        Assert.Equal(ips.Identifier.Value, _m.ToPatientSummary(rec, new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero)).Identifier.Value);
    }

    [Fact]
    public void FhirPathSelect() =>
        Assert.Equal([SyntheticData.Patient(1).Name.Family], FhirJson.Select(_m.ToFhir(SyntheticData.Patient(1)), "Patient.name.family"));

    [Fact]
    public void StoreCrudSearchVersioningAndTransactions()
    {
        var store = new InMemoryFhirStore();
        var rec = SyntheticData.Record(6, 0.5);
        var resp = store.Process(_m.ToTransaction(rec));
        Assert.Equal(Bundle.BundleType.TransactionResponse, resp.Type);
        Assert.All(resp.Entry, e => Assert.StartsWith("201", e.Response.Status));
        Assert.Equal("2", store.Update(_m.ToFhir(rec.Patient)).Meta.VersionId);
        Assert.Equal(1, store.Search("Patient", [new("identifier", $"{CodeSystems.Nik}|{rec.Patient.Nik}")]).Total);
        Assert.Equal(rec.Observations.Count + 5, store.Search("Observation", [new("patient", rec.Patient.Id)]).Total);
        Assert.Equal(1, store.Search("Observation", [new("code", "http://loinc.org|2345-7")]).Total);
        Assert.True(store.Everything(rec.Patient.Id).Total > 10);
        Assert.True(store.Delete("Patient", rec.Patient.Id));
        Assert.Null(store.Read("Patient", rec.Patient.Id));
    }

    [Fact]
    public void FailedTransactionRollsBack()
    {
        var store = new InMemoryFhirStore();
        var tx = new Bundle { Type = Bundle.BundleType.Transaction };
        tx.Entry.Add(new Bundle.EntryComponent { Resource = new global::Hl7.Fhir.Model.Patient { Id = "a" }, Request = new() { Method = Bundle.HTTPVerb.PUT, Url = "Patient/a" } });
        tx.Entry.Add(new Bundle.EntryComponent { Request = new() { Method = Bundle.HTTPVerb.DELETE, Url = "Patient/missing" } });
        Assert.Throws<InvalidOperationException>(() => store.Process(tx));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void SatuSehatRequestShape()
    {
        var c = new SatuSehatClient(new HttpClient(), new SatuSehatOptions { OrganizationId = "100026342" });
        using var req = c.BuildRequest(HttpMethod.Post, "Encounter", new global::Hl7.Fhir.Model.Encounter { Id = "e1" }, "TOKEN");
        Assert.Equal("https://api-satusehat-stg.dto.kemkes.go.id/fhir-r4/v1/Encounter", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        var prepared = c.PrepareEncounter(_m.ToFhir(SyntheticData.Record(1).Encounters[0]), "P02478375538", "Budi");
        Assert.Equal("http://sys-ids.kemkes.go.id/encounter/100026342", prepared.Identifier[0].System);
        Assert.Equal("Organization/100026342", prepared.ServiceProvider.Reference);
    }
}

public class Hl7Tests
{
    private const string Oru = "MSH|^~\\&|LIS|RSUD|EMR|RSUD|20261113083000+0700||ORU^R01^ORU_R01|MSG0001|P|2.5.1\r" +
        "PID|1||MRN555^^^MEDNET^MR~3201234505900001^^^KEMKES^NI||Santoso^Budi||19900505|M\r" +
        "OBR|1|ORD9||24323-8^Metabolic panel^LN|||20261113080000+0700\r" +
        "OBX|1|NM|2823-3^Potassium^LN||6.9|mmol/L^^UCUM|3.5-5.1|HH|||F|||20261113080000+0700\r" +
        "OBX|2|NM|2160-0^Creatinine^LN||1.4|mg/dL^^UCUM|0.7-1.3|H|||F\r";

    [Fact]
    public void ParsesOruAndMapsToDomain()
    {
        var m = Hl7V2.Parse(Oru.Replace('\r', '\n'));
        Assert.Equal("ORU^R01", m.Type);
        Assert.Equal("MSG0001", m.ControlId);
        var p = Hl7Mapping.ToPatient(m);
        Assert.Equal("MRN555", p.Mrn);
        Assert.Equal("3201234505900001", p.Nik);
        Assert.Equal(new DateOnly(1990, 5, 5), p.BirthDate);
        var obs = Hl7Mapping.ToObservations(m, p.Id);
        Assert.Equal(2, obs.Count);
        Assert.Equal(ResultFlag.CriticalHigh, obs[0].Flag);
        Assert.Equal(CodeSystems.Loinc, obs[0].Code.System);
        Assert.Equal(5.1, obs[0].ReferenceHigh);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.FromHours(7)), obs[0].Effective);
    }

    [Fact]
    public void AckSwapsEndpointsAndEchoesControlId()
    {
        var ack = Hl7V2.Parse(Hl7V2.BuildAck(Hl7V2.Parse(Oru), "AE", "bad"));
        Assert.Equal("EMR", ack.SendingApplication);
        Assert.Equal("MSG0001", ack.Get("MSA-2"));
        Assert.Equal("AE", ack.Get("MSA-1"));
    }

    [Fact]
    public void BuiltMessagesRoundTrip()
    {
        var rec = SyntheticData.Record(9);
        var adt = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildAdmit(rec.Patient, rec.Encounters[0], new("ADT", "RS"), new("EMR", "RS"), "C1")));
        Assert.Equal("ADT^A01", adt.Type);
        Assert.Equal(rec.Patient.Name, Hl7Mapping.ToPatient(adt).Name);
        Assert.Equal(rec.Encounters[0].Id, Hl7Mapping.ToEncounter(adt, "x")!.Id);
        var oru = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildResults(rec.Patient, rec.Observations, new("LIS", "LAB"), new("EMR", "RS"))));
        Assert.Equal(rec.Observations.Count, Hl7Mapping.ToObservations(oru, "x").Count);
        var bundle = Hl7Mapping.ToFhirTransaction(oru);
        Assert.Equal(1 + rec.Observations.Count, bundle.Entry.Count);
    }

    [Fact]
    public async Task MllpLoopbackAcknowledges()
    {
        var received = new List<string>();
        await using var server = new MllpServer(new IPEndPoint(IPAddress.Loopback, 0), (m, _) => { lock (received) received.Add(m.ControlId); return ValueTask.FromResult(Hl7V2.BuildAck(m)); });
        server.Start();
        await using var client = new MllpClient("127.0.0.1", server.Endpoint.Port);
        var ack1 = Hl7V2.Parse(await client.SendAsync(Oru));
        var ack2 = Hl7V2.Parse(await client.SendAsync(Oru.Replace("MSG0001", "MSG0002")));
        var reject = await client.SendAsync("this is not hl7");
        Assert.Equal("AA", ack1.Get("MSA-1"));
        Assert.Equal("MSG0002", ack2.Get("MSA-2"));
        Assert.Contains("MSA|AR", reject);
        Assert.Equal(["MSG0001", "MSG0002"], received);
    }

    [Fact]
    public async Task MllpFramingSurvivesChunkBoundaries()
    {
        var bytes = Mllp.Frame("A|1\r").Concat(Mllp.Frame("B|2\r")).ToArray();
        var stream = new TrickleStream(bytes, 3);
        var msgs = new List<string>();
        await foreach (var m in Mllp.ReadMessagesAsync(stream)) msgs.Add(m);
        Assert.Equal(["A|1\r", "B|2\r"], msgs);
    }

    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], ct);
    }
}
