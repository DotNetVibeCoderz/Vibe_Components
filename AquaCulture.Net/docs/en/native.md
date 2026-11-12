# Native kernels

`native/aqua-native` is a Rust `cdylib` exposing a C ABI:

| Function | Purpose |
|---|---|
| `aq_version` | ABI version (10000 = 1.0.0) |
| `aq_nh3_fraction_batch` | un-ionised ammonia fraction for arrays of T, pH, salinity |
| `aq_do_saturation_batch` | oxygen solubility at a barometric pressure |
| `aq_stats` | NaN-skipping count/min/max/mean/M2 of a float field |
| `aq_tgc_monte_carlo` | final weights of N thermal-unit growth simulations with daily temperature noise |
| `aq_advect_diffuse_step` | one explicit upwind advection–diffusion–reaction step on a masked 2-D grid |

The PRNG (xoshiro256** + Marsaglia polar) is bit-identical to `DeterministicRandom`, and `AquaKernels` implements every kernel in C# too.
`AquaKernels.Backend` reports which path runs; set `AQUA_NATIVE=0` or `AquaKernels.ForceManaged = true` to force managed code. Tests compare both.

## Build

```bash
cd native/aqua-native
cargo test --release
pwsh ../../build/build-native.ps1                        # host platform → src/AquaCulture.Native/runtimes/<rid>/native
pwsh ../../build/build-native.ps1 -Target aarch64-unknown-linux-gnu -Rid linux-arm64
```

CI builds win-x64, linux-x64, linux-arm64, osx-arm64 and osx-x64 and packs them into `AquaCulture.Native`.
