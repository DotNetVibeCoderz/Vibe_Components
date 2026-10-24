# Interoperabilitas dengan GNU Radio

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/interop.md)

## ZeroMQ

`GnuRadio.Net.ZeroMQ` mengirim dan menerima data persis seperti blok `gr-zeromq` GNU Radio (byte item mentah per pesan,
`pass_tags=False`), sehingga sebuah flowgraph GNU Radio dan sebuah flowgraph .NET dapat menjadi dua bagian dari sistem
yang sama.

| GnuRadio.Net | GNU Radio |
|---|---|
| `ZmqPubSink<T>` / `ZmqSubSource<T>` | ZMQ PUB Sink / ZMQ SUB Source |
| `ZmqPushSink<T>` / `ZmqPullSource<T>` | ZMQ PUSH Sink / ZMQ PULL Source |
| `ZmqPubMessageSink` / `ZmqSubMessageSource` | ZMQ PUB/SUB Message Sink/Source (format wire PMT) |

```csharp
// .NET transmitter; in GNU Radio Companion add "ZMQ SUB Source", type complex, address tcp://<host>:5555
fg.Connect(source, new Throttle<Complex32>(1e6), new ZmqPubSink<Complex32>("tcp://*:5555"));
```

PUB tidak pernah memblokir (pesan dibuang selama tidak ada yang mendengarkan); PUSH menerapkan back-pressure hingga
`BlockingTimeout`. Pesan dienkode dengan `PmtSerializer`, kompatibel dengan `pmt::serialize` untuk boolean, simbol,
integer, double, kompleks, pasangan, dictionary, tuple, dan vektor u8/f32/c32.

![Jembatan ZeroMQ](../images/web-zeromq.png)

## Berkas GNU Radio Companion

`GnuRadio.Net.Grc` membaca berkas `.grc` (format YAML GNU Radio 3.8+), mengevaluasi ekspresi parameter Python
(`samp_rate / 4`, `int(x // 2)`, `math.pi`, `1e3 if a else b`, list, literal kompleks, `pmt.intern`,
`digital.constellation_qpsk()`…), memetakan blok ke GnuRadio.Net, dan membangun flowgraph yang dapat dijalankan. Blok yang
dinonaktifkan dilewati; blok yang di-bypass disambungkan langsung.

```csharp
using GnuRadio.Grc;

var grc = GrcFlowgraph.Load("receiver.grc", overrides: new Dictionary<string, object?> { ["freq"] = 99.9e6 });
foreach (var w in grc.Warnings) Console.WriteLine(w);       // unsupported blocks, failed expressions
var spectrum = grc.Block<GnuRadio.Sinks.FrequencySink>("qtgui_freq_sink_x_0");
grc.Flowgraph.Start();
```

Sink QT GUI menjadi sink visual GnuRadio.Net, sehingga `.grc` yang sama dapat menggerakkan UI Blazor atau Avalonia.
86 id blok telah dipetakan (source, matematika, konversi, filter, resampler, FFT, analog, digital, kanal, qtgui,
file/wav/audio, pesan); tambahkan lagi dengan `GrcBlockLibrary.Register(id, new GrcMapping(create, code))`.

### Pembuatan kode C#

GRC menghasilkan Python; `GrcCodeGenerator.Generate(document)` menghasilkan program C# yang setara dengan variabel,
blok, dan koneksi, serta komentar TODO untuk blok yang belum memiliki padanan.

![Impor GRC](../images/web-grc-import.png)

## Berkas

`FileSource<Complex32>` membaca rekaman `.cfile` GNU Radio (float32 ter-interleave) secara langsung; `FileSink<T>`
menulisnya. `InterleavedShortToComplex`, `InterleavedCharToComplex` dan `InterleavedUCharToComplex` membaca rekaman
16-bit, 8-bit bertanda (HackRF), dan 8-bit tak bertanda (RTL-SDR).

## TCP dan UDP

`TcpSink`/`TcpSource` dan `UdpSink`/`UdpSource` membawa item mentah, dapat saling beroperasi dengan blok jaringan
GNU Radio, `nc`, dan program apa pun yang membaca sampel mentah.
