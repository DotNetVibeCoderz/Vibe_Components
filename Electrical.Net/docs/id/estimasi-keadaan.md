# Estimasi keadaan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/state-estimation.md)

Paket: `Electrical.StateEstimation` (bergantung pada PowerFlow). Mengimplementasikan estimator AC weighted least squares (WLS) dari Abur &
Expósito, *Power System State Estimation*, bab 2, dengan analisis observabilitas dan pemrosesan bad data.

## Pengukuran

`Measurement(Type, Location, Value, Sigma, End)`:

| `MeasurementType` | `Location` | Satuan |
|---|---|---|
| `VoltageMagnitude` | id bus | pu |
| `ActiveInjection`, `ReactiveInjection` | id bus (pembangkitan − beban) | MW, Mvar |
| `ActiveFlow`, `ReactiveFlow` | indeks cabang, `End` = From/To (daya yang keluar dari bus) | MW, Mvar |
| `VoltageAngle` (PMU) | id bus, relatif terhadap slack | derajat |

`MeasurementGenerator.FromPowerFlow(pf, plan, seed, noise)` menyintesis himpunan pengukuran dari aliran daya yang telah diselesaikan dengan derau
Gaussian (`MeasurementPlan`: meter mana, kedua ujung cabang, bus PMU, σ per tipe). Derau memakai seed dan deterministik.

## Estimasi

```csharp
var net = TestCases.Case14();
var truth = PowerFlowSolver.Solve(net);
var z = MeasurementGenerator.FromPowerFlow(truth, MeasurementPlan.Full with { PmuBuses = [2, 6] }, seed: 2024);

var obs = Observability.Check(net, z);
var est = WlsEstimator.Estimate(net, z);
Console.WriteLine($"Konvergen {est.Converged} dalam {est.Iterations} it, J = {est.ObjectiveJ:0.0} (ambang χ² {est.ChiSquareThreshold:0.0})");
```

Iterasi Gauss–Newton menyelesaikan G·Δx = HᵀW[z − h(x)] dengan Jacobian sparse H dan gain G = HᵀWH yang difaktorkan oleh `SparseLU`. State-nya
adalah semua magnitudo tegangan ditambah sudut bus non-slack. `StateEstimationResult` mengembalikan `Vm`, `VaDeg` (dengan `VmAt`/`VaDegAt`),
`Estimated`, `Residuals`, `NormalizedResiduals`, `ObjectiveJ`, `DegreesOfFreedom`, `ChiSquareThreshold`, `BadDataSuspected`, dan riwayat iterasi.
`StateEstimationOptions` mengatur toleransi, iterasi, tingkat signifikansi χ², dan apakah normalized residual dihitung.

## Bad data

```csharp
// Galat besar pada aliran P cabang 2 (2–3), ujung from
int k = z.FindIndex(m => m.Type == MeasurementType.ActiveFlow && m.Location == 2 && m.End == BranchEnd.From);
z[k] = z[k] with { Value = z[k].Value + 30 };
var bad = BadDataDetector.IdentifyAndRemove(net, z, threshold: 3.0);
foreach (var (m, rn) in bad.Removed) Console.WriteLine($"Dihapus {m} (rN = {rn:0.0})");
```

Deteksi χ² pada J(x̂) diikuti uji largest normalized residual r<sub>N,i</sub> = |r<sub>i</sub>|/√Ω<sub>ii</sub>, Ω = R − H·G⁻¹·Hᵀ; pengukuran
terburuk di atas ambang dihapus lalu estimasi diulang.

```
$ eln se --case case14 --bad 2-3
WLS state estimation on case14: 122 measurements, converged True in 4 it
  Initial J = 586.6 (χ² passed: False); final J = 68.3 ≤ 128.8
  Removed bad measurement: ActiveFlow @ 2 (rN = 22.8)
  Max |ΔV| vs true state 0.0015 pu, max |Δθ| 0.095°
```

## Observabilitas

`Observability.Check(net, measurements)` melakukan uji rank numerik pada Jacobian pengukuran dan mendaftar state yang tidak teramati (misalnya
himpunan yang hanya berisi magnitudo tegangan tidak memberi informasi sudut; menghapus semua meter yang menyentuh bus radial membuatnya tidak
teramati).

## Validasi

- Pengukuran tanpa derau mengembalikan state aliran daya secara eksak (case14).
- Dengan derau, himpunan pengukuran penuh lulus uji χ² dan aliran hasil estimasi rata-rata lebih dekat ke nilai sebenarnya daripada meter mentah.
- Galat besar pada aliran 2–3 terdeteksi, teridentifikasi, dan dihapus.
- Jacobian analitis sesuai dengan beda hingga; sudut PMU mengurangi galat sudut untuk realisasi derau yang sama.
- Kuantil χ² sesuai dengan tabel standar (NIST/SEMATECH).

## Batasan

Hanya WLS (tanpa LAV atau estimator robust lain), statis (tanpa estimasi dinamis/tracking), tanpa pemrosesan galat topologi atau optimasi
penempatan meter. Twin langsung yang memasukkan pemindaian SCADA ke estimator adalah tugas `Electrical.DigitalTwin` (lihat
[AI & digital twin](ai-digital-twin.md)).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/state-estimation.md)*
