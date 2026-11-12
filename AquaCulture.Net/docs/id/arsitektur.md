# Arsitektur

```text
                         ┌────────────── AquaCulture.Core ──────────────┐
                         │ spesies · tambak/kolam/siklus · statistik · DI│
                         └──────────────────────────────────────────────┘
   Native (Rust) ─► WaterQuality ─► Growth ─► Feeding ─► Analytics ─► Reporting
         │              │   └► Disease                        └─ Visualization
         └► Hydrodynamic ◄── Environment
                 └► Forecasting ─► AI (model, RL, ML.NET/ONNX, AquaAgent)
   IoT, GIS, StockAssessment, Population, ComputerVision ─► DigitalTwin ─► komponen Blazor (Three.js 3D)
```

- **Core** hanya berisi basis data spesies dan model tambak; semua modul lain bergantung padanya.
- **Native** memanggil kernel Rust lewat `LibraryImport`; `AquaKernels` punya fallback managed dengan rumus yang sama (diuji kesetaraannya).
- Rantai produksi: **WaterQuality → Growth → Feeding → Analytics**.
- **Hydrodynamic** menggabungkan cuaca dan kimia air untuk neraca oksigen kolam dan model transport 2-D.
- **DigitalTwin** menyatukan data IoT dengan semua modul di atas dan menerbitkan `TwinDecision` beserta perintah aktuator.
- Komputasi galeri ada di `gallery/AquaCulture.Gallery.Shared`; aplikasi Blazor dan Avalonia hanya menampilkan hasil.

## Satuan

°C, mg/L (= g/m³), salinitas ppt, alkalinitas mg/L CaCO₃, TAN dan nitrit sebagai N, gram untuk bobot individu, kg untuk biomassa dan pakan,
m² / m³ untuk kolam, hari untuk masa budidaya, Rp/kg untuk harga.

## Determinisme

Semua data sintetis memakai `DeterministicRandom` (xoshiro256**), identik antar-proses dan antara kernel Rust dan kode managed.
Jangan memakai `HashCode` sebagai seed karena diacak per proses.

## Performa

Statistik SIMD (`Vector<T>`), span di jalur panas, kernel Rust untuk transport 2-D dan Monte-Carlo (grid 80 × 64 selama tiga jam simulasi ≈ 1 detik),
penulis laporan tanpa refleksi dan tanpa dependensi pihak ketiga. Pustaka kompatibel AOT kecuali AI, ComputerVision, GIS dan Blazor.
