# Arsitektur

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/architecture.md)

## Blok dan port

Sebuah blok (`GnuRadio.Block`, padanan `gr::block` di GNU Radio) mendeklarasikan port stream bertipe dan port pesan
bernama, serta mengimplementasikan `GeneralWork(WorkContext)`. Item berupa tipe nilai `unmanaged`: `float`, `Complex32`
(identik secara biner dengan `gr_complex` GNU Radio), `byte`, `short`, `int`… Sebuah port dapat membawa vektor
(`AddInput<Complex32>(vectorLength: 1024)`).

Sebagian besar blok diturunkan dari kelas dasar yang lebih sederhana:

| Kelas dasar | Laju | Yang Anda implementasikan |
|---|---|---|
| `SyncBlock<TIn,TOut>` | 1 : 1 | `Work(ReadOnlySpan<TIn>, Span<TOut>)` |
| `SyncMultiInputBlock<TIn,TOut>` | N masukan, 1 : 1 | `Work(WorkContext, int items, Span<TOut>)` |
| `DecimatorBlock<TIn,TOut>` | D : 1 | `Work(input of n·D items, output of n items)` |
| `InterpolatorBlock<TIn,TOut>` | 1 : I | `Work(input of n, output of n·I)` |
| `SourceBlock<T>` | — | `int Generate(Span<T>)`, kembalikan −1 saat habis |
| `SinkBlock<T>` | — | `bool Consume(ReadOnlySpan<T>)`, kembalikan false untuk selesai |
| `MessageBlock` | hanya pesan | handler pesan |
| `HierBlock` | komposit | pengkabelan blok-blok di dalamnya |

Sebuah blok kustom:

```csharp
public sealed class Clipper(float limit) : SyncBlock<float, float>("clipper")
{
    protected override void Work(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = Math.Clamp(input[i], -limit, limit);
    }
}
```

Blok umum membaca `context.Input<T>(port)`, menulis `context.Output<T>(port)`, dan melaporkan dengan
`context.Consume(port, n)` dan `context.Produce(port, n)`; dengan cara inilah blok berlaju variabel (resampler, framer,
penerima OFDM) ditulis. `MinimumInputItems` dan `OutputMultiple` memberi tahu scheduler jendela terkecil yang berguna.

## Buffer

Setiap keluaran yang terhubung memiliki satu `StreamBuffer<T>`: sebuah ring dengan satu penulis dan banyak pembaca,
dengan penghitung item absolut. GNU Radio memetakan buffer-nya dua kali di memori virtual sehingga blok selalu melihat
jendela yang bersambung; kode managed tidak dapat melakukannya, sehingga buffer menyimpan **wilayah cermin (mirror region)**
setelah area utama dan menyalin penulisan yang menyentuh bagian kepalanya. Dengan demikian pembaca dan penulis selalu
mendapatkan satu span yang bersambung, bahkan melewati titik wrap. Publikasi menggunakan pembacaan/penulisan volatile,
sehingga tidak ada lock pada jalur sampel. Ukuran buffer bawaan adalah 256 KB per keluaran (`Flowgraph.BufferBytes`) dan
akan bertambah ketika sebuah blok membutuhkan jendela yang lebih besar; buffer yang lebih kecil berarti latensi yang lebih rendah.

## Scheduler

`Flowgraph.Start()` menggunakan scheduler **thread-per-blok**, seperti TPB pada GNU Radio: setiap blok berjalan di
thread-nya sendiri, tidur menunggu sinyal yang diberikan oleh blok tetangganya ketika mereka memproduksi atau mengonsumsi.
Sebuah blok selesai ketika masukannya sudah selesai dan terkuras, ketika semua pembaca hilirnya terlepas (misalnya
setelah `Head`), atau ketika `GeneralWork` mengembalikan `WorkResult.Done`. `Flowgraph.RunSynchronously()` menjalankan
langkah per-blok yang sama secara round-robin pada satu thread.

Exception yang dilemparkan oleh sebuah blok menghentikan flowgraph; `Run()` melemparkannya kembali sebagai
`FlowgraphException`, dan `Flowgraph.Error` melaporkannya saat terjadi. `Block.Statistics` menyediakan jumlah pemanggilan
work, jumlah item, dan waktu di dalam work untuk profiling.

## Tag

Tag stream (`Tag(Offset, Key, Value)`) menandai offset item absolut — awal frame, deteksi, stempel waktu. Blok
menambahkannya dengan `context.AddTag` dan membacanya dengan `context.GetTags`. Kebijakan bawaan `AllToAll` menyalin tag
ke setiap keluaran, dengan menskalakan offset menggunakan `RelativeRate` (sehingga blok decimate-by-10 membagi offset dengan 10).

## Pesan dan PMT

Pesan asinkron membawa nilai `Pmt` — padanan tipe polimorfik GNU Radio: boolean, integer, double, kompleks, simbol,
pasangan, dictionary, tuple, serta vektor byte/float/complex. Sebuah **PDU** adalah pasangan
(dictionary metadata . vektor). Blok mendeklarasikan `RegisterMessageInput("in", handler)` dan `RegisterMessageOutput("out")`;
flowgraph menghubungkannya dengan `ConnectMessage`. Handler berjalan pada thread blok penerima. `Block.PostMessage`
bersifat thread-safe, sehingga UI menggunakannya untuk kontrol saat runtime (misalnya port `cmd` milik `SignalSource`).
`PmtSerializer` menghasilkan format wire `pmt::serialize` milik GNU Radio.

## Blok hierarkis

```csharp
public sealed class Doubler : HierBlock
{
    public Doubler() : base("doubler")
    {
        DeclareInput<float>();
        DeclareOutput<float>();
        var mul = new MultiplyConst<float>(2f);
        ConnectInput(0, mul);
        ConnectOutput(0, mul);
    }
}
```

Blok hierarkis diratakan (flatten) sebelum flowgraph dimulai, sehingga tidak menambah biaya saat runtime. `WbfmReceive`,
`ConstellationModulator`, `ConstellationReceiver`, `FskModulator` dan `ChannelModel` adalah contohnya.

## Validasi dan rekonfigurasi

`Flowgraph.Validate()` mendaftar masukan yang tidak terhubung, keluaran wajib yang tidak digunakan, dan ketidakcocokan
tipe; `Start()` menolak graf yang tidak valid. Ubah koneksi hanya saat flowgraph berhenti, atau gunakan
`Reconfigure(fg => …)` (padanan `lock`/`unlock` di GNU Radio). `Flowgraph.ToDot()` mengekspor Graphviz untuk dokumentasi.

## Modul out-of-tree

Tandai blok kustom dengan `[GrBlock("my_block", Category = "…")]` dan muat dengan `BlockRegistry.LoadModule(path)`;
`BlockRegistry.Create(id, args…)` membuat instansnya berdasarkan nama.
