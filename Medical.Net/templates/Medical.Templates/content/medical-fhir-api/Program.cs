using Medical.AspNetCore;
using Medical.Core;
using Medical.Fhir;

// Medical.Net FHIR API server — Made by Gravicode Studios, led by Kang Fadhil.
//   GET  /fhir/metadata                     CapabilityStatement
//   GET  /fhir/Patient?name=...             search (also Observation?patient=..&code=..)
//   POST /fhir                              transaction / batch bundle
//   GET  /fhir/Patient/{id}/$everything
//   /api/...                                scores, CDS, terminology, HL7→FHIR, de-identification
//   MLLP :2575                              HL7 v2 ADT/ORU → FHIR store

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMedical(m => m
    .AddMedicalApi()
    .AddMllpListener(o => o.Port = builder.Configuration.GetValue("Mllp:Port", 2575)));

var app = builder.Build();

// Seed synthetic patients so the API is explorable out of the box (remove for production data).
var store = app.Services.GetRequiredService<InMemoryFhirStore>();
var mapper = app.Services.GetRequiredService<FhirMapper>();
for (int i = 1; i <= 10; i++) store.Process(mapper.ToTransaction(SyntheticData.Record(i, i % 4 == 0 ? 0.85 : 0.2)));

app.MapGet("/", () => Results.Text($"{MedicalInfo.Name} FHIR API — try /fhir/metadata or /fhir/Patient\n{MedicalInfo.Credit}"));
app.MapMedicalFhir();
app.MapMedicalApi();
app.Run();
