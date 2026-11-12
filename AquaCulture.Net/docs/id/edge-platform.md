# Gateway edge, aplikasi lapangan, sinkronisasi offline, dan performa (v2.1)

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/edge-platform.md)

## Gateway edge NativeAOT (`samples/AquaCulture.EdgeGateway`, `aqua-edge`)

Saat internet putus, gateway kolam tetap harus mengatur kincir dengan benar. Karena itu `aqua-edge` mengambil semua keputusan di
lokasi dan hanya *menyinkronkan* data dengan cloud:

```
probe (simulator / Modbus TCP) → EdgeAnalytics (outlier, probe macet, kalibrasi) → EdgeOutbox (disk)
                                                     ├─ AlertEngine (ambang per spesies, histeresis)
                                                     └─ kincir: set point DO ± histeresis; dinyalakan lebih awal bila prakiraan
                                                        DO subuh turun di bawah DO kritis spesies
EdgeOutbox ⇄ CloudSyncClient ⇄ aqua-cloud (/t/{tenant}/sync/{gateway}/…)   data naik, pengaturan turun
```

```bash
# Raspberry Pi 4/5 dengan OS 64-bit (publish di host atau runner Linux arm64; NativeAOT tidak bisa cross-compile dari Windows)
sudo apt-get install -y clang zlib1g-dev
dotnet publish samples/AquaCulture.EdgeGateway -c Release -r linux-arm64 -o out
scp -r out/aqua-edge out/runtimes pi@gateway:/opt/aqua-edge/

/opt/aqua-edge/aqua-edge --selftest --require-native      # 48 jam simulasi dalam sekitar satu detik; exit code 0 = sehat
AQUA_SYNC_KEY=<kunci tenant> /opt/aqua-edge/aqua-edge --modbus 192.168.1.50:502 --pond P-01 --device probe-1 --aerator aer-1 \
    --cloud https://cloud.contoh.id --tenant koperasi-a --gateway gw-tambak-1 --data /var/lib/aqua-edge
```

- Hasilnya satu executable mandiri berukuran sekitar 4–5 MB (Pi tidak perlu runtime .NET), ditambah kernel Rust di `runtimes/<rid>/native`.
- Setiap peringatan trim/AOT dijadikan error. Dependensi yang tidak aman untuk AOT akan menggagalkan build, bukan baru gagal setelah
  berjalan di Pi.
- CI mem-publish gateway untuk `linux-arm64` (di runner arm64) dan `linux-x64`, lalu menjalankan `--selftest --require-native` pada
  binary native-nya.
- Di Windows, publish memerlukan C++ tools Visual Studio (`vswhere.exe` harus ada di `PATH`).

Self-test menjalankan seluruh alur kerja gateway dengan skenario berikut:
- 3 kolam dengan pembacaan setiap 5 menit, dan gateway di-restart di tengah jalan.
- Cloud yang menolak setiap permintaan ke-5 dan menghilangkan setiap respons ke-7.

Lalu self-test memeriksa bahwa:
- kernel Rust termuat dan hasilnya sama dengan rumus managed;
- alert dan perintah kincir benar-benar terjadi;
- pengaturan dari cloud diterapkan;
- setiap pembacaan sampai di cloud tepat satu kali.

Unit systemd untuk Pi:

```ini
[Unit]
Description=Gateway edge AquaCulture.Net
After=network.target
[Service]
ExecStart=/opt/aqua-edge/aqua-edge --modbus 192.168.1.50:502 --pond P-01 --device probe-1 --aerator aer-1 --cloud https://cloud.contoh.id --tenant koperasi-a --data /var/lib/aqua-edge
Environment=AQUA_SYNC_KEY=<kunci tenant>
Restart=always
[Install]
WantedBy=multi-user.target
```

## Aplikasi sampling lapangan (`samples/AquaCulture.FieldApp`, .NET MAUI)

