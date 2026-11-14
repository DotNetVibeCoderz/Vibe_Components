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
| `Medical.Dicom` | Metadata, render window/level, statistik HU, **codec JPEG / JPEG-LS / JPEG 2000 / RLE**, **volume dengan MPR / MIP / MinIP** (kernel Rust), **isosurface → STL / OBJ**, **DICOM SEG + SR TID 1500**, **waveform EKG 12 sadapan**, de-identifikasi PS3.15, phantom CT sintetis, DICOMweb, C-ECHO / C-STORE | fo-dicom, fo-dicom.Codecs |
| `Medical.Terminology` | ICD-10 (WHO 2010 / INA-CBG), SNOMED CT, LOINC, RxNorm — pencarian EN/ID, hierarki, peta SNOMED→ICD-10, digit pemeriksa, pemuat rilis lengkap | — |
| `Medical.Clinical` | NEWS2, qSOFA, SIRS, CURB-65, CHA₂DS₂-VASc, HAS-BLED, GCS, APGAR, MELD, **SOFA, APACHE II, PEWS, MEOWS, Wells, PERC, Child-Pugh**, **Pooled Cohort Equations / Framingham**, **standar pertumbuhan WHO (0–19 th)**, eGFR CKD-EPI 2021, Cockcroft-Gault, IMT (WHO / Asia-Pasifik), dosis, **dosis ginjal / hati dari label FDA**, interpretasi lab | — |
| `Medical.Rules` | Dukungan keputusan: peringatan dini, Sepsis-3, nilai lab kritis, interaksi obat, konflik alergi, dosis ginjal, terapi ganda; alur JSON yang dapat diubah (triase, tanda bahaya DBD, rujukan); **sumber interaksi obat yang dapat dipasang** (kontraindikasi label FDA, CSV, XML DrugBank, gabungan) | NRules, Microsoft RulesEngine |
| `Medical.AI` | Model risiko ML.NET dengan penjelasan; asisten klinis berbasis alat di **Claude**, endpoint kompatibel OpenAI (Hugging Face, Azure OpenAI, DeepSeek, Ollama) atau aturan offline; skrining perdarahan CT; klien layanan MONAI; **NLP klinis** (entitas EN / ID, negasi, saran ICD-10); **jawaban pedoman bersitasi**; **federated learning** dengan agregasi aman dan privasi diferensial; **model card** dan **pemantauan drift** | ML.NET, Anthropic SDK |
| `Medical.HuggingFace` | Model Hugging Face dalam proses: NER biomedis dan embedding kalimat multibahasa untuk pencarian pedoman | HF.Net |
| `Medical.Bpjs` | **BPJS Kesehatan**: klien VClaim dan PCare (tanda tangan HMAC, respons AES + LZ-String), web service E-Klaim (terenkripsi), penyusun klaim INA-CBG dengan pemeriksaan koding sebelum grouping | — |
| `Medical.Signals` / `Privacy` / `Emr` / `AspNetCore` / `Visualization` / `Native` | EKG & HRV, de-identifikasi & persetujuan (UU PDP), penyimpanan EMR EF Core + Redis dan **repositori FHIR berversi**, server FHIR + **otorisasi SMART on FHIR** & MLLP, grafik SVG, kernel Rust | — |
| `Medical.Hospital` | **Inti SIMRS multi-tenant**: model EF Core skema bersama (SQLite / SQL Server) dengan filter kueri dan penjaga tulis per tenant; pendaftaran (de-duplikasi NIK, nomor RM, nomor antrean harian), SOAP + ICD-10, tanda vital dengan NEWS2, rawat inap / pindah / pulang; telaah farmasi (interaksi, alergi, dosis ginjal) dengan penyerahan FEFO; nilai kritis lab dengan konfirmasi baca ulang serta HL7 OML / ORU; worklist dan ekspertise radiologi; kasir (Umum / BPJS / asuransi) dengan data klaim INA-CBG; dasbor (BOR, ALOS, antrean); `MonitoringEngine` NEWS2 waktu nyata | EF Core 10 |
| `Medical.Sync` | **Sinkronisasi offline-first**: hybrid logical clock, kebijakan konflik per tipe (append-only, penulis terakhir menang, server menang, hanya-baca), kursor tarik berdasarkan nomor urut server, penyimpanan klien SQLite, transport HTTP / dalam proses | Microsoft.Data.Sqlite |
| `Medical.Devices` *(pratinjau)* | **Perangkat samping tempat tidur**: monitor pasien HL7 v2 / MLLP, sensor dan pompa infus MQTT + SenML, simulator monitor dan pompa, gateway ke mesin NEWS2. Versi `2.0.0-preview` karena IoTCom.Net masih prarilis | IoTCom.Net 0.22 (pratinjau) |
| `Medical.Cli` · `Medical.Templates` | Perintah `medical` dan templat `dotnet new` | — |

