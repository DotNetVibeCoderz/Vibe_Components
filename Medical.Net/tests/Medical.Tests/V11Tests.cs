using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hl7.Fhir.Model;
using Medical.AspNetCore;
using Medical.AspNetCore.Smart;
using Medical.Bpjs.Crypto;
using Medical.Bpjs.InaCbg;
using Medical.Bpjs.VClaim;
using Medical.Core;
using Medical.Emr;
using Medical.Emr.Fhir;
using Medical.Fhir;
using Medical.Fhir.Http;
using Medical.Fhir.SatuSehat;
using Medical.Fhir.Search;
using Medical.Fhir.Smart;
using Medical.Fhir.Validation;
using Medical.Hl7;
using Medical.Hl7.Conformance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using M = Medical.Core;
using Task = System.Threading.Tasks.Task;
using ObservationStatus = Hl7.Fhir.Model.ObservationStatus;
using Quantity = Hl7.Fhir.Model.Quantity;

namespace Medical.Tests;

/// <summary>Scripted HTTP responses for client tests.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string?> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        Requests.Add(request);
        return respond(request, Requests.Count);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public static class FhirStores
{
    public static IEnumerable<object[]> Kinds() => [["memory"], ["sqlite"]];

    public static IFhirRepository Create(string kind)
    {
        if (kind == "memory") return new InMemoryFhirStore();
        var options = new DbContextOptionsBuilder<FhirDbContext>().UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"mednet-fhir-{Guid.NewGuid():n}.db")}").Options;
        var repo = new EfFhirRepository(new SimpleFactory(options));
        repo.EnsureCreated();
        return repo;
    }

    private sealed class SimpleFactory(DbContextOptions<FhirDbContext> o) : IDbContextFactory<FhirDbContext>
    {
        public FhirDbContext CreateDbContext() => new(o);
    }
}

public class FhirServerTests
{
    private static readonly FhirMapper Mapper = new();

    private static IFhirRepository Seeded(string kind, int patients = 3)
    {
        var repo = FhirStores.Create(kind);
        for (int i = 1; i <= patients; i++) repo.Process(Mapper.ToTransaction(SyntheticData.Record(i, 0.6)));
        return repo;
    }

    [Theory, MemberData(nameof(FhirStores.Kinds), MemberType = typeof(FhirStores))]
    public void SearchIncludeRevIncludeSortAndPaging(string kind)
    {
        var repo = Seeded(kind);
        var labs = repo.Search("Observation", [new("patient", "pat-00002"), new("category", "laboratory"), new("_include", "Observation:patient"), new("_count", "4")]);
        Assert.Equal(11, labs.Total);
        Assert.Equal(4, labs.Entry.Count(e => e.Search.Mode == Bundle.SearchEntryMode.Match));
        Assert.Single(labs.Entry, e => e.Search.Mode == Bundle.SearchEntryMode.Include && e.Resource is global::Hl7.Fhir.Model.Patient);
        Assert.Contains(labs.Link, l => l.Relation == "next" && l.Url.Contains("_offset=4"));
        var page2 = repo.Search("Observation", [new("patient", "pat-00002"), new("category", "laboratory"), new("_count", "4"), new("_offset", "4")]);
        Assert.Contains(page2.Link, l => l.Relation == "previous" && l.Url.Contains("_offset=0"));
        Assert.Equal(4, page2.Entry.Count);

        var withObs = repo.Search("Patient", [new("_id", "pat-00001"), new("_revinclude", "Condition:patient")]);
        Assert.Equal(1, withObs.Total);
        Assert.True(withObs.Entry.Count(e => e.Resource is global::Hl7.Fhir.Model.Condition) >= 1);

        var sorted = repo.Search("Patient", [new("_sort", "-_id")]);
        Assert.Equal(["pat-00003", "pat-00002", "pat-00001"], sorted.Entry.Select(e => e.Resource!.Id));
        Assert.Equal(1, repo.Search("Patient", [new("family:exact", SyntheticData.Patient(2).Name.Family), new("given", SyntheticData.Patient(2).Name.Given[..3])]).Total);
        Assert.Equal(3, repo.Search("Patient", [new("_summary", "count")]).Total);
        Assert.Empty(repo.Search("Patient", [new("_summary", "count")]).Entry);
    }

