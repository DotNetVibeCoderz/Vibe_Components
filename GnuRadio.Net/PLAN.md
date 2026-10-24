# GnuRadio.Net — Roadmap / Peta jalan

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*

Status per area of [features.md](features.md). ✅ done · 🟡 partial · ⏳ planned.
Detailed day-to-day status lives in [Progress.md](Progress.md).

## v0.1 — foundation (this release)

| Feature area | Status | Notes |
|---|---|---|
| 1. Flowgraph architecture | ✅ | Blocks, typed stream ports, vectors, message ports, tags, hierarchical blocks, thread-per-block and single-threaded schedulers, validation, reconfigure |
| 2. GNU Radio Companion | 🟡 | `.grc` import with Python expression evaluation, run, C# code generation, flowgraph diagrams, parameter editors/runtime tuning in the gallery. **No drag-and-drop editor yet** |
| 3. Sources & sinks | ✅ | Signal/noise/file/vector sources; audio (Windows), file, WAV, TCP/UDP sinks; time, FFT, waterfall, constellation sinks |
| 4. DSP | ✅ | FIR/IIR, firdes, windows, rational and arbitrary resampling, correlation, matched filtering, moving average, Hilbert |
| 5. FFT & spectrum | ✅ | FFT/IFFT (radix-2 + Bluestein), windows, Welch PSD, spectrogram, waterfall |
| 6. Analog | ✅ | AM, FM (WBFM/NBFM rx, FM tx), SSB, CW (keyer + decoder), PM |
| 7. Digital | ✅ | BPSK/QPSK/M-PSK, QAM, FSK/GFSK/GMSK, OFDM |
| 8. Error correction | ✅ | Viterbi, LDPC, Reed–Solomon, Polar (SC), CRC |
| 9. Synchronisation | ✅ | Gardner and M&M timing, Costas / decision-directed carrier, PLL, frame sync (access code, correlation estimator, Schmidl & Cox) |
| 10. Channel models | ✅ | AWGN, multipath, Doppler, Rayleigh/Rician, frequency-selective fading |
| 11. Networking | ✅ | TCP, UDP, ZeroMQ (gr-zeromq compatible), distributed flowgraphs |
| 12. SDR hardware | 🟡 | SoapySDR bindings for USRP/Pluto/Lime/HackRF/bladeRF/RTL-SDR/Airspy, managed rtl_tcp, simulated SDR. **Not yet validated on physical radios** |
| 13. RFNoC | ⏳ | Needs UHD C++ API bindings |
| 14. Python & C++ development | 🟡 | .NET equivalent: custom blocks in C#, OOT modules via `BlockRegistry`; native kernels in Rust. No Python bridge |
| 15. Performance | ✅ | VOLK-style SIMD kernels, optional Rust FIR/FFT, multi-threaded scheduler, allocation-free work paths |
| 16. Visualisation | ✅ | Blazor and Avalonia spectrum, waterfall, constellation, scope, charts, controls |
| 17. Application areas | 🟡 | Gallery covers SDR, wireless, satellite, radar, IoT/LoRa, spectrum monitoring, ML for RF. **LTE/5G not covered** |
| 18. Extensions | 🟡 | gr-lora (CSS PHY), gr-radar (FMCW), gr-satellites (AX.25/HDLC), gr-inspector (detector). **gr-gsm, gr-ieee802-11 not started** |

## v0.2 — hardware and editor

- Validate SoapySDR on RTL-SDR, HackRF, PlutoSDR and USRP B2xx; publish a compatibility table
- Drag-and-drop flowgraph editor (Blazor first, then Avalonia) on top of `BlockRegistry` and the GRC mappings; save as `.grc`
- Audio output backends for Linux (ALSA/PulseAudio) and macOS (CoreAudio)
- Stream tags over ZeroMQ (`pass_tags=True` format)
- Blazor WebAssembly hosting of flowgraphs (single-threaded scheduler)

## v0.3 — protocols and decoders

- gr-ieee802-11-style 802.11a/g receiver on top of the OFDM blocks
- LoRa: whitening, Hamming coding, interleaving and header for LoRaWAN-compatible frames
- gr-gsm-style GSM broadcast channel decoding
- SCL (list) decoding for Polar codes, CRC-aided
- More gr-satellites decoders (CCSDS framing, AO-73 style telemetry)

## v0.4 — scale

- RFNoC via UHD bindings
- GPU (CUDA/OpenCL) FFT and FIR offload
- Integration with Gravicode supporting libraries: GravicodeScience (NumPy/SciPy style analysis), RLNet (cognitive radio with reinforcement learning), HF.Net (Hugging Face models for RF classification), Visual.Net/ThreeNet (3-D waterfalls), IoTComNet (IoT gateways)

## Release & packaging

- NuGet packages: GnuRadio.Net, .Blazor, .Avalonia, .Hardware, .ZeroMQ, .Grc (icon: `assets/icon.png`)
- CI (`.github/workflows/gnuradio-net.yml` in Vibe_Components) builds the native kernels for win-x64, linux-x64 and
  osx-arm64, tests and packs; pushing a tag `gnuradio-net-vX.Y.Z` publishes to NuGet with the `NUGET_API_KEY` secret.
  `scripts/pack.ps1` packs locally
