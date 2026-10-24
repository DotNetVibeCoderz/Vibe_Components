# Dokumentasi GnuRadio.Net

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/index.md)

GnuRadio.Net adalah framework Software Defined Radio dan DSP untuk .NET 10 yang mengikuti model GNU Radio. Blok-blok
dihubungkan menjadi flowgraph; scheduler mengalirkan sampel di antara blok-blok tersebut; sink visual menyuplai data ke
komponen Blazor dan Avalonia.

| Panduan | Isi |
|---|---|
| [Memulai](getting-started.md) | Instalasi, flowgraph pertama, menjalankan galeri |
| [Arsitektur](architecture.md) | Blok, port, buffer, scheduler, tag, pesan, blok hierarkis |
| [Referensi blok](blocks.md) | Setiap modul beserta bloknya, dengan padanannya di GNU Radio |
| [Komponen UI](ui-components.md) | Tampilan dan kontrol Blazor dan Avalonia |
| [Perangkat keras](hardware.md) | Perangkat SoapySDR, rtl_tcp, SDR simulasi |
| [Interoperabilitas dengan GNU Radio](interop.md) | Stream dan PDU ZeroMQ, impor `.grc`, pembuatan kode C#, format berkas |
| [Performa](performance.md) | Kernel SIMD, pustaka Rust opsional, angka hasil pengukuran, penyetelan |
| [Galeri](gallery.md) | 17 demo dan cara menambahkan demo baru |

![Beranda galeri](../images/web-home.png)
