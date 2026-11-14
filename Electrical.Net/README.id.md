<p align="center"><img src="assets/icon-512.png" width="120" alt="Logo Electrical.Net" /></p>

<h1 align="center">Electrical.Net</h1>
<p align="center"><b>Framework Teknik Elektro &amp; Sistem Tenaga Listrik Terlengkap untuk .NET</b><br/>
.NET 10 · kernel native Rust · Blazor &amp; Avalonia · MIT<br/>
<i>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil</i></p>

<p align="center"><a href="README.md">🇬🇧 Read in English</a></p>

![Galeri Electrical.Net](docs/images/web-home.png)

Electrical.Net menyediakan blok bangunan perangkat lunak sistem tenaga untuk pengembang .NET: aliran daya (seimbang dan tiga fasa tak
seimbang), hubung singkat, proteksi, stabilitas, harmonik, energi terbarukan, kendali smart grid, optimasi (aliran daya optimal DC
dan AC, LP/MILP), pasar listrik, estimasi keadaan, SCADA dan
protokol gardu induk, pertukaran model (CIM, PSS/E RAW, IEEE CDF, MATPOWER, OpenDSS), GIS, simulasi, API cloud, dan digital twin. Framework ini
dirancang sebagai fondasi aplikasi EMS, DMS, ADMS, DERMS, VPP, SCADA, dan digital twin. Algoritmanya diuji terhadap nilai acuan yang telah
dipublikasikan (MATPOWER, penyulang uji IEEE PES, Kersting, IEC 60909, IEEE 1584, Anderson & Fouad, Baran & Wu, Billinton & Allan, Wood &
Wollenberg, dan lainnya); lihat [Validasi](#validasi).

```csharp
using Electrical.Core.Network;
using Electrical.PowerFlow;

var pf = PowerFlowSolver.Solve(TestCases.Case14());   // Newton–Raphson sparse
Console.WriteLine(pf);
// NewtonRaphson: converged in 3 it, mismatch 3.44E-009, gen 272.393 MW, load 259 MW, losses 13.393 MW, Vmin 1.0100 pu @ bus 3
```

## Yang baru di 1.2

- **Aliran daya optimal AC** (`Electrical.Optimization`): `AcOpf.Solve` adalah formulasi polar `runopf` MATPOWER (keseimbangan daya AC,
  batas tegangan, P/Q generator, dan MVA cabang) yang diselesaikan dengan titik interior primal-dual dan Hessian eksak (`InteriorPointSolver`,
  publik untuk `NonlinearProgram` Anda sendiri). Hasilnya mencakup LMP untuk P dan Q, harga bayangan cabang/tegangan/generator, kendala
  terikat, dan tampilan `ToPowerFlowResult()` untuk diagram segaris. case9 5296,69 $/jam dan case14 8081,53 $/jam (LMP bus 1 36,724 $/MWh)
  identik dengan MATPOWER. [Panduan](docs/id/optimasi.md#aliran-daya-optimal-ac-v12)
- **Dekomposisi LMP**: `LmpDecomposition` memecah harga AC menjadi komponen energi, rugi, kongesti, dan tegangan (faktor rugi dari Jacobian
  AC) dan harga DC menjadi energi dan kongesti. [Panduan](docs/id/optimasi.md#dekomposisi-lmp)
- **Lapisan LP / MILP** (`Electrical.Optimization`): `LinearModel` dengan dual simplex berbatas bawaan (dengan dual) dan branch and bound,
  ditambah dua paket adapter baru, **`Electrical.Optimization.Highs`** (HiGHS 1.16.0) dan **`Electrical.Optimization.OrTools`** (OR-Tools
  9.15), yang memberi optimum dan dual yang sama. [Panduan](docs/id/solver.md)
- **Pasar listrik** (`Electrical.Markets`, baru): unit commitment dengan kendala keamanan sebagai MILP (start-up, waktu hidup/mati minimum,
  ramp, cadangan, batas saluran PTDF, dan N-1 melalui LODF yang ditambahkan secara malas), kliring day-ahead dan real-time dari penawaran jual
  dan beli, LMP dengan komponen energi/kongesti, penyelesaian (pendapatan, pembayaran, sewa kongesti, surplus), dan FTR dengan pemeriksaan
  kecukupan pendapatan. Pada kasus Wood & Wollenberg 24 jam jadwal MILP berbiaya 183 665 $ vs 184 368 $ untuk DP daftar prioritas.
  [Panduan](docs/id/pasar-listrik.md)
- **Keandalan DC-OPF**: titik interior QP kini dimulai dari dispatch proporsional Pmax yang seimbang; `SyntheticGrid(300)` konvergen dalam 8
  iterasi (sebelumnya macet di 100).
- **Galeri**: tiga demo baru: Aliran daya optimal AC, Unit commitment dengan kendala keamanan, Kliring pasar listrik.
- **CLI**: `eln acopf`, `eln scuc`, dan `eln market`. [Referensi](docs/id/cli.md)

## Yang baru di 1.1

- **Distribusi tak seimbang** (`Electrical.Distribution`, baru): aliran daya kerangka fasa (ladder sweep Kersting, injeksi arus untuk
  penyulang sedikit mesh), matriks saluran 3×3 dari geometri konduktor (Carson termodifikasi + Kron, kabel netral konsentris dan berpelindung
  pita), beban ZIP bintang/delta, hubungan trafo tiga fasa, regulator bertingkat dengan LDC, kapasitor terswitch, pengendali per fasa dan
  deret waktu, pembaca skrip OpenDSS, serta penyulang IEEE 4 dan 13 node. Tegangan IEEE 13 node berada dalam 0,148 % dari hasil terbitan.
  [Panduan](docs/id/distribusi.md)
- **Pertukaran model** (`Electrical.Interop`, baru): pembaca dan penulis PSS/E RAW v33/v35 (trafo tiga belitan, shunt terswitch, header
  `@!` v35), pembaca IEEE CDF, deteksi format berbasis isi dengan peringatan konversi. [Panduan](docs/id/pertukaran-model.md)
- **Kendali tegangan dalam QSTS** (`Electrical.Simulation`): `OltcController`, `VoltageRegulatorController` (kompensasi jatuh tegangan
  saluran), dan `CapacitorController` bertindak setelah setiap langkah waktu. Pada penyulang 33 bus, langkah pelanggaran tegangan turun dari
  64 menjadi 1 dari 96. [Panduan](docs/id/simulasi-hpc-native.md#kendali-tegangan-dalam-qsts-v11)
- **Aliran daya**: flat start Newton–Raphson kini mengambil sudut dari aliran daya DC, sehingga jaringan dengan phase shifter besar konvergen
  ke solusi operasional. Case9 dan case14 juga konvergen satu iterasi lebih cepat.
  [Panduan](docs/id/aliran-daya.md#flat-start-sudut-dari-aliran-daya-dc)
- **CLI**: `--file` membaca `.raw`/`.cdf`, `convert --out x.raw [--version 33|35]`, `qsts --control`, serta perintah baru `dist`, `dss`,
  dan `linecode`. [Referensi](docs/id/cli.md)
- **Galeri**: tiga demo baru: Distribusi tak seimbang (IEEE 13 node), Pertukaran model, dan Kendali tegangan dalam deret waktu.
- **Templat**: templat konsol baru `eln-distribution` (IEEE 13 node atau skrip OpenDSS); semua templat mereferensikan paket 1.1.0.
- Action CI diperbarui ke `actions/checkout@v7`, `upload-artifact@v7`, `download-artifact@v8`, dan `setup-dotnet@v6`.

## Modul

| Paket | Isi |
|---|---|
| **Electrical.Core** | Aljabar linear dense/sparse generik (`SparseLU<Complex>` dengan pengurutan minimum-degree), sistem per-unit, fasor, komponen simetris, model `PowerNetwork` bus-cabang, `NetworkBuilder` dalam satuan fisik, pustaka penghantar, Y-bus (urutan positif/negatif/nol), topologi, I/O MATPOWER, kasus uji (case9, case14, Baran–Wu 33-bus, jaringan sintetis), `TimeSeries`, builder DI dan event bus |
| **Electrical.Circuit** | Analisis nodal termodifikasi (MNA): titik kerja DC, sapuan AC / Bode, transien (trapezoidal, backward Euler); R, L, C, induktor terkopel, sumber, dioda, saklar, sumber terkendali, trafo ideal; netlist mirip SPICE; rangkaian tiga fasa bintang/segitiga |
| **Electrical.PowerFlow** | Newton–Raphson (sparse, batas Q, beban ZIP, flat start berawal DC), Gauss–Seidel, fast-decoupled XB, DC, backward/forward sweep; penyaringan kontingensi N-1 (paralel); PTDF / LODF |
| **Electrical.Distribution** (1.1) | Aliran daya tiga fasa tak seimbang dalam kerangka fasa (ladder sweep, injeksi arus), konstanta saluran Carson/Kron untuk saluran udara dan kabel, beban ZIP bintang/delta, trafo tiga fasa, regulator bertingkat dengan LDC, kapasitor terswitch, pengendali per fasa dan deret waktu, pembaca OpenDSS, penyulang IEEE 4 dan 13 node, reduksi seimbang |
| **Electrical.ShortCircuit** | IEC 60909: gangguan 3φ, SLG, LL, LLG melalui jaringan urutan; faktor c, κ (metode A/B/C), I″k, ip, Ib, Ith, komponen DC; kontribusi cabang dan sumber; pemindaian semua bus |
| **Electrical.Protection** | Relai waktu-terbalik IEC 60255 / IEEE C37.112, fuse, kurva TCC, koordinasi CTI, zona jarak mho, relai diferensial persentase, kemampuan pemutus (breaker duty), arc flash IEEE 1584-2002 |
| **Electrical.Stability** | Stabilitas transien multi-mesin model klasik (RK4) dengan daftar kejadian, waktu pemutusan kritis, kriteria luas sama SMIB, analisis eigen sinyal kecil dengan faktor partisipasi, kurva PV / QV, indeks-L, respons frekuensi (ROCOF, nadir) |
| **Electrical.Harmonic** | FFT (radix-2 + Bluestein), spektrum, THD/TDD/faktor-K, batas IEEE 519 dan IEC 61000, aliran daya harmonik, frequency scan dan deteksi resonansi, desain filter single-tuned, ketidakseimbangan |
| **Electrical.Renewable** | Posisi matahari (setara SPA), model clear-sky dan transposisi, suhu sel, PV single-diode (Villalva), PVWatts, model inverter, MPPT; turbin angin, AEP Weibull, wind shear; baterai dengan degradasi; volt-var / volt-watt / freq-watt IEEE 1547; hosting capacity |
| **Electrical.SmartGrid** | Demand response berbasis harga, peak shaving baterai, penjadwalan pengisian EV (valley filling), EMS microgrid (aturan dan dynamic programming), virtual power plant, optimasi Volt/VAR, rekonfigurasi penyulang, SAIFI/SAIDI/CAIDI/ENS |
| **Electrical.Optimization** | Economic dispatch (dengan koefisien rugi B), DC-OPF dengan LMP dan kendala aktif, DC-OPF dengan kendala keamanan N-1, AC-OPF (titik interior primal-dual, Hessian eksak) dengan dekomposisi LMP (energi / rugi / kongesti / tegangan), LP/MILP `LinearModel` dengan dual simplex dan branch and bound bawaan, unit commitment, penempatan kapasitor, QP dan NLP interior-point, Nelder–Mead, particle swarm |
| **Electrical.Optimization.Highs** (1.2) | Adapter solver LP/MIP HiGHS (`HighsSolver`, `AddHighs()`), pustaka native untuk win/linux/osx x64/arm64 |
| **Electrical.Optimization.OrTools** (1.2) | Adapter Google OR-Tools (`OrToolsSolver`, `AddOrTools()`): SCIP, CBC, GLOP, PDLP |
| **Electrical.Markets** (1.2) | Unit commitment dengan kendala keamanan (MILP, N-1), kliring pasar day-ahead dan real-time dengan penawaran jual dan beli, LMP dengan komponen energi/kongesti, penyelesaian, hak transmisi finansial |
| **Electrical.StateEstimation** | Estimator AC WLS (matriks gain sparse), pengukuran V/P/Q/aliran/PMU, observabilitas, uji χ², penghapusan bad data dengan largest normalized residual |
| **Electrical.SCADA** | Klien/server Modbus TCP dan codec PDU, basis data tag dengan penskalaan dan kualitas, alarm ISA-18.2, historian swinging-door, simulator RTU, codec APDU IEC 60870-5-104 |
| **Electrical.IEC61850** | Parser SCL (ICD/CID/SCD), object reference dan katalog logical node, data MMS, ASN.1 BER, frame GOOSE dan Sampled Values (9-2LE), retransmisi GOOSE |
| **Electrical.CIM** | Ekspor RDF/XML IEC 61970 CIM / gaya CGMES (EQ, TP, SSH, SV) dan impor dengan pemrosesan topologi |
| **Electrical.Interop** (1.1) | Baca dan tulis PSS/E RAW v33/v35, pembaca IEEE Common Data Format, deteksi format berbasis isi (RAW, CDF, MATPOWER, JSON Cloud) dengan peringatan konversi, sampel IEEE 14 bus tertanam |
| **Electrical.DigitalTwin** | Kembaran jaringan langsung: tag SCADA → estimasi keadaan WLS dengan penghapusan bad data → KPI, alarm yang dijelaskan, deviasi prakiraan; simulasi what-if; penuaan aset IEEE C57.91; simulator twin loop tertutup |
| **Electrical.Forecasting** | Holt–Winters (musiman tunggal/ganda), regresi dengan fitur cuaca, k-NN hari serupa, ensemble, prakiraan daya surya dan angin, metrik galat, profil sintetis |
| **Electrical.AI** | Agen operator jaringan LLM dwibahasa dengan 10 tool yang membumi (OpenAI, Azure OpenAI, Hugging Face, Ollama, atau aturan offline), deteksi anomali (robust z, EWMA, CUSUM), klasifikasi dan lokasi gangguan, klasterisasi profil beban, lingkungan RL Volt/VAR dengan Q-learning |
| **Electrical.GIS** | Ekspor/impor GeoJSON, Web Mercator, panjang saluran haversine, tata letak geografis force-directed, indeks spasial, peta SVG, area padam |
| **Electrical.Visualization** (+ **.Blazor**) | Tata letak dan render SVG diagram segaris otomatis dengan overlay aliran daya, grafik SVG (garis, batang, heatmap, gauge, fasor, TCC), encoder PNG, variabel tema terang/gelap; komponen Blazor |
| **Electrical.Simulation** | Simulasi deret waktu kuasi-statis (QSTS) dengan profil beban/PV, kejadian, dan loop kendali (OLTC, regulator tegangan LDC, kapasitor terswitch), manajer skenario, kecukupan pembangkitan (analitis dan Monte-Carlo sekuensial) |
| **Electrical.Cloud** | Runner job studi, kontrak JSON jaringan, minimal API ASP.NET Core (`MapElectricalApi`), penyimpanan skenario multi-tenant |
| **Electrical.HPC** | Aliran daya batch paralel, sapuan beban, aliran daya probabilistik Monte-Carlo, benchmark skalabilitas |
| **Electrical.Native** | Kernel Rust (FFT, harmonik Goertzel, THD/RMS, injeksi daya kompleks sparse, sampling normal deterministik) dengan fallback managed yang identik |
| **Electrical.Cli** | Dotnet tool `eln` dengan 36 perintah (termasuk `eln ask`, `eln twin`, `eln dist`, `eln dss`, `eln acopf`, `eln scuc`, dan `eln market`); membaca berkas MATPOWER, PSS/E RAW, IEEE CDF, JSON, dan CIM |
| **Electrical.Templates** | `dotnet new eln-console / eln-blazor / eln-avalonia / eln-scada / eln-webapi / eln-distribution` |

## Mulai cepat

```bash
dotnet add package Electrical.PowerFlow       # otomatis menarik Electrical.Core
dotnet add package Electrical.ShortCircuit
dotnet add package Electrical.Visualization
dotnet add package Electrical.Markets         # v1.2: SCUC dan kliring pasar (menarik Electrical.Optimization)
dotnet add package Electrical.Optimization.Highs   # solver HiGHS opsional (atau Electrical.Optimization.OrTools)
```

Bangun jaringan dalam satuan fisik, selesaikan aliran dayanya, lalu jalankan studi hubung singkat:

```csharp
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.ShortCircuit;

var net = new NetworkBuilder("plant", baseMva: 100, frequencyHz: 50)
    .AddBus(1, 150, BusType.Slack, "Grid 150 kV")
    .AddBus(2, 20, name: "MV bus")
    .AddBus(3, 20, name: "Feeder end")
    .AddGenerator(1, 0, vset: 1.0)                                   // infeed slack
    .AddTransformer(1, 2, ratingMva: 60, ukPercent: 12, copperLossKw: 250)
    .AddLine(2, 3, 8, "NA2XS2Y 1x240 RM/25 12/20 kV")               // kabel standar 8 km
    .AddLoad(3, 18, 6)                                               // 18 MW, 6 Mvar
    .AddShunt(3, 3)                                                  // bank kapasitor 3 Mvar
    .Build();                                                        // memvalidasi model

var pf = PowerFlowSolver.Solve(net);
Console.WriteLine($"V3 = {pf.Bus(3).Vm:0.000} pu, rugi {pf.LossesMw:0.000} MW");

var sc = ShortCircuitAnalysis.Calculate(net, 3, FaultType.ThreePhase, new ShortCircuitOptions
{
    ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 3000, RxRatio = 0.1 }],
});
Console.WriteLine(sc);   // I"k, ip, Ib, Ith, S"k di bus 3
```

Dengan dependency injection:

```csharp
services.AddElectrical(b => b.AddPowerFlow().AddShortCircuit().AddProtection().AddScada().AddCloud());
```

CLI dan templat:

```bash
dotnet tool install -g Electrical.Cli
eln pf --case case14
eln sc --case case14 --bus 4 --type slg
eln stability --case case9 --bus 8 --clear 0.083 --trip 8-9
eln ask "Berapa arus hubung singkat 3 fasa di bus 4?"           # agen AI (LLM atau offline)
eln twin --steps 24 --lang id                               # demo digital twin

dotnet new install Electrical.Templates
dotnet new eln-blazor -n DasborJaringanSaya
```

## Validasi

Setiap baris di bawah ini diuji oleh suite xUnit di `tests/Electrical.Tests` (sitasinya ada di dalam tes) atau dicetak oleh CLI `eln`.

| Studi | Acuan | Nilai acuan | Electrical.Net |
|---|---|---|---|
| Aliran daya AC, IEEE 9-bus | MATPOWER `case9` | P<sub>G1</sub> 71,64 MW, rugi 4,641 MW, \|V<sub>9</sub>\| 0,9956 pu | 71,64 MW, 4,641 MW, 0,9956 pu |
| Aliran daya AC, IEEE 14-bus | MATPOWER `case14` | P<sub>G1</sub> 232,39 MW, rugi 13,393 MW, V<sub>14</sub> 1,036 ∠ −16,04° | 232,39 MW, 13,393 MW, 1,0355 ∠ −16,034° |
| Penyulang radial (NR dan backward/forward sweep) | Baran & Wu 33-bus | rugi 202,7 kW, V<sub>min</sub> 0,9131 pu di bus 18 | 202,7 kW, 0,9131 pu di bus 18 |
| Gangguan 3φ IEC 60909, penyulang radial 110/20 kV | Perhitungan tangan, IEC 60909-0 pers. 6, 7, 12 | I″k3 = 3,899 kA | 3,899 kA |
| Faktor IEC 60909 | IEC 60909-0 | κ(R/X = 0,1) = 1,7460, μ(5; 0,1 s) = 0,76537 | 1,7460, 0,76537 |
| Arc flash 480 V, 30 kA, 0,1 s, 610 mm, G = 32 mm | IEEE 1584-2002 pers. 1, 3, 6 | I<sub>a</sub> 15,72 kA, E 3,434 cal/cm², AFB 1245 mm | 15,72 kA, 3,434 cal/cm², 1245 mm |
| Arc flash 13,8 kV, 20 kA, 0,2 s, 910 mm | IEEE 1584-2002 | I<sub>a</sub> 19,18 kA, E 5,23 cal/cm² | 19,18 kA, 5,226 cal/cm² |
| Relai waktu-terbalik, M = 10, TMS = 1 | IEC 60255-151 Standard Inverse | 2,971 s | 2,971 s |
| Sudut rotor awal model klasik, 9-bus | Anderson & Fouad | δ = 2,27°, 19,73°, 13,17° | 2,27°, 19,73°, 13,17° |
| CCT, gangguan di bus 7 (A&F) dibuka oleh saluran 5–7 | Literatur model klasik ≈ 0,16–0,17 s | | 0,161–0,162 s |
| Mode elektromekanis, 9-bus klasik | Sauer & Pai / Anderson & Fouad | ω ≈ 8,7 dan 13,4 rad/s | 8,690 dan 13,360 rad/s (1,383 / 2,126 Hz) |
| Gangguan bolted SMIB (H = 5 s, P<sub>m</sub> = 0,8, P<sub>max</sub> = 1,8) | Kriteria luas sama (bentuk tertutup) | t<sub>cr</sub> = 0,2600 s | 0,2599 s lewat simulasi |
| Kurva PV dua bus, X = 0,1 pu | Transfer maksimum analitis V²/2X | λ<sub>max</sub> = 5,0 | 4,95–5,0 |
| Frequency scan, 500 MVA / 20 Mvar | h<sub>r</sub> = √(S<sub>sc</sub>/Q<sub>c</sub>) | 5,0 (300 Hz) | 5,0 (300 Hz) |
| PV single-diode, Kyocera KC200GT | Villalva dkk. 2009, Tabel II | R<sub>s</sub> 0,221 Ω, R<sub>p</sub> 415,4 Ω, I<sub>pv</sub> 8,214 A, I<sub>0</sub> 9,825e-8 A | 0,221 Ω, 416,3 Ω, 8,214 A, 9,825e-8 A; P<sub>mp</sub> 200,15 W |
| Posisi matahari | Contoh laporan NREL SPA (Reda & Andreas 2008) | zenit 50,11162°, azimut 194,34024° | dalam 0,05° |
| Economic dispatch, 9-bus, 315 MW | Biaya MATPOWER case9 (biaya inkremental sama) | λ 24,04 $/MWh, 86,56 / 134,38 / 94,06 MW | 24,044 $/MWh, 86,56 / 134,38 / 94,06 MW |
| Daftar prioritas unit commitment | Wood & Wollenberg Contoh 4A | biaya rata-rata beban penuh 9,79 / 9,40 / 11,19 $/MWh | sama; prioritas 2, 1, 3 |
| AC-OPF, IEEE 9 bus | `runopf` MATPOWER | 5296,69 $/jam, P<sub>G</sub> 89,80 / 134,32 / 94,19 MW | 5296,69 $/jam, 89,80 / 134,32 / 94,19 MW |
| AC-OPF, IEEE 14 bus | `runopf` MATPOWER | 8081,53 $/jam, λ<sub>P</sub>(bus 1) 36,724 $/MWh | 8081,53 $/jam, 36,724 $/MWh |
| LMP AC | Beda-hingga biaya optimal (±0,5 MW) | biaya marginal per bus | dalam 1 %; residu dekomposisi < 1e-3 $/MWh |
| SCUC, 24 jam, 3 unit, cadangan 5 % | DP daftar prioritas Wood & Wollenberg | 184 368 $ | 183 665 $ (komitmen MILP pada biaya eksak) |
| SCUC, 3 jam | Brute force atas 512 komitmen | optimum | identik |
| LP SCUC / pasar dengan solver eksternal | HiGHS 1.16, OR-Tools 9.15 | objektif, dual | identik dengan solver bawaan |
| Pasar dua bus, saluran 100 MW | Perhitungan manual | LMP 20 / 50 $/MWh, sewa kongesti 3 000 $ | 20 / 50 $/MWh, 3 000 $ = pembayaran FTR |
| SCUC case9, saluran 8–9 pada 57,5 MW | Harga tunggal tanpa kongesti | pemisahan harga | LMP 21,39–28,10 $/MWh pukul 14:00 |
| Rekonfigurasi penyulang, 33-bus + 5 tie | Optimum literatur | buka 7-8, 9-10, 14-15, 32-33, 25-29; ≈ 139,5 kW | saklar yang sama; 139,6 kW |
| Penempatan kapasitor, 33-bus (3 bank) | Rentang literatur | 132–145 kW | 132,2 kW |
| Keandalan radial | Billinton & Allan §7.2, Tabel 7.1 | λ<sub>C</sub> 0,45 f/thn, U<sub>C</sub> 2,9 jam | 0,45 f/thn, 2,9 jam |
| Tabel probabilitas kapasitas keluar | Billinton & Allan §2.2 | 0,9604 / 0,0392 / 0,0004 | identik |
| Estimasi keadaan WLS tanpa derau | Solusi aliran daya | keadaan eksak | diperoleh kembali secara eksak; aliran buruk dihapus oleh uji LNR |
| Round trip CIM | Jaringan asli | aliran daya identik | identik (case14 13,393 MW, case33 202,7 kW) |
| Aliran daya tak seimbang, IEEE 13 node | Hasil penyulang uji IEEE PES (node 675: 0,9835 / 1,0553 / 0,9758 pu) | galat terbesar thd tegangan terbitan | 0,9831 / 1,0559 / 0,9772 pu; terbesar 0,148 % |
| Aliran daya tak seimbang, IEEE 4 node (GrY–GrY, Δ–GrY) | Hasil penyulang uji IEEE PES | galat terbesar | 0,026 % |
| Konstanta saluran udara | Kersting Contoh 4.1 | z<sub>aa</sub> = 0,4576 + j1,0780 Ω/mil | 0,4576 + j1,0781 Ω/mil |
| Konfigurasi IEEE 13 node 601–607 dari geometri | Lembar data IEEE | matriks impedansi fasa | dalam 0,0001 Ω/mil |
| PSS/E RAW v33/v35 dan IEEE CDF, IEEE 14 bus | MATPOWER case14 | rugi 13,393 MW | 13,393 MW |
| Kendali volt/var QSTS, 33 bus di belakang trafo 10 MVA | Hari yang sama tanpa kendali | 64 langkah pelanggaran, V<sub>min</sub> 0,9237 pu | 1 langkah, V<sub>min</sub> 0,9617 pu (4 langkah tap, 2 pensaklaran) |
| Penuaan trafo | IEEE C57.91 | θ<sub>H</sub> = 110 °C pada beban pengenal (ambien 30 °C), F<sub>AA</sub>(110 °C) = 1, ≈ 2 pada 116,8 °C | 110 °C, 1,0, 2,0 |
| Digital twin, case14 24 jam dengan derau RTU | Keadaan aliran daya sebenarnya | galat tegangan < 0,005 pu, beban dalam 3 % | rata-rata 0,00017 pu, maks 0,00077 pu |
| Agen LLM, live | Azure OpenAI `gpt-5-mini`, rugi case14 (MATPOWER 13,393 MW) | 13,39 MW dikutip dari tool | lulus |

## Galeri

Dua puluh enam demo langsung yang mencakup semua modul dengan UI profesional, sebagai aplikasi web **Blazor** dan aplikasi desktop **Avalonia** yang berbagi kode skenario
(`gallery/Electrical.Gallery.Shared`). Setiap angka dihitung oleh pustaka saat aplikasi berjalan, di samping kode C# yang menghasilkannya.

```bash
dotnet run --project gallery/Electrical.Gallery.Web --urls http://localhost:5290
dotnet run --project gallery/Electrical.Gallery.Desktop
```

| Web (Blazor) | Desktop (Avalonia) |
|---|---|
| ![Aliran daya](docs/images/web-powerflow.png) | ![Aliran daya](docs/images/desktop-powerflow.png) |
| ![Digital twin](docs/images/web-digitaltwin.png) | ![Agen AI](docs/images/desktop-agent.png) |
| ![Hubung singkat](docs/images/web-shortcircuit.png) | ![Stabilitas](docs/images/desktop-stability.png) |
| ![SCADA](docs/images/web-scada.png) | ![IEC 61850](docs/images/desktop-iec61850.png) |
| ![Peta GIS](docs/images/web-gis.png) | ![Smart grid](docs/images/desktop-smartgrid.png) |
| ![Distribusi tak seimbang](docs/images/web-distribution.png) | ![Pertukaran model](docs/images/desktop-interop.png) |
| ![Kendali tegangan](docs/images/web-voltagecontrol.png) | ![Distribusi tak seimbang](docs/images/desktop-distribution.png) |
| ![Aliran daya optimal AC](docs/images/web-acopf.png) | ![Unit commitment dengan kendala keamanan](docs/images/desktop-scuc.png) |
| ![Kliring pasar listrik](docs/images/web-market.png) | ![Aliran daya optimal AC](docs/images/desktop-acopf.png) |

Tema gelap: ![Tema gelap](docs/images/web-home-dark.png)

Tangkapan layar desktop bernama `desktop-<demo>.png` (misalnya `docs/images/desktop-powerflow.png`). Semua tangkapan layar ada di
[docs/images](docs/images); lihat [docs/id/galeri.md](docs/id/galeri.md).

## Arsitektur

```mermaid
graph TD
    Core[Electrical.Core<br/>numerik · jaringan · satuan · DI]
    Native[Electrical.Native<br/>kernel Rust]
    PF[PowerFlow]
    Core --> PF
    Core --> Circuit & ShortCircuit & IEC61850 & CIM & Forecasting & Interop
    ShortCircuit --> Protection
    PF --> Stability & Harmonic & Renewable & Optimization & StateEstimation & SCADA & GIS & Visualization & Distribution
    Renewable --> SmartGrid
    Renewable --> Simulation
    Forecasting --> Simulation
    Native --> HPC
    PF --> HPC
    PF --> Cloud
    ShortCircuit --> Cloud
    StateEstimation --> DigitalTwin
    SCADA --> DigitalTwin
    Forecasting --> DigitalTwin
    ShortCircuit --> AI
    Optimization --> AI
    Optimization --> Markets
    PF --> Markets
    Optimization --> Highs[Optimization.Highs] & OrTools[Optimization.OrTools]
    Forecasting --> AI
    DigitalTwin --> AI
    Stability --> AI
    Renewable --> AI
    Visualization --> VizBlazor[Visualization.Blazor]
    Cloud & HPC & Visualization & SCADA --> Apps[CLI · Templat · Galeri]
```

Dependensi bersifat asiklik (Core → PowerFlow → modul analisis → SmartGrid/Simulation → DigitalTwin/AI/Cloud). Pustaka kompatibel AOT,
memakai satuan teknik di permukaan API (MW, Mvar, kV, kA, Ω, pu, derajat, detik), dan deterministik. Detail di
[docs/id/arsitektur.md](docs/id/arsitektur.md).

## Struktur repositori

```
src/              28 paket pustaka (+ Electrical.Visualization.Blazor)
native/           Crate Rust electrical-native (C ABI, fungsi eln_*)
tools/            Electrical.Cli (dotnet tool "eln")
templates/        Electrical.Templates (paket dotnet new)
gallery/          Skenario bersama, galeri web Blazor, galeri desktop Avalonia
tests/            Suite tes xUnit dengan sitasi nilai acuan
docs/en, docs/id  Dokumentasi (Inggris / Bahasa Indonesia)
build/            build-native.ps1, pack.ps1, publish-nuget.ps1, ci/electrical-net.yml
```

## Build dari sumber

```bash
pwsh build/build-native.ps1                 # kernel Rust (opsional; tanpa ini dipakai fallback managed)
dotnet build Electrical.Net.slnx
dotnet test tests/Electrical.Tests
dotnet run --project tools/Electrical.Cli -- help
pwsh build/pack.ps1                         # paket NuGet → artifacts/packages
```

Membutuhkan .NET 10 SDK; Rust (stable) hanya untuk kernel native. Tag `electrical-net-vX.Y.Z` di monorepo menjalankan
`.github/workflows/electrical-net.yml`, yang membangun kernel untuk win-x64, linux-x64, linux-arm64, osx-x64, dan osx-arm64, menjalankan tes, lalu
memublikasikan paket ke NuGet.

## Dokumentasi

- Bahasa Indonesia: [docs/id](docs/id/README.md): memulai, arsitektur, panduan modul, aliran daya, distribusi tak seimbang, hubung singkat &
  proteksi, stabilitas, harmonik, energi terbarukan & smart grid, optimasi, pasar listrik, solver LP/MILP, estimasi keadaan, SCADA / IEC 61850 / CIM, pertukaran model,
  GIS & visualisasi, simulasi / HPC / native, API cloud, AI & digital twin, CLI, templat, galeri.
- English: [docs/en](docs/en/README.md).
- Peta jalan: [PLAN.md](PLAN.md).

## Lisensi

MIT © 2026 Gravicode Studios. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
