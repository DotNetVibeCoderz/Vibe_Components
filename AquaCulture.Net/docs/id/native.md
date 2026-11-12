# Kernel native

`native/aqua-native` adalah crate Rust `cdylib` dengan C ABI: `aq_version`, `aq_nh3_fraction_batch`, `aq_do_saturation_batch`, `aq_stats`,
`aq_tgc_monte_carlo` dan `aq_advect_diffuse_step`. PRNG-nya identik bit demi bit dengan `DeterministicRandom`, dan `AquaKernels` mengimplementasikan
setiap kernel juga dalam C#. `AquaKernels.Backend` menunjukkan jalur yang aktif; set `AQUA_NATIVE=0` atau `AquaKernels.ForceManaged = true` untuk memaksa kode managed.

```bash
cd native/aqua-native && cargo test --release
pwsh ../../build/build-native.ps1                                      # platform saat ini
pwsh ../../build/build-native.ps1 -Target aarch64-unknown-linux-gnu -Rid linux-arm64
```

CI membangun win-x64, linux-x64, linux-arm64, osx-arm64 dan osx-x64 lalu mengemasnya ke `AquaCulture.Native`.
