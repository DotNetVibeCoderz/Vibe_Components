# CLI (`eln`)

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/cli.md)

```bash
dotnet tool install -g Electrical.Cli
eln help
```

Dari sumber: `dotnet run --project tools/Electrical.Cli -- <perintah> [opsi]`.

**Input jaringan** (sebagian besar perintah): `--case case9 | case14 | case33bw | synthetic<N>` atau `--file grid.m | grid.raw | grid.cdf |
grid.json | grid.xml` (MATPOWER, PSS/E RAW v33–35, IEEE CDF, JSON Electrical.Net, CIM RDF/XML). Berkas RAW dan CDF dideteksi dari isinya oleh
[`Electrical.Interop`](pertukaran-model.md) dan peringatan konversi ditulis ke stderr. Kasus default adalah `case14` kecuali disebutkan lain
oleh perintahnya.

## Perintah

| Perintah | Opsi | Keterangan |
|---|---|---|
| `info` | | Status framework, modul, dan kernel native |
| `cases` | | Jaringan uji bawaan |
| `pf` | `--method nr\|fd\|gs\|dc\|bfs`, `--scale 1.2`, `--qlim`, `--json`, `--out sld.svg` | Aliran daya |
| `n1` | `--top 15` | Peringkat kontingensi cabang N-1 |
| `plf` | `--samples 500`, `--std 0.1`, `--seed 2026` | Aliran daya probabilistik (Monte-Carlo) |
| `bench` | `--sizes 100,1000,3000` | Benchmark skalabilitas aliran daya pada jaringan sintetis |
| `sld` | `--out sld.svg`, `--literal`, `--dark` | Diagram segaris dengan overlay aliran daya |
| `geojson` | `--out grid.geojson`, `--svg map.svg`, `--lat -6.2 --lon 106.8` | Ekspor GIS |
| `convert` | `--out grid.m\|grid.json\|grid.xml\|grid.raw`, `--version 33\|35` | Konversi MATPOWER / JSON / CIM / PSS/E RAW |
| `rtu` | `--port 1502`, `--seconds 0` | Simulator RTU Modbus TCP yang menyajikan pengukuran aliran daya |
| `serve` | `--urls http://localhost:5300` | REST API (Electrical.Cloud) |
| `sc` | `--bus 4 --type 3ph\|slg\|ll\|llg` | Hubung singkat IEC 60909 (pemindaian semua bus tanpa `--bus`) |
| `stability` | `--bus 8`, `--clear 0.083`, `--trip 8-9` | Stabilitas transien (model klasik) dan waktu pemutusan kritis |
| `pv` | `--step 0.05` | Kurva PV (margin loadability) dan indeks-L |
| `modes` | | Nilai eigen sinyal kecil (frekuensi, redaman) |
| `harmonic` | `--bus 9`, `--source 6pulse\|vfd\|led`, `--pu 0.1` | Aliran daya harmonik, THD, dan pemeriksaan IEEE 519 |
| `arcflash` | `--kv 0.48 --ika 30 --time 0.2`, `--dist 455`, `--equipment switchgear` | Arc flash IEEE 1584 |
| `relay` | `--pickup 400 --tms 0.1`, `--curve iec-si`, `--current 4000` | Waktu kerja relai |
| `ed` | `--losses` | Economic dispatch (rugi koefisien-B dengan `--losses`) |
| `opf` | `--limit 5-6:60` | DC-OPF dengan LMP (override batas saluran dalam MW) |
| `uc` | | Unit commitment 24 jam (contoh 3 unit Wood & Wollenberg) |
| `se` | `--bad 2-3` | Estimasi keadaan WLS dengan deteksi bad data (galat besar disuntikkan pada sebuah aliran) |
| `forecast` | `--method hw\|reg\|knn\|ens`, `--days 14` | Prakiraan beban hari-ke-depan pada profil sintetis |
| `reconfig` | | Rekonfigurasi penyulang, 33-bus dengan 5 saklar tie |
| `vvo` | | Optimasi Volt/VAR, 33-bus |
| `caps` | | Penempatan kapasitor optimal, 33-bus |
| `hosting` | `--bus 18`, `--max 5` | Hosting capacity PV di sebuah bus (33-bus) |
| `qsts` | `--days 1`, `--pv 1.5`, `--control` | Deret waktu kuasi-statis (beban + PV 15 menit); `--control` menambah OLTC dan kapasitor terswitch |
| `ask` | `"<pertanyaan>"`, `--case case14` | Agen AI jaringan (LLM lewat `ELECTRICAL_LLM_*`, selain itu offline) |
| `twin` | `--steps 24`, `--lang en\|id` | Digital twin: SCADA → estimasi keadaan → alarm yang dijelaskan → kesehatan aset |
| `dist` | `--feeder ieee13\|ieee4`, `--auto`, `--delta`, `--unbalanced` | Aliran daya tiga fasa tak seimbang pada penyulang uji IEEE |
| `dss` | `--file feeder.dss`, `--auto` | Membaca skrip OpenDSS dan menjalankan aliran daya tak seimbang |
| `linecode` | `--config 601` | Matriks impedansi fasa Carson + Kron untuk konfigurasi saluran IEEE 13 node (601–607) |

