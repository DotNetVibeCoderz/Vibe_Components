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
| `acopf` | `--limit 5-6:42` (MVA), `--no-limits` | Aliran daya optimal AC (titik interior): biaya vs DC-OPF, dispatch P/Q, dekomposisi LMP, kendala terikat (v1.2) |
| `scuc` | `--limit 8-9:57.5` (MW), `--reserve 0.1`, `--solver highs\|builtin`, `--gap 1e-4`, `--time 30` | Unit commitment dengan kendala keamanan 24 jam (MILP, HiGHS secara bawaan) dengan LMP per jam (v1.2) |
| `market` | `--limit 8-9:60` (MW) | Kliring pasar satu periode dari penawaran jual dan beli: LMP, penyelesaian, sewa kongesti, pembayaran FTR (v1.2) |
| `dyn` | `--case kundur\|case9`, `--exc const\|st\|pss`, `--fault 8`, `--clear 0.1`, `--trip 7-8`, `--end 10`, `--modes` | Dinamika rinci (GENROU, eksiter, PSS): simulasi gangguan dan mode elektromekanis (v1.3) |
| `emt` | `--case line\|capbank`, `--km 150`, `--kv 230`, `--dt 50e-6` | Transien elektromagnetik: tegangan lebih energisasi saluran atau inrush bank kapasitor (v1.3) |

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

### Baru di 1.2

