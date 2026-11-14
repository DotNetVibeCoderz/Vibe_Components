# Aliran daya

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/power-flow.md)

Paket: `Electrical.PowerFlow` (bergantung pada `Electrical.Core`).

## Metode

| `PowerFlowMethod` | Algoritma | Kegunaan |
|---|---|---|
| `NewtonRaphson` (default) | Newton–Raphson polar, Jacobian sparse difaktorkan dengan `SparseLU`; perpindahan PV→PQ pada batas Q; beban ZIP | Jaringan transmisi dan distribusi umum |
| `FastDecoupled` | Fast-decoupled XB (B′/B″ konstan) | Penyelesaian berulang pada jaringan transmisi yang well-conditioned |
| `GaussSeidel` | Gauss–Seidel dengan faktor akselerasi 1,6 | Pengajaran, sistem kecil |
| `DC` | θ = B⁻¹P linear, tanpa rugi | Studi pasar, PTDF/LODF, penyaringan |
| `BackwardForwardSweep` | Sweep penjumlahan arus untuk penyulang radial | Penyulang distribusi (radial, satu sumber) |

```csharp
var net = TestCases.Case14();
foreach (var method in Enum.GetValues<PowerFlowMethod>().Where(m => m != PowerFlowMethod.BackwardForwardSweep))
{
    var r = PowerFlowSolver.Solve(net, new PowerFlowOptions { Method = method, Tolerance = 1e-8 });
    Console.WriteLine($"{method,-14} it={r.Iterations,3} losses={r.LossesMw:0.000} MW");
}

// Penyulang distribusi radial: backward/forward sweep
var feeder = PowerFlowSolver.Solve(TestCases.Case33BW(),
    new PowerFlowOptions { Method = PowerFlowMethod.BackwardForwardSweep });
Console.WriteLine($"{feeder.LossesMw * 1000:0.0} kW, Vmin {feeder.MinVoltageBus.Vm:0.0000} pu @ bus {feeder.MinVoltageBus.BusId}");
// 202.7 kW, Vmin 0.9131 pu @ bus 18
```

`PowerFlowSolver.Solve` memvalidasi jaringan lebih dulu (bus tak dikenal, impedansi nol, Pmin > Pmax) dan melempar
`InvalidOperationException` berisi daftar masalah. Bila tidak ada bus `Slack`, bus dengan kapasitas pembangkitan terbesar dijadikan slack.

## Opsi

```csharp
var r = PowerFlowSolver.Solve(net, new PowerFlowOptions
{
    Method = PowerFlowMethod.NewtonRaphson,
    EnforceQLimits = true,     // perpindahan PV → PQ pada batas Q generator
    FlatStart = false,         // mulai dari tegangan yang tersimpan di jaringan
    Tolerance = 1e-10,         // mismatch daya maksimum [pu]
    MaxIterations = 20,
});

foreach (var g in r.Generators.Where(g => g.AtQLimit))
    Console.WriteLine($"Generator di bus {g.Bus} mencapai batas Q ({g.Q:0.0} Mvar)");
foreach (var v in r.VoltageViolations())
    Console.WriteLine($"Bus {v.BusId}: {v.Vm:0.000} pu");
foreach (var br in r.Overloads(90))
    Console.WriteLine($"{br.From}-{br.To}: {br.LoadingPercent:0.0} %");

r.ApplyTo(net);   // warm start: tulis kembali V, θ, dan P/Q generator ke model
```

### Flat start (sudut dari aliran daya DC)

