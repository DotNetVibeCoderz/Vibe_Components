# Perkakas: CLI, templat, dan Galeri

## CLI `medical`

```bash
dotnet tool install -g Medical.Cli
medical help
```

| Perintah | Contoh |
|---|---|
| Skor | `medical news2 --rr 24 --spo2 93 --o2 --sbp 105 --hr 115 --temp 38.5 --lang id` · `medical qsofa --rr 22 --sbp 98 --altered` |
| Kalkulator | `medical egfr --cr 1.2 --age 60 --sex f` · `medical bmi --weight 70 --height 172` · `medical dose amoxicillin --weight 25` |
| Terminologi | `medical code search demam berdarah --lang id` · `medical code validate loinc 2345-7` · `medical code map 44054006` |
| Keamanan obat | `medical interactions warfarin ibuprofen --allergy penicillin --lang id` |
| FHIR / HL7 | `medical fhir ips --index 3 --out ips.json` · `medical hl7 sample --out oru.hl7` · `medical hl7 listen --port 2575` |
| DICOM | `medical dicom phantom --out ct` · `medical dicom render ct/IM015.dcm --window brain` · `medical dicom hemorrhage ct --lang id` |
| Lainnya | `medical ecg --rhythm af` · `medical deid "Ny. Siti, HP 0812…"` · `medical cds --lang id` · `medical risk` · `medical ask "…" --patient 7` · `medical emr seed` |

## Templat `dotnet new`

```bash
dotnet new install Medical.Templates
dotnet new medical-fhir-api -n RumahSakit.Api
```

| Templat | Hasil |
|---|---|
| `medical-console` | Aplikasi konsol: NEWS2, dukungan keputusan, daftar masalah dua bahasa, ekspor IPS |
| `medical-fhir-api` | Server FHIR ASP.NET Core + API klinis + listener MLLP, berisi pasien sintetis |
| `medical-hl7-gateway` | Worker service: MLLP → FHIR, peringatan CDS di log, opsi meneruskan ke server FHIR |
| `medical-blazor` | Papan bangsal Blazor diurutkan berdasarkan NEWS2 |
| `medical-avalonia` | Penampil DICOM Avalonia dengan preset jendela, statistik HU, skrining perdarahan |

## Galeri

Tiga belas kasus penggunaan di dua aplikasi yang berbagi `gallery/Medical.Gallery.Shared`:

```bash
dotnet run --project gallery/Medical.Gallery.Web --urls http://localhost:5290      # buka /?lang=id
dotnet run --project gallery/Medical.Gallery.Desktop
dotnet run --project gallery/Medical.Gallery.Desktop -- --screenshot docs/images id
```

| | |
|---|---|
| ![](../images/desktop-news2-id.png) | ![](../images/desktop-cds-id.png) |
| ![](../images/desktop-dicom-id.png) | ![](../images/desktop-terminology-id.png) |

## Baru di 1.1

| Perintah | Contoh |
|---|---|
| Validasi profil FHIR | `medical fhir validate obs.json --profile http://hl7.org/fhir/StructureDefinition/heartrate` |
| Order / jadwal / dokumen HL7 | `medical hl7 sample --type orm|oml|siu|mdm --out msg.hl7` · `medical hl7 conform msg.hl7` |
| SATUSEHAT | `medical satusehat bundle --index 3 --out kunjungan.json` |
| BPJS | `medical bpjs claim --index 7 --lang id` (data klaim INA-CBG + pemeriksaan koding; kode keluar 2 bila ada galat) |

Templat `medical-fhir-api` kini menerima `--smart` (otorisasi SMART on FHIR) dan `--persist` (FHIR tersimpan di EF Core/SQLite); `--MedicalNetVersion` bawaan `1.1.0`.

Galeri kini berisi tujuh belas kasus, termasuk **Server FHIR**, **Order & jadwal HL7**, **Kunjungan SATUSEHAT** dan **Klaim BPJS (INA-CBG)**:

| | |
|---|---|
| ![](../images/desktop-fhir-server-id.png) | ![](../images/desktop-hl7-orders-id.png) |
| ![](../images/desktop-satusehat-id.png) | ![](../images/desktop-bpjs-id.png) |

Tangkapan layar web dibuat dengan Chrome headless (`--screenshot`, `--blink-settings=preferredColorScheme=1` untuk mode terang, `--force-prefers-reduced-motion` agar animasi EKG sudah selesai).
