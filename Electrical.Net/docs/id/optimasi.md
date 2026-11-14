# Optimasi

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/optimization.md)

Paket: `Electrical.Optimization` (bergantung pada PowerFlow). Biaya berbentuk polinomial `C(P) = C2·P² + C1·P + C0` [$/jam] dengan P dalam MW
(`PolynomialCost` di setiap `Generator`).

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

CLI: `eln opf --case case9 [--limit 5-6:60]` (batas override dalam MW).

## Unit commitment

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

## Batasan

Model jaringan DC untuk OPF (tanpa tegangan, daya reaktif, atau rugi); unit commitment berupa DP daftar prioritas, bukan MILP; tidak ada kendala
jaringan dalam UC. AC-OPF, SCUC dengan solver MILP eksternal, dan market clearing direncanakan untuk v1.2.

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/optimization.md)*
