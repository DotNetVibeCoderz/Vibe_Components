# Kernel native (Rust)

`native/agritech-native` adalah pustaka Rust (`cdylib`) dengan ABI C sederhana. Paket `Agritech.Native` memanggilnya melalui `LibraryImport`
(ramah AOT) dan otomatis beralih ke implementasi SIMD terkelola (`Vector<T>`) dengan hasil identik bila pustaka native tidak tersedia.

Fungsi yang diekspor: `agt_normalized_difference` (NDVI, NDWI, NDRE, NBR), `agt_savi`, `agt_evi`, `agt_stats` (statistik tahan NaN, Welford paralel),
dan `agt_et0_pm_batch` (Penman–Monteith FAO-56). Data berukuran 65.536 elemen atau lebih diproses multithread.

```csharp
AgriKernels.ActiveBackend;   // Rust atau Managed
AgriKernels.NormalizedDifference(nir, red, ndvi);
```

Kecepatan NDVI di mesin pengembangan sekitar 560–780 megapiksel/detik.

## Membangun

```powershell
./build/build-native.ps1                                   # platform saat ini
./build/build-native.ps1 -Target x86_64-unknown-linux-gnu -Rid linux-x64
```
