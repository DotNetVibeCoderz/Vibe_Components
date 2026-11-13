# Kernel native (Rust)

`native/medical-native` adalah `cdylib` dengan C ABI (`mdn_*`):

| Fungsi | Kegunaan |
|---|---|
| `mdn_window_level_i16 / _u16` | VOI LUT linear DICOM PS3.3 C.11.2.1.2 dengan rescale modality dan inversi MONOCHROME1 → 8-bit |
| `mdn_stats_i16` | Statistik Welford untuk nilai piksel ter-rescale (HU) |
| `mdn_detect_r_peaks` | Deteksi QRS Pan-Tompkins (turunan, kuadrat, integrasi 150 ms, ambang, refrakter 200 ms) |
| `mdn_version` | `major*10000 + minor*100 + patch` |

`MedicalKernels` memanggil fungsi ini melalui `LibraryImport`, mencari pustaka di folder aplikasi atau `runtimes/<rid>/native`, dan beralih ke implementasi C# dengan aritmetika f64 yang sama bila pustaka tidak dapat dimuat. `MedicalKernels.ActiveBackend` menunjukkan backend yang aktif; atur `PreferredBackend = KernelBackend.Managed` untuk memaksa fallback. Tes `RustAndManagedBackendsAgree` memastikan hasilnya identik.

```bash
cd native/medical-native && cargo test --release
pwsh build/build-native.ps1
pwsh build/build-native.ps1 -Target aarch64-unknown-linux-gnu -Rid linux-arm64
```

CI membangun win-x64, linux-x64, linux-arm64, osx-arm64 dan osx-x64 lalu mengemasnya ke paket `Medical.Native`.
