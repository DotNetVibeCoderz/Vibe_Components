# Dokumentasi Electrical.Net (Bahasa Indonesia)

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/README.md)

Electrical.Net adalah framework sistem tenaga untuk .NET 10: 22 modul pustaka ditambah kernel native Rust, tool CLI, templat proyek, dan galeri
Blazor/Avalonia. Mulailah dari [Memulai](memulai.md), lalu baca panduan untuk bidang yang Anda kerjakan.

| Panduan | Isi |
|---|---|
| [Memulai](memulai.md) | Instalasi, program pertama, membangun jaringan, dependency injection, satuan dan konvensi |
| [Arsitektur](arsitektur.md) | Graf dependensi, model jaringan, numerik, aturan desain, kinerja, interop native |
| [Panduan modul](modul.md) | Setiap paket: tujuan, tipe utama, contoh singkat, acuan dan batasan |
| [Aliran daya](aliran-daya.md) | Newton–Raphson, fast-decoupled, Gauss–Seidel, DC, backward/forward sweep, beban ZIP, batas Q, N-1, PTDF/LODF |
| [Hubung singkat & proteksi](hubung-singkat-proteksi.md) | IEC 60909, relai, koordinasi, proteksi jarak dan diferensial, breaker duty, arc flash |
| [Stabilitas](stabilitas.md) | Stabilitas transien, CCT, kriteria luas sama, sinyal kecil, kurva PV/QV, indeks-L, respons frekuensi |
| [Harmonik & kualitas daya](harmonik.md) | FFT dan spektrum, IEEE 519 / IEC 61000, aliran daya harmonik, frequency scan, filter |
| [Energi terbarukan & smart grid](energi-terbarukan-smartgrid.md) | PV, angin, baterai, grid code, hosting capacity, DR, EV, microgrid, VPP, VVO, rekonfigurasi, keandalan, prakiraan |
| [Optimasi](optimasi.md) | Economic dispatch, DC-OPF dan LMP, kendala keamanan, unit commitment, penempatan kapasitor |
| [Estimasi keadaan](estimasi-keadaan.md) | Estimator WLS, pengukuran, observabilitas, deteksi bad data |
| [SCADA, IEC 61850 & CIM](scada-iec61850-cim.md) | Modbus TCP, tag, alarm, historian, simulator RTU, IEC 104, GOOSE, SV, SCL, CGMES |
| [GIS & visualisasi](gis-visualisasi.md) | GeoJSON, peta, indeks spasial, area padam, diagram segaris, grafik, komponen Blazor |
| [Simulasi, HPC & native](simulasi-hpc-native.md) | Simulasi rangkaian, QSTS, kecukupan pembangkitan, aliran daya batch dan probabilistik, kernel Rust |
| [API cloud](api-cloud.md) | Endpoint REST, job studi, penyimpanan skenario, kontrak JSON jaringan |
| [AI & digital twin](ai-digital-twin.md) | Agen LLM operator jaringan dan tool-nya, deteksi anomali, analitik gangguan, RL Volt/VAR, digital twin langsung, what-if, kesehatan aset |
| [CLI](cli.md) | Referensi perintah `eln` dengan contoh keluaran |
| [Templat](templat.md) | Templat `dotnet new eln-*` |
| [Galeri](galeri.md) | Aplikasi pameran Blazor dan Avalonia, tangkapan layar, menambah demo |

![Galeri](../images/web-home.png)

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/README.md)*