`acopf`, `scuc`, dan `market` memakai `case9` secara bawaan (`--case`/`--file` berlaku seperti perintah lain). `scuc` diselesaikan dengan
HiGHS kecuali diberi `--solver builtin`: branch and bound bawaan lemah pada relaksasi unit commitment (case9 24 jam: berhenti pada gap
18,6 % setelah 20 detik, sedangkan HiGHS membuktikan optimum 92 701,61 $ dalam sekitar satu detik), lihat
[Solver LP / MILP](solver.md#solver-mana-yang-dipakai). `--gap` adalah gap MIP relatif dan `--time` batas waktu dalam detik. Tabel panjang
dipersingkat dengan `…` di bawah.

```
$ eln acopf
AC-OPF on case9: optimal in 11 it, cost 5296.69 $/h (DC-OPF 5216.03 $/h)
  Gen 1: P    89.80 MW  Q    12.94 Mvar
  Gen 2: P   134.32 MW  Q     0.05 Mvar
  Gen 3: P    94.19 MW  Q   -22.62 Mvar
  Bus    Vm[pu]   LMP[$/MWh]  energy    loss  congestion  voltage
  1      1.1000      24.756   24.756   0.000       0.000    0.000
  2      1.0974      24.035   24.756  -0.641      -0.000   -0.080
  3      1.0866      24.076   24.756  -0.623      -0.000   -0.057
  4      1.0942      24.756   24.756  -0.039      -0.000    0.040
  5      1.0844      24.998   24.756   0.178      -0.000    0.064
  6      1.1000      24.076   24.756  -0.662      -0.000   -0.018
  7      1.0895      24.254   24.756  -0.512      -0.000    0.010
  8      1.1000      24.035   24.756  -0.699      -0.000   -0.022
  9      1.0717      24.999   24.756   0.166      -0.000    0.077
  Binding: Bus 1 at Vmax (shadow price 8.384)
  Binding: Bus 6 at Vmax (shadow price 75.329)
  Binding: Bus 8 at Vmax (shadow price 77.457)

$ eln acopf --limit 5-6:42
AC-OPF on case9: optimal in 12 it, cost 5472.70 $/h (DC-OPF 5338.40 $/h)
  Gen 1: P   116.38 MW  Q    15.39 Mvar
  Gen 2: P   130.03 MW  Q    71.79 Mvar
  Gen 3: P    72.83 MW  Q   -37.37 Mvar
  Bus    Vm[pu]   LMP[$/MWh]  energy    loss  congestion  voltage
  1      0.9327      30.604   30.604   0.000       0.000    0.000
  2      1.0246      23.305   30.604  -0.607      -6.693    0.001
  3      0.9001      18.843   30.604  -0.590     -11.172    0.000
  4      0.9260      30.604   30.604  -0.100       0.101   -0.001
  5      0.9061      33.818   30.604   0.433       2.782   -0.002
  6      0.9256      18.843   30.604  -0.665     -11.096   -0.001
  7      0.9450      21.752   30.604  -0.491      -8.360   -0.001
  8      0.9840      23.305   30.604  -0.778      -6.520   -0.000
  9      0.9130      28.731   30.604   0.322      -2.193   -0.002
  Binding: Bus 3 at Vmin (shadow price 2.484)
  Binding: Branch 5-6 (from end) (shadow price 7.949)
  Binding: Branch 5-6 (to end) (shadow price 12.344)

$ eln acopf --case case14
AC-OPF on case14: optimal in 11 it, cost 8081.53 $/h (DC-OPF 7642.59 $/h)
  Gen 1: P   194.33 MW  Q     0.00 Mvar
  Gen 2: P    36.72 MW  Q    23.69 Mvar
  Gen 3: P    28.74 MW  Q    24.13 Mvar
  Gen 4: P     0.00 MW  Q    11.55 Mvar
  Gen 5: P     8.50 MW  Q     8.27 Mvar
  Bus    Vm[pu]   LMP[$/MWh]  energy    loss  congestion  voltage
  1      1.0600      36.724   36.724   0.000       0.000    0.000
  2      1.0408      38.360   36.724   1.014      -0.000    0.622
  …                                                    (buses 3–13)
  14     1.0239      41.197   36.724   2.133      -0.000    2.341
  Binding: G1 at Qmin (shadow price 0.094)
  Binding: G4 at Pmin (shadow price 0.266)
  Binding: Bus 1 at Vmax (shadow price 583.779)
  Binding: Bus 6 at Vmax (shadow price 55.184)
  Binding: Bus 8 at Vmax (shadow price 71.069)

$ eln scuc
SCUC on case9 (24 h, HiGHS): Optimal, total cost 92701.61 $ (production 92701.61, startup 0.00), gap 0.000 %
  Hour  Demand    U1[MW]   U2[MW]   U3[MW]   λ[$/MWh]  LMP min..max
     0   189.0      31.5     82.5     75.0      13.80  13.80..13.80
  …
    14   315.0      85.0    155.0     75.0      27.00  27.00..27.00
  …

$ eln scuc --limit 8-9:57.5
SCUC on case9 (24 h, HiGHS): Optimal, total cost 94259.28 $ (production 94259.28, startup 0.00), gap 0.000 %
  Hour  Demand    U1[MW]   U2[MW]   U3[MW]   λ[$/MWh]  LMP min..max
     0   189.0      31.5     82.5     75.0      13.80  13.80..13.80
  …
     8   237.2      70.0     92.2     75.0      21.39  21.39..21.39
     9   259.0      75.0    109.0     75.0      27.00  21.39..28.10
  …
    14   315.0     117.1    122.9     75.0      27.00  21.39..28.10
  …
    20   237.2      70.0     92.2     75.0      21.39  21.39..21.39
  …
  Binding: Flow hour 9 branch 7 (57.5/57.5 MW, μ 8.79)
  Binding: Flow hour 10 branch 7 (57.5/57.5 MW, μ 8.79)
  Binding: Flow hour 11 branch 7 (57.5/57.5 MW, μ 8.79)
  Binding: Flow hour 12 branch 7 (57.5/57.5 MW, μ 8.79)
  Binding: Flow hour 13 branch 7 (57.5/57.5 MW, μ 8.79)

$ eln market
Market clearing on case9: Optimal, system λ 26.70 $/MWh
  G1   bus 1:    83.3 MW, revenue   2225.00 $
  G2   bus 2:   141.7 MW, revenue   3782.50 $
  G3   bus 3:    90.0 MW, revenue   2403.00 $
  Load 5  :    90.0 MW, pays   2403.00 $
  Load 7  :   100.0 MW, pays   2670.00 $
  Load 9  :   125.0 MW, pays   3337.50 $
  LMP [$/MWh]: 1:26.70  2:26.70  3:26.70  4:26.70  5:26.70  6:26.70  7:26.70  8:26.70  9:26.70
  Congestion rent 0.00 $ (Σ μ·flow 0.00 $), consumer surplus (elastic bids) 1048.95 $, producer surplus 4065.37 $

$ eln market --limit 8-9:60
Market clearing on case9: Optimal, system λ 32.50 $/MWh
  G1   bus 1:   107.2 MW, revenue   3485.23 $
  G2   bus 2:   117.8 MW, revenue   3144.25 $
  G3   bus 3:    90.0 MW, revenue   2610.46 $
  Load 5  :    90.0 MW, pays   2814.55 $
  Load 7  :   100.0 MW, pays   2766.04 $
  Load 9  :   125.0 MW, pays   4204.23 $
  LMP [$/MWh]: 1:32.50  2:26.70  3:29.01  4:32.50  5:31.27  6:29.01  7:27.66  8:26.70  9:33.63
  Binding: Flow branch 7 (60.0/60.0 MW, μ 9.08 $/MWh)
  Congestion rent 544.89 $ (Σ μ·flow 544.89 $), consumer surplus (elastic bids) 911.52 $, producer surplus 4756.16 $
  FTR 50 MW 1→9 pays 56.69 $
```

Panduan: [Optimasi](optimasi.md#aliran-daya-optimal-ac-v12), [Pasar listrik](pasar-listrik.md).

### Baru di 1.3

`dyn` menjalankan model dinamis rinci ([Dinamika sistem tenaga](dinamika.md)): `--case kundur` (bawaan, Kundur dua area) atau `case9`
(GENSAL/GENROU + IEEET1), `--exc const|st|pss` memilih eksitasi Kundur (tegangan medan konstan, ESST1A tanpa PSS, ESST1A dengan PSS;
bawaan `pss`), `--fault <bus>` dan `--clear <s>` mengatur gangguan tiga fasa pada t = 1 s, `--trip f-t` membuka cabang saat gangguan
dibersihkan, `--end` adalah waktu simulasi, dan `--modes` hanya mencetak mode sinyal kecil. `emt` menjalankan studi transien
elektromagnetik ([EMT](transien-elektromagnetik.md)): `--case line` (bawaan; `--km`, `--kv`) memberi energi saluran berujung terbuka,
`--case capbank` (`--kv 11`, `--mvar 5`) memberi energi bank kapasitor; `--dt` adalah langkah waktu. Waktu eksekusi pertama termasuk JIT.

```
$ eln emt
Line energization: 230 kV, 150 km open-ended line, breaker closes at 5 ms (Δt 50 µs, 1201 steps, native (Rust), 136 ms)
  Open end phase a: peak 1.17 pu at 9.60 ms
  Open end phase b: peak 2.23 pu at 6.60 ms
  Open end phase c: peak 2.15 pu at 12.90 ms

$ eln emt --case capbank
Capacitor bank energization: 5 Mvar at 11 kV, S"k 250 MVA (Δt 50 µs, native (Rust))
  Inrush peak 3043 A = 8.2 × rated peak; ring frequency ≈ 354 Hz
  Bus overvoltage peak 1.97 pu

$ eln dyn --exc const --modes
kundur2area: Kundur two-area system, ConstantField
Electromechanical modes (stable):
   0.542 Hz  ζ =   3.8 %  λ = -0.1289 ± j3.4074  G3.GENROU.dw 0.24, G3.GENROU.delta 0.24, G4.GENROU.dw 0.16
   1.077 Hz  ζ =   9.0 %  λ = -0.6116 ± j6.7639  G2.GENROU.dw 0.26, G2.GENROU.delta 0.25, G1.GENROU.dw 0.20
   1.112 Hz  ζ =   8.8 %  λ = -0.6182 ± j6.9867  G4.GENROU.dw 0.27, G4.GENROU.delta 0.27, G3.GENROU.dw 0.18

$ eln dyn --exc st --modes
kundur2area: Kundur two-area system, StaticExciter
Electromechanical modes (stable):
   0.607 Hz  ζ =   0.0 %  λ = -0.0004 ± j3.8155  G3.GENROU.delta 0.17, G3.GENROU.dw 0.17, G4.GENROU.delta 0.13
   1.130 Hz  ζ =   9.3 %  λ = -0.6644 ± j7.0981  G2.GENROU.dw 0.23, G2.GENROU.delta 0.23, G1.GENROU.dw 0.20
   1.165 Hz  ζ =   9.1 %  λ = -0.6712 ± j7.3191  G4.GENROU.dw 0.24, G4.GENROU.delta 0.24, G3.GENROU.dw 0.19

$ eln dyn
kundur2area: Kundur two-area system, StaticExciterWithPss
Electromechanical modes (stable):
   0.609 Hz  ζ =  18.6 %  λ = -0.7257 ± j3.8257  G3.GENROU.dw 0.16, G3.GENROU.delta 0.16, G4.GENROU.dw 0.12
   1.297 Hz  ζ =  24.3 %  λ = -2.0408 ± j8.1499  G2.GENROU.dw 0.22, G1.GENROU.dw 0.18, G2.GENROU.delta 0.17
   1.351 Hz  ζ =  24.4 %  λ = -2.1323 ± j8.4860  G4.GENROU.dw 0.23, G4.GENROU.delta 0.17, G3.GENROU.dw 0.17
Three-phase fault at bus 8 (t = 1 s), cleared after 100 ms: STABLE, 1200 steps in 127 ms
  Unit   δ(COI) max swing   speed range [pu]        V term min [pu]   Efd max [pu]
  G1             3.82°   1.00000–1.00342          0.939          9.000
  G2             4.05°   1.00000–1.00424          0.855          9.000
  G3             3.94°   0.99997–1.00274          0.919          9.000
  G4             3.30°   0.99998–1.00312          0.828          9.000

$ eln dyn --case case9 --fault 7 --clear 0.083 --trip 8-9
case9: WSCC 9-bus, GENSAL/GENROU + IEEET1
Electromechanical modes (stable):
   1.331 Hz  ζ =   4.9 %  λ = -0.4100 ± j8.3598  G2.GENROU.dw 0.29, G2.GENROU.delta 0.29, G1.GENSAL.dw 0.14
   2.098 Hz  ζ =   8.5 %  λ = -1.1198 ± j13.1812  G3.GENROU.dw 0.37, G3.GENROU.delta 0.37, G2.GENROU.dw 0.09
Three-phase fault at bus 7 (t = 1 s), cleared after 83 ms by tripping 8-9: STABLE, 1201 steps in 185 ms
  Unit   δ(COI) max swing   speed range [pu]        V term min [pu]   Efd max [pu]
  G1            26.48°   1.00000–1.01674          0.874          1.723
  G2            77.56°   1.00000–1.01792          0.516          3.182
  G3            45.61°   1.00000–1.01627          0.483          3.136
```

Panduan: [Dinamika sistem tenaga](dinamika.md#cli), [Transien elektromagnetik](transien-elektromagnetik.md#cli).

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
