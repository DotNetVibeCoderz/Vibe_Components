# Stabilitas

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/stability.md)

Paket: `Electrical.Stability` (bergantung pada PowerFlow). Mencakup stabilitas sudut rotor (transien dan sinyal kecil), tegangan, dan frekuensi.

## Stabilitas transien (model klasik multi-mesin)

`ClassicalModel.FromPowerFlow` menyelesaikan (atau memakai ulang) aliran daya, merepresentasikan setiap generator yang beroperasi sebagai E′∠δ
di belakang X′d dengan inersia `H` dan redaman `D` (pu pada `MBase`), mengubah beban menjadi admitansi konstan, dan melakukan reduksi Kron ke
node internal mesin untuk setiap konfigurasi switching. `TransientStability.Simulate` mengintegrasikan persamaan ayunan dengan RK4 dan
menerapkan `StabilityEvent`.

```csharp
var net = TestCases.Case9();                          // titik operasi Anderson & Fouad, data mesin klasik
var model = ClassicalModel.FromPowerFlow(net);
foreach (var m in model.Machines)
    Console.WriteLine($"{m.Name}: δ0 = {m.Delta0Deg:0.00}°");   // 2,27°, 19,73°, 13,17°

// "Gangguan dekat bus 7" A&F = bus 8 MATPOWER; dibersihkan dengan membuka saluran 8–9 (A&F 5–7)
int line = TransientStability.BranchIndex(net, 8, 9);
var opt = new TransientStabilityOptions { TimeStepS = 0.002, EndTimeS = 2.0 };
var run = TransientStability.SimulateFault(model, faultBusId: 8, clearingTimeS: 0.083, tripBranchIndex: line, options: opt);
Console.WriteLine($"Stabil: {run.Stable}, deviasi COI maks {run.MaxAngleDeviationDeg:0.0}°");

double cct = TransientStability.CriticalClearingTime(model, 8, line, 0.05, 0.6, 0.002, opt);   // ≈ 0,162 s
```

```
$ eln stability --case case9 --bus 8 --clear 0.083 --trip 8-9
Transient stability on case9: 3φ fault at bus 8, cleared at 0.083 s by tripping branch 8-9
  Result: STABLE
  Critical clearing time: 0.161 s
```

Penomoran bus: `TestCases.Case9()` memakai penomoran MATPOWER. Bus 7 Anderson & Fouad (bus TT generator 2) adalah bus 8 MATPOWER, dan saluran
5–7 A&F adalah cabang 8–9 MATPOWER.

Daftar kejadian mencakup gangguan, pembersihan, buka/tutup cabang, lepas generator, dan penskalaan beban:

```csharp
var events = new[]
{
    StabilityEvent.Fault(0.1, busId: 5),
    StabilityEvent.Clear(0.2, busId: 5),
    StabilityEvent.TripGenerator(1.0, generatorIndex: 2),
    StabilityEvent.ScaleLoad(1.5, busId: 9, factor: 0.9),
};
var res = TransientStability.Simulate(model, events, new TransientStabilityOptions { EndTimeS = 5 });
double[] d12 = res.AngleDifferenceDeg(0, 1);
```

`TransientStabilityResult` berisi `Time`, `AngleDeg`, `AngleCoiDeg` (relatif terhadap pusat inersia), `SpeedPu`, `ElectricalPowerPu`, `Stable`,
`InstabilityTimeS`, dan `MaxAngleDeviationDeg`. Sebuah simulasi dinyatakan tidak stabil bila ada mesin yang menyimpang dari COI lebih dari
`InstabilityAngleDeg` (default 180°). Generator dengan `H = 0` diperlakukan sebagai bus tak hingga.

## Single machine – infinite bus dan kriteria luas sama

```csharp
var s = new SmibSystem(H: 5, Pm: 0.8, E: 1.17, V: 1.0, XPre: 0.65, XFault: double.PositiveInfinity, XPost: 0.65, FrequencyHz: 60);
var eac = Smib.EqualArea(s);                       // δ0, δcr, tcr bentuk tertutup untuk gangguan bolted
double tcr = Smib.CriticalClearingTime(s);         // lewat simulasi
var traj = Smib.Simulate(s, clearingTimeS: tcr - 0.01);
```

Hasil: δ0 = 26,39°, δ<sub>cr</sub> = 84,77°, t<sub>cr</sub> bentuk tertutup = 0,2600 s, simulasi 0,2599 s. Gangguan parsial (`XFault` berhingga)
dan perubahan topologi pasca-gangguan (`XPost`) didukung.

## Analisis sinyal kecil

`SmallSignal.Analyze` melinearkan model klasik (state Δδ, Δω), menghitung nilai eigen dengan algoritma QR double-shift Francis setelah
balancing dan reduksi Hessenberg (`EigenSolver`, gaya EISPACK), serta faktor partisipasi.

```csharp
var ss = SmallSignal.Analyze(TestCases.Case9());
foreach (var mode in ss.OscillatoryModes)
    Console.WriteLine($"λ = {mode.Eigenvalue.Real:0.000} ± j{mode.Eigenvalue.Imaginary:0.000}  f = {mode.FrequencyHz:0.000} Hz  ζ = {mode.DampingRatio:P2}");
```

