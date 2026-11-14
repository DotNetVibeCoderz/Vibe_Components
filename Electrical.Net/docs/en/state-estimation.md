# State estimation

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/estimasi-keadaan.md)

Package: `Electrical.StateEstimation` (depends on PowerFlow). Implements the weighted-least-squares (WLS) AC estimator of Abur & Expósito,
*Power System State Estimation*, ch. 2, with observability analysis and bad-data processing.

## Measurements

`Measurement(Type, Location, Value, Sigma, End)`:

| `MeasurementType` | `Location` | Unit |
|---|---|---|
| `VoltageMagnitude` | bus id | pu |
| `ActiveInjection`, `ReactiveInjection` | bus id (generation − load) | MW, Mvar |
| `ActiveFlow`, `ReactiveFlow` | branch index, `End` = From/To (power leaving the bus) | MW, Mvar |
| `VoltageAngle` (PMU) | bus id, relative to the slack | degrees |

`MeasurementGenerator.FromPowerFlow(pf, plan, seed, noise)` synthesises a measurement set from a solved power flow with Gaussian noise
(`MeasurementPlan`: which meters, both branch ends, PMU buses, σ per type). The noise is seeded and deterministic.

## Estimation

```csharp
var net = TestCases.Case14();
var truth = PowerFlowSolver.Solve(net);
var z = MeasurementGenerator.FromPowerFlow(truth, MeasurementPlan.Full with { PmuBuses = [2, 6] }, seed: 2024);

var obs = Observability.Check(net, z);
var est = WlsEstimator.Estimate(net, z);
Console.WriteLine($"Converged {est.Converged} in {est.Iterations} it, J = {est.ObjectiveJ:0.0} (χ² threshold {est.ChiSquareThreshold:0.0})");
```

Gauss–Newton iterations solve G·Δx = HᵀW[z − h(x)] with the sparse Jacobian H and gain G = HᵀWH factorised by `SparseLU`. The state is all
voltage magnitudes plus the angles of the non-slack buses. `StateEstimationResult` returns `Vm`, `VaDeg` (with `VmAt`/`VaDegAt`),
`Estimated`, `Residuals`, `NormalizedResiduals`, `ObjectiveJ`, `DegreesOfFreedom`, `ChiSquareThreshold`, `BadDataSuspected` and the iteration
history. `StateEstimationOptions` sets tolerance, iterations, the χ² significance level and whether normalized residuals are computed.

## Bad data

```csharp
// Gross error on the P flow of branch 2 (2–3), from end
int k = z.FindIndex(m => m.Type == MeasurementType.ActiveFlow && m.Location == 2 && m.End == BranchEnd.From);
z[k] = z[k] with { Value = z[k].Value + 30 };
var bad = BadDataDetector.IdentifyAndRemove(net, z, threshold: 3.0);
foreach (var (m, rn) in bad.Removed) Console.WriteLine($"Removed {m} (rN = {rn:0.0})");
```

χ² detection on J(x̂) followed by the largest-normalized-residual test r<sub>N,i</sub> = |r<sub>i</sub>|/√Ω<sub>ii</sub>,
Ω = R − H·G⁻¹·Hᵀ; the worst measurement above the threshold is removed and the estimate repeated.

```
$ eln se --case case14 --bad 2-3
WLS state estimation on case14: 122 measurements, converged True in 4 it
  Initial J = 586.6 (χ² passed: False); final J = 68.3 ≤ 128.8
  Removed bad measurement: ActiveFlow @ 2 (rN = 22.8)
  Max |ΔV| vs true state 0.0015 pu, max |Δθ| 0.095°
```

## Observability

`Observability.Check(net, measurements)` performs a numerical rank test on the measurement Jacobian and lists unobservable states (for
example a set of voltage magnitudes only gives no angle information; removing every meter touching a radial bus makes it unobservable).

## Validation

- Noise-free measurements recover the power-flow state exactly (case14).
- With noise, the full measurement set passes the χ² test and estimated flows are closer to the truth than raw meters on average.
- A gross error on the 2–3 flow is detected, identified and removed.
- The analytic Jacobian matches finite differences; PMU angles reduce the angle error for the same noise.
- χ² quantiles match standard tables (NIST/SEMATECH).

## Limitations

WLS only (no LAV or other robust estimators), static (no tracking/dynamic estimation), no topology error processing or meter-placement
optimisation. A live twin that feeds SCADA scans into the estimator is the job of `Electrical.DigitalTwin` (see
[AI & digital twin](ai-digital-twin.md)).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/estimasi-keadaan.md)*
