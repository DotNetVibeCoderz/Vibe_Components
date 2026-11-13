using Medical.AspNetCore;
using Medical.Core;
using Medical.Fhir;
using Medical.Fhir.Search;
#if (persist)
using Medical.Emr;
#endif
#if (smart)
using Medical.AspNetCore.Smart;
#endif

// Medical.Net FHIR API server — Made by Gravicode Studios, led by Kang Fadhil.
//   GET  /fhir/metadata                          CapabilityStatement
//   GET  /fhir/Observation?patient=..&_include=Observation:patient&_count=20
//   GET  /fhir/Patient/{id}/_history[/{vid}]     history and vread; PUT with If-Match → 412 on conflict
//   POST /fhir                                   transaction / batch (urn:uuid references, ifNoneExist)
//   POST /fhir/Observation/$validate?profile=…   profile validation (FHIR R4 core + IG packages)
//   GET  /fhir/Patient/{id}/$everything
//   /api/...                                     scores, CDS, terminology, HL7→FHIR, HL7 conformance, de-identification
//   MLLP :2575                                   HL7 v2 ADT/ORU/ORM/OML/SIU/MDM → FHIR store
#if (smart)
//   /.well-known/smart-configuration, /auth/authorize, /auth/token   SMART on FHIR (PKCE, client_credentials)
#endif

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMedical(m =>
{
    m.AddMedicalApi()
     .AddMllpListener(o => o.Port = builder.Configuration.GetValue("Mllp:Port", 2575));
#if (persist)
    // Durable FHIR repository (every version + search index) on EF Core; SQLite here, SQL Server in production.
    m.AddEmr(o => o.ConnectionString = builder.Configuration.GetConnectionString("Fhir") ?? "Data Source=fhir.db");
#endif
#if (smart)
    m.AddSmartOnFhir(o =>
    {
        o.SigningKey = builder.Configuration["Smart:SigningKey"] ?? "";   // set it to keep tokens valid across restarts
        o.DefaultPatient = "pat-00001";                                  // development: standalone launches pick this patient
        o.Clients.Add(new SmartClientRegistration("demo-app", ["http://localhost:5173/callback"], "launch/patient openid fhirUser patient/*.rs"));
        o.Clients.Add(new SmartClientRegistration("etl", [], "system/*.rs", Secret: builder.Configuration["Smart:EtlSecret"] ?? "change-me"));
    });
#endif
});

var app = builder.Build();
#if (persist)
await app.Services.EnsureEmrDatabaseAsync();
#endif

// Seed synthetic patients so the API is explorable out of the box (remove for production data).
var repository = app.Services.GetRequiredService<IFhirRepository>();
var mapper = app.Services.GetRequiredService<FhirMapper>();
if (repository.Count == 0)
    for (int i = 1; i <= 10; i++) repository.Process(mapper.ToTransaction(SyntheticData.Record(i, i % 4 == 0 ? 0.85 : 0.2)));

app.MapGet("/", () => Results.Text($"{MedicalInfo.Name} FHIR API — try /fhir/metadata or /fhir/Patient\n{MedicalInfo.Credit}"));
app.MapMedicalFhir();
app.MapMedicalApi();
#if (smart)
app.MapSmartAuthorization();
#endif
app.Run();