```
$ eln modes --case case9
Small-signal analysis on case9: stable, minimum damping ratio 0.00 %
  λ =   0.0000 ± j 13.3602   f = 2.126 Hz   ζ = 0.00 %
  λ =   0.0000 ± j  8.6898   f = 1.383 Hz   ζ = 0.00 %
```

Dua mode elektromekanis (8,69 dan 13,36 rad/s) sesuai dengan Sauer & Pai / Anderson & Fouad untuk model klasik. Redaman nol karena data 9-bus
memakai `D = 0` pada model klasik; isi `Generator.D` untuk melihat mode teredam. Mode nol dari referensi sudut diabaikan oleh `IsStable`.

## Stabilitas tegangan

```csharp
var pv = VoltageStability.PvCurve(TestCases.Case14(), new PvCurveOptions { InitialStep = 0.1, MinimumStep = 1e-3 });
Console.WriteLine($"λmax = {pv.LambdaMax:0.000}, margin {pv.MarginMw:0.0} MW, bus terlemah {pv.CriticalBusId}");
double[] v14 = pv.Voltage(14);   // kurva hidung bus 14

var qv = VoltageStability.QvCurve(TestCases.Case14(), busId: 14);
Console.WriteLine($"Margin reaktif di bus 14: {qv.ReactiveMarginMvar:0.0} Mvar");

var l = VoltageStability.LIndex(TestCases.Case14());
Console.WriteLine($"Indeks-L maks {l.Max:0.000} di bus {l.CriticalBusId}");
```

- **Kurva PV**: kontinuasi dengan penskalaan beban dan pembagian dua langkah di dekat hidung; pembangkitan unit non-slack ikut diskalakan
  dengan λ secara default (`ScaleGeneration`), `LoadBuses` membatasi beban yang diskalakan, `EnforceQLimits` menerapkan batas generator. Pada
  case14 (tanpa batas Q) λ<sub>max</sub> = 4,059 dengan margin 792,4 MW; pada sistem dua bus dengan X = 0,1 pu nilainya mencapai V²/2X = 5 pu
  analitis.
- **Kurva QV**: injeksi reaktif yang diperlukan untuk mempertahankan setiap tegangan; `ReactiveMarginMvar` adalah −Q<sub>min</sub>. Case14 bus 14:
  115,3 Mvar.
- **Indeks-L** (Kessel–Glavitsch): 0 tanpa beban, 1 saat runtuh. Kasus dasar case14: maks 0,077 di bus 14.

## Respons frekuensi

Model satu area teragregasi dengan konstanta waktu governor dan turbin, droop R, dan redaman beban D:

```csharp
var p = new FrequencyResponseParameters(H: 5, D: 1, R: 0.05, DisturbancePu: 0.1);   // 50 Hz
var r = FrequencyResponse.Simulate(p, endTimeS: 60, stepS: 0.005);
Console.WriteLine($"ROCOF {r.InitialRocofHzPerS:0.000} Hz/s, nadir {r.NadirHz:0.000} Hz pada {r.NadirTimeS:0.0} s, Δf_ss {r.SteadyStateDeviationHz:0.000} Hz");
// ROCOF −0,500 Hz/s (−ΔP·f0/2H), Δf_ss −0,238 Hz (−ΔP/(D + 1/R)·f0)

var fromGrid = FrequencyResponseParameters.FromNetwork(TestCases.Case9(), disturbanceMw: 50, droop: 0.05);   // H_sys = ΣH·S/S_base
```

## Validasi

| Pemeriksaan | Acuan | Hasil |
|---|---|---|
| Sudut awal klasik, 9-bus | Anderson & Fouad: 2,27°, 19,73°, 13,17° | sama hingga 0,1° |
| CCT, gangguan bus 7 A&F dibersihkan oleh saluran 5–7 | Literatur ≈ 0,16–0,17 s | 0,161–0,162 s (tidak stabil pada CCT + 0,02 s) |
| Mode elektromekanis | 8,7 / 13,4 rad/s | 8,690 / 13,360 rad/s |
| Gangguan bolted SMIB | t<sub>cr</sub> EAC bentuk tertutup | sama hingga 1 ms |
| Model jaringan SMIB vs. rumus | ω<sub>n</sub> = √(ω<sub>s</sub>K<sub>s</sub>/2H), CCT EAC | sama |
| Kurva PV dua bus | V²/2X = 5 pu, V<sub>hidung</sub> = 1/√2 | 4,95–5,0; 0,70–0,75 |
| Respons frekuensi | Rumus ROCOF dan Δf<sub>ss</sub> | eksak |

## Batasan

Hanya model generator klasik (tanpa AVR, governor, atau PSS di simulasi transien; model respons frekuensi bersifat teragregasi). Model mesin detail
(GENROU/GENSAL), eksiter, governor, PSS, dan model sumber berbasis inverter direncanakan untuk v1.3.

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/stability.md)*
