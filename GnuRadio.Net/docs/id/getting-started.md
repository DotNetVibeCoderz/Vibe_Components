# Memulai

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/getting-started.md)

## Persyaratan

- .NET 10 SDK
- Opsional: Rust (`cargo`) untuk mem-build kernel native, SoapySDR untuk perangkat keras

## Instalasi

```bash
dotnet add package GnuRadio.Net
dotnet add package GnuRadio.Net.Blazor      # web components
dotnet add package GnuRadio.Net.Avalonia    # desktop controls
dotnet add package GnuRadio.Net.Hardware    # SDR devices
dotnet add package GnuRadio.Net.ZeroMQ      # gr-zeromq compatible networking
dotnet add package GnuRadio.Net.Grc         # GNU Radio Companion files
```

## Flowgraph pertama Anda

Sebuah nada, sebuah filter low-pass, dan sebuah sink yang mengumpulkan hasilnya:

```csharp
using GnuRadio;
using GnuRadio.Blocks;
using GnuRadio.Filter;

var fg = new Flowgraph("first");
var tone = new SignalSource<float>(sampleRate: 48_000, Waveform.Sine, frequency: 1_000, amplitude: 0.5f);
var head = new Head<float>(48_000);                              // stop after one second
var lowPass = new FirFilterFff(1, Firdes.LowPass(1, 48_000, 3_000, 500));
var sink = new VectorSink<float>();

fg.Connect(tone, head, lowPass, sink);   // port 0 → port 0 along the chain
fg.Run();                                // start, wait until the source is exhausted

Console.WriteLine($"{sink.Count} samples");
```

`Connect(a, b, c)` merangkai port 0 ke port 0. Untuk port lain gunakan `Connect(source, sourcePort, destination, destinationPort)`.
Tipe item diperiksa saat Anda menghubungkan: keluaran `float` tidak dapat menyuplai masukan `Complex32`.

## Berjalan terus-menerus

Source seperti `SignalSource` tidak pernah berakhir. Gunakan `Throttle` untuk mengatur kecepatan simulasi sesuai laju
sampelnya, `Start()` untuk menjalankannya di latar belakang, dan `Stop()` untuk menghentikannya:

```csharp
var fg = new Flowgraph();
var src = new SignalSource<Complex32>(1e6, Waveform.Cosine, 100e3);
var spectrum = new GnuRadio.Sinks.FrequencySink(2048, 1e6);
fg.Connect(src, new Throttle<Complex32>(1e6), spectrum);
fg.Start();
// … read spectrum.TryCopy(...) or bind it to <SpectrumView Sink="spectrum" />
src.Frequency = 200e3;   // most parameters can change while running
fg.Stop();
```

`RunSynchronously()` menjalankan setiap blok pada thread pemanggil secara deterministik — praktis untuk unit test.

## Menampilkannya di UI

Blazor (interactive server):

```razor
@using GnuRadio.Blazor.Components
<SpectrumView Sink="spectrum" MinDb="-100" MaxDb="0" Title="Spectrum" Height="260px" />
```

Tambahkan stylesheet ke halaman host Anda: `<link rel="stylesheet" href="_content/GnuRadio.Net.Blazor/gnuradio.css" />`.

Avalonia:

```csharp
var view = new GnuRadio.Avalonia.SpectrumView { Sink = spectrum, MinDb = -100, MaxDb = 0, Title = "Spectrum", Height = 260 };
```

## Galeri

```bash
dotnet run --project samples/GnuRadio.Net.Gallery.Web        # http://localhost:5180
dotnet run --project samples/GnuRadio.Net.Gallery.Desktop
dotnet run --project samples/QuickStart                      # console QPSK link
```

Selanjutnya: [arsitektur](architecture.md).