## Contoh keluaran

Semua keluaran di bawah ini diambil langsung dari tool versi 1.1.0 (teks CLI dalam bahasa Inggris, kecuali `ask` dan `twin --lang id`).

```
$ eln help
Electrical.Net CLI 1.1.0 — Comprehensive Electrical Engineering & Power System Framework for .NET
Made by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

Usage: eln <command> [options]
Network input (most commands): --case case9|case14|case33bw|synthetic<N>  or  --file grid.m|.raw|.cdf|.json|.xml

  info                                    Framework, modules and native kernel status
  cases                                   Built-in test networks
  pf [--method nr|fd|gs|dc|bfs] [--scale 1.2] [--qlim] [--json] [--out sld.svg]   Power flow
  n1 [--top 15]                           N-1 branch contingency ranking
  plf [--samples 500] [--std 0.1] [--seed 2026]                                   Probabilistic (Monte-Carlo) load flow
  bench [--sizes 100,1000,3000]           Power-flow scaling benchmark on synthetic grids
  sld --out sld.svg [--literal --dark]    Single-line diagram with power-flow overlay
  geojson --out grid.geojson [--svg map.svg] [--lat -6.2 --lon 106.8]            GIS export
  convert --out grid.m|.json|.xml|.raw [--version 33|35]   MATPOWER / JSON / CIM / PSS/E RAW conversion
  rtu [--port 1502] [--seconds 0]         Modbus TCP RTU simulator serving power-flow measurements
  serve [--urls http://localhost:5300]    REST API (Electrical.Cloud)

  sc [--bus 4 --type 3ph|slg|ll|llg]     IEC 60909 short circuit (all-bus scan without --bus)
  stability --bus 8 [--clear 0.083] [--trip 8-9]   Transient stability (classical model) + critical clearing time
  pv [--step 0.05]                        Voltage stability PV curve (loadability margin) and L-index
  modes                                   Small-signal eigenvalues (frequency, damping)
  harmonic --bus 9 [--source 6pulse|vfd|led] [--pu 0.1]   Harmonic load flow, THD and IEEE 519 check
  arcflash --kv 0.48 --ika 30 --time 0.2 [--dist 455] [--equipment switchgear]     IEEE 1584 arc flash
  relay --pickup 400 --tms 0.1 [--curve iec-si] --current 4000                     Relay operating time
  ed [--losses]                           Economic dispatch (λ, unit outputs; B-coefficient losses with --losses)
  opf [--limit 5-6:60]                    DC optimal power flow with LMPs (optional line limit override in MW)
  uc                                      24 h unit commitment (Wood & Wollenberg 3-unit example)
  se [--bad 2-3]                          WLS state estimation with bad-data detection (gross error injected on a flow)
  forecast [--method hw|reg|knn|ens] [--days 14]   Day-ahead load forecast on a synthetic profile (MAPE)
  reconfig                                Feeder reconfiguration on the 33-bus feeder with 5 tie switches
  vvo                                     Volt/VAR optimization on the 33-bus feeder
  caps                                    Optimal capacitor placement on the 33-bus feeder
  hosting [--bus 18] [--max 5]            PV hosting capacity at a bus (33-bus feeder)
  qsts [--days 1] [--pv 1.5] [--control]  Quasi-static time-series simulation (15-min load + PV; OLTC + switched capacitor with --control)
  ask "Run a power flow on case14 at 120% load" | "Berapa arus hubung singkat di bus 4?"   AI grid agent
                                          (LLM via ELECTRICAL_LLM_PROVIDER/_ENDPOINT/_API_KEY/_MODEL, offline otherwise)
  twin [--steps 24] [--lang en|id]        Digital twin: SCADA → state estimation → explained alarms → asset health
  dist [--feeder ieee13|ieee4] [--auto] [--delta] [--unbalanced]   Unbalanced three-phase power flow on an IEEE test feeder
  dss --file feeder.dss [--auto]          Read an OpenDSS script and run the unbalanced power flow
  linecode --config 601                   Carson + Kron phase impedance matrix of an IEEE 13-node line configuration (601–607)
```

