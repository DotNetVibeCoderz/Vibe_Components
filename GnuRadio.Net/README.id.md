<p align="center"><img src="assets/icon.png" width="96" alt="Logo GnuRadio.Net" /></p>

<h1 align="center">GnuRadio.Net</h1>

<p align="center">
Software Defined Radio dan DSP untuk .NET 10, mengikuti model <a href="https://github.com/gnuradio/gnuradio">GNU Radio</a> —
flowgraph, filter, FFT, modulasi analog dan digital, FEC, sinkronisasi, model kanal, jaringan, perangkat keras SDR,
serta komponen instrumen langsung untuk <b>Blazor</b> dan <b>Avalonia</b>.
</p>

<p align="center"><b>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil</b></p>

<p align="center"><a href="README.md">English</a> · <a href="docs/id/index.md">Dokumentasi</a> · <a href="docs/en/index.md">Documentation</a></p>

![Galeri GnuRadio.Net — pemindaian pita FM secara langsung](docs/images/web-home.png)

## Apa itu GnuRadio.Net

GnuRadio.Net membawa model pemrograman GNU Radio ke C#: Anda menghubungkan **blok** menjadi sebuah **flowgraph**, dan
scheduler thread-per-blok mengalirkan sampel di antara blok-blok tersebut melalui ring buffer tanpa lock. Nama blok,
parameter, dan perilakunya mengikuti GNU Radio (`fir_filter_ccf`, `quadrature_demod_cf`, `costas_loop_cc`, `channel_model`…),
sehingga pengetahuan dan flowgraph GNU Radio dapat langsung dipakai, dan keduanya dapat bekerja bersama:

- **Jalankan berkas `.grc`** dari GNU Radio Companion, atau konversikan ke C# (`GnuRadio.Net.Grc`).
- **Bertukar sampel dan PDU** dengan GNU Radio melalui ZeroMQ, kompatibel hingga level byte dengan `gr-zeromq` (`GnuRadio.Net.ZeroMQ`).
- **Baca dan tulis** berkas sampel mentah GNU Radio (`.cfile`) secara langsung.

Ini adalah implementasi .NET native — tanpa Python, tanpa instalasi GNU Radio — sehingga dapat berjalan di mana pun .NET 10
berjalan, termasuk di dalam web server dan aplikasi desktop. Pustaka Rust opsional mempercepat pemfilteran FIR dan FFT.

```csharp
using GnuRadio;
using GnuRadio.Analog;
using GnuRadio.Filter;
using GnuRadio.Hardware;
using GnuRadio.Sinks;

var sdr = new SimulatedSdrDevice { CenterFrequency = 99.5e6, SampleRate = 2.4e6 };   // or Sdr.RtlSdr(), Sdr.HackRf()…
var channel = new FreqXlatingFirFilter(10, Firdes.LowPass(1, 2.4e6, 100e3, 50e3), 99.9e6 - 99.5e6, 2.4e6);
var audio = new TimeSink<float>(1024, 48e3);

var fg = new Flowgraph("wbfm");
fg.Connect(new SdrSource(sdr), channel, new WbfmReceive(240e3, 5), audio);
fg.Start();
```

## Paket