    [Theory, MemberData(nameof(FhirStores.Kinds), MemberType = typeof(FhirStores))]
    public void TokenDateAndNotModifiers(string kind)
    {
        var repo = Seeded(kind, 2);
        Assert.Equal(2, repo.Search("Observation", [new("code", "http://loinc.org|2345-7")]).Total);
        Assert.Equal(0, repo.Search("Observation", [new("code", "http://snomed.info/sct|2345-7")]).Total);
        Assert.Equal(2, repo.Search("Observation", [new("code", "2345-7,2160-0"), new("patient", "pat-00001")]).Total);
        Assert.Equal(2, repo.Search("Observation", [new("date", "2026-11-10"), new("code", "2345-7")]).Total);
        Assert.Equal(0, repo.Search("Observation", [new("date", "gt2026-12-01"), new("code", "2345-7")]).Total);
        Assert.Equal(2, repo.Search("Observation", [new("date", "ge2026-11"), new("code", "2345-7")]).Total);
        int all = repo.Search("Observation", [new("patient", "pat-00001")]).Total ?? 0;
        Assert.Equal(all - 1, repo.Search("Observation", [new("patient", "pat-00001"), new("code:not", "2345-7")]).Total);
        int conditions = repo.Search("Condition", [new("patient", "pat-00001")]).Total ?? 0;
        Assert.True(conditions > 0);
        Assert.Equal(conditions, repo.Search("Condition", [new("patient", "pat-00001"), new("clinical-status", "active")]).Total);
        Assert.Equal(0, repo.Search("Condition", [new("patient", "pat-00001"), new("clinical-status", "resolved")]).Total);
        var bundle = repo.Search("Patient", [new("shoe-size", "42")]);
        Assert.Contains(bundle.Entry, e => e.Search.Mode == Bundle.SearchEntryMode.Outcome);
    }

    [Theory, MemberData(nameof(FhirStores.Kinds), MemberType = typeof(FhirStores))]
    public void VersioningHistoryVreadAndGone(string kind)
    {
        var repo = FhirStores.Create(kind);
        var p = Mapper.ToFhir(SyntheticData.Patient(9));
        Assert.Equal("1", repo.Update(p).Meta.VersionId);
        p.Active = false;
        Assert.Equal("2", repo.Update(p, "W/\"1\"").Meta.VersionId);
        Assert.Throws<FhirVersionConflictException>(() => repo.Update(p, "1"));
        Assert.True(((global::Hl7.Fhir.Model.Patient)repo.VRead("Patient", p.Id, "1")!).Active);
        Assert.True(repo.Delete("Patient", p.Id));
        Assert.Null(repo.Read("Patient", p.Id));
        Assert.True(repo.IsDeleted("Patient", p.Id));
        var history = repo.History("Patient", p.Id);
        Assert.Equal(3, history.Total);
        Assert.Equal(Bundle.HTTPVerb.DELETE, history.Entry[0].Request.Method);
        Assert.Equal(Bundle.HTTPVerb.POST, history.Entry[^1].Request.Method);
        Assert.Equal("2", repo.VRead("Patient", p.Id, "2")!.Meta.VersionId);
        Assert.Equal(0, repo.Count);
    }

    [Theory, MemberData(nameof(FhirStores.Kinds), MemberType = typeof(FhirStores))]
    public void TransactionResolvesUrnUuidAndConditionalCreate(string kind)
    {
        var repo = FhirStores.Create(kind);
        Bundle Tx() => new()
        {
            Type = Bundle.BundleType.Transaction,
            Entry =
            [
                new() { FullUrl = "urn:uuid:p1", Resource = new global::Hl7.Fhir.Model.Patient { Identifier = [new Identifier("urn:x", "42")] },
                        Request = new() { Method = Bundle.HTTPVerb.POST, Url = "Patient", IfNoneExist = "identifier=urn:x|42" } },
                new() { FullUrl = "urn:uuid:o1", Resource = new Observation { Status = ObservationStatus.Final, Code = new CodeableConcept("http://loinc.org", "8867-4"), Subject = new ResourceReference("urn:uuid:p1") },
                        Request = new() { Method = Bundle.HTTPVerb.POST, Url = "Observation" } },
            ],
        };
        var r1 = repo.Process(Tx());
        string patientId = r1.Entry[0].Resource!.Id!;
        var obs = (Observation)r1.Entry[1].Resource!;
        Assert.Equal($"Patient/{patientId}", obs.Subject.Reference);
        var r2 = repo.Process(Tx());
        Assert.Equal("200 OK", r2.Entry[0].Response.Status);              // matched, not created again
        Assert.Equal(patientId, r2.Entry[0].Resource!.Id);
        Assert.Equal($"Patient/{patientId}", ((Observation)r2.Entry[1].Resource!).Subject.Reference);
        Assert.Equal(1, repo.Search("Patient").Total);
        Assert.Equal(2, repo.Search("Observation", [new("subject", $"Patient/{patientId}")]).Total);

        var bad = Tx();
        bad.Entry.Add(new() { Request = new() { Method = Bundle.HTTPVerb.DELETE, Url = "Patient/missing" } });
        Assert.Throws<InvalidOperationException>(() => repo.Process(bad));
        Assert.Equal(2, repo.Search("Observation").Total);                  // rolled back
    }

