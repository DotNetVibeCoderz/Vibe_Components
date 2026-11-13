# Deployment

## Server FHIR + listener HL7 (ASP.NET Core)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMedical(m => m
    .AddMedicalApi()
    .AddMllpListener(o => o.Port = 2575)
    .AddEmr(o => { o.Provider = EmrProvider.SqlServer; o.ConnectionString = builder.Configuration["ConnectionStrings:Emr"]!; o.Redis = builder.Configuration["Redis"]; }));
var app = builder.Build();
await app.Services.EnsureEmrDatabaseAsync();
app.MapMedicalFhir("/fhir");
app.MapMedicalApi("/api");
app.Run();
```

| Endpoint | Fungsi |
|---|---|
| `GET /fhir/metadata` | CapabilityStatement |
| `GET/POST/PUT/DELETE /fhir/{type}[/{id}]` | CRUD berversi; pencarian `_id`, `patient`, `identifier`, `code`, `name`, `category`, `status`, `_count` |
| `POST /fhir` | transaksi / batch (transaksi di-rollback bila gagal) |
| `GET /fhir/Patient/{id}/$everything` | seluruh data pasien |
| `/api/...` | skor, kalkulator, CDS, terminologi, HL7→FHIR, de-identifikasi, audit |

`InMemoryFhirStore` ditujukan untuk pengembangan, tes, dan gateway. Untuk produksi simpan melalui `Medical.Emr` atau teruskan transaksi ke server FHIR khusus (HAPI, Firely Server, Azure Health Data Services) — lihat templat `medical-hl7-gateway`.

## SATUSEHAT

Daftar di portal pengembang SATUSEHAT, atur `SATUSEHAT_CLIENT_ID`, `SATUSEHAT_CLIENT_SECRET`, `SATUSEHAT_ORG_ID`, `SATUSEHAT_ENV=sandbox|production`, lalu panggil `AddFhir(satuSehat: SatuSehatOptions.FromEnvironment())`. Periksa persetujuan pasien terlebih dahulu (`ConsentPurpose.SatuSehatExchange`).

## Pencitraan

- PACS: `StoreScp.StartServer(port, inbox)` menerima C-STORE; `DicomScu.StoreAsync` mengirim. DICOMweb melalui `DicomWebClient`.
- AI: jalankan `services/medical-ai-service` (tersedia Dockerfile) lalu `AddAI(imagingServiceUrl: "http://imaging-ai:8000")`.

## Daftar periksa keamanan & kepatuhan

- **Audit**: biarkan `PersistAudit = true`; setiap pembacaan rekam medis, ekspor, de-identifikasi dan peringatan dicatat beserta pengguna dan tujuannya.
- **Kontrol akses**: tempatkan API di belakang identity provider (`UseAuthentication`/`UseAuthorization`).
- **Rahasia**: variabel lingkungan atau vault; jangan di `appsettings.json` atau kode.
- **Privasi**: de-identifikasi sebelum ekspor penelitian; samarkan identitas di layar (`Masking`); hormati persetujuan (`ConsentRegistry`) — UU PDP No. 27/2022, Permenkes 24/2022 tentang rekam medis elektronik, HIPAA.
- **Keselamatan klinis**: validasi skor, aturan, dan model secara lokal; tampilkan disclaimer; perlakukan keluaran AI sebagai pendukung keputusan.
- **Transport**: TLS untuk HTTP; jalankan MLLP di jaringan tepercaya atau melalui terowongan TLS.
