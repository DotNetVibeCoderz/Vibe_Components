# SDR hardware

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/hardware.md)

`GnuRadio.Net.Hardware` wraps radios behind `ISdrDevice` (sample rate, centre frequency, bandwidth, gain, AGC, read and
write). `SdrSource` and `SdrSink` put a device in a flowgraph; their `cmd` message port accepts a dictionary with
`freq`, `gain`, `rate`, `bw`, `agc`, like GNU Radio's Soapy blocks.

## SoapySDR (USRP, PlutoSDR, LimeSDR, HackRF, bladeRF, RTL-SDR, Airspy)

`SoapySdrDevice` binds the SoapySDR C API, so any radio with a SoapySDR module works. Install the runtime:

- **Windows**: PothosSDR or radioconda (puts `SoapySDR.dll` and modules on `PATH`)
- **Linux**: `sudo apt install libsoapysdr0.8 soapysdr-module-all` (or the module for your radio)
- **macOS**: `brew install soapysdr` plus the module

```csharp
using GnuRadio.Hardware;

foreach (var d in SoapySdrDevice.Enumerate()) Console.WriteLine(string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value}")));

using var hackrf = Sdr.HackRf();            // "driver=hackrf"
hackrf.SampleRate = 8e6;
hackrf.CenterFrequency = 433.92e6;
hackrf.Gain = 30;
fg.Connect(new SdrSource(hackrf), spectrum);
```

Shortcuts: `Sdr.Usrp()`, `Sdr.PlutoSdr("hostname=192.168.2.1")`, `Sdr.LimeSdr()`, `Sdr.HackRf()`, `Sdr.BladeRf()`,
`Sdr.RtlSdr()`, `Sdr.Airspy()`. Streams use CF32, so samples arrive as `Complex32` without conversion.

> Status: the bindings follow the SoapySDR 0.8 C API but have not yet been exercised against physical radios by the
> project's CI. Please report results with your hardware.

## rtl_tcp

`RtlTcpDevice` talks to any `rtl_tcp` server (an RTL-SDR shared over the network) in pure managed code:

```csharp
var dongle = new RtlTcpDevice("192.168.1.20", 1234) { SampleRate = 2.048e6, CenterFrequency = 100e6 };
dongle.SetFrequencyCorrection(ppm: 1);
fg.Connect(new SdrSource(dongle), spectrum);
```

## Simulated SDR

`SimulatedSdrDevice` synthesises an RF scene around its tuned frequency — by default an FM broadcast band with
stations, a data carrier, telemetry bursts and a spur — and paces itself to real time. Use it to develop without
hardware; the gallery uses it for the band scanner and FM receiver.

```csharp
var sim = new SimulatedSdrDevice(
[
    new Emitter(433.92e6, -40, EmitterKind.Burst, 25e3, "Key fob"),
    new Emitter(434.10e6, -55, EmitterKind.NarrowFm, 12e3, "Voice"),
]) { CenterFrequency = 434e6, SampleRate = 1e6 };
```

## RFNoC

RFNoC (FPGA processing on USRPs) needs UHD's C++ API and is on the [roadmap](../../PLAN.md); today a USRP is used as a
plain sample source/sink through SoapyUHD.
