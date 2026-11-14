# Stability

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/stabilitas.md)

Package: `Electrical.Stability` (depends on PowerFlow). Covers rotor-angle (transient and small-signal), voltage and frequency stability.

## Transient stability (classical multi-machine model)

`ClassicalModel.FromPowerFlow` solves (or reuses) a power flow, represents every in-service generator as E′∠δ behind X′d with inertia `H` and
damping `D` (pu on `MBase`), converts loads to constant admittances and Kron-reduces the network to the machine internal nodes for each
switching configuration. `TransientStability.Simulate` integrates the swing equations with RK4 and applies `StabilityEvent`s.

```csharp
var net = TestCases.Case9();                          // Anderson & Fouad operating point, classical machine data
var model = ClassicalModel.FromPowerFlow(net);
foreach (var m in model.Machines)
    Console.WriteLine($"{m.Name}: δ0 = {m.Delta0Deg:0.00}°");   // 2.27°, 19.73°, 13.17°

// A&F "fault near bus 7" = MATPOWER bus 8; cleared by opening line 8–9 (A&F 5–7)
int line = TransientStability.BranchIndex(net, 8, 9);
var opt = new TransientStabilityOptions { TimeStepS = 0.002, EndTimeS = 2.0 };
var run = TransientStability.SimulateFault(model, faultBusId: 8, clearingTimeS: 0.083, tripBranchIndex: line, options: opt);
Console.WriteLine($"Stable: {run.Stable}, max COI deviation {run.MaxAngleDeviationDeg:0.0}°");

double cct = TransientStability.CriticalClearingTime(model, 8, line, 0.05, 0.6, 0.002, opt);   // ≈ 0.162 s
```

```
$ eln stability --case case9 --bus 8 --clear 0.083 --trip 8-9
Transient stability on case9: 3φ fault at bus 8, cleared at 0.083 s by tripping branch 8-9
  Result: STABLE
  Critical clearing time: 0.161 s
```

Bus numbering: `TestCases.Case9()` uses MATPOWER numbering. Anderson & Fouad bus 7 (generator 2 HV bus) is MATPOWER bus 8, and A&F line 5–7
is MATPOWER branch 8–9.

Event lists cover faults, clearing, branch trip/close, generator trip and load scaling:

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

`TransientStabilityResult` gives `Time`, `AngleDeg`, `AngleCoiDeg` (relative to the centre of inertia), `SpeedPu`, `ElectricalPowerPu`,
`Stable`, `InstabilityTimeS` and `MaxAngleDeviationDeg`. A run is unstable when any machine leaves the COI by more than
`InstabilityAngleDeg` (180° by default). A generator with `H = 0` is treated as an infinite bus.

## Single machine – infinite bus and the equal-area criterion

```csharp
var s = new SmibSystem(H: 5, Pm: 0.8, E: 1.17, V: 1.0, XPre: 0.65, XFault: double.PositiveInfinity, XPost: 0.65, FrequencyHz: 60);
var eac = Smib.EqualArea(s);                       // δ0, δcr, closed-form tcr for a bolted fault
double tcr = Smib.CriticalClearingTime(s);         // by simulation
var traj = Smib.Simulate(s, clearingTimeS: tcr - 0.01);
```

Results: δ0 = 26.39°, δ<sub>cr</sub> = 84.77°, closed-form t<sub>cr</sub> = 0.2600 s, simulated 0.2599 s. Partial faults (`XFault` finite)
and post-fault topology changes (`XPost`) are supported.

## Small-signal analysis

`SmallSignal.Analyze` linearises the classical model (states Δδ, Δω), computes eigenvalues with a balanced Hessenberg QR solver
(`EigenSolver`) and participation factors.

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

The two electromechanical modes (8.69 and 13.36 rad/s) match Sauer & Pai / Anderson & Fouad for the classical model. Damping is zero because
the 9-bus data has `D = 0` in the classical model; set `Generator.D` to see damped modes. The zero mode from the angle reference is ignored by
`IsStable`.

