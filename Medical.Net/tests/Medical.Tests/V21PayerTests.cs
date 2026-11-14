using System.Net;
using System.Text.Json.Nodes;
using Medical.Bpjs.Crypto;
using Medical.Bpjs.InaCbg;
using Medical.Bpjs.VClaim;
using Medical.Core;
using Medical.Fhir;
using Medical.His;
using Medical.Hospital;
using Medical.Hospital.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using F = Hl7.Fhir.Model;

namespace Medical.Tests;

/// <summary>v2.1 payer / national-platform workflows from the HIS against fakes that speak the BPJS, E-Klaim and SATUSEHAT wire formats.</summary>
public class V21PayerTests
{
    private static readonly BpjsCredentials Cred = new("1234", "s3cret", "user-key");
    private const string Key = "4a6f8c2d1e9b7a3c5d8e2f1a6b9c4d7e0f3a8b5c2d9e6f1a4b7c0d3e8f5a2b9c";
    private const string Sep = "0301R0111126V000001";
    private const string CoderNik = "3171012501900009";

    private static async Task<(Department Clinic, HisPatient Patient, Staff Doctor)> SetupAsync(HospitalDbContext db, HisFixture fx)
    {
        var clinic = new Department { Code = "POLI-UMUM", Name = "General", NameId = "Poli Umum", Kind = DepartmentKind.Outpatient, QueuePrefix = "A" };
        var doctor = new Staff { UserName = "dr.andi", Name = "dr. Andi", Role = StaffRole.Doctor, DepartmentId = clinic.Id, Nik = "3171010101800001" };
        db.Departments.Add(clinic);
        db.Staff.Add(doctor);
        db.Tariffs.Add(new Tariff { Code = "REG", Category = ChargeCategory.Registration, Price = 50_000 });
        db.Tariffs.Add(new Tariff { Code = "KONS-UMUM", Category = ChargeCategory.Consultation, Price = 75_000 });
        await db.SaveChangesAsync();
        var p = await new RegistrationService(db, fx.Clock).RegisterAsync(new NewPatient("Siti", "Rahayu", Gender.Female, new DateOnly(1990, 1, 25), "3171012501900001", "0001234567890"));
        return (clinic, p, doctor);
    }