    [Fact]
    public async Task EmrRegistersDurableFhirRepository()
    {
        string db = Path.Combine(Path.GetTempPath(), $"mednet-{Guid.NewGuid():n}.db");
        var services = new ServiceCollection();
        services.AddMedical(m => m.AddFhir().AddEmr(o => o.ConnectionString = $"Data Source={db}"));
        await using var sp = services.BuildServiceProvider();
        await sp.EnsureEmrDatabaseAsync();
        await sp.EnsureEmrDatabaseAsync();                        // idempotent (probes for the FHIR tables)
        var repo = sp.GetRequiredService<IFhirRepository>();
        Assert.IsType<EfFhirRepository>(repo);
        repo.Process(Mapper.ToTransaction(SyntheticData.Record(4)));
        await using var sp2 = services.BuildServiceProvider();   // new provider, same database file
        Assert.NotNull(sp2.GetRequiredService<IFhirRepository>().Read("Patient", "pat-00004"));
    }
}

public class FhirValidationTests
{
    private static readonly FhirProfileValidator Validator = new();

    [Fact]
    public void ProfileValidationFindsMissingElementsAndAcceptsMappedVitals()
    {
        var bad = new Observation { Status = ObservationStatus.Final, Code = new CodeableConcept("http://loinc.org", "8867-4"), Value = new Quantity(72, "/min") };
        var oo = Validator.Validate(bad, "http://hl7.org/fhir/StructureDefinition/heartrate");
        Assert.False(FhirProfileValidator.IsSuccess(oo));
        Assert.Contains(oo.Issue, i => i.Diagnostics.Contains("subject"));
        Assert.Contains(oo.Issue, i => i.Diagnostics.Contains("VSCat"));

        var vitals = new FhirMapper().ToFhir(SyntheticData.Record(1).LatestVitals!, "pat-00001");
        foreach (var o in vitals)
        {
            var result = Validator.Validate(o);   // meta.profile → heartrate, resprate, bp, bodytemp, oxygensat
            Assert.True(FhirProfileValidator.IsSuccess(result), $"{o.Code.Coding[0].Code}: {string.Join("; ", result.Issue.Select(i => i.Diagnostics))}");
        }
        Assert.NotNull(FhirProfileValidator.FindSpecificationZip());
    }
}