```
$ eln info
Electrical.Net 1.1.0 — Comprehensive Electrical Engineering & Power System Framework for .NET
Framework Teknik Elektro & Sistem Tenaga Listrik Terlengkap untuk .NET
Made by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

Modules (25): Core, Circuit, PowerFlow, Distribution, ShortCircuit, Stability, Harmonic, Protection, Renewable, SmartGrid, Optimization, StateEstimation, SCADA, IEC61850, CIM, DigitalTwin, Forecasting, AI, GIS, Visualization, Simulation, Cloud, HPC, Interop, Native
Native kernels: Rust library loaded (native (Rust))
Test cases: case9, case14, case33bw, synthetic

$ eln cases
  case9: 9 buses, 9 branches, 3 generators, 3 loads (base 100 MVA)
  case14: 14 buses, 20 branches, 5 generators, 11 loads (base 100 MVA)
  case33bw: 33 buses, 32 branches, 1 generators, 32 loads (base 10 MVA)
  synthetic100: 100 buses, 180 branches, 20 generators, 80 loads (base 100 MVA)

$ eln pf --case case9
NewtonRaphson: converged in 3 it, mismatch 9.80E-011, gen 319.641 MW, load 315 MW, losses 4.641 MW, Vmin 0.9956 pu @ bus 9

$ eln pf --case case33bw --method bfs
BackwardForwardSweep: converged in 8 it, mismatch 1.23E-009, gen 3.918 MW, load 3.715 MW, losses 0.203 MW, Vmin 0.9131 pu @ bus 18

$ eln sc --case case14 --bus 4 --type slg
SingleLineToGround fault at bus 4 (138 kV): I"k = 4.625 kA, ip = 11.234 kA, Ib = 4.625 kA, Ith = 4.682 kA, S"k = 1105.4 MVA
  κ = 1.718, R/X = 0.113, 3I0 = 4.625 kA

$ eln stability --case case9 --bus 8 --clear 0.083 --trip 8-9
Transient stability on case9: 3φ fault at bus 8, cleared at 0.083 s by tripping branch 8-9
  Result: STABLE
  Critical clearing time: 0.161 s

$ eln pv --case case14
PV curve on case14: λmax = 4.059 (margin 792.4 MW), critical bus 5
L-index: critical bus 14

$ eln arcflash --kv 0.48 --ika 30 --time 0.2
Arc flash (IEEE 1584-2002): Ia = 15.72 kA, E = 10.58 cal/cm² (44.2 J/cm²)
  Arc-flash boundary 1994 mm · PPE category 3: arc rating ≥ 25 cal/cm²

$ eln relay --pickup 400 --tms 0.1 --current 4000
IecStandardInverse, pickup 400 A, TMS 0.1: I = 4000 A (10.0× pickup) → t = 0.297 s

$ eln opf --case case9
DC-OPF on case9: optimal, cost 5216.03 $/h, λ = 24.044 $/MWh
  Gen 1:    86.56 MW
  Gen 2:   134.38 MW
  Gen 3:    94.06 MW
  LMP [$/MWh]: 1:24.04  2:24.04  3:24.04  4:24.04  5:24.04  6:24.04  7:24.04  8:24.04  9:24.04

$ eln uc
Unit commitment (24 h): feasible, production 185167 $, startup 100 $, total 185267 $
  Priority order: 2, 1, 3

$ eln se --case case14
WLS state estimation on case14: 122 measurements, converged True in 4 it
  Initial J = 68.5 (χ² passed: True); final J = 68.5 ≤ 130.0
  Max |ΔV| vs true state 0.0015 pu, max |Δθ| 0.096°

$ eln reconfig
Feeder reconfiguration (33-bus, 5 ties): open 14-15, 25-29, 32-33, 7-8, 9-10
  Losses 202.7 → 139.6 kW (31.1 %), Vmin 0.9131 → 0.9378 pu, 114 power flows

$ eln vvo
Volt/VAR optimization (33-bus): losses 202.7 → 118.6 kW, V [0.913, 1.000] → [0.995, 1.050] pu, feasible True
  Substation set-point 1.050 pu; capacitors: bus 14 300 kvar, bus 24 450 kvar, bus 30 900 kvar; inverters: bus 18 100 kvar, bus 33 100 kvar

$ eln caps
Capacitor placement (33-bus): bus 13 400 kvar, bus 24 550 kvar, bus 30 1150 kvar
  Losses 202.7 → 132.2 kW (34.8 %), Vmin 0.9131 → 0.9370 pu

$ eln hosting
PV hosting capacity at bus 18 of case33bw: 2.09 MW (limited by Overvoltage)
```