## Voltage stability

```csharp
var pv = VoltageStability.PvCurve(TestCases.Case14(), new PvCurveOptions { InitialStep = 0.1, MinimumStep = 1e-3 });
Console.WriteLine($"λmax = {pv.LambdaMax:0.000}, margin {pv.MarginMw:0.0} MW, weakest bus {pv.CriticalBusId}");
double[] v14 = pv.Voltage(14);   // nose curve of bus 14

var qv = VoltageStability.QvCurve(TestCases.Case14(), busId: 14);
Console.WriteLine($"Reactive margin at bus 14: {qv.ReactiveMarginMvar:0.0} Mvar");

var l = VoltageStability.LIndex(TestCases.Case14());
Console.WriteLine($"L-index max {l.Max:0.000} at bus {l.CriticalBusId}");
```

- **PV curve**: continuation by load scaling with step halving near the nose; generation of non-slack units scales with λ by default
  (`ScaleGeneration`), `LoadBuses` restricts the scaled loads, `EnforceQLimits` applies generator limits. On case14 (no Q limits) λ<sub>max</sub>
  = 4.059 with a 792.4 MW margin; on a two-bus system with X = 0.1 pu it reaches the analytic V²/2X = 5 pu.
- **QV curve**: reactive injection needed to hold each voltage; `ReactiveMarginMvar` is −Q<sub>min</sub>. Case14 bus 14: 115.3 Mvar.
- **L-index** (Kessel–Glavitsch): 0 at no load, 1 at collapse. Case14 base case: max 0.077 at bus 14.

## Frequency response

An aggregated single-area model with governor and turbine time constants, droop R and load damping D:

```csharp
var p = new FrequencyResponseParameters(H: 5, D: 1, R: 0.05, DisturbancePu: 0.1);   // 50 Hz
var r = FrequencyResponse.Simulate(p, endTimeS: 60, stepS: 0.005);
Console.WriteLine($"ROCOF {r.InitialRocofHzPerS:0.000} Hz/s, nadir {r.NadirHz:0.000} Hz at {r.NadirTimeS:0.0} s, Δf_ss {r.SteadyStateDeviationHz:0.000} Hz");
// ROCOF −0.500 Hz/s (−ΔP·f0/2H), Δf_ss −0.238 Hz (−ΔP/(D + 1/R)·f0)

var fromGrid = FrequencyResponseParameters.FromNetwork(TestCases.Case9(), disturbanceMw: 50, droop: 0.05);   // H_sys = ΣH·S/S_base
```

## Validation

| Check | Reference | Result |
|---|---|---|
| Classical initial angles, 9-bus | Anderson & Fouad: 2.27°, 19.73°, 13.17° | equal to 0.1° |
| CCT, A&F bus-7 fault cleared by line 5–7 | Literature ≈ 0.16–0.17 s | 0.161–0.162 s (unstable at CCT + 0.02 s) |
| Electromechanical modes | 8.7 / 13.4 rad/s | 8.690 / 13.360 rad/s |
| SMIB bolted fault | Closed-form EAC t<sub>cr</sub> | equal to 1 ms |
| SMIB network model vs. formula | ω<sub>n</sub> = √(ω<sub>s</sub>K<sub>s</sub>/2H), EAC CCT | equal |
| Two-bus PV curve | V²/2X = 5 pu, V<sub>nose</sub> = 1/√2 | 4.95–5.0, 0.70–0.75 |
| Frequency response | ROCOF and Δf<sub>ss</sub> formulas | exact |

## Limitations

Classical generator model only (no AVR, governor or PSS in the transient simulation; the frequency-response model is aggregated). Detailed
machine models (GENROU/GENSAL), exciters, governors, PSS and inverter-based resource models are planned for v1.3.

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/stabilitas.md)*