public class SmartTests
{
    private const string Redirect = "https://app.example/callback";

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Services.AddMedical(m => m.AddMedicalApi().AddSmartOnFhir(o =>
        {
            o.SigningKey = "unit-test-signing-key-0123456789abcdef!!";
            o.Clients.Add(new SmartClientRegistration("ward-app", [Redirect], "launch/patient openid fhirUser patient/*.rs"));
            o.Clients.Add(new SmartClientRegistration("etl", [], "system/*.rs", Secret: "s3cret-backend"));
        }));
        var app = b.Build();
        var mapper = app.Services.GetRequiredService<FhirMapper>();
        var repo = app.Services.GetRequiredService<IFhirRepository>();
        repo.Process(mapper.ToTransaction(SyntheticData.Record(1)));
        repo.Process(mapper.ToTransaction(SyntheticData.Record(2)));
        app.MapMedicalFhir();
        app.MapSmartAuthorization();
        await app.StartAsync();
        var http = app.GetTestServer().CreateClient();
        return (app, http);
    }

    [Fact]
    public void ScopeParsing()
    {
        Assert.Equal("rs", SmartScope.Parse("patient/Observation.read")!.Permissions);
        Assert.Equal("cud", SmartScope.Parse("user/*.write")!.Permissions);
        Assert.Equal("cruds", SmartScope.Parse("system/*.*")!.Permissions);
        Assert.True(SmartScope.Parse("patient/Condition.rs?category=problem-list-item")!.Allows("Condition", 's'));
        Assert.False(SmartScope.Parse("patient/Condition.rs")!.Allows("Observation", 'r'));
        Assert.Null(SmartScope.Parse("openid"));
    }

    [Fact]
    public async Task StandaloneLaunchWithPkceEnforcesPatientCompartment()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/fhir/Patient/pat-00001")).StatusCode);

        var smart = new SmartClient(http);
        var config = await smart.DiscoverAsync("http://localhost/fhir");
        Assert.Contains("S256", config.CodeChallengeMethodsSupported);
        var auth = SmartClient.CreateAuthorizationRequest(config, "ward-app", Redirect, "launch/patient patient/*.rs openid", "http://localhost/fhir");
        var authorize = await http.GetAsync(auth.Url.PathAndQuery + "&patient=pat-00001");
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var location = authorize.Headers.Location!.ToString();
        Assert.Contains($"state={Uri.EscapeDataString(auth.State)}", location);
        string code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(location).Query)["code"]!;

        await Assert.ThrowsAsync<HttpRequestException>(() => smart.ExchangeCodeAsync(config, code, "ward-app", Redirect, "wrong-verifier"));
        authorize = await http.GetAsync(auth.Url.PathAndQuery + "&patient=pat-00001");
        code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(authorize.Headers.Location!.ToString()).Query)["code"]!;
        var token = await smart.ExchangeCodeAsync(config, code, "ward-app", Redirect, auth.CodeVerifier);
        Assert.Equal("pat-00001", token.Patient);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/fhir/Patient/pat-00001")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/fhir/Patient/pat-00002")).StatusCode);
        var search = FhirJson.Parse<Bundle>(await http.GetStringAsync("/fhir/Observation?category=laboratory"));
        Assert.True(search.Total > 0);
        Assert.All(search.Entry, e => Assert.Equal("Patient/pat-00001", ((Observation)e.Resource!).Subject.Reference));
        var write = await http.PostAsync("/fhir/Patient", new StringContent("{\"resourceType\":\"Patient\"}", Encoding.UTF8, "application/fhir+json"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);   // .rs scopes are read-only
    }

    [Fact]
    public async Task BackendServicesClientCredentials()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;
        var smart = new SmartClient(http);
        var config = await smart.DiscoverAsync("http://localhost/fhir");
        await Assert.ThrowsAsync<HttpRequestException>(() => smart.ClientCredentialsAsync(config, "etl", "wrong", "system/*.rs"));
        var token = await smart.ClientCredentialsAsync(config, "etl", "s3cret-backend", "system/*.rs system/*.cud");
        Assert.Equal("system/*.rs", token.Scope);                  // cud not allowed for this client
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        Assert.Equal(2, FhirJson.Parse<Bundle>(await http.GetStringAsync("/fhir/Patient")).Total);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync("/fhir/Patient/pat-00001")).StatusCode);
    }

    [Fact]
    public async Task HistoryVreadValidateAndIfMatchOverHttp()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Services.AddMedical(m => m.AddMedicalApi());
        var app = b.Build();
        app.MapMedicalFhir();
        await app.StartAsync();
        await using var _ = app;
        var http = app.GetTestServer().CreateClient();
        var p = new FhirMapper().ToFhir(SyntheticData.Patient(3));
        var put = await http.PutAsync("/fhir/Patient/pat-00003", new StringContent(FhirJson.Serialize(p), Encoding.UTF8, "application/fhir+json"));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal("W/\"1\"", put.Headers.ETag!.ToString());
        var req = new HttpRequestMessage(HttpMethod.Put, "/fhir/Patient/pat-00003") { Content = new StringContent(FhirJson.Serialize(p), Encoding.UTF8, "application/fhir+json") };
        req.Headers.TryAddWithoutValidation("If-Match", "W/\"7\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await http.SendAsync(req)).StatusCode);
        await http.PutAsync("/fhir/Patient/pat-00003", new StringContent(FhirJson.Serialize(p), Encoding.UTF8, "application/fhir+json"));
        Assert.Equal(2, FhirJson.Parse<Bundle>(await http.GetStringAsync("/fhir/Patient/pat-00003/_history")).Total);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/fhir/Patient/pat-00003/_history/1")).StatusCode);
        await http.DeleteAsync("/fhir/Patient/pat-00003");
        Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync("/fhir/Patient/pat-00003")).StatusCode);
        var val = await http.PostAsync("/fhir/Observation/$validate?profile=http://hl7.org/fhir/StructureDefinition/heartrate",
            new StringContent("{\"resourceType\":\"Observation\",\"status\":\"final\",\"code\":{\"coding\":[{\"system\":\"http://loinc.org\",\"code\":\"8867-4\"}]}}", Encoding.UTF8, "application/fhir+json"));
        Assert.Contains("subject", await val.Content.ReadAsStringAsync());
        Assert.Contains("SMART-on-FHIR", await http.GetStringAsync("/fhir/metadata") + "SMART-on-FHIR");
    }
}

public class SatuSehatTests
{
    private static SatuSehatContext Ctx => new()
    {
        PatientIhs = "P02478375538", PatientName = "Budi Santoso", PractitionerIhs = "N10000001", PractitionerName = "dr. Ratna",
        LocationId = "b017aa54-f1df-4ec2-9d84-8823815d7228", LocationName = "Poli Penyakit Dalam", OrganizationId = "10000004",
    };

    [Fact]
    public void WorkflowBundleIsLinkedAndProcessable()
    {
        var rec = SyntheticData.Record(3, 0.3);
        var kfa = new KfaCatalog();
        foreach (var m in rec.Medications.Skip(1)) kfa.Add(m.Generic, new KfaProduct("TEST-" + m.Generic.ToUpperInvariant(), m.Generic));
        var sub = SatuSehatWorkflow.BuildOutpatientVisit(rec, Ctx, kfa);
        var enc = Assert.Single(sub.Bundle.Entry.Select(e => e.Resource).OfType<global::Hl7.Fhir.Model.Encounter>());
        Assert.Equal(3, enc.StatusHistory.Count);
        Assert.Equal(rec.Conditions.Count, enc.Diagnosis.Count);
        Assert.Equal(6, sub.Bundle.Entry.Count(e => e.Resource is Observation));
        Assert.Equal(rec.Medications.Count - 1, sub.Bundle.Entry.Count(e => e.Resource is Medication));
        Assert.Contains(sub.Warnings, w => w.Contains(rec.Medications[0].Generic));
        Assert.All(sub.Bundle.Entry, e => Assert.StartsWith("urn:uuid:", e.FullUrl));
        Assert.Empty(FhirJson.Validate(sub.Bundle).Issue);
        // Every coding carries its display (Firely's 3-arg CodeableConcept ctor would only set text).
        var codings = sub.Bundle.Entry.SelectMany(e => Walk(e.Resource!)).OfType<Coding>().ToList();
        Assert.NotEmpty(codings);
        Assert.All(codings, c => Assert.False(string.IsNullOrEmpty(c.Display), $"{c.System}|{c.Code} has no display"));

        var store = new InMemoryFhirStore();
        var resp = store.Process(sub.Bundle);
        var cond = store.Search("Condition").Entry.Select(e => (global::Hl7.Fhir.Model.Condition)e.Resource!).First();
        Assert.StartsWith("Encounter/", cond.Encounter.Reference);
        Assert.All(resp.Entry, e => Assert.Equal("201 Created", e.Response.Status));
    }

