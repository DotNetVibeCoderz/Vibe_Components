# Galeri

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/gallery.md)

Galeri Electrical.Net memperlihatkan pustaka bekerja dalam 29 demo langsung, masing-masing di samping kode C# yang menghasilkannya. Galeri
tersedia sebagai aplikasi web **Blazor** dan aplikasi desktop **Avalonia** yang berbagi kode skenario (`gallery/Electrical.Gallery.Shared`).
Keduanya dwibahasa (EN/ID) serta punya tema terang dan gelap. Setiap angka dihitung oleh pustaka saat berjalan; tidak ada yang dirender lebih dulu.

```bash
dotnet run --project gallery/Electrical.Gallery.Web --urls http://localhost:5290
dotnet run --project gallery/Electrical.Gallery.Desktop
```

![Beranda galeri](../images/web-home.png)

## Demo

| Bagian | Demo (`/demo/<id>`) | Modul |
|---|---|---|
| Analisis jaringan | Aliran daya & perbandingan metode (`powerflow`) | PowerFlow, Visualization |
| | Peringkat kontingensi N-1 (`contingency`) | PowerFlow |
| | Distribusi tak seimbang (IEEE 13 node) (`distribution`) | Distribution |
| | Estimasi keadaan & data buruk (`stateestimation`) | StateEstimation |
| Gangguan & proteksi | Hubung singkat IEC 60909 (`shortcircuit`) | ShortCircuit |
| | Koordinasi relai & arc flash (`protection`) | Protection |
| Dinamika & kualitas daya | Stabilitas transien & tegangan (`stability`) | Stability |
| | Harmonik & resonansi (`harmonic`) | Harmonic |
| | Simulasi rangkaian (MNA) (`circuit`) | Circuit |
| | Dinamika sistem tenaga (Kundur dua area) (`dynamics`) (v1.3) | Dynamics, Stability |
| | Sumber berbasis inverter (`ibr`) (v1.3) | Dynamics, Renewable |
| | Transien elektromagnetik (`emt`) (v1.3) | Emt, Native |
| Energi terbarukan & smart grid | Energi surya PV & angin (`renewable`) | Renewable |
| | Operasi smart grid (`smartgrid`) | SmartGrid |
| Pasar, peramalan & perencanaan | OPF, LMP & unit commitment (`optimization`) | Optimization |
| | Aliran daya optimal AC (`acopf`) (v1.2) | Optimization, Visualization |
| | Unit commitment dengan kendala keamanan (`scuc`) (v1.2) | Markets, Optimization |
| | Kliring pasar listrik (`market`) (v1.2) | Markets |
| | Peramalan beban (`forecasting`) | Forecasting |
| | Simulasi deret waktu & kecukupan (`simulation`) | Simulation |
| | Kendali tegangan dalam deret waktu (`voltagecontrol`) | Simulation |
| Kembaran digital & AI | Kembaran digital langsung (`digitaltwin`) | DigitalTwin |
| | Agen AI operator jaringan (`agent`) | AI |
| Komputasi kinerja tinggi | Aliran daya probabilistik (`plf`) | HPC, Native |
| Operasi & protokol | SCADA: RTU, alarm & historian (`scada`) | SCADA |
| | IEC 61850: GOOSE, SV & SCL (`iec61850`) | IEC61850 |
| Pertukaran data & geografi | Pertukaran model (PSS/E RAW, IEEE CDF, MATPOWER, CIM) (`interop`) | Interop, CIM |
| | Ekspor CIM / CGMES (`cim`) | CIM |
| | Peta jaringan GIS (`gis`) | GIS |

## Tangkapan layar

Semua tangkapan layar ada di [docs/images](../images). Penamaan: `web-home.png`, `web-home-dark.png`, `web-<demo>.png`, `desktop-home.png`,
`desktop-<demo>.png`.

| Web (Blazor) | Desktop (Avalonia) |
|---|---|
| ![Aliran daya](../images/web-powerflow.png) | ![Aliran daya](../images/desktop-powerflow.png) |
| ![Hubung singkat](../images/web-shortcircuit.png) | ![Stabilitas](../images/desktop-stability.png) |
| ![Digital twin](../images/web-digitaltwin.png) | ![Agen AI](../images/desktop-agent.png) |
| ![SCADA](../images/web-scada.png) | ![IEC 61850](../images/desktop-iec61850.png) |
| ![GIS](../images/web-gis.png) | ![Smart grid](../images/desktop-smartgrid.png) |
| ![Distribusi tak seimbang](../images/web-distribution.png) | ![Pertukaran model](../images/desktop-interop.png) |
| ![Kendali tegangan](../images/web-voltagecontrol.png) | ![Distribusi tak seimbang](../images/desktop-distribution.png) |
| ![Aliran daya optimal AC](../images/web-acopf.png) | ![Aliran daya optimal AC](../images/desktop-acopf.png) |
| ![Unit commitment dengan kendala keamanan](../images/web-scuc.png) | ![Unit commitment dengan kendala keamanan](../images/desktop-scuc.png) |
| ![Kliring pasar listrik](../images/web-market.png) | ![Kliring pasar listrik](../images/desktop-market.png) |
| ![Dinamika sistem tenaga](../images/web-dynamics.png) | ![Dinamika sistem tenaga](../images/desktop-dynamics.png) |
| ![Sumber berbasis inverter](../images/web-ibr.png) | ![Sumber berbasis inverter](../images/desktop-ibr.png) |
| ![Transien elektromagnetik](../images/web-emt.png) | ![Transien elektromagnetik](../images/desktop-emt.png) |

Tema gelap: ![Gelap](../images/web-home-dark.png)

Membuat ulang tangkapan layar desktop (renderer Skia headless):

```bash
dotnet run --project gallery/Electrical.Gallery.Desktop -- --screenshot docs/images en          # desktop-<id>.png
dotnet run --project gallery/Electrical.Gallery.Desktop -- --screenshot docs/images id light    # desktop-<id>-id-light.png
```

Hentikan server galeri yang sedang berjalan sebelum `dotnet build`, karena DLL yang terkunci menyebabkan galat MSB3027.

## Menambah demo

1. Tambahkan `DemoInfo(id, category, judul EN/ID, ringkasan EN/ID, modules, code)` ke `GalleryCatalog.Demos` di
   `gallery/Electrical.Gallery.Shared/GalleryCatalog.cs`. `id` menjadi slug URL dan nama tangkapan layar.
2. Tambahkan kelas skenario di `gallery/Electrical.Gallery.Shared/Scenarios/`. Jalur yang biasa adalah deklaratif: implementasikan
   `IInteractiveScenario` (kontrol berupa `ChoiceParam`/`RangeParam`, hasil berupa `Kpi` serta `ChartBlock`, `TableBlock`, `ListingBlock`, dan
   `NoteBlock`) lalu daftarkan di `InteractiveScenarios.All`. Kedua aplikasi menampilkannya dengan tampilan generik (`InteractiveView.razor`,
   `Views.Interactive`) sehingga langkah 3–4 tidak diperlukan; `distribution`, `interop`, `voltagecontrol`, `acopf`, `scuc`, `market`, `dynamics`, `ibr`, dan `emt` dibuat dengan cara ini.
3. Hanya untuk UI khusus: web `Components/Demos/<Nama>View.razor` dan sebuah case di `DemoHost.razor`.
4. Hanya untuk UI khusus: case desktop di `Views.Build`.
5. Jaga agar teks dwibahasa dan data deterministik (ber-seed).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/gallery.md)*