Dengan `FlatStart = true` (bawaan), Newton–Raphson mulai dari |V| = 1 pu di bus PQ dan nilai setelan di bus PV/slack, dan **sejak v1.1
sudutnya diambil dari aliran daya DC** (θ = B⁻¹P termasuk sudut phase shifter), bukan nol. Ini menjaga iterasi tetap di daerah tarik
solusi operasional bila jaringan memiliki pergeseran fasa besar atau transfer berat: phase shifter ideal 30° dahulu divergen dari θ = 0
dan yang 45° konvergen ke solusi palsu, sedangkan awal DC mencapai solusi yang benar dalam beberapa iterasi (uji regresi
`LargePhaseShift_FlatStartConvergesToOperationalSolution`). Kasus biasa juga hemat satu iterasi (case9 dan case14 kini konvergen dalam 3).
Awal DC dilewati secara otomatis, kembali ke θ = 0, untuk jaringan dengan cabang berreaktansi nol, beberapa pulau, atau solusi DC yang
tidak wajar. `FlatStart = false` tetap mulai dari tegangan yang tersimpan di jaringan (warm start, misalnya setelah `ApplyTo`).

## Hasil

`PowerFlowResult` berisi daya dalam MW/Mvar dan tegangan dalam pu/derajat:

- `Converged`, `Iterations`, `MaxMismatch`, `History` (mismatch per iterasi), `Elapsed`, `Voltages` kompleks.
- `Buses` (`BusResult`: `Vm`, `VaDeg`, `PGen`, `QGen`, `PLoad`, `QLoad`, `Type`, `Kv(baseKv)`).
- `Branches` (`BranchResult`: `PFrom`, `QFrom`, `PTo`, `QTo`, `PLoss`, `QLoss`, `SFrom`, `STo`, `LoadingPercent` terhadap `RateA`).
- `Generators` (`GeneratorResult`: `P`, `Q`, `AtQLimit`).
- Total: `TotalGenerationMw`, `TotalLoadMw`, `LossesMw`, `LossesMvar`; `MinVoltageBus`, `MaxVoltageBus`, `MostLoadedBranch`.
- `ToString()` memberi ringkasan satu baris.

Keluaran CLI untuk studi yang sama:

```
$ eln pf --case case14
case14: 14 buses, 20 branches, 5 generators, 11 loads (base 100 MVA)
NewtonRaphson: converged in 3 it, mismatch 3.44E-009, gen 272.393 MW, load 259 MW, losses 13.393 MW, Vmin 1.0100 pu @ bus 3

  Bus     Vm[pu]   Va[deg]    Pg[MW]   Qg[Mvar]    Pd[MW]   Qd[Mvar]
  1       1.0600     0.000    232.39     -16.55      0.00       0.00
  2       1.0450    -4.983     40.00      43.56     21.70      12.70
  ...
  14      1.0355   -16.034      0.00       0.00     14.90       5.00

  Losses: 13.393 MW / 30.122 Mvar · Vmin 1.0100 pu @ bus 3
  Voltage violations: 6 (1.070), 7 (1.062), 8 (1.090)
```

(Bus 6, 7, dan 8 ditandai karena tegangannya melebihi `Vmax` bus (1,06 pu) pada data MATPOWER case14.)

## Model beban

Setiap `Load` memiliki `ZipModel(Z, I, P)`; jumlah fraksinya harus 1. `ZipModel.ConstantPower` adalah default.

```csharp
var net = TestCases.Case33BW();
foreach (var load in net.Loads)
    load.Model = new ZipModel(Z: 0.3, I: 0.3, P: 0.4);
var r = PowerFlowSolver.Solve(net);
```

## Penyaringan kontingensi N-1

`ContingencyAnalysis.RunN1` melepas setiap cabang yang beroperasi satu per satu, menghitung ulang aliran daya AC (warm start dari kasus dasar,
secara paralel), lalu mengurutkan gangguan berdasarkan indeks keparahan (beban lebih di atas 100 %, tegangan di bawah 0,95 pu, jumlah
pelanggaran; pembentukan pulau dan divergensi bernilai tak hingga).

```csharp
var ranking = ContingencyAnalysis.RunN1(TestCases.Case14());   // paralel, terburuk lebih dulu
foreach (var c in ranking.Take(5))
    Console.WriteLine($"{c.Outage,-6} conv={c.Converged} island={c.Islanding} " +
                      $"max {c.MaxLoadingPercent:0.0} % Vmin {c.MinVoltage:0.000}@{c.MinVoltageBus} severity {c.Severity:0.0}");
```