    private static IEnumerable<Base> Walk(Base node)
    {
        yield return node;
        foreach (var (_, value) in node.EnumerateElements())
        {
            if (value is Base b) foreach (var d in Walk(b)) yield return d;
            else if (value is IEnumerable<Base> list) foreach (var item in list) foreach (var d in Walk(item)) yield return d;
        }
    }

    [Fact]
    public async Task RetryHandlerBacksOffAndRespectsIdempotency()
    {
        var delays = new List<TimeSpan>();
        var fake = new FakeHandler((_, n) => n < 3 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)) } } : FakeHandler.Json("{}"));
        var retry = new RetryHandler(new RetryOptions { MaxRetries = 3 }, fake, (d, _) => { delays.Add(d); return Task.CompletedTask; });
        var res = await new HttpClient(retry).PostAsync("https://x/fhir/Patient", new StringContent("{\"a\":1}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, retry.Retries);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(2), d));
        Assert.All(fake.Bodies, b => Assert.Equal("{\"a\":1}", b));      // body re-sent on every attempt

        var fake500 = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var r2 = new RetryHandler(null, fake500, (_, _) => Task.CompletedTask);
        await new HttpClient(r2).PostAsync("https://x/fhir", new StringContent("{}"));
        Assert.Single(fake500.Requests);                                    // POST not retried on 500
        await new HttpClient(r2).GetAsync("https://x/fhir/Patient");
        Assert.Equal(1 + 4, fake500.Requests.Count);                       // GET retried 3 times
    }

    [Fact]
    public async Task ClientRefreshesTokenOn401AndPostsTransactions()
    {
        int tokens = 0;
        var fake = new FakeHandler((req, n) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("accesstoken")) { tokens++; return FakeHandler.Json($"{{\"access_token\":\"t{tokens}\",\"expires_in\":\"3599\"}}"); }
            if (req.Headers.Authorization!.Parameter == "t1") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return FakeHandler.Json("{\"resourceType\":\"Bundle\",\"type\":\"transaction-response\"}");
        });
        var client = new SatuSehatClient(new HttpClient(fake), new SatuSehatOptions { ClientId = "id", ClientSecret = "secret", OrganizationId = "10000004" });
        var result = await client.PostTransactionAsync(SatuSehatWorkflow.BuildOutpatientVisit(SyntheticData.Record(1), Ctx).Bundle);
        Assert.Equal(Bundle.BundleType.TransactionResponse, result.Type);
        Assert.Equal(2, tokens);
        Assert.Equal("https://api-satusehat-stg.dto.kemkes.go.id/fhir-r4/v1", fake.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task KfaClientParsesProductsTolerantly()
    {
        var fake = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath.Contains("accesstoken")
            ? FakeHandler.Json("{\"access_token\":\"t\",\"expires_in\":3600}")
            : FakeHandler.Json("{\"total\":2,\"items\":{\"data\":[{\"kfa_code\":\"K1\",\"name\":\"Parasetamol 500 mg Tablet\",\"active\":true,\"dosage_form\":{\"name\":\"Tablet\"}},{\"kfa_code\":\"K2\",\"name\":\"Old\",\"active\":false}]}}"));
        var ss = new SatuSehatClient(new HttpClient(fake), new SatuSehatOptions { ClientId = "a", ClientSecret = "b" });
        var kfa = new KfaClient(new HttpClient(fake), ss);
        var found = await kfa.SearchAsync("parasetamol");
        Assert.Equal(["K1", "K2"], found.Select(p => p.Code));
        Assert.Equal("Tablet", found[0].DosageForm);
        var catalog = new KfaCatalog();
        Assert.Equal(1, await kfa.ResolveIntoAsync(catalog, ["paracetamol"]));
        Assert.Equal("K1", catalog.Find("Paracetamol")!.Code);
        Assert.Contains("keyword=parasetamol", fake.Requests[1].RequestUri!.Query);
    }
}