## Yang baru di 2.0

- **Inti SIMRS** (`Medical.Hospital`): satu basis data untuk banyak rumah sakit, dengan setiap baris diberi tenant, difilter dan dijaga dari penulisan lintas tenant. Cakupannya pendaftaran dan antrean, rawat jalan dan rawat inap, farmasi, laboratorium, radiologi, kasir dengan data klaim INA-CBG BPJS, serta dasbor.
- **Pemantauan waktu nyata** (`Medical.Devices`, pratinjau): monitor HL7 / MLLP serta sensor dan pompa MQTT / SenML (IoTCom.Net) mengalirkan data ke mesin NEWS2. Mesin ini menyaring plausibilitas dan median, mengikuti eskalasi RCP, memberi peringatan terkonfirmasi dengan histeresis, dan mencatat tiap 15 menit untuk divalidasi perawat.
- **Sinkronisasi offline-first** (`Medical.Sync`): hybrid logical clock, data klinis append-only, tugas penulis-terakhir-menang, daftar pasien hanya-baca, dan kursor tarik berdasarkan nomor urut server sehingga kiriman luring yang terlambat tidak pernah terlewat.
- **Aplikasi referensi**: SIMRS Blazor Server untuk dua rumah sakit fiktif (`apps/Medical.His`) dan aplikasi visite MAUI untuk Windows dan Android (`apps/Medical.Rounds`).

Lihat [Platform rumah sakit](docs/id/platform.md).

## Yang baru di 1.3

- **Skor**: SOFA, APACHE II (dengan prediksi mortalitas), PEWS, MEOWS, Wells EP / TVD, PERC, Child-Pugh; Pooled Cohort Equations ASCVD dan Framingham, diverifikasi terhadap implementasi independen.
- **Pertumbuhan WHO**: Standar Pertumbuhan Anak dan Referensi 2007 dengan algoritma WHO. Mesinnya cocok dengan seluruh 2.101 z-score survei validasi WHO; tabel diunduh dari WHO saat runtime.
- **Keamanan obat**: sumber interaksi yang dapat dipasang (kontraindikasi label FDA, ekspor CSV, XML DrugBank berlisensi) serta dosis ginjal / hati dari label.
- **NLP klinis**: entitas bahasa Indonesia / Inggris dengan negasi dan riwayat keluarga, saran ICD-10, dan NER Hugging Face melalui HF.Net (`Medical.HuggingFace`).
- **Pedoman**: pencarian hibrida atas teks PNPK Kemenkes / WHO dan jawaban LLM dengan sitasi tervalidasi, serta jawaban ekstraktif saat offline.
- **Federated learning**: agregasi aman, privasi diferensial dengan anggaran ε, model card per subgrup dan pemantauan drift.

Lihat [Kecerdasan klinis](docs/id/clinical-intelligence.md).

## Yang baru di 1.2

