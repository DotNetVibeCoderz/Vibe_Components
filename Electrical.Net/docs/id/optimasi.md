# Optimasi

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/optimization.md)

Paket: `Electrical.Optimization` (bergantung pada PowerFlow). Biaya berbentuk polinomial `C(P) = C2·P² + C1·P + C0` [$/jam] dengan P dalam MW
(`PolynomialCost` di setiap `Generator`).

Baru di v1.2: [aliran daya optimal AC](#aliran-daya-optimal-ac-v12) dengan titik interior primal-dual, [dekomposisi LMP](#dekomposisi-lmp)
menjadi komponen energi, rugi, kongesti, dan tegangan, titik awal DC-OPF yang lebih andal, serta lapisan LP/MILP `LinearModel` dengan solver
bawaan dan eksternal ([Solver LP / MILP](solver.md)). Unit commitment dengan kendala keamanan dan kliring pasar ada di paket baru
[Electrical.Markets](pasar-listrik.md).

## Economic dispatch

Dispatch biaya inkremental sama (iterasi λ) dengan batas unit; rugi koefisien-B opsional.

```csharp
var net = TestCases.Case9();
var ed = EconomicDispatch.Solve(net);                      // tanpa rugi, biaya inkremental sama
Console.WriteLine($"λ = {ed.Lambda:0.000} $/MWh, P = {string.Join(" / ", ed.OutputMw.Select(p => p.ToString("0.00")))} MW");
// λ = 24.044 $/MWh, P = 86.56 / 134.38 / 94.06 MW

var lossy = EconomicDispatch.SolveWithLosses(net);        // koefisien B dari aliran daya AC, diiterasi
var b = LossCoefficients.FromPowerFlow(PowerFlowSolver.Solve(net));

var units = new List<DispatchUnit>
{
    new("Coal", new PolynomialCost(0.0016, 7.92, 561), 150, 600),
    new("Gas",  new PolynomialCost(0.0048, 7.97, 78),   50, 200),
};
var r = EconomicDispatch.Solve(units, demandMw: 700);
```

```
$ eln ed --case case9
Economic dispatch on case9: λ = 24.044 $/MWh, cost 5216.03 $/h, losses 0 MW
$ eln ed --case case9 --losses
Economic dispatch on case9: λ = 24.867 $/MWh, cost 5309.38 $/h, losses 3.816 MW
```

`LossCoefficients.FromPowerFlow` menurunkan rumus rugi Kron pada titik operasi (eksak di titik itu, sebagaimana diuji). Sebagai pembanding,
optimum AC-OPF MATPOWER untuk case9 adalah 5296,69 $/jam; dispatch dengan rugi-B berada dalam 0,5 % darinya.

## DC optimal power flow dan LMP

`DcOpf.Solve` adalah DC-OPF formulasi PTDF dengan biaya kuadratik, batas generator, dan batas cabang, diselesaikan dengan `QpSolver`
interior-point. LMP diperoleh dari variabel dual: LMP<sub>i</sub> = λ − Σ μ<sub>c</sub>·PTDF<sub>c,i</sub>.

```csharp
var net = TestCases.Case9();
int line = net.Branches.FindIndex(b => (b.From == 5 && b.To == 6) || (b.From == 6 && b.To == 5));
var opf = DcOpf.Solve(net, new DcOpfOptions { RatingOverrides = new Dictionary<int, double> { [line] = 60 } });
Console.WriteLine($"Biaya {opf.TotalCost:0.00} $/jam, λ {opf.Lambda:0.00} $/MWh");
foreach (var id in opf.BusIds) Console.WriteLine($"LMP bus {id}: {opf.LmpAt(id):0.00} $/MWh");
foreach (var c in opf.Binding) Console.WriteLine($"{c.Kind} {c.Description}: μ = {c.ShadowPrice:0.00} $/MWh");

var scopf = DcOpf.SolveSecurityConstrained(net, contingencies: [0, 3, 6], emergencyRatingFactor: 1.2);
```

- Tanpa kongesti, DC-OPF sama dengan economic dispatch tanpa rugi (5216,03 $/jam, LMP seragam 24,04 $/MWh pada case9).
- Saat terjadi kongesti, LMP terpisah; tes memeriksa bahwa LMP di sebuah bus sama dengan biaya marginal beda-hingga untuk melayani ±0,5 MW di
  sana.
- `DcOpfResult` juga melaporkan `FlowsMw`, `DispatchMw`, kendala `Binding` ("Flow", "ContingencyFlow", "Pmax", "Pmin"), dan `CongestionRent`.
- Mode dengan kendala keamanan menambahkan batas pasca-kontingensi f<sub>l</sub> + LODF<sub>l,k</sub>·f<sub>k</sub> ≤ rating darurat untuk setiap
  gangguan yang didaftarkan; tes memverifikasi setiap gangguan dengan aliran daya DC pasca-kontingensi.

- **Perbaikan keandalan v1.2**: `QpSolver` titik interior dulu dimulai dari x = 0, yang macet pada jaringan lebih besar (`SyntheticGrid(300)`:
  100 iterasi dan 205 062 $/jam alih-alih optimum dispatch ekonomis 186 364 $/jam). DC-OPF kini dimulai dari dispatch proporsional Pmax yang
  memenuhi keseimbangan daya, dengan slack dan dual pada jalur pusat; jaringan 300 bus konvergen dalam 8 iterasi ke biaya dispatch ekonomis
  (uji regresi untuk 100 dan 300 bus). `QpSolver.Solve(qp, initialX: ...)` menerima titik awal Anda sendiri.

CLI: `eln opf --case case9 [--limit 5-6:60]` (batas override dalam MW).

## Aliran daya optimal AC (v1.2)

`AcOpf.Solve` adalah AC-OPF polar dari `runopf` MATPOWER: minimalkan Σ C<sub>i</sub>(P<sub>g,i</sub>) [$/jam] dengan kendala keseimbangan daya AC
penuh (P dan Q) di setiap bus, batas tegangan bus, batas P/Q generator, dan batas daya semu cabang |S| ≤ RateA di kedua ujung (bentuk
kuadrat). Beban berupa daya konstan. Diselesaikan oleh `InteriorPointSolver`, metode titik interior primal-dual dengan Hessian eksak (gaya
MIPS MATPOWER) dan faktorisasi KKT sparse yang urutan pengurangan fill-in-nya dipakai ulang antar-iterasi.

```csharp
using Electrical.Core.Network;
using Electrical.Optimization;

var net = TestCases.Case9();
var opf = AcOpf.Solve(net);                                   // formulasi runopf MATPOWER, flat start
Console.WriteLine(opf);
Console.WriteLine($"Pg = {string.Join(" / ", opf.PgMw.Select(p => p.ToString("0.00")))} MW");
foreach (var b in opf.Binding) Console.WriteLine($"{b.Kind,-5} {b.Description}: {b.ShadowPrice:0.000}");
```

```
AC-OPF: converged in 11 it, cost 5296.69 $/h, losses 3.307 MW, LMP 24.03–25 $/MWh
Pg = 89.80 / 134.32 / 94.19 MW
Vmax  Bus 1 at Vmax: 8.384
Vmax  Bus 6 at Vmax: 75.329
Vmax  Bus 8 at Vmax: 77.457
```

Biaya dan dispatch sama dengan MATPOWER (5296,69 $/jam; 89,80 / 134,32 / 94,19 MW). DC-OPF untuk kasus yang sama berbiaya 5216,03 $/jam:
optimum AC membayar rugi 3,31 MW dan mematuhi batas tegangan (bus 1, 6, dan 8 pada |V| = 1,1 pu).

`AcOpfOptions`: `Tolerance` (1e-6), `MaxIterations` (150), `Start` (`Flat` seperti MATPOWER, `PowerFlow` warm start dari Newton–Raphson, atau
`Network` untuk set-point tersimpan), `EnforceLineLimits`, `RatingFactor`, `RatingOverrides` [MVA per indeks cabang], `StepControl`,
`CostScaling`, `BindingThreshold`.

`AcOpfResult` memuat `TotalCost` [$/jam], `PgMw`, `QgMvar`, `Vm` [pu], `VaDeg`, `Lmp` [$/MWh] (pengali keseimbangan P), `LmpQ` [$/Mvarh],
`Branches` (aliran di kedua ujung, rating yang diberlakukan), harga bayangan cabang (`BranchShadowPriceFrom/To`, $/MVAh), harga bayangan
tegangan (`VmaxShadowPrice`/`VminShadowPrice`), harga bayangan batas generator (`Pmax/Pmin/Qmax/QminShadowPrice`), `Binding` (jenis "Flow",
"Vmax", "Vmin", "Pmax", "Pmin", "Qmax", "Qmin"), `LossesMw`, `MaxMismatchMw`, `Iterations`, `Status`, serta hasil `Solver` mentah dengan
`History` konvergensinya.

### Kongesti, dekomposisi, dan diagram segaris

```csharp
var net = TestCases.Case9();
int line = net.Branches.FindIndex(b => b.From == 5 && b.To == 6);   // index 2
var opf = AcOpf.Solve(net, new AcOpfOptions { RatingOverrides = new Dictionary<int, double> { [line] = 42 } });  // MVA
var lmp = opf.Decompose();                                    // bus referensi = slack OPF (bus 1)
for (int i = 0; i < lmp.BusIds.Length; i++)
    Console.WriteLine($"bus {lmp.BusIds[i]}: {lmp.Lmp[i]:0.00} = {lmp.Energy[i]:0.00} + {lmp.Loss[i]:0.00} (loss) " +
                      $"+ {lmp.Congestion[i]:0.00} (congestion) + {lmp.Voltage[i]:0.00} (voltage)");
Console.WriteLine($"shadow price 5-6: {opf.BranchShadowPriceFrom[line]:0.00} / {opf.BranchShadowPriceTo[line]:0.00} $/MVAh");

var pf = opf.ToPowerFlowResult();                             // tampilan PowerFlowResult dari titik optimum
string svg = SldRenderer.Render(net, pf);                     // using Electrical.Visualization;
Console.WriteLine($"losses {pf.LossesMw:0.000} MW, svg {svg.Length} chars");
```

```
bus 1: 30.60 = 30.60 + 0.00 (loss) + 0.00 (congestion) + 0.00 (voltage)
bus 2: 23.31 = 30.60 + -0.61 (loss) + -6.69 (congestion) + 0.00 (voltage)
bus 3: 18.84 = 30.60 + -0.59 (loss) + -11.17 (congestion) + 0.00 (voltage)
bus 4: 30.60 = 30.60 + -0.10 (loss) + 0.10 (congestion) + -0.00 (voltage)
bus 5: 33.82 = 30.60 + 0.43 (loss) + 2.78 (congestion) + -0.00 (voltage)
bus 6: 18.84 = 30.60 + -0.67 (loss) + -11.10 (congestion) + -0.00 (voltage)
bus 7: 21.75 = 30.60 + -0.49 (loss) + -8.36 (congestion) + -0.00 (voltage)
bus 8: 23.31 = 30.60 + -0.78 (loss) + -6.52 (congestion) + -0.00 (voltage)
bus 9: 28.73 = 30.60 + 0.32 (loss) + -2.19 (congestion) + -0.00 (voltage)
shadow price 5-6: 7.95 / 12.34 $/MVAh
losses 4.244 MW, svg 11689 chars
```

Menahan saluran 5–6 pada 42 MVA menaikkan biaya menjadi 5472,70 $/jam dan memisahkan harga dari 18,84 hingga 33,82 $/MWh; kongesti mendominasi
selisih tersebut, sedangkan rugi menggeser harga kurang dari 1 $/MWh. `ToPowerFlowResult()` mengubah titik optimum menjadi `PowerFlowResult`
biasa (tegangan bus, aliran cabang, keluaran generator), sehingga diagram segaris, laporan, dan warm start `ApplyTo` bekerja tanpa perubahan.
Demo galeri `acopf` menggambarnya.

AC vs DC pada case14 (MATPOWER: 8081,53 $/jam, λ<sub>P</sub>(bus 1) = 36,724 $/MWh), dengan warm start aliran daya:

```csharp
var net = TestCases.Case14();
var ac = AcOpf.Solve(net, new AcOpfOptions { Start = AcOpfStart.PowerFlow, Tolerance = 1e-8 });
var dc = DcOpf.Solve(net);
Console.WriteLine($"AC {ac.TotalCost:0.00} $/h ({ac.Iterations} it, {ac.LossesMw:0.000} MW losses), DC {dc.TotalCost:0.00} $/h");
Console.WriteLine($"LMP bus 1: AC {ac.LmpAt(1):0.000}, DC {dc.LmpAt(1):0.000} $/MWh");
var dcParts = LmpDecomposition.Decompose(dc);                 // energi = λ, kongesti = LMP − λ, rugi = 0
```

```
AC 8081.53 $/h (12 it, 9.287 MW losses), DC 7642.59 $/h
LMP bus 1: AC 36.724, DC 39.016 $/MWh
```

Dengan dependency injection, `AddOptimization()` mendaftarkan `IAcOpfService` (`Solve`, `Decompose`):

```csharp
using var sp = new ServiceCollection().AddElectrical(b => b.AddOptimization()).BuildServiceProvider();
var acopf = sp.GetRequiredService<IAcOpfService>();
var r = acopf.Solve(TestCases.Case14());
Console.WriteLine($"{r.TotalCost:0.00} $/h, LMP bus 1 {r.LmpAt(1):0.000} $/MWh");   // 8081.53 $/h, LMP bus 1 36.724 $/MWh
```

## Dekomposisi LMP

`LmpDecomposition.Decompose(AcOpfResult, referenceBusId?)` (juga `opf.Decompose()`) memecah setiap LMP AC menjadi
**LMP = energi + rugi + kongesti + tegangan** (`LmpComponents`):

- **energi**: harga di bus referensi (bawaan: slack OPF), sama di setiap bus;
- **rugi**: −LF<sub>i</sub>·λ<sub>ref</sub>, dengan faktor rugi marginal LF<sub>i</sub> = ∂P<sub>rugi</sub>/∂P<sub>i</sub> dari Jacobian AC di titik
  optimum (`LossFactors`, `DeliveryFactors` = 1 − LF);
- **kongesti**: kontribusi batas MVA cabang yang terikat;
- **tegangan**: kontribusi batas |V| yang terikat (nol bila tidak ada batas tegangan yang terikat), yang terbawa dalam harga AC tetapi tidak
  bisa ada dalam harga DC.

Metode ini menyelesaikan kondisi stasioner Lagrangian OPF terhadap tegangan, J<sub>V</sub>ᵀ·λ + J<sub>h,V</sub>ᵀ·μ + ν<sub>V</sub> = 0, untuk λ
dengan harga referensi sebagai parameter. `MaxResidual` melaporkan |LMP − Σ komponen| (di bawah 1e-3 $/MWh dalam pengujian). Pengujian juga
memeriksa setiap LMP terhadap biaya marginal beda-hingga untuk beban ±0,5 MW (dalam 1 %), dengan dan tanpa kongesti.

`LmpDecomposition.Decompose(DcOpfResult)` memberikan pemisahan DC: energi = λ, kongesti = LMP − λ, rugi = 0. Hasil pasar membawa pemisahan DC
yang sama sebagai `DcLmpComponents` ([Pasar listrik](pasar-listrik.md#komponen-lmp)).

## Solver titik interior untuk NLP Anda sendiri

`InteriorPointSolver.Solve(NonlinearProgram, x0, InteriorPointOptions?)` menyelesaikan min f(x) s.t. g(x) = 0, h(x) ≤ 0,
x<sub>min</sub> ≤ x ≤ x<sub>max</sub> dari callback: objektif + gradien, kendala, Jacobian sparse, dan Hessian Lagrangian
∇²(σ·f + λᵀg + μᵀh). Kembalikan matriks dengan pola sparsity tetap agar urutan KKT dapat dipakai ulang.

```csharp
using Electrical.Core.Numerics;

// min (x0 − 1)² + (x1 − 2)²  s.t.  x0 + x1 = 1,  0 ≤ x ≤ 10
var nlp = new NonlinearProgram
{
    VariableCount = 2, EqualityCount = 1,
    Lower = [0, 0], Upper = [10, 10],
    Objective = (x, grad) =>
    {
        grad[0] = 2 * (x[0] - 1); grad[1] = 2 * (x[1] - 2);
        return Math.Pow(x[0] - 1, 2) + Math.Pow(x[1] - 2, 2);
    },
    Constraints = (x, g, h) => g[0] = x[0] + x[1] - 1,
    Jacobians = x =>
    {
        var jg = new SparseBuilder<double>(1, 2); jg.Add(0, 0, 1); jg.Add(0, 1, 1);
        return (jg.ToCsr(), new SparseBuilder<double>(0, 2).ToCsr());
    },
    Hessian = (x, sigma, lambda, mu) =>
    {
        var hb = new SparseBuilder<double>(2, 2); hb.Add(0, 0, 2 * sigma); hb.Add(1, 1, 2 * sigma);
        return hb.ToCsr();
    },
};
var r = InteriorPointSolver.Solve(nlp, [0.5, 0.5]);
Console.WriteLine($"{r.Status} in {r.Iterations} it: x = ({r.X[0]:0.0000}, {r.X[1]:0.0000}), λ = {r.Lambda[0]:0.0000}");
// Converged in 11 it: x = (0.0005, 0.9995), λ = 2.0009
```

Optimum eksaknya (0, 1) dengan x0 berada di batas bawah; solver berhenti pada toleransi bawaan 1e-6 (perketat
`InteriorPointOptions.FeasibilityTolerance` dan toleransi lain untuk digit lebih banyak). `InteriorPointResult` memberikan `X`, `Lambda`
(kesamaan), `Mu` (ketaksamaan), `MuLower`/`MuUpper` (batas), `History`, dan `Status`.


## Unit commitment

Untuk unit commitment MILP dengan kendala jaringan, keamanan N-1, ramp, dan harga, gunakan `SecurityConstrainedUnitCommitment` di
[Electrical.Markets](pasar-listrik.md) (v1.2). DP daftar prioritas di bawah tetap tersedia sebagai heuristik cepat dan pembanding.

Forward dynamic programming atas state daftar prioritas (Wood & Wollenberg §4.2), dengan biaya start-up, waktu nyala/mati minimum, dan kebutuhan
spinning reserve.

```csharp
var units = UnitCommitment.WoodWollenbergThreeUnits();
double[] demand = [450, 420, 400, 400, 450, 550, 700, 850, 950, 1000, 1050, 1050, 1000, 1000, 950, 900, 950, 1050, 1100, 1000, 850, 700, 550, 480];
var uc = UnitCommitment.Solve(units, demand, reserveFraction: 0.05);
Console.WriteLine($"Layak {uc.Feasible}, total {uc.TotalCost:0} $, prioritas {string.Join(",", uc.PriorityOrder.Select(i => i + 1))}");
```

Unit W&W Contoh 4A memiliki biaya rata-rata beban penuh 9,79; 9,40; dan 11,19 $/MWh sehingga prioritasnya 2, 1, 3. `UnitCommitmentResult`
berisi `Status[unit, jam]`, `DispatchMw`, `Lambda`, `SpinningReserveMw`, biaya produksi, dan biaya start-up. `eln uc` mencetak jadwal 24 jam.

## Penempatan kapasitor dan optimizer umum

```csharp
var caps = CapacitorPlacement.Optimize(TestCases.Case33BW(), maxCapacitors: 3);
Console.WriteLine($"{caps.LossesBeforeMw * 1000:0.0} → {caps.LossesAfterMw * 1000:0.0} kW");

var rosen = NelderMead.Minimize(x => Math.Pow(1 - x[0], 2) + 100 * Math.Pow(x[1] - x[0] * x[0], 2), [-1.2, 1.0]);
```

Penempatan kapasitor mengurutkan bus kandidat berdasarkan sensitivitas rugi dan menentukan ukuran bank dalam langkah `stepMvar` dengan aliran
daya AC. `eln caps` menghasilkan bus 13 400 kvar, bus 24 550 kvar, bus 30 1150 kvar: 202,7 → 132,2 kW (literatur melaporkan 132–145 kW untuk tiga
bank).

`QuadraticProgram`/`QpSolver` (primal-dual interior point, kendala persamaan dan pertidaksamaan, variabel dual), `NelderMead`, dan
`ParticleSwarm` (seed deterministik) bersifat publik untuk masalah Anda sendiri.

## Validasi (v1.2)

| Pemeriksaan | Acuan | Hasil |
|---|---|---|
| AC-OPF case9 | `runopf` MATPOWER 7 (MIPS): 5296,69 $/jam, Pg 89,80 / 134,32 / 94,19 MW | identik (±0,01) |
| AC-OPF case14 | MATPOWER: 8081,53 $/jam, Pg 194,33 / 36,72 / 28,74 / 0 / 8,50 MW, λ<sub>P</sub>(1) 36,724 $/MWh | identik; < 1 detik termasuk JIT |
| LMP AC, dengan dan tanpa kongesti | Beda-hingga biaya optimal, beban ±0,5 MW | dalam 1 % |
| Dekomposisi LMP AC | Jumlah komponen; Vmax terikat di bus 1, 6, 8 (seperti MATPOWER) | residu < 1e-3 $/MWh |
| Flat start, warm start, step control | Optimum yang sama | sama hingga 0,01 $/jam |
| DC-OPF `SyntheticGrid(300)` | Dispatch ekonomis (tanpa kongesti) | biaya sama dalam 8 iterasi (dulu 100, tidak konvergen) |

## Batasan

AC-OPF: formulasi polar dengan beban daya konstan, tanpa kendali diskrit (tap, shunt yang disaklar), tanpa kendala keamanan, dan tanpa biaya
generator linear sepotong-sepotong; flat start dapat gagal pada kasus sangat besar atau berskala buruk (coba `Start = AcOpfStart.PowerFlow`
atau `StepControl = true`). Unit commitment MILP dan pasar ada di [Electrical.Markets](pasar-listrik.md).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/optimization.md)*
