# Dokumentasi Medical.Net

Medical.Net — Platform Medis Komprehensif untuk .NET 10. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/README.md)

| Panduan | Isi |
|---|---|
| [Memulai](getting-started.md) | Instalasi, rekam pasien pertama, NEWS2, dukungan keputusan, ekspor FHIR |
| [Arsitektur](architecture.md) | Lapisan paket, model domain, DI, event, audit, aturan desain |
| [Modul](modules.md) | Semua paket beserta contoh: FHIR, HL7, DICOM, terminologi, klinis, aturan, sinyal, privasi, EMR, visualisasi |
| [Interoperabilitas (v1.1)](interoperability.md) | Server FHIR (pencarian, `_include`, `_history`, persistensi), validasi profil, SMART on FHIR, alur SATUSEHAT + KFA, order / jadwal / dokumen / kesesuaian HL7, BPJS VClaim · PCare · E-Klaim / INA-CBG |
| [Pencitraan dan sinyal (v1.2)](imaging-signals.md) | Codec JPEG / JPEG-LS / JPEG 2000, volume, MPR / MIP (Rust), isosurface + 3D (ThreeNet, three.js), DICOM SEG + SR TID 1500, nodul paru + bundle MONAI, EKG 12 sadapan (QT/QTc, kriteria STEMI), waveform DICOM dan SCP-ECG |
| [Kecerdasan klinis (v1.3)](clinical-intelligence.md) | SOFA, APACHE II, PEWS, MEOWS, Wells, PERC, ASCVD / Framingham, pertumbuhan WHO, sumber interaksi obat, dosis ginjal / hati, NLP klinis + saran ICD-10 (HF.Net), jawaban pedoman bersitasi, federated learning, model card, drift |
| [Platform rumah sakit (v2.0)](platform.md) | Inti SIMRS multi-tenant (pendaftaran, rawat jalan, rawat inap, farmasi, lab, radiologi, kasir, data klaim INA-CBG, dasbor), NEWS2 waktu nyata dari monitor pasien dan pompa infus (IoTCom.Net, pratinjau), sinkronisasi offline-first, aplikasi SIMRS Blazor dan visite MAUI |
| [AI klinis](ai.md) | Model risiko, asisten klinis (Claude / kompatibel OpenAI / offline), AI pencitraan dan layanan MONAI |
| [Deployment](deployment.md) | Server FHIR, listener MLLP, SATUSEHAT, database & cache EMR, layanan pencitraan, keamanan & kepatuhan |
| [Perkakas](tools.md) | CLI `medical`, templat `dotnet new`, aplikasi Galeri dan tangkapan layar |
| [Kernel native](native.md) | Crate Rust, fallback managed, build untuk platform lain |

![Galeri](../images/web-home-id.png)

> Medical.Net adalah perangkat lunak pengembang, bukan alat kesehatan bersertifikat. Skor klinis, aturan, dan keluaran AI membantu — bukan menggantikan — penilaian tenaga medis yang berwenang.
