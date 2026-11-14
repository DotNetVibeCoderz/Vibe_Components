# Solver LP / MILP

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/solvers.md)

Baru di v1.2. Model linear dan mixed-integer ditulis sekali dengan `LinearModel` (paket `Electrical.Optimization`) dan diselesaikan oleh
`ILpSolver` / `IMilpSolver` mana pun. Electrical.Net menyertakan sepasang solver bawaan tanpa dependensi dan dua adapter opsional untuk solver
eksternal:

| Paket | Solver | Antarmuka | Kode native | AOT |
|---|---|---|---|---|
| `Electrical.Optimization` | `SimplexSolver` (dual simplex berbatas) dan `BranchAndBoundSolver` | `ILpSolver`, `IMilpSolver` + `ILpSolver` | tidak ada | ya |
| `Electrical.Optimization.Highs` | `HighsSolver` (HiGHS 1.16.0 melalui `Highs.Native`) | `IMilpSolver` + `ILpSolver` | HiGHS (`highs-native.*`) | tidak |
| `Electrical.Optimization.OrTools` | `OrToolsSolver` (Google OR-Tools 9.15.6755: SCIP, CBC, GLOP, PDLP …) | `IMilpSolver` + `ILpSolver` | runtime OR-Tools | tidak |

Setiap solver mengembalikan `LinearSolution` dengan konvensi yang sama, sehingga hasilnya (termasuk dual LP) dapat dipertukarkan. Unit
commitment dan kliring pasar di [Electrical.Markets](pasar-listrik.md) menerima solver melalui `ScucOptions.Solver`.

## Membangun model

`LinearModel` menampung variabel kontinu, integer, dan biner beserta batasnya, baris linear (`≤`, `=`, `≥`), dan fungsi objektif linear.
Ekspresi dibangun dengan operator (`3 * x + 2 * y`) atau bertahap dengan `LinearExpression.Add`.

```csharp
using Electrical.Optimization;

// "Wyndor Glass" Hillier & Lieberman: maks 3x + 5y, x ≤ 4, 2y ≤ 12, 3x + 2y ≤ 18
var m = new LinearModel { Name = "wyndor" };
var x = m.AddContinuous(name: "x");
var y = m.AddContinuous(name: "y");
var plant1 = m.AddConstraint(x, ConstraintSense.LessOrEqual, 4, "plant1");
var plant2 = m.AddConstraint(2 * y, ConstraintSense.LessOrEqual, 12, "plant2");
var plant3 = m.AddConstraint(3 * x + 2 * y, ConstraintSense.LessOrEqual, 18, "plant3");
m.SetObjective(3 * x + 5 * y, ObjectiveSense.Maximize);

var s = new SimplexSolver().SolveLp(m);
Console.WriteLine(s);
Console.WriteLine($"x = {s.Value(x)}, y = {s.Value(y)}, duals {s.Dual(plant1)} / {s.Dual(plant2)} / {s.Dual(plant3)}");
```

```
Electrical.Net dual simplex: Optimal, objective 36, gap 0.0000%, 0 nodes, 2 iterations
x = 2, y = 6, duals -0 / 1.5 / 1
```

Knapsack 0/1 sebagai MILP:

```csharp
double[] value = [10, 13, 7, 8, 11, 9], weight = [5, 7, 3, 4, 6, 4];
var m = new LinearModel { Name = "knapsack" };
var pick = value.Select((_, i) => m.AddBinary($"item{i}")).ToArray();
var load = new LinearExpression();
var profit = new LinearExpression();
for (int i = 0; i < pick.Length; i++) { load.Add(pick[i], weight[i]); profit.Add(pick[i], value[i]); }
m.AddConstraint(load, ConstraintSense.LessOrEqual, 15, "capacity");
m.SetObjective(profit, ObjectiveSense.Maximize);

IMilpSolver solver = new BranchAndBoundSolver();
var s = solver.Solve(m, new SolverOptions { RelativeGap = 1e-6, TimeLimit = TimeSpan.FromSeconds(10) });
Console.WriteLine($"{s.Status}: {s.Objective}, items {string.Join(",", pick.Where(p => s.Value(p) > 0.5).Select(p => p.Index))}, " +
                  $"{s.Nodes} nodes, bound {s.BestBound}");
// Optimal: 30, items 0,1,2, 39 nodes, bound 30.00003
```

