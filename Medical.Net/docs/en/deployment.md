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
| `GET/POST/PUT/DELETE /fhir/{type}[/{id}]` | CRUD with versioning, `_id`, `patient`, `identifier`, `code`, `name`, `category`, `status`, `_count` |
| `POST /fhir` | transaction / batch (transactions roll back on failure) |
| `GET /fhir/Patient/{id}/$everything` | patient compartment |
| `/api/scores/*`, `/api/calc/*`, `/api/cds/*`, `/api/terminology/*`, `/api/hl7/to-fhir`, `/api/privacy/deidentify`, `/api/audit` | clinical JSON API |

The bundled `InMemoryFhirStore` is for development, tests and edge gateways. For production, persist through `Medical.Emr` or forward transactions to a dedicated FHIR server (HAPI, Firely Server, Azure Health Data Services) — see the `medical-hl7-gateway` template.

## SATUSEHAT

Register in the SATUSEHAT developer portal, then set `SATUSEHAT_CLIENT_ID`, `SATUSEHAT_CLIENT_SECRET`, `SATUSEHAT_ORG_ID` and `SATUSEHAT_ENV=sandbox|production`, and call `AddFhir(satuSehat: SatuSehatOptions.FromEnvironment())`. Check consent first (`ConsentPurpose.SatuSehatExchange`).

## Imaging

- PACS: `StoreScp.StartServer(port, inbox)` receives C-STORE; `DicomScu.StoreAsync` sends. DICOMweb via `DicomWebClient`.
- AI: run `services/medical-ai-service` (Docker image provided) and register `AddAI(imagingServiceUrl: "http://imaging-ai:8000")`.

## Security & compliance checklist

- **Audit**: keep `PersistAudit = true`; every record read, export, de-identification and alert is logged with user and purpose.
- **Access control**: put the API behind your identity provider (`UseAuthentication`/`UseAuthorization`); `FhirEndpoints` uses `HttpContext.User` for audit.
- **Secrets**: environment variables or a vault; never in `appsettings.json` or source.
- **Privacy**: de-identify before research exports (`TextDeidentifier`, `DicomDeidentifier`); mask identifiers on screens (`Masking`); honour consent (`ConsentRegistry`) — UU PDP No. 27/2022, Permenkes 24/2022 (rekam medis elektronik), HIPAA.
- **Clinical safety**: validate scores, rules and models locally; keep the disclaimer visible; treat AI output as decision support.
- **Transport**: TLS for HTTP; run MLLP inside a trusted network or behind a TLS tunnel.