- **Pencitraan**: codec JPEG / JPEG-LS / JPEG 2000 / HT-J2K / RLE; `DicomVolume` dengan MPR, MIP, MinIP dan slab miring di kernel Rust; isosurface surface-nets → STL; penampil 3D dengan ThreeNet (desktop) dan three.js (web).
- **AI pencitraan**: deteksi nodul paru dengan Lung-RADS v2022; hasil perdarahan dan nodul sebagai **DICOM SEG + SR TID 1500** (divalidasi dengan highdicom); bundle MONAI `lung_nodule_ct_detection` di layanan pencitraan.
- **EKG 12 sadapan**: QT/QTc (Bazett, Fridericia, Framingham, Hodges), aksis, ST di J, kriteria STEMI (UDMI 2018); waveform DICOM 12 sadapan dan SCP-ECG; QT divalidasi terhadap PhysioNet QT Database.

Lihat [Pencitraan dan sinyal](docs/id/imaging-signals.md).

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
medical dicom ai ct/ --out-dir hasil && medical ecg12 --pattern inferior --out ekg.svg --lang id
medical hl7 sample --type siu --out siu.hl7 && medical hl7 conform siu.hl7
medical growth --sex f --age-months 24 --weight 9 --height 77.5 --lang id && medical nlp "demam berdarah, tidak ada sesak napas" --lang id
medical guideline ask "Berapa lama pengobatan TB paru?" --lang id
medical his seed --db his.db && medical his dashboard --db his.db --tenant rs-bahari --lang id
medical monitor --minutes 120 --lang id && medical sync demo --lang id
dotnet new install Medical.Templates && dotnet new medical-fhir-api -n RumahSakit.Api --smart --persist
```

## Galeri

Tiga puluh dua kasus penggunaan di Galeri Medical.Net tersedia untuk web (Blazor) dan desktop (Avalonia) dalam bahasa Indonesia dan Inggris:

| Web | Desktop |
|---|---|
| ![](docs/images/web-home-id.png) | ![](docs/images/desktop-news2-id.png) |
| ![](docs/images/web-dicom.png) | ![](docs/images/desktop-ecg-id.png) |
| ![](docs/images/web-satusehat.png) | ![](docs/images/desktop-bpjs-id.png) |
| ![](docs/images/web-hl7-orders.png) | ![](docs/images/desktop-fhir-server-id.png) |
| ![](docs/images/web-volume3d.png) | ![](docs/images/desktop-ecg12-id.png) |
| ![](docs/images/web-growth.png) | ![](docs/images/desktop-clinical-nlp-id.png) |
| ![](docs/images/web-federated.png) | ![](docs/images/desktop-drug-safety-id.png) |
| ![](docs/images/web-live-monitoring.png) | ![](docs/images/desktop-offline-sync-id.png) |

```bash
dotnet run --project gallery/Medical.Gallery.Web --urls http://localhost:5290   # buka /?lang=id
dotnet run --project gallery/Medical.Gallery.Desktop
```

## Aplikasi rumah sakit

Aplikasi referensi SIMRS menjalankan dua rumah sakit fiktif — RS Medika Nusantara (Jakarta) dan RSUD Kota Bahari (Makassar) — dengan monitor ICU tersimulasi:

| | |
|---|---|
| ![](docs/images/his-dashboard.png) | ![](docs/images/his-monitoring.png) |
| ![](docs/images/his-registration.png) | ![](docs/images/his-lab.png) |
| ![](docs/images/rounds-ward-id.png) | ![](docs/images/rounds-vitals-id.png) |

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project apps/Medical.His --urls http://localhost:5300   # login demo di /login
dotnet build Medical.Rounds.slnx                                    # visite MAUI (perlu workload maui-windows)
dotnet run --project apps/Medical.Rounds -f net10.0-windows10.0.19041.0
```

Login demo tidak memakai kata sandi. Ganti dengan OpenID Connect dan isi `His:DeviceKeys:{tenant}` sebelum dipakai sungguhan. Lihat [Platform rumah sakit](docs/id/platform.md).

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
