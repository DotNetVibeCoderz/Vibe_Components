# Arsitektur

```
                      Medical.Core  (model domain, kode klinis, UCUM, audit, event, DI, data sintetis, PNG)
         ┌───────────────┬──────────┴──────┬───────────────┬──────────────┐
  Medical.Clinical  Medical.Terminology  Medical.Fhir   Medical.Privacy   Medical.Emr
         │               │                  │  (Firely)                    (EF Core, Redis)
         └──── Medical.Rules (NRules, RulesEngine)   Medical.Hl7 (NHapi, MLLP) ──┐
                                                                                 │
  Medical.Native (Rust) ── Medical.Dicom (fo-dicom) ── Medical.Signals           │
                     └────────────── Medical.AI (ML.NET, Anthropic SDK) ─────────┤
  Medical.Visualization (SVG)          Medical.AspNetCore (REST FHIR, API, host MLLP)
```

## Model domain

`Medical.Core` mendefinisikan record immutable — `Patient`, `Encounter`, `VitalSigns`, `ClinicalObservation`, `Condition`, `MedicationOrder`, `Allergy` — yang dihimpun dalam `PatientRecord`. Kode berupa `ClinicalCode(System, Code, Display)` dengan URI sistem FHIR (`CodeSystems.Snomed`, `.Loinc`, `.Icd10`, `.RxNorm`, `.Nik`). Satuan memakai UCUM (`Ucum.Convert`). Teks untuk pengguna berupa `Localized(En, Id)`.

Model FHIR adalah format pertukaran, bukan model internal: `FhirMapper` mengonversi dua arah sehingga aturan, AI, dan skor tidak bergantung pada versi FHIR tertentu.

## Layanan lintas modul

- `services.AddMedical(b => b.AddX())` — setiap modul menambahkan ekstensi `AddX(this MedicalBuilder)` yang hanya terdaftar sekali (`TryAddModule`).
- `IMedicalEventBus` — pub/sub dalam proses (server MLLP menerbitkan `Hl7Message`, dukungan keputusan menerbitkan `ClinicalAlert`).
- `IAuditLog` — setiap pembacaan, kueri, ekspor, de-identifikasi, dan peringatan dicatat (`InMemoryAuditLog`, atau `EfAuditLog` di database EMR).
- `Pseudonymizer` — pseudonim HMAC-SHA256 berkunci dan pergeseran tanggal per subjek, dipakai bersama oleh de-identifikasi DICOM dan teks.

## Alur dukungan keputusan

`FactBuilder` mengubah `PatientRecord` menjadi fakta (`PatientFact`, `VitalsFact` dengan NEWS2/qSOFA, `LabFact` dengan interpretasi, `MedicationFact` dengan tag golongan RxNorm, `AllergyFact`). Kumpulan aturan NRules (`Medical.Rules.Definitions`) dikompilasi sekali menjadi jaringan Rete; setiap evaluasi membuat sesi, menjalankan aturan, lalu mengumpulkan fakta `ClinicalAlert`. Kebijakan yang dapat diubah (triase, tanda bahaya DBD, rujukan) berupa alur JSON Microsoft RulesEngine atas `ClinicalSnapshot`, dapat diedit tanpa kompilasi ulang.

## Kernel native

Loop berat (window/level setiap piksel, statistik HU, deteksi QRS EKG) berjalan di Rust melalui C ABI. `MedicalKernels` memuat pustaka dengan resolver khusus dan beralih ke implementasi C# dengan aritmetika identik; sebuah tes memastikan kedua backend menghasilkan keluaran yang sama.

## Aturan desain

- Pustaka bersifat deterministik: data sintetis memakai `SeededRandom` (SplitMix64) dengan seed hash FNV-1a — jangan `GetHashCode`.
- Semua yang dibaca klinisi tersedia dua bahasa (Inggris / Indonesia).
- AI tidak mengarang angka: asisten menjawab melalui alat tervalidasi; model risiko menampilkan faktor penentunya.
- Tidak ada rahasia di kode: kunci dibaca dari variabel lingkungan (`ANTHROPIC_API_KEY`, `SATUSEHAT_CLIENT_SECRET`, …).
- Di dalam `namespace Medical.*`, nama Firely lengkap harus diawali `global::Hl7.Fhir…` karena `Medical.Hl7` menutupi root `Hl7`.