Operasi model lainnya: `SetBounds`, `SetType`, `SetRhs`, `SetObjectiveCoefficient`, `Clone`, `FixIntegers` (LP dengan integer dikunci pada
suatu solusi, dipakai untuk penetapan harga), `EvaluateObjective`, dan `MaxViolation`.

## Konvensi solusi

`LinearSolution` memuat `Status` (`Optimal`, `Feasible`, `Infeasible`, `Unbounded`, `TimeLimit`, `IterationLimit`, `Error`), `Objective`,
`Values`, `BestBound`, `MipGap`, `Iterations`, `Nodes`, `SolveTime`, dan nama solver. Untuk LP juga ada `Duals` dan `ReducedCosts` dengan satu
konvensi yang sama untuk semua solver: **dual = ∂objektif/∂rhs** menurut arah objektif model itu sendiri. Pada minimisasi, baris `≤` yang
terikat memiliki dual ≤ 0 dan baris `≥` yang terikat dual ≥ 0. Adapter mengonversi dual HiGHS dan OR-Tools ke konvensi ini, sehingga model
pasar yang diberi harga dengan solver mana pun menghasilkan LMP yang sama.

`SolverOptions`: `TimeLimit`, `RelativeGap` (bawaan 1e-6), `Threads` (solver eksternal), `Verbose`, `MaxNodes` (bawaan).

## Solver bawaan

- **`SimplexSolver`**: dual simplex berbatas (satu variabel logis per baris yang membawa batas baris), invers basis padat eksplisit dengan
  pembaruan bentuk-produk dan faktorisasi ulang berkala, pricing dual steepest-edge, uji rasio dua langkah Harris, dan aturan Bland sebagai
  cadangan anti-siklus. Mengembalikan dual dan reduced cost. Divalidasi pada LP buku teks (Wyndor, arah kendala campuran, variabel bebas)
  dan terhadap HiGHS / OR-Tools.
- **`BranchAndBoundSolver`**: branch and bound berbasis LP yang memulai dual simplex dari basis induknya, menyelam depth-first untuk
  mendapatkan incumbent lebih awal lalu melanjutkan simpul terbuka dengan bound terbaik, bercabang dengan pseudo-cost (skor produk), dan
  memangkas terhadap incumbent. Mengimplementasikan `IMilpSolver` sekaligus `ILpSolver` (LP diteruskan ke simplex). Cek brute force:
  knapsack dan unit commitment 3 jam yang dienumerasi atas semua 512 komitmen.

Keduanya sepenuhnya managed, kompatibel AOT, dan deterministik. Keduanya adalah bawaan `AddMarkets()` dan `ScucOptions` bila tidak ada
solver yang diberikan.

## Solver eksternal: HiGHS dan OR-Tools

```bash
dotnet add package Electrical.Optimization.Highs      # HiGHS (MIT), pustaka native untuk win/linux/osx x64/arm64
dotnet add package Electrical.Optimization.OrTools    # Google OR-Tools (Apache-2.0): SCIP, CBC, GLOP, PDLP, ...
```

```csharp
using Electrical.Optimization.Highs;
using Electrical.Optimization.OrTools;

foreach (ILpSolver lp in new ILpSolver[] { new SimplexSolver(), new HighsSolver(), new OrToolsSolver() })
{
    var s = lp.SolveLp(m);
    Console.WriteLine($"{lp.Name,-32} {s.Status} z = {s.Objective} dual(plant3) = {s.Dual(plant3):0.###}");
}
```

```
Electrical.Net dual simplex      Optimal z = 36 dual(plant3) = 1
HiGHS                            Optimal z = 36 dual(plant3) = 1
OR-Tools (SCIP/GLOP)             Optimal z = 36 dual(plant3) = 1
```

- `HighsSolver { LpAlgorithm = "simplex" | "ipm" | "pdlp" | "choose" }`: LP dengan dual simplex HiGHS secara bawaan, MILP dengan branch and
  cut-nya.
- `OrToolsSolver { MipBackend = "SCIP" | "CBC" | "SAT", LpBackend = "GLOP" | "PDLP" | "CLP" }`: MILP dengan SCIP secara bawaan, LP dengan GLOP
  (dual dari `DualValue()`).

Unit commitment Wood & Wollenberg 24 jam yang sama (72 biner, biaya 4 segmen, cadangan 5 %) dengan setiap solver:

```csharp
var units = UnitCommitment.WoodWollenbergThreeUnits().Select(u => ScucUnit.FromUcUnit(u)).ToList();
foreach (IMilpSolver solver in new IMilpSolver[] { new BranchAndBoundSolver(), new HighsSolver(), new OrToolsSolver { MipBackend = "SCIP" } })
{
    var r = SecurityConstrainedUnitCommitment.Solve(units, demand, new ScucOptions { ReserveFraction = 0.05, Solver = solver });
    Console.WriteLine($"{solver.Name,-32} {r.Status} {r.Objective:0.00} $ in {r.MilpSolution.SolveTime.TotalMilliseconds:0} ms");
}
```

```
Electrical.Net branch and bound  Optimal 183728.14 $ in 1306 ms
HiGHS                            Optimal 183728.14 $ in 119 ms
OR-Tools (SCIP/GLOP)             Optimal 183728.14 $ in 92 ms
```

(Waktu dari satu kali jalan di PC desktop; rangkaian uji memastikan objektif dan LMP yang sama untuk ketiga solver.)

### Dependency injection

`AddMarkets()` mendaftarkan `BranchAndBoundSolver` bawaan sebagai `IMilpSolver` dan `SimplexSolver` sebagai `ILpSolver` kecuali sudah ada
solver yang terdaftar. `AddHighs()` / `AddOrTools()` menggantinya, sebelum atau sesudah `AddMarkets()`:

```csharp
using var sp = new ServiceCollection()
    .AddElectrical(b => b.AddMarkets().AddHighs())            // atau .AddOrTools("SCIP", "GLOP"); hilangkan untuk solver bawaan
    .BuildServiceProvider();
var market = sp.GetRequiredService<IMarketService>();
Console.WriteLine(market.Solver.Name);                        // HiGHS
```

## Solver mana yang dipakai

| Situasi | Rekomendasi |
|---|---|
| LP (penetapan harga mirip DC-OPF, masalah dispatch kecil), pengajaran, aplikasi AOT / trimmed / WebAssembly, biner native tidak diizinkan | `SimplexSolver` bawaan |
| MILP kecil: beberapa unit selama sehari, kliring pasar satu periode, studi yang harus berjalan di mana saja | `BranchAndBoundSolver` bawaan |
| SCUC day-ahead dengan jaringan, baris keamanan N-1, puluhan unit atau lebih, gap MIP ketat, batas waktu penting | `HighsSolver` (satu pustaka native, lisensi MIT, LP dan MIP cepat) |
| Sudah memakai OR-Tools, ingin SCIP/CBC atau PDLP untuk LP sangat besar | `OrToolsSolver` |

**Solver MILP bawaan ditujukan untuk model kecil dan pengajaran.** Solver ini tidak memiliki cutting plane atau presolve, dan relaksasi LP
unit commitment lemah, sehingga waktu jalannya naik cepat seiring jumlah biner dan baris jaringan/kontingensi. Pada SCUC case9 24 jam dari
`eln scuc` (unit dari generator jaringan, cadangan 10 %) solver ini berhenti pada gap 18,6 % setelah 20 detik, sedangkan HiGHS membuktikan
optimum 92 701,61 $ dalam sekitar satu detik. **Gunakan HiGHS atau OR-Tools untuk SCUC dan pasar day-ahead yang sesungguhnya**; `eln scuc`
memakai HiGHS secara bawaan (`--solver builtin` untuk membandingkan). Karena ketiganya berbagi `LinearModel` dan konvensi dual, Anda dapat
mengembangkan dengan solver bawaan lalu berpindah dengan satu baris.

## Catatan pustaka native

- `Electrical.Optimization.Highs` dan `Electrical.Optimization.OrTools` **tidak kompatibel AOT** (`IsAotCompatible=false`) dan membawa biner
  native; paket inti `Electrical.Optimization` dan `Electrical.Markets` tetap sepenuhnya managed.
- `Highs.Native` dan `Google.OrTools.runtime.*` sama-sama mengirim berkas `runtimes/<rid>/native/highs.dll` (build HiGHS berbeda dengan ABI
  berbeda). Dalam satu folder keluaran keduanya saling menimpa dan salah satu solver gagal dimuat. Karena itu adapter HiGHS mengecualikan aset
  native paket tersebut, mengirimnya ulang sebagai `runtimes/<rid>/native/highs-native.{dll,so,dylib}`, dan memuatnya dengan resolver
  `DllImport`. Kedua adapter dapat dirujuk oleh aplikasi yang sama tanpa konfigurasi tambahan.
- RID yang didukung: win-x64/x86/arm64, linux-x64/arm64, osx-x64/arm64 (HiGHS); platform paket runtime Google.OrTools.

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/solvers.md)*
