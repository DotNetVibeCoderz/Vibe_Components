# Native kernels (Rust)

`native/medical-native` is a `cdylib` with a C ABI (`mdn_*`):

| Function | Purpose |
|---|---|
| `mdn_window_level_i16 / _u16` | DICOM PS3.3 C.11.2.1.2 linear VOI LUT with modality rescale and MONOCHROME1 inversion → 8-bit |
| `mdn_stats_i16` | Welford statistics of rescaled pixels (HU) |
| `mdn_detect_r_peaks` | Pan-Tompkins QRS detection (derivative, squaring, 150 ms integration, threshold, 200 ms refractory) |
| `mdn_version` | `major*10000 + minor*100 + patch` |

`Medical.Native.MedicalKernels` P/Invokes these through `LibraryImport`, finds the library in the app folder or `runtimes/<rid>/native`, and falls back to a C# implementation with the same f64 arithmetic when it cannot load. `MedicalKernels.ActiveBackend` reports which one runs; set `PreferredBackend = KernelBackend.Managed` to force the fallback. `NativeKernelTests.RustAndManagedBackendsAgree` asserts identical results.

```bash
cd native/medical-native && cargo test --release
pwsh build/build-native.ps1                                   # host platform
pwsh build/build-native.ps1 -Target aarch64-unknown-linux-gnu -Rid linux-arm64
```

CI builds win-x64, linux-x64, linux-arm64, osx-arm64 and osx-x64 and packs them into the `Medical.Native` package.