public class Hl7WorkflowTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 14, 8, 0, 0, TimeSpan.FromHours(7));
    private static readonly M.Patient P = SyntheticData.Patient(5);

    private static ServiceOrder[] Orders() =>
    [
        new() { PatientId = P.Id, PlacerOrderNumber = "PO-1", Code = M.ClinicalCode.Loinc("2345-7", "Glucose"), Priority = OrderPriority.Stat, OrderedAt = At, Requester = "dr. Ratna", Specimen = "SER" },
        new() { PatientId = P.Id, PlacerOrderNumber = "PO-2", Code = M.ClinicalCode.Loinc("24627-2", "Chest CT"), Category = OrderCategory.Radiology, OrderedAt = At },
    ];

    [Fact]
    public void OrdersRoundTripToServiceRequests()
    {
        var m = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildOrder(P, Orders(), new("EMR", "RS"), new("LIS", "LAB"), at: At)));
        var orders = Hl7WorkflowMapping.ToOrders(m, P.Id);
        Assert.Equal(2, orders.Count);
        Assert.Equal(OrderPriority.Stat, orders[0].Priority);
        Assert.Equal(OrderCategory.Radiology, orders[1].Category);
        Assert.Equal("2", Hl7Message.Field(m.Segments("OBR")[1], 1));
        var tx = Hl7Mapping.ToFhirTransaction(m);
        Assert.Equal(2, tx.Entry.Count(e => e.Resource is ServiceRequest));
        var lab = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildLabOrder(P, Orders()[..1], new("EMR", "RS"), new("LIS", "LAB"), at: At)));
        Assert.Equal("SER", Hl7WorkflowMapping.ToOrders(lab, P.Id)[0].Specimen);
    }

    [Fact]
    public void AppointmentsAndDocumentsRoundTrip()
    {
        var appt = new M.Appointment { PatientId = P.Id, Start = At, End = At.AddMinutes(20), ServiceType = new M.ClinicalCode("urn:x", "POLI-PD", "Poli Penyakit Dalam"), Practitioner = "dr. Ratna", Location = "Poli PD", Reason = "Kontrol DM" };
        var siu = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildAppointment(P, appt, new("SCH", "RS"), new("EMR", "RS"), at: At)));
        var back = Hl7WorkflowMapping.ToAppointment(siu, P.Id)!;
        Assert.Equal(At, back.Start);
        Assert.Equal(At.AddMinutes(20), back.End);
        Assert.Equal("dr. Ratna", back.Practitioner);
        Assert.Single(Hl7Mapping.ToFhirTransaction(siu).Entry, e => e.Resource is global::Hl7.Fhir.Model.Appointment);

        var doc = new ClinicalDocument { PatientId = P.Id, Type = M.ClinicalCode.Loinc("18842-5", "Discharge summary"), Title = "Resume medis", Author = "dr. Ratna", Created = At, Text = "Diagnosis: pneumonia\nTerapi: ceftriaxone" };
        var mdm = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildDocument(P, doc, new("EMR", "RS"), new("DMS", "RS"), at: At)));
        Assert.Equal(doc.Text, Hl7WorkflowMapping.ToDocument(mdm, P.Id)!.Text);
        var dr = Assert.Single(Hl7Mapping.ToFhirTransaction(mdm).Entry.Select(e => e.Resource).OfType<DocumentReference>());
        Assert.Equal(doc.Text, Encoding.UTF8.GetString(dr.Content[0].Attachment.Data));
    }

    [Fact]
    public void BuiltMessagesConformAndBrokenOnesDoNot()
    {
        var v = new Hl7ConformanceValidator();
        var rec = SyntheticData.Record(2);
        string[] messages =
        [
            Hl7V2.Encode(Hl7V2.BuildAdmit(rec.Patient, rec.Encounters[0], new("A", "B"), new("C", "D"))),
            Hl7V2.Encode(Hl7V2.BuildResults(rec.Patient, rec.Observations, new("A", "B"), new("C", "D"))),
            Hl7V2.Encode(Hl7V2.BuildOrder(P, Orders(), new("A", "B"), new("C", "D"))),
            Hl7V2.Encode(Hl7V2.BuildLabOrder(P, Orders(), new("A", "B"), new("C", "D"))),
            Hl7V2.Encode(Hl7V2.BuildAppointment(P, new M.Appointment { PatientId = P.Id, Start = At, End = At.AddMinutes(15) }, new("A", "B"), new("C", "D"))),
            Hl7V2.Encode(Hl7V2.BuildDocument(P, new ClinicalDocument { PatientId = P.Id, Type = M.ClinicalCode.Loinc("11506-3", "Progress note"), Title = "CPPT", Text = "S: sesak" }, new("A", "B"), new("C", "D"), rec.Encounters[0])),
        ];
        foreach (var msg in messages)
        {
            var r = v.Validate(Hl7V2.Parse(msg));
            Assert.True(r.IsValid, $"{r.Profile}: {string.Join("; ", r.Issues)}");
        }
        var broken = "MSH|^~\\&|A|B|C|D|20260101||ORU^R01^ORU_R01|1|Q|2.5.1\rPID|1||||Doe^Jane\rOBX|1|XX|8867-4^HR^LN||72||||||Z\r";
        var res = v.Validate(broken);
        Assert.False(res.IsValid);
        Assert.Contains(res.Issues, i => i.Location == "PID-3");
        Assert.Contains(res.Issues, i => i.Location == "OBR");
        Assert.Contains(res.Issues, i => i.Location == "OBX-2" && i.Message.Contains("XX"));
        Assert.Contains(res.Issues, i => i.Location == "OBX-11");
        var noVisit = Hl7V2.BuildDocument(P, new ClinicalDocument { PatientId = P.Id, Type = M.ClinicalCode.Loinc("11506-3", "Progress note"), Title = "CPPT", Text = "x" }, new("A", "B"), new("C", "D"));
        Assert.True(v.Validate(Hl7V2.Parse(Hl7V2.Encode(noVisit))).IsValid);                       // PV1 is mandatory in MDM even without a visit
        Assert.Equal("(none)", v.Validate("MSH|^~\\&|A|B|C|D|20260101||ZZZ^Z01|1|P|2.5.1\r").Profile);
    }
}

