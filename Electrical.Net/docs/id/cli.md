# CLI (`eln`)

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/cli.md)

```bash
dotnet tool install -g Electrical.Cli
eln help
```

Dari sumber: `dotnet run --project tools/Electrical.Cli -- <perintah> [opsi]`.

**Input jaringan** (sebagian besar perintah): `--case case9 | case14 | case33bw | synthetic<N>` atau `--file grid.m | grid.json | grid.xml`
(MATPOWER, JSON Electrical.Net, CIM RDF/XML). Kasus default adalah `case14` kecuali disebutkan lain oleh perintahnya.

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
| `convert` | `--out grid.m\|grid.json\|grid.xml` | Konversi MATPOWER / JSON / CIM |
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
| `qsts` | `--days 1`, `--pv 1.5` | Deret waktu kuasi-statis (beban + PV 15 menit) |
| `ask` | `"<pertanyaan>"`, `--case case14` | Agen AI jaringan (LLM lewat `ELECTRICAL_LLM_*`, selain itu offline) |
| `twin` | `--steps 24`, `--lang en\|id` | Digital twin: SCADA → estimasi keadaan → alarm yang dijelaskan → kesehatan aset |

## Contoh keluaran

Semua keluaran di bawah ini diambil langsung dari tool (teks CLI dalam bahasa Inggris, kecuali `ask` dan `twin --lang id`).

```
$ eln info
Electrical.Net 1.0.0 — Comprehensive Electrical Engineering & Power System Framework for .NET
Framework Teknik Elektro & Sistem Tenaga Listrik Terlengkap untuk .NET
Made by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

Modules (23): Core, Circuit, PowerFlow, ShortCircuit, Stability, Harmonic, Protection, Renewable, SmartGrid, Optimization, StateEstimation, SCADA, IEC61850, CIM, DigitalTwin, Forecasting, AI, GIS, Visualization, Simulation, Cloud, HPC, Native
Native kernels: Rust library loaded (native (Rust))
Test cases: case9, case14, case33bw, synthetic

$ eln cases
  case9: 9 buses, 9 branches, 3 generators, 3 loads (base 100 MVA)
  case14: 14 buses, 20 branches, 5 generators, 11 loads (base 100 MVA)
  case33bw: 33 buses, 32 branches, 1 generators, 32 loads (base 10 MVA)
  synthetic100: 100 buses, 180 branches, 20 generators, 80 loads (base 100 MVA)

$ eln pf --case case9
NewtonRaphson: converged in 4 it, mismatch 1.85E-014, gen 319.641 MW, load 315 MW, losses 4.641 MW, Vmin 0.9956 pu @ bus 9

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

Keluaran lain (`pf`, `n1`, pemindaian `sc`, `modes`, `harmonic`, `ed`, `forecast`, `plf`, `qsts`, `ask`, `twin`) ada di panduan topik:
[aliran daya](aliran-daya.md), [hubung singkat](hubung-singkat-proteksi.md), [stabilitas](stabilitas.md), [harmonik](harmonik.md),
[optimasi](optimasi.md), [energi terbarukan & smart grid](energi-terbarukan-smartgrid.md), [simulasi & HPC](simulasi-hpc-native.md),
[AI & digital twin](ai-digital-twin.md).

## Tips

- `eln pf --json` mencetak JSON aliran daya yang sama dengan REST API (`NetworkJson`).
- `eln sld --literal` menulis warna literal (untuk penampil yang mengabaikan variabel CSS); `--dark` memilih palet gelap.
- `eln convert --case case14 --out case14.xml` mengekspor CIM; `--out grid.m` menulis MATPOWER; `--out grid.json` menulis JSON Electrical.Net.
  Gabungkan dengan `--file` untuk mengonversi antar format.
- `eln rtu --port 1502` menyajikan pengukuran case14 lewat Modbus TCP sampai dihentikan (`--seconds N` untuk berhenti otomatis); arahkan templat
  `eln-scada` ke sana dengan `--rtu 127.0.0.1 --port 1502`.
- `eln ask` memakai LLM bila `ELECTRICAL_LLM_PROVIDER` (atau `OPENAI_API_KEY` / `HF_TOKEN`) diset; lihat [AI & digital twin](ai-digital-twin.md).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/cli.md)*
