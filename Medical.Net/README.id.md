# Medical.Net

**Platform Medis Komprehensif untuk .NET 10** — FHIR R4, HL7 v2, DICOM, SNOMED CT, ICD-10, LOINC dan RxNorm dalam satu toolkit, lengkap dengan dukungan keputusan klinis, AI pencitraan, asisten klinis berbasis LLM, dan kernel Rust untuk jalur yang menuntut performa.

Dibuat oleh **Gravicode Studios** dipimpin oleh **Kang Fadhil**. · [English](README.md) · [Dokumentasi](docs/id/README.md)

![Galeri Medical.Net](docs/images/web-home-id.png)

> Medical.Net adalah perangkat lunak pengembang, bukan alat kesehatan bersertifikat. Skor klinis, aturan, dan keluaran AI membantu — bukan menggantikan — penilaian tenaga medis yang berwenang. Semua data contoh bersifat sintetis.

## Paket

| Paket | Fungsi | Dasar |
|---|---|---|
| `Medical.Core` | Model pasien, kunjungan, tanda vital, observasi, diagnosis, obat, alergi; kode klinis; satuan UCUM; jejak audit; event bus; `services.AddMedical(...)`; pasien sintetis deterministik | — |
| `Medical.Fhir` | Pemetaan domain ↔ FHIR R4, bundle transaksi dan **International Patient Summary**, JSON, FHIRPath; repositori FHIR dengan pencarian lengkap (`_include`/`_revinclude`, `_sort`, prefiks tanggal, modifier), `_history`, If-Match; **validasi profil** terhadap inti R4 dan paket IG; klien **SMART on FHIR**; alur kunjungan **SATUSEHAT**, KFA, retry/backoff | Firely .NET SDK |
| `Medical.Hl7` | Parse/buat HL7 v2 (ADT^A01, ORU^R01, **ORM^O01, OML^O21, SIU^S12, MDM^T02**, ACK), **profil kesesuaian** tingkat segmen, HL7 v2 → FHIR, server & klien MLLP | NHapi |
| `Medical.Dicom` | Metadata, render window/level ke PNG, statistik HU, de-identifikasi PS3.15, phantom CT sintetis, DICOMweb, C-ECHO / C-STORE | fo-dicom |
| `Medical.Terminology` | ICD-10 (WHO 2010 / INA-CBG), SNOMED CT, LOINC, RxNorm — pencarian EN/ID, hierarki, peta SNOMED→ICD-10, digit pemeriksa, pemuat rilis lengkap | — |
| `Medical.Clinical` | NEWS2, qSOFA, SIRS, CURB-65, CHA₂DS₂-VASc, HAS-BLED, GCS, APGAR, MELD, eGFR CKD-EPI 2021, Cockcroft-Gault, IMT (WHO / Asia-Pasifik), dosis, interpretasi lab | — |
| `Medical.Rules` | Dukungan keputusan: peringatan dini, Sepsis-3, nilai lab kritis, interaksi obat, konflik alergi, dosis ginjal, terapi ganda; alur JSON yang dapat diubah (triase, tanda bahaya DBD, rujukan) | NRules, Microsoft RulesEngine |
| `Medical.AI` | Model risiko ML.NET dengan penjelasan; asisten klinis berbasis alat di **Claude**, endpoint kompatibel OpenAI (Hugging Face, Azure OpenAI, DeepSeek, Ollama) atau aturan offline; skrining perdarahan CT; klien layanan MONAI | ML.NET, Anthropic SDK |
| `Medical.Bpjs` | **BPJS Kesehatan**: klien VClaim dan PCare (tanda tangan HMAC, respons AES + LZ-String), web service E-Klaim (terenkripsi), penyusun klaim INA-CBG dengan pemeriksaan koding sebelum grouping | — |
| `Medical.Signals` / `Privacy` / `Emr` / `AspNetCore` / `Visualization` / `Native` | EKG & HRV, de-identifikasi & persetujuan (UU PDP), penyimpanan EMR EF Core + Redis dan **repositori FHIR berversi**, server FHIR + **otorisasi SMART on FHIR** & MLLP, grafik SVG, kernel Rust | — |
| `Medical.Cli` · `Medical.Templates` | Perintah `medical` dan templat `dotnet new` | — |

## Yang baru di 1.1

- **Server FHIR**: repositori EF Core yang tahan lama, `_include`/`_revinclude`, `_history`/vread, If-Match, pencarian tanggal/token/referensi/string dengan modifier, `$validate` terhadap inti R4 dan paket IG, **SMART on FHIR** (PKCE, layanan backend, kompartemen pasien).
- **SATUSEHAT**: bundle kunjungan rawat jalan dalam satu panggilan (Encounter → Condition → Observation → Medication/KFA → MedicationRequest), pencarian KFA, retry/backoff dan pembaruan token.
- **HL7 v2**: order ORM/OML, penjadwalan SIU, dokumen MDM, profil kesesuaian tingkat segmen.
- **BPJS** (`Medical.Bpjs`): VClaim, PCare, E-Klaim dan pemeriksaan klaim INA-CBG.

Lihat [Interoperabilitas](docs/id/interoperability.md).

## Mulai cepat

```csharp
var record = SyntheticData.Record(3, deterioration: 0.8);
Console.WriteLine(ClinicalScores.News2(record.LatestVitals!).Interpretation.Id);
foreach (var a in new ClinicalDecisionSupport().Evaluate(record)) Console.WriteLine(a.Title.Id);
var ips = new FhirMapper().ToPatientSummary(record);   // dokumen FHIR R4 IPS
```

```bash
dotnet tool install -g Medical.Cli
medical news2 --rr 24 --spo2 93 --o2 --sbp 105 --hr 115 --temp 38.5 --lang id
medical code search "demam berdarah"
medical satusehat bundle --index 3 && medical bpjs claim --index 7 --lang id
medical hl7 sample --type siu --out siu.hl7 && medical hl7 conform siu.hl7
dotnet new install Medical.Templates && dotnet new medical-fhir-api -n RumahSakit.Api --smart --persist
```

## Galeri

Tujuh belas kasus penggunaan di Galeri Medical.Net tersedia untuk web (Blazor) dan desktop (Avalonia) dalam bahasa Indonesia dan Inggris:

| Web | Desktop |
|---|---|
| ![](docs/images/web-home-id.png) | ![](docs/images/desktop-news2-id.png) |
| ![](docs/images/web-dicom.png) | ![](docs/images/desktop-ecg-id.png) |
| ![](docs/images/web-satusehat.png) | ![](docs/images/desktop-bpjs-id.png) |
| ![](docs/images/web-hl7-orders.png) | ![](docs/images/desktop-fhir-server-id.png) |

```bash
dotnet run --project gallery/Medical.Gallery.Web --urls http://localhost:5290   # buka /?lang=id
dotnet run --project gallery/Medical.Gallery.Desktop
```

## Build dari kode sumber

```bash
pwsh build/build-native.ps1     # kernel Rust (opsional)
dotnet build Medical.Net.slnx
dotnet test tests/Medical.Tests
pwsh build/pack.ps1
```

Dokumentasi lengkap: [docs/id](docs/id/README.md) · Peta jalan: [PLAN.md](PLAN.md) · Status: [Progress.md](Progress.md)

---
© 2026 Gravicode Studios. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. Lisensi MIT.