| Paket | Isi |
|---|---|
| `GnuRadio.Net` | Runtime (flowgraph, scheduler, buffer, tag, pesan/PMT, blok hierarkis), blok, filter (FIR/IIR/resampler/firdes), FFT & estimasi spektral, analog (AM/FM/SSB/PM/CW, PLL, AGC), digital (PSK/QAM/FSK/GMSK/OFDM, paket, scrambler), FEC (CRC, Reed–Solomon, Viterbi, LDPC, Polar), sinkronisasi (Costas, Gardner, M&M, korelator), kanal (AWGN, fading, Doppler, multipath), TCP/UDP, sink visual, ekstensi (LoRa CSS, radar FMCW, AX.25/HDLC, detektor sinyal, pengklasifikasi modulasi), kernel SIMD (mirip VOLK) |
| `GnuRadio.Net.Blazor` | `<SpectrumView>`, `<WaterfallView>`, `<ConstellationView>`, `<TimeScope>`, `<VectorView>`, `<LineChart>`, `<Knob>`, `<FrequencyReadout>`, `<LevelMeter>`, `<FlowgraphView>` |
| `GnuRadio.Net.Avalonia` | Tampilan dan kontrol yang sama untuk desktop |
| `GnuRadio.Net.Hardware` | Binding SoapySDR (USRP, PlutoSDR, LimeSDR, HackRF, bladeRF, RTL-SDR, Airspy), klien `rtl_tcp` terkelola (managed), SDR simulasi |
| `GnuRadio.Net.ZeroMQ` | Blok stream PUB/SUB dan PUSH/PULL, blok pesan PMT — kompatibel di level protokol dengan `gr-zeromq` |
| `GnuRadio.Net.Grc` | Memuat dan menjalankan berkas `.grc` GNU Radio Companion; menghasilkan kode C# yang setara |

```bash
dotnet add package GnuRadio.Net
dotnet add package GnuRadio.Net.Blazor     # or GnuRadio.Net.Avalonia
```

## Galeri

Tujuh belas contoh kasus penggunaan secara langsung, masing-masing dengan kontrol, tampilan instrumen, pembacaan nilai,
dan contoh kode yang dapat disalin, tersedia dalam aplikasi web Blazor maupun aplikasi desktop Avalonia, dalam bahasa
Inggris dan Bahasa Indonesia.

```bash
dotnet run --project samples/GnuRadio.Net.Gallery.Web       # http://localhost:5180
dotnet run --project samples/GnuRadio.Net.Gallery.Desktop
```

| | |
|---|---|
| ![Penerima FM](docs/images/web-fm-receiver.png) | ![Modem QAM](docs/images/web-psk-qam.png) |
| ![OFDM](docs/images/web-ofdm.png) | ![Laboratorium BER FEC](docs/images/web-ber-lab.png) |
| ![LoRa](docs/images/web-lora.png) | ![Telemetri AX.25](docs/images/web-satellite.png) |
| ![Galeri desktop](docs/images/desktop-band-scanner.png) | ![QAM desktop](docs/images/desktop-psk-qam.png) |

Demo: pemindai pita FM · penerima siaran FM · AM & SSB · CW/Morse · modem PSK & QAM · FSK/GFSK/GMSK · tautan OFDM ·
pemulihan pembawa & timing · kanal fading · laboratorium BER FEC · perancang FIR · chirp LoRa · radar FMCW ·
telemetri AX.25 CubeSat · machine learning untuk RF · jembatan ZeroMQ · menjalankan berkas `.grc`.

## Build, uji, benchmark

```bash
dotnet build GnuRadio.Net.slnx
dotnet test tests/GnuRadio.Net.Tests                       # 130+ tests
dotnet run -c Release --project tests/GnuRadio.Net.Benchmarks -- --filter *
pwsh native/build.ps1                                      # optional Rust kernels (needs cargo)
```

## Dokumentasi

Bahasa Indonesia: [indeks](docs/id/index.md) · [memulai](docs/id/getting-started.md) · [arsitektur](docs/id/architecture.md) ·
[blok](docs/id/blocks.md) · [komponen UI](docs/id/ui-components.md) · [perangkat keras](docs/id/hardware.md) ·
[interoperabilitas dengan GNU Radio](docs/id/interop.md) · [performa](docs/id/performance.md) · [galeri](docs/id/gallery.md)

Peta jalan: [PLAN.md](PLAN.md) · kemajuan: [Progress.md](Progress.md)

## Hubungan dengan GNU Radio

GnuRadio.Net adalah implementasi .NET independen yang terinspirasi oleh arsitektur dan pustaka blok GNU Radio; proyek ini
tidak mengandung maupun me-link kode GNU Radio. GNU Radio adalah proyek dari GNU Radio Foundation. GnuRadio.Net
berlisensi MIT.

---

**Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.** © 2026 Gravicode Studios — [Lisensi MIT](LICENSE).