    /// <summary>An outpatient visit seen by the doctor, with a diagnosis and vitals, finished 20 minutes after it opened.</summary>
    private static async Task<Visit> FinishedVisitAsync(HospitalDbContext db, HisFixture fx, Department clinic, HisPatient p, Staff doctor, PayerType payer, string? sep, string icd10)
    {
        var v = await new RegistrationService(db, fx.Clock).StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, payer, sep);
        var clin = new ClinicalService(db, fx.Clock);
        await clin.CallNextAsync(clinic.Id, doctor.Id);
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        await clin.RecordVitalsAsync(p.Id, v.Id, new VitalSigns
        {
            Timestamp = fx.Clock.GetUtcNow(), RespiratoryRate = 16, SpO2 = 98, SystolicBp = 150, DiastolicBp = 95, HeartRate = 82, TemperatureC = 36.8, Consciousness = Consciousness.Alert,
        });
        await clin.AddDiagnosisAsync(v.Id, icd10);
        fx.Clock.Advance(TimeSpan.FromMinutes(15));
        await clin.FinishVisitAsync(v.Id);
        return v;
    }

    // ---------------------------------------------------------------- VClaim fake (signature check + AES/LZ-String responses keyed by X-timestamp)

    private static HttpResponseMessage Bpjs(HttpRequestMessage req, string code, string message, string? json)
    {
        string ts = req.Headers.GetValues("X-timestamp").Single();
        Assert.Equal(BpjsCrypto.Signature(Cred, ts), req.Headers.GetValues("X-signature").Single());
        return FakeHandler.Json(new JsonObject
        {
            ["metaData"] = new JsonObject { ["code"] = code, ["message"] = message },
            ["response"] = json is null ? null : BpjsCrypto.EncryptResponse(Cred, ts, json),
        }.ToJsonString());
    }

    [Fact]
    public async Task SepIsCreatedThroughVClaimAndStoredOnTheVisit()
    {
        using var fx = new HisFixture();
        await using var db = fx.Ctx("t1", "pendaftaran", StaffRole.Registration);
        var (clinic, p, _) = await SetupAsync(db, fx);
        var v = await new RegistrationService(db, fx.Clock).StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Bpjs);
        JsonNode? tSep = null;
        var fake = new FakeHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Get)
                return Bpjs(req, "200", "OK", "{\"peserta\":{\"noKartu\":\"0001234567890\",\"nama\":\"SITI RAHAYU\",\"hakKelas\":{\"kode\":\"1\",\"keterangan\":\"KELAS I\"},\"statusPeserta\":{\"kode\":\"0\",\"keterangan\":\"AKTIF\"}}}");
            tSep = JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!["request"]!["t_sep"];
            return Bpjs(req, "200", "Sukses", $"{{\"sep\":{{\"noSep\":\"{Sep}\",\"tglSep\":\"2026-11-16\"}}}}");
        });
        var svc = new BpjsWorkflowService(db, new VClaimClient(new HttpClient(fake), Cred, clock: fx.Clock), null, fx.Clock);
        var request = new SepRequest
        {
            PpkPelayanan = "0301R011", DiagnosaAwal = "i10", PoliTujuan = "INT", KodeDpjp = "31574",
            Rujukan = new SepReferral("1", "030107010217Y001465", new DateOnly(2026, 11, 10), "03010701"),
        };

        Assert.Equal(Sep, await svc.CreateSepAsync(v.Id, request));
        Assert.Equal(Sep, (await db.Visits.SingleAsync(x => x.Id == v.Id)).SepNumber);
        Assert.Contains(await db.Audit.ToListAsync(), a => a.Resource == "Visit/SEP" && a.Detail == Sep);
        Assert.EndsWith("/Peserta/nokartu/0001234567890/tglSEP/2026-11-16", fake.Requests[0].RequestUri!.ToString());
        Assert.EndsWith("/vclaim-rest-dev/SEP/2.0/insert", fake.Requests[1].RequestUri!.ToString());
        Assert.Equal("0001234567890", tSep!["noKartu"]!.ToString());
        Assert.Equal("2026-11-16", tSep["tglSep"]!.ToString());                       // 02:00 UTC = 09:00 WIB
        Assert.Equal("2", tSep["jnsPelayanan"]!.ToString());                          // rawat jalan
        Assert.Equal("1", tSep["klsRawat"]!["klsRawatHak"]!.ToString());              // from Peserta.hakKelas
        Assert.Equal("INT", tSep["poli"]!["tujuan"]!.ToString());
        Assert.Equal("I10", tSep["diagAwal"]!.ToString());
        Assert.Equal("31574", tSep["dpjpLayan"]!.ToString());
        Assert.Equal("030107010217Y001465", tSep["rujukan"]!["noRujukan"]!.ToString());
        Assert.Equal(p.Mrn, tSep["noMR"]!.ToString());

        var again = await Assert.ThrowsAsync<PayerIntegrationException>(() => svc.CreateSepAsync(v.Id, request));
        Assert.Contains("sudah memiliki SEP", again.Text.Id);
        Assert.Equal(2, fake.Requests.Count);

        // Business error from VClaim (metaData 201): nothing stored, bilingual message carries the BPJS text.
        await using var db2 = fx.Ctx("t1");
        var p2 = await new RegistrationService(db2, fx.Clock).RegisterAsync(new NewPatient("Budi", "Santoso", Gender.Male, new DateOnly(1980, 5, 1), "3171010105800002", "0009876543210"));
        var v2 = await new RegistrationService(db2, fx.Clock).StartVisitAsync(p2.Id, clinic.Id, VisitKind.Outpatient, PayerType.Bpjs);
        var refuse = new FakeHandler((req, _) => Bpjs(req, "201", "Rujukan sudah digunakan", null));
        var svc2 = new BpjsWorkflowService(db2, new VClaimClient(new HttpClient(refuse), Cred, clock: fx.Clock), null, fx.Clock);
        var ex = await Assert.ThrowsAsync<PayerIntegrationException>(() => svc2.CreateSepAsync(v2.Id, request with { KelasHak = 3 }));
        Assert.Contains("Rujukan sudah digunakan", ex.Text.Id);
        Assert.Null((await db2.Visits.SingleAsync(x => x.Id == v2.Id)).SepNumber);

        // Not configured: a clear bilingual message instead of a crash.
        var none = await Assert.ThrowsAsync<PayerIntegrationException>(() => new BpjsWorkflowService(db2, null, null, fx.Clock).CreateSepAsync(v2.Id, request));
        Assert.Contains("belum dikonfigurasi", none.Text.Id);
    }

    // ---------------------------------------------------------------- E-Klaim fake (encrypted request/response envelopes)

    private sealed class EKlaimFake
    {
        public List<string> Methods { get; } = [];
        public List<JsonNode> Calls { get; } = [];
        public Func<string, int, JsonObject?> Fail { get; set; } = (_, _) => null;
        public FakeHandler Handler { get; }

        public EKlaimFake() => Handler = new FakeHandler((req, n) =>
        {
            var call = JsonNode.Parse(EKlaimCrypto.Decrypt(req.Content!.ReadAsStringAsync().Result, Key))!;
            string method = call["metadata"]!["method"]!.ToString();
            Methods.Add(method);
            Calls.Add(call);
            var resp = Fail(method, n) ?? new JsonObject
            {
                ["metadata"] = new JsonObject { ["code"] = 200, ["message"] = "Ok" },
                ["response"] = method == "grouper"
                    ? new JsonObject { ["cbg"] = new JsonObject { ["code"] = "Q-5-44-0", ["description"] = "PENYAKIT KRONIS KECIL LAIN-LAIN", ["tariff"] = "186000" }, ["kelas"] = "kelas_3" }
                    : new JsonObject(),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{EKlaimCrypto.Begin}\n{EKlaimCrypto.Encrypt(resp.ToJsonString(), Key)}\n{EKlaimCrypto.End}") };
        });

        public EKlaimClient Client => new(new HttpClient(Handler), "http://eklaim/E-Klaim/ws.php", Key);
    }

    [Fact]
    public async Task ClaimWithValidationErrorsIsRefusedBeforeEKlaim()
    {
        using var fx = new HisFixture();
        await using var db = fx.Ctx("t1", "koder", StaffRole.Coder);
        var (clinic, p, doctor) = await SetupAsync(db, fx);
        var v = await FinishedVisitAsync(db, fx, clinic, p, doctor, PayerType.Bpjs, sep: null, "I10");   // no SEP
        var fake = new EKlaimFake();
        var outcome = await new BpjsWorkflowService(db, null, fake.Client, fx.Clock).SubmitClaimAsync(v.Id, "123");   // bad coder NIK too

        Assert.True(outcome.Refused);
        Assert.Empty(fake.Methods);
        Assert.Contains(outcome.Issues, i => i.Field == "nomor_sep" && i.Severity == ClaimIssueSeverity.Error);
        var rec = await db.Set<BpjsClaimRecord>().SingleAsync();
        Assert.Equal(BpjsClaimStatus.ValidationFailed, rec.Status);
        Assert.Contains("nomor_sep", rec.LastError);
        Assert.Contains("coder_nik", rec.LastError);
    }

    [Fact]
    public async Task FullEKlaimSequenceStoresGrouperResultAndSetsInvoicePayerAmount()
    {
        using var fx = new HisFixture();
        await using var db = fx.Ctx("t1", "koder", StaffRole.Coder);
        var (clinic, p, doctor) = await SetupAsync(db, fx);
        var v = await FinishedVisitAsync(db, fx, clinic, p, doctor, PayerType.Bpjs, Sep, "I10");
        var inv = await new BillingService(db, fx.Clock).IssueAsync(v.Id);
        Assert.Equal(125_000m, inv.PayerAmount);                                       // before grouping: hospital tariff

        var fake = new EKlaimFake();
        var svc = new BpjsWorkflowService(db, null, fake.Client, fx.Clock);
        var outcome = await svc.SubmitClaimAsync(v.Id, CoderNik);

        Assert.True(outcome.Sent, outcome.Record.LastError);
        Assert.Equal(["new_claim", "set_claim_data", "grouper", "claim_final", "send_claim_individual"], fake.Methods);
        Assert.Equal(Sep, fake.Calls[1]["metadata"]!["nomor_sep"]!.ToString());
        Assert.Equal("1", fake.Calls[2]["metadata"]!["stage"]!.ToString());
        Assert.Equal(CoderNik, fake.Calls[3]["data"]!["coder_nik"]!.ToString());
        Assert.Equal("I10", fake.Calls[1]["data"]!["diagnosa"]!.ToString());
        Assert.Equal("2", fake.Calls[1]["data"]!["jenis_rawat"]!.ToString());

        var rec = await db.Set<BpjsClaimRecord>().SingleAsync();
        Assert.Equal(("Q-5-44-0", "PENYAKIT KRONIS KECIL LAIN-LAIN", 186_000m), (rec.CbgCode, rec.CbgDescription, rec.CbgTariff));
        Assert.Equal(125_000m, rec.HospitalTariff);
        Assert.Equal(-61_000m, rec.Selisih);                                          // INA-CBG pays more than the hospital tariff
        Assert.Equal(fx.Clock.GetUtcNow().UtcDateTime, rec.SubmittedAt);
        Assert.Contains("Q-5-44-0", rec.RawResponse);

        var after = await db.Invoices.SingleAsync();
        Assert.Equal(186_000m, after.PayerAmount);
        Assert.Equal(125_000m, after.Total);                                          // the hospital tariff stays for the selisih report
        var report = await svc.SelisihReportAsync(fx.Clock.GetUtcNow().UtcDateTime.AddDays(-1), fx.Clock.GetUtcNow().UtcDateTime.AddDays(1));
        Assert.Equal(-61_000m, Assert.Single(report).Selisih);
        Assert.Contains(await db.Audit.ToListAsync(), a => a.Resource == "Claim/E-Klaim" && a.Detail!.Contains("Q-5-44-0"));

        await Assert.ThrowsAsync<PayerIntegrationException>(() => svc.SubmitClaimAsync(v.Id, CoderNik));   // already sent
        Assert.Equal(5, fake.Methods.Count);
    }

    [Fact]
    public async Task EKlaimErrorIsRecordedAndTheRetryResumes()
    {
        using var fx = new HisFixture();
        await using var db = fx.Ctx("t1", "koder", StaffRole.Coder);
        var (clinic, p, doctor) = await SetupAsync(db, fx);
        var v = await FinishedVisitAsync(db, fx, clinic, p, doctor, PayerType.Bpjs, Sep, "I10");
        await new BillingService(db, fx.Clock).IssueAsync(v.Id);
        var fake = new EKlaimFake
        {
            Fail = (m, _) => m == "grouper" ? new JsonObject { ["metadata"] = new JsonObject { ["code"] = 400, ["message"] = "Klaim belum lengkap" } } : null,
        };
        var svc = new BpjsWorkflowService(db, null, fake.Client, fx.Clock);
        var failed = await svc.SubmitClaimAsync(v.Id, CoderNik);

        Assert.False(failed.Sent);
        var rec = await db.Set<BpjsClaimRecord>().SingleAsync();
        Assert.Equal(BpjsClaimStatus.Failed, rec.Status);
        Assert.Equal(BpjsClaimStatus.DataSet, rec.Completed);
        Assert.Equal("grouper: E-Klaim 400: Klaim belum lengkap", rec.LastError);
        Assert.Equal(125_000m, (await db.Invoices.SingleAsync()).PayerAmount);         // untouched without a grouper result

        fake.Fail = (_, _) => null;
        fake.Methods.Clear();
        var ok = await svc.SubmitClaimAsync(v.Id, CoderNik);
        Assert.True(ok.Sent);
        Assert.Equal(["set_claim_data", "grouper", "claim_final", "send_claim_individual"], fake.Methods);   // no duplicate new_claim
        Assert.Null(ok.Record.LastError);
        Assert.Equal(2, ok.Record.Attempts);
        Assert.Equal(186_000m, (await db.Invoices.SingleAsync()).PayerAmount);
    }

    // ---------------------------------------------------------------- SATUSEHAT fake (OAuth token, NIK lookups, transaction endpoint)

    [Fact]
    public async Task FinishedVisitIsPublishedToSatuSehatAndFailuresAreRetried()
    {
        using var fx = new HisFixture();
        await using var db = fx.Ctx("t1", "dr.andi", StaffRole.Doctor);
        var (clinic, p, doctor) = await SetupAsync(db, fx);
        var earlier = await FinishedVisitAsync(db, fx, clinic, p, doctor, PayerType.Umum, null, "E11.9");
        fx.Clock.Advance(TimeSpan.FromHours(2));
        var v = await FinishedVisitAsync(db, fx, clinic, p, doctor, PayerType.Umum, null, "I10");

        bool down = true;
        int patientLookups = 0;
        F.Bundle? posted = null;
        var fake = new FakeHandler((req, _) =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("accesstoken")) return FakeHandler.Json("{\"access_token\":\"tok\",\"expires_in\":\"3599\"}");
            Assert.Equal("tok", req.Headers.Authorization!.Parameter);
            if (url.Contains("/Patient?identifier="))
            {
                patientLookups++;
                Assert.Contains("https://fhir.kemkes.go.id/id/nik|3171012501900001", Uri.UnescapeDataString(req.RequestUri.Query));
                return FakeHandler.Json("{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"entry\":[{\"resource\":{\"resourceType\":\"Patient\",\"id\":\"P02478375538\"}}]}");
            }
            if (url.Contains("/Practitioner?identifier="))
                return FakeHandler.Json("{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"entry\":[{\"resource\":{\"resourceType\":\"Practitioner\",\"id\":\"N10000001\"}}]}");
            if (down)
                return FakeHandler.Json("{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"exception\",\"diagnostics\":\"service unavailable\"}]}", HttpStatusCode.ServiceUnavailable);
            posted = FhirJson.Parse<F.Bundle>(req.Content!.ReadAsStringAsync().Result);
            var resp = new F.Bundle { Type = F.Bundle.BundleType.TransactionResponse };
            foreach (var e in posted.Entry)
                resp.Entry.Add(new F.Bundle.EntryComponent
                {
                    Response = new F.Bundle.ResponseComponent { Status = "201 Created", Location = e.Resource is F.Encounter ? "Encounter/enc-srv-1/_history/1" : $"{e.Resource!.TypeName}/x/_history/1" },
                });
            return FakeHandler.Json(FhirJson.Serialize(resp, false));
        });
        var client = new SatuSehatClient(new HttpClient(fake), new SatuSehatOptions { ClientId = "id", ClientSecret = "secret", OrganizationId = "10000004" }, fx.Clock);
        var publisher = new SatuSehatPublisher(db, client, new SatuSehatPublisherOptions { DefaultLocationId = "b017aa54-f1df-4ec2-9d84-8823815d7228" }, fx.Clock);

        var row = await publisher.PublishVisitAsync(v.Id);
        Assert.Equal(SatuSehatSubmissionStatus.Failed, row.Status);
        Assert.Contains("503", row.LastError);
        Assert.Contains("service unavailable", row.LastError);
        Assert.Equal("P02478375538", row.PatientIhs);

        down = false;
        Assert.Equal(1, await publisher.EnqueueFinishedVisitsAsync(DateTime.MinValue));   // the earlier visit; v is already queued
        var run = await publisher.RetryPendingAsync();
        Assert.Equal(new SatuSehatRunResult(2, 0), run);
        var sent = await db.Set<SatuSehatSubmissionRecord>().SingleAsync(s => s.VisitId == v.Id);
        Assert.Equal((SatuSehatSubmissionStatus.Sent, "enc-srv-1", 2), (sent.Status, sent.EncounterId, sent.Attempts));
        Assert.Null(sent.LastError);
        Assert.Equal(2, patientLookups);                                              // IHS cached on the row; one lookup per visit

        // The last bundle posted is the earlier visit: only its own diagnosis, encounter and vitals.
        var encounters = posted!.Entry.Select(e => e.Resource).OfType<F.Encounter>().ToList();
        Assert.Equal(earlier.Id, Assert.Single(Assert.Single(encounters).Identifier).Value);
        Assert.Equal("E11.9", Assert.Single(posted.Entry.Select(e => e.Resource).OfType<F.Condition>()).Code.Coding[0].Code);
        Assert.Equal(6, posted.Entry.Count(e => e.Resource is F.Observation));
        Assert.Equal("Patient/P02478375538", encounters[0].Subject.Reference);
        Assert.Equal("Practitioner/N10000001", encounters[0].Participant[0].Individual.Reference);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var open = await new RegistrationService(db, fx.Clock).StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Umum);
            await publisher.EnqueueAsync(open.Id);                                     // not finished yet
        });
    }

    [Fact]
    public void PayerIntegrationRegistersOnlyConfiguredClients()
    {
        var none = new ServiceCollection().AddHisPayerIntegration(new ConfigurationBuilder().Build()).BuildServiceProvider();
        Assert.Null(none.GetService<VClaimClient>());
        Assert.Null(none.GetService<EKlaimClient>());
        Assert.Null(none.GetService<SatuSehatClient>());

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["His:Bpjs:VClaim:ConsId"] = "1", ["His:Bpjs:VClaim:Secret"] = "s", ["His:Bpjs:VClaim:UserKey"] = "u", ["His:Bpjs:VClaim:Environment"] = "Production",
            ["His:Bpjs:EKlaim:Url"] = "http://eklaim/E-Klaim/ws.php", ["His:Bpjs:EKlaim:Key"] = Key,
            ["His:SatuSehat:ClientId"] = "c", ["His:SatuSehat:ClientSecret"] = "x", ["His:SatuSehat:Locations:POLI-UMUM"] = "loc-1",
        }).Build();
        var sp = new ServiceCollection().AddHisPayerIntegration(cfg).BuildServiceProvider();
        Assert.StartsWith("https://apijkn.bpjs-kesehatan.go.id/vclaim-rest/", sp.GetRequiredService<VClaimClient>().BaseUrl);
        Assert.NotNull(sp.GetService<EKlaimClient>());
        Assert.Equal(SatuSehatEnvironment.Sandbox, sp.GetRequiredService<SatuSehatClient>().Options.Environment);
        Assert.Equal("loc-1", sp.GetRequiredService<SatuSehatPublisherOptions>().LocationIds["poli-umum"]);
    }
}