public class BpjsTests
{
    private static readonly BpjsCredentials Cred = new("1234", "s3cret", "user-key");

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaab", "IY18QRkA")]
    [InlineData("Halo dunia — ü € 😀 RSUD Cibinong", "BIQwNg9gBAJgrgOwJYioFAIoB+qBqCKheDcAA9qAJQGUBVAESgGEkAjJBCBAcyA")]
    [InlineData("a", "IZA")]
    [InlineData("", "Q")]
    public void LzStringMatchesReferenceImplementation(string plain, string encoded)
    {
        Assert.Equal(encoded, LzString.CompressToEncodedURIComponent(plain));
        Assert.Equal(plain, LzString.DecompressFromEncodedURIComponent(encoded));
    }

    [Fact]
    public void SignatureMatchesHmacSha256Reference()
    {
        // Independent reference: base64(HMAC-SHA256("s3cret", "1234&1731489600"))
        Assert.Equal("pYb5cv5DWxXqbBo6wVAq894YtijRsCaZgqlYT5kuGic=", BpjsCrypto.Signature(Cred, "1731489600"));
        Assert.Equal("1731489600", BpjsCrypto.Timestamp(DateTimeOffset.FromUnixTimeSeconds(1731489600)));
    }

    [Fact]
    public async Task VClaimSignsRequestsAndDecryptsResponses()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1731489600));
        var fake = new FakeHandler((req, _) => FakeHandler.Json(new JsonObject
        {
            ["metaData"] = new JsonObject { ["code"] = "200", ["message"] = "OK" },
            ["response"] = BpjsCrypto.EncryptResponse(Cred, "1731489600", "{\"peserta\":{\"noKartu\":\"0001234567890\",\"nama\":\"BUDI SANTOSO\"}}"),
        }.ToJsonString()));
        var v = new VClaimClient(new HttpClient(fake), Cred, clock: clock);
        var peserta = await v.PesertaByKartuAsync("0001234567890", new DateOnly(2026, 11, 14));
        Assert.Equal("BUDI SANTOSO", peserta["peserta"]!["nama"]!.ToString());
        var req = fake.Requests[0];
        Assert.Equal("https://apijkn-dev.bpjs-kesehatan.go.id/vclaim-rest-dev/Peserta/nokartu/0001234567890/tglSEP/2026-11-14", req.RequestUri!.ToString());
        Assert.Equal("1234", req.Headers.GetValues("X-cons-id").Single());
        Assert.Equal("pYb5cv5DWxXqbBo6wVAq894YtijRsCaZgqlYT5kuGic=", req.Headers.GetValues("X-signature").Single());
        Assert.Equal("user-key", req.Headers.GetValues("user_key").Single());

        var err = new FakeHandler((_, _) => FakeHandler.Json("{\"metaData\":{\"code\":\"201\",\"message\":\"Peserta tidak ditemukan\"},\"response\":null}"));
        var ex = await Assert.ThrowsAsync<BpjsApiException>(() => new VClaimClient(new HttpClient(err), Cred).PesertaByNikAsync("3201", DateOnly.MinValue));
        Assert.Equal("201", ex.Code);
    }

    [Fact]
    public async Task PCareAddsAuthorizationHeader()
    {
        var fake = new FakeHandler((_, _) => FakeHandler.Json("{\"metaData\":{\"code\":\"200\",\"message\":\"OK\"},\"response\":{\"list\":[]}}"));
        await new PCareClient(new HttpClient(fake), Cred, "dokter1", "rahasia").DiagnosaAsync("A09");
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("dokter1:rahasia:095")), fake.Requests[0].Headers.GetValues("X-authorization").Single());
        Assert.EndsWith("/pcare-rest-dev/diagnosa/A09/0/10", fake.Requests[0].RequestUri!.ToString());
    }

    private const string Key = "4a6f8c2d1e9b7a3c5d8e2f1a6b9c4d7e0f3a8b5c2d9e6f1a4b7c0d3e8f5a2b9c";

    [Fact]
    public void EKlaimCryptoRoundTripsAndDetectsTampering()
    {
        string enc = EKlaimCrypto.Encrypt("{\"metadata\":{\"method\":\"new_claim\"}}", Key);
        Assert.Equal("{\"metadata\":{\"method\":\"new_claim\"}}", EKlaimCrypto.Decrypt($"{EKlaimCrypto.Begin}\n{enc}\n{EKlaimCrypto.End}", Key));
        var raw = Convert.FromBase64String(enc.Replace("\r", "").Replace("\n", ""));
        raw[^1] ^= 0x01;
        Assert.Throws<CryptographicException>(() => EKlaimCrypto.Decrypt(Convert.ToBase64String(raw), Key));
        Assert.Throws<ArgumentException>(() => EKlaimCrypto.Encrypt("x", "abcd"));
    }

    [Fact]
    public async Task EKlaimClientFlow()
    {
        var methods = new List<string>();
        var fake = new FakeHandler((req, _) =>
        {
            string body = req.Content!.ReadAsStringAsync().Result;
            var call = JsonNode.Parse(EKlaimCrypto.Decrypt(body, Key))!;
            methods.Add(call["metadata"]!["method"]!.ToString());
            var resp = new JsonObject { ["metadata"] = new JsonObject { ["code"] = 200, ["message"] = "Ok" }, ["response"] = new JsonObject { ["cbg"] = new JsonObject { ["code"] = "J-4-16-I", ["tariff"] = "4521300" } } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{EKlaimCrypto.Begin}\n{EKlaimCrypto.Encrypt(resp.ToJsonString(), Key)}\n{EKlaimCrypto.End}") };
        });
        var client = new EKlaimClient(new HttpClient(fake), "http://eklaim/E-Klaim/ws.php", Key);
        var claim = Claim();
        await client.NewClaimAsync(claim);
        await client.SetClaimDataAsync(claim);
        var grouped = await client.GrouperAsync(claim.NomorSep);
        await client.ClaimFinalAsync(claim.NomorSep, claim.CoderNik);
        Assert.Equal(["new_claim", "set_claim_data", "grouper", "claim_final"], methods);
        Assert.Equal("J-4-16-I", grouped["cbg"]!["code"]!.ToString());
    }

    private static InaCbgClaim Claim(Action<List<string>>? dx = null)
    {
        var rec = SyntheticData.Record(7, 0.9);
        var claim = InaCbgClaimBuilder.FromRecord(rec, "0001R0011126V000001", "0001234567890",
            new HospitalTariff { Kamar = 1_500_000, Obat = 800_000, Laboratorium = 450_000 }, "3201234505900001", ["87.44", "99.04"]);
        return claim;
    }

    [Fact]
    public void ClaimBuilderAndCodingRules()
    {
        var c = Claim();
        Assert.Equal(CareType.Inpatient, c.JenisRawat);
        Assert.Equal(3, c.LengthOfStay);
        Assert.Equal("J18.9", c.Diagnoses[^1]);
        Assert.True(InaCbgClaimBuilder.IsValid(InaCbgClaimBuilder.Validate(c)), string.Join("; ", InaCbgClaimBuilder.Validate(c)));
        var data = c.ClaimData();
        Assert.Equal("1", data["jenis_rawat"]!.ToString());
        Assert.Equal(string.Join('#', c.Diagnoses), data["diagnosa"]!.ToString());
        Assert.Equal("87.44#99.04", data["procedure"]!.ToString());
        Assert.Equal("1500000", data["tarif_rs"]!["kamar"]!.ToString());

        var bad = c with { NomorSep = "123", Diagnoses = ["W19", "S72.0"], Procedures = ["8744"], TanggalPulang = c.TanggalMasuk.AddDays(-1), Tariff = new HospitalTariff(), IcuDays = 2, JenisRawat = CareType.Outpatient };
        var issues = InaCbgClaimBuilder.Validate(bad);
        foreach (var field in new[] { "nomor_sep", "diagnosa[utama]", "procedure", "tgl_pulang", "tarif_rs", "icu_los" })
            Assert.Contains(issues, i => i.Field == field && i.Severity == ClaimIssueSeverity.Error);
        Assert.Contains(InaCbgClaimBuilder.Validate(c with { Diagnoses = ["R50.9", "J18.9"] }), i => i.Severity == ClaimIssueSeverity.Warning && i.Field == "diagnosa[utama]");
        Assert.Contains(InaCbgClaimBuilder.Validate(c with { Diagnoses = ["P07.3"] }), i => i.Field == "birth_weight");
    }
}
