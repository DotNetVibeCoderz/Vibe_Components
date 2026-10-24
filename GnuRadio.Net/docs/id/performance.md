# Performa

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/performance.md)

## Pilihan desain

- **Tanpa alokasi pada jalur sampel.** Fungsi work menerima span ke dalam ring buffer; blok menggunakan ulang array
  kerjanya; sink UI menyalin ke array milik pemanggil hanya ketika sebuah frame berubah.
- **Buffer tanpa lock.** Satu penulis, banyak pembaca, publikasi volatile, wilayah cermin untuk jendela yang bersambung.
- **Kernel SIMD.** `GnuRadio.Kernels.Volk` menggunakan `Vector128`/`Vector256` (SSE/AVX2 pada x64, NEON pada ARM64)
  dengan fused multiply-add: perkalian titik FIR, perkalian kompleks, magnitudo, penskalaan.
- **Thread per blok.** Blok berjalan paralel di thread masing-masing dan tidur ketika menganggur.
- **Desimasi di tempat yang murah.** Sink visual dan `LogPowerFft` melewati sampel di antara frame, sehingga biayanya
  mengikuti laju frame, bukan laju sampel; `FreqXlatingFirFilter` hanya menghitung keluaran yang tersisa setelah desimasi.

## Kernel Rust opsional

`native/gnuradio-native` adalah pustaka Rust kecil (C ABI) dengan kernel FIR yang menggunakan intrinsik AVX2/FMA yang
dipilih saat runtime, serta FFT dari `rustfft`. Build dengan `pwsh native/build.ps1` (atau `native/build.sh`); hasilnya
disalin ke `runtimes/<rid>/native` dan dikemas ke dalam paket NuGet. Aktifkan dengan:

```csharp
GnuRadio.Kernels.Volk.UseNative = true;   // falls back to managed code when the library is absent
```

Hanya kernel yang terukur lebih cepat yang dialihkan: pemfilteran FIR dan FFT.

## Hasil pengukuran

BenchmarkDotNet (`ShortRun`), .NET 10, x64 dengan AVX2, pada mesin pengembangan:

| Kernel | SIMD managed | Native (Rust) |
|---|---:|---:|
| FIR, tap kompleks × real, 65 536 sampel, 165 tap | 1.83 ms | **1.29 ms** |
| FFT, 4096 titik | 77–103 µs | **7.9 µs** |
| Perkalian kompleks, 65 536 sampel | **59 µs** | 68 µs |

Tidak ada benchmark yang melakukan alokasi. Jalankan sendiri:

```bash
dotnet run -c Release --project tests/GnuRadio.Net.Benchmarks -- --filter *
```

## Penyetelan

- `Flowgraph.BufferBytes`: buffer yang lebih kecil menurunkan latensi (penting untuk source yang terisi data kosong,
  seperti framer); buffer yang lebih besar mengurangi overhead penjadwalan.
- `Block.MaxOutputItems`: membatasi jumlah item per pemanggilan work untuk sebuah blok (padanan `set_max_noutput_items`
  di GNU Radio).
- `VisualSink.UpdateRate`: jumlah frame per detik yang dipublikasikan oleh sebuah sink.
- `Throttle` hanya untuk simulasi — source perangkat keras mengatur kecepatannya sendiri.
