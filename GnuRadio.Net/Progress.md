# GnuRadio.Net — Progress / Kemajuan

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*

Development log and current state. The roadmap is in [PLAN.md](PLAN.md).

## 2026-10-24 — v0.1.0 foundation

### Done

**Core (`GnuRadio.Net`, ~17k lines with the UI libraries)**
- Runtime: `Block` and simple bases, typed/vector ports, `StreamBuffer<T>` (lock-free single-writer/multi-reader ring with
  a mirrored region for contiguous windows), thread-per-block and single-threaded schedulers, stream tags with
  rate-scaled propagation, PMT messages + GNU Radio-compatible `PmtSerializer`, hierarchical blocks, validation,
  `Reconfigure`, Graphviz export, `BlockRegistry` for out-of-tree modules
- Blocks: sources, sinks, stream control, math, type conversions, bit packing, PDUs, file/WAV I/O, Windows audio sink
- Filters: firdes (GNU Radio tap-count rules), windows, FIR fff/ccf/ccc, frequency-translating FIR, interpolating FIR,
  rational and arbitrary resamplers, IIR/biquads/Butterworth, single-pole IIR, moving average, DC blocker, Hilbert
- FFT: radix-2 + Bluestein plans, FFT block, log-power FFT, Welch PSD, spectrogram
- Analog: FM mod/demod, WBFM/NBFM receivers, FM transmitter with pre-emphasis, AM, SSB (phasing), PM, AGC, PLLs,
  Morse keyer and adaptive decoder
- Digital: Gray-coded constellations with soft decisions, modulator/receiver chains, FSK/GFSK/GMSK, OFDM
  (Schmidl & Cox, LS equalisation, pilots), packet framing/deframing, scramblers, differential coding
- FEC: CRC family, Reed–Solomon, convolutional + Viterbi, LDPC (min-sum, alist), Polar (SC), BER tools
- Synchronisation: Costas (2/4/8), decision-directed carrier, Gardner symbol sync, Mueller & Müller, access-code and
  correlation-estimator frame sync
- Channels: AWGN, frequency offset, Doppler, Rayleigh/Rician, frequency-selective fading, channel model
- Networking: TCP/UDP sources and sinks
- Visual sinks: time, frequency, waterfall, constellation, vector, number; colormaps and axis helpers
- Extensions: LoRa CSS, FMCW radar + CFAR + range-Doppler, AX.25/HDLC/NRZI, signal detector, modulation classifier
- Kernels: VOLK-style SIMD (Vector128/256, FMA)

**Packages**
- `GnuRadio.Net.ZeroMQ` (NetMQ): PUB/SUB, PUSH/PULL, PMT message blocks
- `GnuRadio.Net.Hardware`: SoapySDR P/Invoke, rtl_tcp client, simulated SDR, `SdrSource`/`SdrSink`
- `GnuRadio.Net.Grc`: `.grc` parser, Python expression evaluator, 86 block mappings, importer, C# code generator
- `GnuRadio.Net.Blazor`: 10 components, canvas renderer, themeable CSS (light/dark)
- `GnuRadio.Net.Avalonia`: the same displays and controls for desktop

**Native**: Rust `gnuradio-native` (AVX2/FMA FIR, rustfft FFT), build scripts, runtime probing and fallback

**Gallery**: 17 bilingual demos shared by a Blazor web app and an Avalonia desktop app; screenshots in `docs/images`

**Quality**: 130 xUnit tests (runtime, DSP, FEC, modems, sync, OFDM, channels, extensions, networking, ZeroMQ, GRC,
PMT wire format, native kernels, every gallery demo); BenchmarkDotNet suite

**Docs**: README (EN/ID), `docs/en` and `docs/id` guides, PLAN.md, this file, CLAUDE.md

### Measured

| | Managed | Native |
|---|---:|---:|
| FIR ccf 65 536 × 165 taps | 1.83 ms | 1.29 ms |
| FFT 4096 | 77–103 µs | 7.9 µs |

### Bugs found and fixed during this phase
- Polar code frozen set used the wrong bit order of the Bhattacharyya recursion (BER 0.23 → 4e-4 at 4 dB)
- 8PSK must be rotated by π/8 to match the 8th-order Costas detector, as in GNU Radio
- OFDM receiver lost timing when samples trickled in (discarded half the Schmidl & Cox plateau); regression test added
- LoRa demodulator now picks the best decimation phase during the preamble (fractional-chip timing)
- Framers that emit idle data need small buffers or beacons queue behind seconds of flags (documented, gallery fixed)
- Rust FIR first ran 5.8× slower than managed (strided access, `mul_add` without FMA); rewritten with intrinsics

### Known limitations
- SoapySDR bindings are not yet validated on physical radios
- No drag-and-drop flowgraph editor; RFNoC, gr-gsm, 802.11 and LTE/5G are not implemented
- Audio output is Windows-only (pluggable backend for others)
- LoRa is the CSS PHY only (not LoRaWAN-compatible)
- ZeroMQ stream tags (`pass_tags=True`) are not supported yet
- Blazor components are tested with Interactive Server; WebAssembly hosting is untested

### Repository
- Moved into the `Vibe_Components` monorepo (`GnuRadio.Net/`) with a GitHub Actions workflow for native builds,
  tests, packing and tag-driven NuGet publishing

### Next
See [PLAN.md](PLAN.md) v0.2.