### Baru di 1.1

```
$ eln pf --file ieee14_v35.raw
warning: Three-winding transformer 4-9-8 converted to a star equivalent with internal bus 15.
warning: Switched shunt at bus 9 modelled as a fixed shunt at its current value 19 Mvar.
IEEE 14 BUS TEST CASE (RAW V35): 14 buses, 20 branches, 5 generators, 11 loads (base 100 MVA)
NewtonRaphson: converged in 3 it, mismatch 3.44E-009, gen 272.393 MW, load 259 MW, losses 13.393 MW, Vmin 1.0100 pu @ bus 3

$ eln convert --case case9 --out case9.raw --version 35
Saved case9.raw

$ eln qsts --control
QSTS (33-bus, 96 steps of 15 min): converged 96/96
  Energy: load 55.97 MWh, PV 11.24 MWh, losses 1.406 MWh, import 46.13 MWh, export 0.00 MWh
  Voltage band: min 0.9617 pu, max 1.0501 pu, steps outside 0.95–1.05 pu: 1
  Controllers: OLTC T1 (4 operations), Cap bus 30 (2 operations)
  00:00  load  1.464 MW  PV  0.000 MW  Vmin 0.9617  losses   29.6 kW
  ...

$ eln dist
Unbalanced power flow: converged in 8 it (LadderSweep), source 3576.5 kW / 1719.9 kvar, losses 110.1 kW
  Regulator Reg taps: 10/8/11
  Max voltage unbalance (IEC): 2.05 %
  Bus          A [pu]          B [pu]          C [pu]        max |Δ| vs IEEE
  650      1.0000∠   0.00 1.0000∠-120.00 1.0000∠ 120.00    0.000 %
  ...
  675      0.9831∠  -5.55 1.0559∠-122.53 0.9772∠ 116.10    0.144 %
  634      0.9940∠  -3.23 1.0217∠-122.23 0.9962∠ 117.34    0.023 %

$ eln linecode --config 603
IEEE 13-node configuration 603 (2 phases): phase impedance matrix [Ω/mile]
   1.3238+j1.3569    0.2066+j0.4591
   0.2066+j0.4591    1.3294+j1.3471
```

Keluaran lengkap `dist`, `dss`, dan `linecode` ada di [Distribusi tak seimbang](distribusi.md#cli), penanganan RAW/CDF di
[Pertukaran model](pertukaran-model.md#cli), dan pengendali QSTS di [Simulasi, HPC & native](simulasi-hpc-native.md#kendali-tegangan-dalam-qsts-v11).

Keluaran lain (`pf`, `n1`, pemindaian `sc`, `modes`, `harmonic`, `ed`, `forecast`, `plf`, `qsts`, `ask`, `twin`) ada di panduan topik:
[aliran daya](aliran-daya.md), [hubung singkat](hubung-singkat-proteksi.md), [stabilitas](stabilitas.md), [harmonik](harmonik.md),
[optimasi](optimasi.md), [energi terbarukan & smart grid](energi-terbarukan-smartgrid.md), [simulasi & HPC](simulasi-hpc-native.md),
[AI & digital twin](ai-digital-twin.md).

## Tips

- `eln pf --json` mencetak JSON aliran daya yang sama dengan REST API (`NetworkJson`).
- `eln sld --literal` menulis warna literal (untuk penampil yang mengabaikan variabel CSS); `--dark` memilih palet gelap.
- `eln convert --case case14 --out case14.xml` mengekspor CIM; `--out grid.m` menulis MATPOWER; `--out grid.json` menulis JSON Electrical.Net.
  `--out grid.raw --version 33|35` menulis PSS/E RAW. Gabungkan dengan `--file` (termasuk `.raw`/`.cdf`) untuk mengonversi antar format,
  misalnya `eln convert --file ieee14.cdf --out ieee14.raw`.
- `eln rtu --port 1502` menyajikan pengukuran case14 lewat Modbus TCP sampai dihentikan (`--seconds N` untuk berhenti otomatis); arahkan templat
  `eln-scada` ke sana dengan `--rtu 127.0.0.1 --port 1502`.
- `eln ask` memakai LLM bila `ELECTRICAL_LLM_PROVIDER` (atau `OPENAI_API_KEY` / `HF_TOKEN`) diset; lihat [AI & digital twin](ai-digital-twin.md).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/cli.md)*
