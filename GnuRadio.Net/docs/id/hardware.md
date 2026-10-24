# Perangkat keras SDR

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/hardware.md)

`GnuRadio.Net.Hardware` membungkus radio di balik `ISdrDevice` (laju sampel, frekuensi tengah, bandwidth, penguatan,
AGC, baca dan tulis). `SdrSource` dan `SdrSink` menempatkan sebuah perangkat ke dalam flowgraph; port pesan `cmd`
miliknya menerima dictionary dengan `freq`, `gain`, `rate`, `bw`, `agc`, seperti blok Soapy di GNU Radio.

## SoapySDR (USRP, PlutoSDR, LimeSDR, HackRF, bladeRF, RTL-SDR, Airspy)

`SoapySdrDevice` melakukan binding ke C API SoapySDR, sehingga radio apa pun yang memiliki modul SoapySDR dapat
digunakan. Instal runtime-nya:

- **Windows**: PothosSDR atau radioconda (menempatkan `SoapySDR.dll` dan modul-modulnya di `PATH`)
- **Linux**: `sudo apt install libsoapysdr0.8 soapysdr-module-all` (atau modul untuk radio Anda)
- **macOS**: `brew install soapysdr` beserta modulnya

```csharp
using GnuRadio.Hardware;

foreach (var d in SoapySdrDevice.Enumerate()) Console.WriteLine(string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value}")));

using var hackrf = Sdr.HackRf();            // "driver=hackrf"
hackrf.SampleRate = 8e6;
hackrf.CenterFrequency = 433.92e6;
hackrf.Gain = 30;
fg.Connect(new SdrSource(hackrf), spectrum);
```

Pintasan: `Sdr.Usrp()`, `Sdr.PlutoSdr("hostname=192.168.2.1")`, `Sdr.LimeSdr()`, `Sdr.HackRf()`, `Sdr.BladeRf()`,
`Sdr.RtlSdr()`, `Sdr.Airspy()`. Stream menggunakan CF32, sehingga sampel tiba sebagai `Complex32` tanpa konversi.

> Status: binding ini mengikuti C API SoapySDR 0.8 tetapi belum diuji terhadap radio fisik oleh CI proyek.
> Silakan laporkan hasil pengujian dengan perangkat keras Anda.

## rtl_tcp

`RtlTcpDevice` berkomunikasi dengan server `rtl_tcp` mana pun (RTL-SDR yang dibagikan melalui jaringan) sepenuhnya
dengan kode managed:

```csharp
var dongle = new RtlTcpDevice("192.168.1.20", 1234) { SampleRate = 2.048e6, CenterFrequency = 100e6 };
dongle.SetFrequencyCorrection(ppm: 1);
fg.Connect(new SdrSource(dongle), spectrum);
```

## SDR simulasi

`SimulatedSdrDevice` menyintesis sebuah skenario RF di sekitar frekuensi yang disetel — secara bawaan berupa pita siaran
FM dengan sejumlah stasiun, sebuah pembawa data, semburan (burst) telemetri, dan sebuah spur — serta mengatur kecepatannya
agar sesuai waktu nyata. Gunakan untuk pengembangan tanpa perangkat keras; galeri menggunakannya untuk pemindai pita dan
penerima FM.

```csharp
var sim = new SimulatedSdrDevice(
[
    new Emitter(433.92e6, -40, EmitterKind.Burst, 25e3, "Key fob"),
    new Emitter(434.10e6, -55, EmitterKind.NarrowFm, 12e3, "Voice"),
]) { CenterFrequency = 434e6, SampleRate = 1e6 };
```

## RFNoC

RFNoC (pemrosesan FPGA pada USRP) membutuhkan C++ API milik UHD dan ada dalam [peta jalan](../../PLAN.md); saat ini USRP
digunakan sebagai source/sink sampel biasa melalui SoapyUHD.
