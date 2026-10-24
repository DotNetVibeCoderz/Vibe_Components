# Performance

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/performance.md)

## Design choices

- **No allocation on the sample path.** Work functions receive spans into the ring buffers; blocks reuse their scratch
  arrays; UI sinks copy into caller-owned arrays only when a frame changed.
- **Lock-free buffers.** Single writer, multiple readers, volatile publication, mirrored region for contiguous windows.
- **SIMD kernels.** `GnuRadio.Kernels.Volk` uses `Vector128`/`Vector256` (SSE/AVX2 on x64, NEON on ARM64) with
  fused multiply-add: FIR dot products, complex multiply, magnitude, scaling.
- **Thread per block.** Blocks run in parallel on their own threads and sleep when idle.
- **Decimation where it is cheap.** Visual sinks and `LogPowerFft` skip samples between frames, so their cost follows the
  frame rate, not the sample rate; `FreqXlatingFirFilter` computes only the outputs that survive decimation.

## Optional Rust kernels

`native/gnuradio-native` is a small Rust library (C ABI) with FIR kernels using AVX2/FMA intrinsics selected at
runtime and FFTs from `rustfft`. Build it with `pwsh native/build.ps1` (or `native/build.sh`); it is copied to
`runtimes/<rid>/native` and packed into the NuGet package. Enable it with:

```csharp
GnuRadio.Kernels.Volk.UseNative = true;   // falls back to managed code when the library is absent
```

Only the kernels that measured faster are routed: FIR filtering and FFTs.

## Measured

BenchmarkDotNet (`ShortRun`), .NET 10, x64 with AVX2, on the development machine:

| Kernel | Managed SIMD | Native (Rust) |
|---|---:|---:|
| FIR, complex × real taps, 65 536 samples, 165 taps | 1.83 ms | **1.29 ms** |
| FFT, 4096 points | 77–103 µs | **7.9 µs** |
| Complex multiply, 65 536 samples | **59 µs** | 68 µs |

No benchmark allocates. Run them yourself:

```bash
dotnet run -c Release --project tests/GnuRadio.Net.Benchmarks -- --filter *
```

## Tuning

- `Flowgraph.BufferBytes`: smaller buffers lower latency (important for sources that fill with idle data, such as
  framers); larger buffers reduce scheduling overhead.
- `Block.MaxOutputItems`: caps the items per work call for a block (GNU Radio's `set_max_noutput_items`).
- `VisualSink.UpdateRate`: frames per second published by a sink.
- `Throttle` only for simulations — hardware sources pace themselves.