Aplikasi ini untuk teknisi di pematang kolam. Teknisi mencatat kunjungan: hasil alat ukur genggam atau test kit, sampling jala, dan
jumlah udang/ikan mati. Penilaian langsung muncul, lalu data disimpan di ponsel. Aplikasi tetap jalan tanpa sinyal dan mengunggah
data lewat sinkronisasi yang sama dengan gateway.

| Yang tampil | Sumbernya |
|---|---|
| WQI, kelas, NH₃, masalah | `WaterQualityIndex.Assess` untuk spesies di kolam itu |
| Tindakan beserta dosisnya (kapur, aerasi, ganti air, …) | `RecommendationEngine` dengan volume dan aerasi kolam |
| Berat rata-rata, biomassa, pertumbuhan sejak kunjungan terakhir | sampling jala (`kg × 1000 / jumlah ekor`) dan riwayat |
| Pakan hari ini (kg, frekuensi, penyesuaian) | `FeedingCalculator.Plan`, dikurangi sesuai DO dan NH₃ yang baru diukur |
| Pemeriksaan salah ketik ("pH 78 di luar 4–11") | `FieldSample.Check` |

- Sampel yang disimpan masuk ke `EdgeOutbox` sebagai pembacaan, termasuk jenis baru `Transparency` (kecerahan Secchi, cm),
  `AverageWeight` (g), dan `Mortality`. Data diunggah ke `aqua-cloud` oleh `CloudSyncClient` saat ponsel tersambung internet: setelah
  menyimpan, saat aplikasi dibuka kembali, atau lewat tombol "Sinkron sekarang". Id perangkatnya `field-xxxxxxxx`.
- API key disimpan di `SecureStorage`. Riwayat bisa diekspor sebagai CSV lewat menu berbagi.
- Daftar kolam diisi per baris dengan format `id, nama, spesies, luas m², kedalaman m, populasi, kincir kW`.
- Logikanya ada di `samples/AquaCulture.FieldApp.Core` (net10.0, punya unit test). Halaman MAUI hanya menampilkan hasilnya.

```bash
dotnet workload install maui-android maui-windows
dotnet build samples/AquaCulture.FieldApp -f net10.0-windows10.0.19041.0
dotnet publish samples/AquaCulture.FieldApp -f net10.0-android -c Release -p:AndroidPackageFormats=apk
```

CI mem-build target Windows dan mem-publish APK Android sebagai artifact `aquaculture-field-android`. APK itu ditandatangani dengan
debug key, jadi tanda tangani dengan kunci Anda sendiri sebelum dibagikan. iOS bisa di-build di Mac setelah `net10.0-ios`
ditambahkan ke `TargetFrameworks`.

## Sinkronisasi offline-first (`AquaCulture.IoT`, `AquaCulture.Data`, `aqua-cloud`)

```csharp
using var outbox = new EdgeOutbox("/var/lib/aqua-edge/outbox");     // segmen JSON-lines + watermark "acked"
outbox.Append(pembacaan);                                            // setiap pembacaan diberi nomor urut berikutnya

var sync = new CloudSyncClient(http, new Uri("https://cloud.contoh.id/"), "koperasi-a", apiKey, "gw-tambak-1");
sync.ConfigChanged += cfg => setPoint = cfg.Number("doSetPoint") ?? setPoint;
await sync.RunAsync(outbox, TimeSpan.FromMinutes(1), ct: stop);      // saat offline, jeda coba ulang naik dari 5 detik sampai 5 menit
```

| Masalah | Penanganannya |
|---|---|
| Koneksi putus berhari-hari | Pembacaan menunggu di outbox (disk). Gateway tetap mengatur kincir secara lokal |
| Listrik padam saat menulis | Baris terakhir yang terpotong dibuang saat outbox dibuka lagi. Penomoran berlanjut dari pembacaan utuh terakhir |
| Kirim ulang setelah respons hilang | Cloud menyimpan watermark per gateway (tabel `sync_gateways`). Pembacaan dengan nomor ≤ watermark dilewati, jadi tidak ada data ganda |
| Kartu SD penuh | `MaxBytes` (bawaan 256 MB) membuang segmen paling lama dan mencatat jumlahnya di `Dropped` |
| Pengaturan dari kantor | `PUT /t/{tenant}/sync/{gateway}/config` (objek JSON apa pun). Gateway memeriksanya dengan `If-None-Match` dan menyimpan salinan terakhir di disk |

