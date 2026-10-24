# Getting started

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/getting-started.md)

## Requirements

- .NET 10 SDK
- Optional: Rust (`cargo`) to build the native kernels, SoapySDR for hardware

## Install

```bash
dotnet add package GnuRadio.Net
dotnet add package GnuRadio.Net.Blazor      # web components
dotnet add package GnuRadio.Net.Avalonia    # desktop controls
dotnet add package GnuRadio.Net.Hardware    # SDR devices
dotnet add package GnuRadio.Net.ZeroMQ      # gr-zeromq compatible networking
dotnet add package GnuRadio.Net.Grc         # GNU Radio Companion files
```

## Your first flowgraph

A tone, a low-pass filter, and a sink that collects the result:

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

`Connect(a, b, c)` chains port 0 to port 0. For other ports use `Connect(source, sourcePort, destination, destinationPort)`.
Item types are checked when you connect: a `float` output cannot feed a `Complex32` input.

## Running continuously

Sources such as `SignalSource` never end. Use `Throttle` to pace a simulation at its sample rate, `Start()` to run in
the background, and `Stop()` to end:

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

`RunSynchronously()` runs every block on the calling thread, deterministically — handy in unit tests.

## Showing it in a UI

Blazor (interactive server):

```razor
@using GnuRadio.Blazor.Components
<SpectrumView Sink="spectrum" MinDb="-100" MaxDb="0" Title="Spectrum" Height="260px" />
```

Add the stylesheet to your host page: `<link rel="stylesheet" href="_content/GnuRadio.Net.Blazor/gnuradio.css" />`.

Avalonia:

```csharp
var view = new GnuRadio.Avalonia.SpectrumView { Sink = spectrum, MinDb = -100, MaxDb = 0, Title = "Spectrum", Height = 260 };
```

## The gallery

```bash
dotnet run --project samples/GnuRadio.Net.Gallery.Web        # http://localhost:5180
dotnet run --project samples/GnuRadio.Net.Gallery.Desktop
dotnet run --project samples/QuickStart                      # console QPSK link
```

Next: [architecture](architecture.md).