```
$ eln n1 --case case14 --top 5
N-1 contingency screening on case14: 20 branch outages
  Outage        Conv  Island  MaxLoad%  MostLoaded   Vmin@bus        Severity
  7-8           NO    YES          0.0  -            0.000@0       ∞
  5-6           yes   -            0.0  -            1.010@3       60.0
  9-14          yes   -            0.0  -            0.997@14      50.0
  9-10          yes   -            0.0  -            1.010@3       40.0
  3-4           yes   -            0.0  -            1.010@3       30.0
```

Cabang 7-8 adalah satu-satunya jalur ke kondensor sinkron di bus 8, sehingga lepasnya membuat bus itu menjadi pulau. `MaxLoad%` bernilai 0
karena MATPOWER case14 tidak punya rating cabang; isi `Branch.RateA` (MVA) untuk mendapatkan pemeriksaan pembebanan.

## Sensitivitas (DC)

```csharp
var net = TestCases.Case14();
var ptdf = DcPowerFlow.Ptdf(net);           // [cabang, bus]: ΔMW di cabang per MW yang diinjeksikan di bus (diambil di slack)
var lodf = DcPowerFlow.Lodf(net, ptdf);     // [l, k]: bagian aliran cabang k yang pindah ke l saat k lepas
int bus14 = net.IndexOf(14);
Console.WriteLine($"Cabang 0 menanggung {ptdf[0, bus14]:P1} dari injeksi di bus 14");
```

Kolom LODF untuk gangguan radial (membentuk pulau) bernilai `NaN`; diagonalnya −1. `DcModel.Build(net)` memberi akses ke matriks B tereduksi
untuk perhitungan sudut/aliran sendiri (`Angles`, `BranchFlowsMw`). Tes memeriksa bahwa PTDF memprediksi perubahan aliran DC secara eksak.

## Validasi

| Kasus | Acuan | Electrical.Net |
|---|---|---|
| case9 (NR, FD, GS) | MATPOWER: P<sub>G1</sub> 71,64 MW, rugi 4,641 MW, V<sub>9</sub> 0,9956 pu | identik |
| case14 (NR) | MATPOWER: P<sub>G1</sub> 232,39 MW, rugi 13,393 MW, V<sub>14</sub> 1,036 ∠ −16,04° | identik |
| case33bw (NR dan sweep) | Baran & Wu: 202,7 kW, V<sub>min</sub> 0,9131 pu di bus 18 | identik |
| DC case9 | Slack = beban − pembangkitan tetap, rugi nol | eksak |
| Round trip MATPOWER | `Matpower.Parse(Matpower.Write(net))` | rugi sama hingga 1e-6 |
| PSS/E RAW v33/v35 dan IEEE CDF IEEE 14 bus ([pertukaran model](pertukaran-model.md)) | MATPOWER case14, rugi 13,393 MW | identik |
| Phase shifter ideal 30° / 45°, flat start | V<sub>3</sub> = V<sub>2</sub>∠−geser | konvergen dalam ≤ 6 it ke solusi operasional |

## Batasan

- Model urutan positif seimbang. Untuk penyulang tiga fasa tak seimbang (phase frame, geometri saluran Carson, regulator) gunakan
  [`Electrical.Distribution`](distribusi.md).
- Tidak ada kendali tap trafo/OLTC atau shunt terswitch dalam satu kali penyelesaian; loop kendali deret waktu (OLTC, regulator LDC,
  kapasitor terswitch) berjalan di [QSTS](simulasi-hpc-native.md#kendali-tegangan-dalam-qsts-v11), dan `Electrical.SmartGrid.VoltVarOptimization` mengoptimalkan nilai setelan.
- Backward/forward sweep membutuhkan jaringan radial dengan satu sumber.

Lihat juga: [Stabilitas](stabilitas.md) (kurva PV), [Optimasi](optimasi.md) (DC-OPF), [Simulasi, HPC & native](simulasi-hpc-native.md)
(aliran daya batch dan probabilistik).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/power-flow.md)*