Endpoint cloud, semuanya memakai `X-Api-Key` tenant:

| Endpoint | Fungsi |
|---|---|
| `POST /t/{tenant}/sync/{gateway}/readings` | Unggah batch (format `SyncWire`). Jawabannya `{"acked": n, "stored": …, "duplicates": …}` |
| `GET` / `PUT /t/{tenant}/sync/{gateway}/config` | Pengaturan gateway dengan ETag |
| `GET /t/{tenant}/sync/gateways` | Watermark, total, dan kontak terakhir setiap gateway |

`AquaStore.IngestGatewayBatchAsync` menyimpan batch dan menaikkan watermark dalam satu transaksi. Database SQLite yang dibuat
oleh versi 2.0 otomatis mendapat tabel `sync_gateways`. Untuk provider lain, tambahkan tabel itu lewat migrasi.

## Benchmark dan anggaran performa (`benchmarks/AquaCulture.Benchmarks`)

```bash
dotnet run -c Release --project benchmarks/AquaCulture.Benchmarks -- --filter "*"                                   # BenchmarkDotNet
dotnet run -c Release --project benchmarks/AquaCulture.Benchmarks -- --budgets budgets.json --job short --filter "*"  # yang dijalankan CI
```

- `KernelBenchmarks` membandingkan setiap kernel Rust dengan fallback managed-nya:
  - satu langkah adveksi–difusi pada 200 × 200 sel;
  - saturasi DO dan NH₃ untuk 10.000 sampel;
  - statistik 100.000 nilai;
  - 1.000 simulasi Monte-Carlo pertumbuhan.
- `ModelBenchmarks` mengukur pekerjaan per pembacaan dan per jam di server tambak atau gateway:
  - neraca oksigen 48 jam;
  - simulasi pertumbuhan 120 hari;
  - penilaian kualitas air;
  - filter edge;
  - prakiraan DO 12 jam hasil pembelajaran;
  - dokumen EPCIS.

`budgets.json` berisi batas atas untuk setiap benchmark: rata-rata waktu per operasi (µs) dan byte yang dialokasikan per operasi.
Run gagal (exit code 1) jika ada anggaran yang terlampaui atau ada benchmark beranggaran yang tidak berjalan. Hasilnya ditulis
sebagai tabel di ringkasan job GitHub.

- Anggaran waktu adalah nilai terbesar antara 3× hasil di runner CI dan 1,5× hasil di desktop. Perlambatan sekitar 3× akan menggagalkan CI, tetapi fluktuasi runner tidak.
- Anggaran alokasi dibuat ketat karena jumlah alokasi deterministik. Kernel tidak boleh mengalokasikan memori sama sekali.
- Publish rilis menunggu sampai anggaran ini lolos.

Baseline (Windows 11, .NET 10, x64):

| Benchmark | Rust | Managed |
|---|---:|---:|
| Langkah adveksi–difusi, 200 × 200 | 526 µs | 926 µs |
| Saturasi DO × 10.000 | 107 µs | 133 µs |
| Monte-Carlo TGC, 1.000 × 120 hari | 1,6 ms | 1,9 ms |

| Model | Rata-rata | Alokasi |
|---|---:|---:|
| Neraca oksigen, 48 jam | 10 µs | 5,4 KB |
| Pertumbuhan, 120 hari | 28 µs | 15 KB |
| Penilaian kualitas air | 0,8 µs | 1 KB |
| Filter edge, per pembacaan | 0,8 µs | 750 B |
| Prakiraan DO GBT, 12 jam | 39 µs | 2,4 KB |
