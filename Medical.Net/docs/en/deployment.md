# Deployment

## FHIR server + HL7 listener (ASP.NET Core)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMedical(m => m
    .AddMedicalApi()                                        // FHIR, terminology, rules, privacy, HL7
    .AddMllpListener(o => o.Port = 2575)                    // ADT/ORU → FHIR store, ACK
    .AddEmr(o => { o.Provider = EmrProvider.SqlServer; o.ConnectionString = builder.Configuration["ConnectionStrings:Emr"]!; o.Redis = builder.Configuration["Redis"]; }));
var app = builder.Build();
await app.Services.EnsureEmrDatabaseAsync();
app.MapMedicalFhir("/fhir");
app.MapMedicalApi("/api");
app.Run();
```

| Endpoint | Purpose |
|---|---|
| `GET /fhir/metadata` | CapabilityStatement |
| `GET/POST/PUT/DELETE /fhir/{type}[/{id}]` | CRUD with versioning and ETags (`If-Match` → 412), search with `_include`/`_revinclude`, `_sort`, `_count`/`_offset`, date prefixes and modifiers — see [Interoperability](interoperability.md) |
| `GET /fhir/{type}/{id}/_history[/{vid}]`, `/fhir/{type}/_history`, `/fhir/_history` | history and vread (deleted resources answer 410 Gone) |
| `POST /fhir/{type}/$validate[?profile=…]` | profile validation (R4 core + IG packages) |
| `POST /fhir` | transaction / batch (transactions roll back on failure; `urn:uuid` references, `ifNoneExist`) |
| `/fhir/.well-known/smart-configuration`, `/auth/*` | SMART on FHIR (with `AddSmartOnFhir` + `MapSmartAuthorization`) |
| `GET /fhir/Patient/{id}/$everything` | patient compartment |
| `/api/scores/*`, `/api/calc/*`, `/api/cds/*`, `/api/terminology/*`, `/api/hl7/to-fhir`, `/api/hl7/conformance`, `/api/privacy/deidentify`, `/api/audit` | clinical JSON API |

With `AddEmr()` the FHIR endpoints use the durable `EfFhirRepository` (every version kept, search index in SQL); without it the in-memory store is used (development, tests, edge gateways). You can also forward transactions to a dedicated FHIR server (HAPI, Firely Server, Azure Health Data Services) — see the `medical-hl7-gateway` template.

## SATUSEHAT

Register in the SATUSEHAT developer portal, then set `SATUSEHAT_CLIENT_ID`, `SATUSEHAT_CLIENT_SECRET`, `SATUSEHAT_ORG_ID` and `SATUSEHAT_ENV=sandbox|production`, and call `AddFhir(satuSehat: SatuSehatOptions.FromEnvironment())` — the `"satusehat"` `HttpClient` retries 429/5xx with backoff. Build visits with `SatuSehatWorkflow.BuildOutpatientVisit` and resolve drug codes with `KfaClient`. Check consent first (`ConsentPurpose.SatuSehatExchange`).

## BPJS Kesehatan

Set `BPJS_CONS_ID`, `BPJS_SECRET_KEY`, `BPJS_USER_KEY` (from the BPJS integration team) and call `AddBpjs(o => { o.Environment = BpjsEnvironment.Production; o.EKlaimUrl = …; o.EKlaimKey = …; })`. Keep the E-Klaim key in a secret store; run the E-Klaim server inside the hospital network.

## Imaging

- PACS: `StoreScp.StartServer(port, inbox)` receives C-STORE; `DicomScu.StoreAsync` sends. DICOMweb via `DicomWebClient`.
- AI: run `services/medical-ai-service` (Docker image provided) and register `AddAI(imagingServiceUrl: "http://imaging-ai:8000")`.

## Security & compliance checklist

- **Audit**: keep `PersistAudit = true`; every record read, export, de-identification and alert is logged with user and purpose.
- **Access control**: enable SMART on FHIR (`AddSmartOnFhir`, scopes checked on every interaction, patient compartments enforced) and put `/auth/authorize` behind your identity provider; `FhirEndpoints` records the user in the audit trail.
- **Secrets**: environment variables or a vault; never in `appsettings.json` or source.
- **Privacy**: de-identify before research exports (`TextDeidentifier`, `DicomDeidentifier`); mask identifiers on screens (`Masking`); honour consent (`ConsentRegistry`) — UU PDP No. 27/2022, Permenkes 24/2022 (rekam medis elektronik), HIPAA.
- **Clinical safety**: validate scores, rules and models locally; keep the disclaimer visible; treat AI output as decision support.
- **Transport**: TLS for HTTP; run MLLP inside a trusted network or behind a TLS tunnel.
