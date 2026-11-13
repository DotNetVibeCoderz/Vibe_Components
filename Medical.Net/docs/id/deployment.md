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

## Baru di 1.1: server FHIR, SMART dan BPJS

| Endpoint | Fungsi |
|---|---|
| `GET /fhir/{type}?…` | pencarian dengan `_include`/`_revinclude`, `_sort`, `_count`/`_offset`, prefiks tanggal dan modifier |
| `GET /fhir/{type}/{id}/_history[/{vid}]`, `/fhir/{type}/_history`, `/fhir/_history` | riwayat dan vread (resource terhapus dijawab 410 Gone) |
| `PUT /fhir/{type}/{id}` dengan `If-Match` | pembaruan bersyarat (412 bila versi berubah) |
| `POST /fhir/{type}/$validate[?profile=…]` | validasi profil (inti R4 + paket IG) |
| `/fhir/.well-known/smart-configuration`, `/auth/*` | SMART on FHIR (`AddSmartOnFhir` + `MapSmartAuthorization`) |
| `POST /api/hl7/conformance` | validasi kesesuaian HL7 v2 |

- **Persistensi**: dengan `AddEmr()` endpoint FHIR memakai `EfFhirRepository` (semua versi disimpan, indeks pencarian di SQL).
- **Kontrol akses**: aktifkan SMART on FHIR; scope diperiksa pada setiap interaksi dan kompartemen pasien ditegakkan. Pasang `/auth/authorize` di belakang penyedia identitas Anda.
- **SATUSEHAT**: `HttpClient` `"satusehat"` mengulang 429/5xx dengan backoff; susun kunjungan dengan `SatuSehatWorkflow.BuildOutpatientVisit` dan kode obat dengan `KfaClient`.
- **BPJS Kesehatan**: setel `BPJS_CONS_ID`, `BPJS_SECRET_KEY`, `BPJS_USER_KEY` lalu `AddBpjs(o => { o.Environment = BpjsEnvironment.Production; o.EKlaimUrl = …; o.EKlaimKey = …; })`. Simpan kunci E-Klaim di secret store dan jalankan server E-Klaim di jaringan internal rumah sakit.

Detail: [Interoperabilitas](interoperability.md).

## Baru di 1.2: layanan pencitraan

- `POST /v1/ct/lung-nodules` — deteksi nodul paru (klasik, atau bundle MONAI `lung_nodule_ct_detection` bila `MEDNET_MONAI_LUNG_BUNDLE` disetel; perlu `torch` + `torchvision`, GPU sangat disarankan).
- `ImagingAiClient.LungNodulesAsync` memakai layanan dan kembali ke `LungNoduleDetector` lokal bila layanan tidak terjangkau.
- Simpan hasil sebagai DICOM SEG + SR (`ImagingAiReports`) ke PACS di samping studi aslinya.
- Codec native (`fo-dicom.Codecs`) memerlukan Visual C++ 14 runtime di Windows; tersedia untuk win/linux/osx x64 dan arm64.
