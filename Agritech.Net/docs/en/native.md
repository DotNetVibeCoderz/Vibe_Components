# Native kernels (Rust)

`native/agritech-native` is a Rust `cdylib` with a small C ABI. `Agritech.Native` binds to it through source-generated `LibraryImport`,
which is AOT-friendly, and falls back to a managed `Vector<T>` SIMD implementation with identical semantics when the library can't be loaded.

| Export | Purpose |
|---|---|
| `agt_version()` | ABI version (10000 = 1.0.0) |
| `agt_normalized_difference(a, b, out, len)` | NDVI / NDWI / NDRE / NBR … (NaN when a + b = 0) |
| `agt_savi(nir, red, out, len, L)` | SAVI |
| `agt_evi(nir, red, blue, out, len, G, C1, C2, L)` | EVI |
| `agt_stats(values, len, *AgtStats)` | NaN-aware count/min/max/mean/M2 (Welford + Chan parallel merge) |
| `agt_et0_pm_batch(days, len, lat, elev, out)` | Batch FAO-56 Penman–Monteith |

Inputs of 65,536 elements or more are split across threads with `std::thread::scope`. No external crates are needed, and the release profile uses fat LTO.

```csharp
AgriKernels.ActiveBackend            // Rust or Managed
AgriKernels.PreferredBackend = KernelBackend.Managed; // force managed path (benchmarks)
AgriKernels.NormalizedDifference(nir, red, ndvi);
```

On the gallery machine NDVI runs at roughly 560–780 megapixels/s with the Rust backend. The NDVI page shows the live figure.

## Building

```powershell
./build/build-native.ps1                                   # host platform → src/Agritech.Native/runtimes/<rid>/native
./build/build-native.ps1 -Target x86_64-unknown-linux-gnu -Rid linux-x64
./build/build-native.ps1 -Target aarch64-apple-darwin -Rid osx-arm64
```

The package ships whatever is in `runtimes/**`. The resolver searches the app folder and `runtimes/<rid>/native`.
Rust unit tests (including FAO-56 Example 18) run with `cargo test --release`.
