# Power flow

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/aliran-daya.md)

Package: `Electrical.PowerFlow` (depends on `Electrical.Core`).

## Methods

| `PowerFlowMethod` | Algorithm | Use it for |
|---|---|---|
| `NewtonRaphson` (default) | Polar Newton–Raphson, sparse Jacobian factorised with `SparseLU`; PV→PQ switching at Q limits; ZIP loads | General transmission and distribution networks |
| `FastDecoupled` | XB fast-decoupled load flow (constant B′/B″) | Repeated solves on well-conditioned transmission grids |
| `GaussSeidel` | Gauss–Seidel with acceleration factor 1.6 | Teaching, small systems |
| `DC` | Linear θ = B⁻¹P, lossless | Market studies, PTDF/LODF, screening |
| `BackwardForwardSweep` | Current-summation sweep for radial feeders | Distribution feeders (radial, one source) |

```csharp
var net = TestCases.Case14();
foreach (var method in Enum.GetValues<PowerFlowMethod>().Where(m => m != PowerFlowMethod.BackwardForwardSweep))
{
    var r = PowerFlowSolver.Solve(net, new PowerFlowOptions { Method = method, Tolerance = 1e-8 });
    Console.WriteLine($"{method,-14} it={r.Iterations,3} losses={r.LossesMw:0.000} MW");
}

// Radial distribution feeder: backward/forward sweep
var feeder = PowerFlowSolver.Solve(TestCases.Case33BW(),
    new PowerFlowOptions { Method = PowerFlowMethod.BackwardForwardSweep });
Console.WriteLine($"{feeder.LossesMw * 1000:0.0} kW, Vmin {feeder.MinVoltageBus.Vm:0.0000} pu @ bus {feeder.MinVoltageBus.BusId}");
// 202.7 kW, Vmin 0.9131 pu @ bus 18
```

`PowerFlowSolver.Solve` validates the network first (unknown buses, zero impedance, Pmin > Pmax) and throws `InvalidOperationException`
with the list of issues. If no bus is marked `Slack`, the bus with the largest generation capacity is promoted.

## Options

```csharp
var r = PowerFlowSolver.Solve(net, new PowerFlowOptions
{
    Method = PowerFlowMethod.NewtonRaphson,
    EnforceQLimits = true,     // PV → PQ switching at generator Q limits
    FlatStart = false,         // start from the voltages stored in the network
    Tolerance = 1e-10,         // max power mismatch [pu]
    MaxIterations = 20,
});

foreach (var g in r.Generators.Where(g => g.AtQLimit))
    Console.WriteLine($"Generator at bus {g.Bus} is at its Q limit ({g.Q:0.0} Mvar)");
foreach (var v in r.VoltageViolations())
    Console.WriteLine($"Bus {v.BusId}: {v.Vm:0.000} pu");
foreach (var br in r.Overloads(90))
    Console.WriteLine($"{br.From}-{br.To}: {br.LoadingPercent:0.0} %");

r.ApplyTo(net);   // warm start: write V, θ and generator P/Q back into the model
```

### Flat start (DC-initialised angles)

With `FlatStart = true` (the default) Newton–Raphson starts from |V| = 1 pu at PQ buses and the set-points at PV/slack buses, and
**since v1.1 the angles come from a DC power flow** (θ = B⁻¹P including phase-shifter angles) instead of zero. This keeps the iteration in
the basin of the operational solution when a network has large phase shifts or heavy transfers: an ideal 30° phase shifter used to
diverge from θ = 0 and a 45° one converged to a spurious solution, while the DC start reaches the correct one in a few iterations
(regression test `LargePhaseShift_FlatStartConvergesToOperationalSolution`). Ordinary cases also save an iteration (case9 and case14
now converge in 3). The DC start is skipped automatically, falling back to θ = 0, for networks with zero-reactance branches, several
islands or an implausible DC solution. `FlatStart = false` still starts from the voltages stored in the network (warm start, e.g. after
`ApplyTo`).

## Results

`PowerFlowResult` holds powers in MW/Mvar and voltages in pu/degrees:

- `Converged`, `Iterations`, `MaxMismatch`, `History` (mismatch per iteration), `Elapsed`, complex `Voltages`.
- `Buses` (`BusResult`: `Vm`, `VaDeg`, `PGen`, `QGen`, `PLoad`, `QLoad`, `Type`, `Kv(baseKv)`).
- `Branches` (`BranchResult`: `PFrom`, `QFrom`, `PTo`, `QTo`, `PLoss`, `QLoss`, `SFrom`, `STo`, `LoadingPercent` against `RateA`).
- `Generators` (`GeneratorResult`: `P`, `Q`, `AtQLimit`).
- Totals: `TotalGenerationMw`, `TotalLoadMw`, `LossesMw`, `LossesMvar`; `MinVoltageBus`, `MaxVoltageBus`, `MostLoadedBranch`.
- `ToString()` gives a one-line summary.

CLI output for the same study:

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

(Buses 6, 7 and 8 are flagged because their voltages exceed the bus `Vmax` (1.06 pu) of the MATPOWER case14 data.)

## Load models

Each `Load` has a `ZipModel(Z, I, P)`; the fractions must sum to 1. `ZipModel.ConstantPower` is the default.

```csharp
var net = TestCases.Case33BW();
foreach (var load in net.Loads)
    load.Model = new ZipModel(Z: 0.3, I: 0.3, P: 0.4);
var r = PowerFlowSolver.Solve(net);
```

## N-1 contingency screening

`ContingencyAnalysis.RunN1` trips every in-service branch in turn, re-solves an AC power flow (warm-started from the base case, in parallel)
and sorts outages by a severity index (overload above 100 %, voltage below 0.95 pu, count of violations; islanding and divergence are infinite).

```csharp
var ranking = ContingencyAnalysis.RunN1(TestCases.Case14());   // parallel, worst first
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

Branch 7-8 is radial to the synchronous condenser at bus 8, so its outage islands that bus. `MaxLoad%` is 0 because MATPOWER case14 has no
branch ratings; set `Branch.RateA` (MVA) to get loading checks.

## Sensitivities (DC)

```csharp
var net = TestCases.Case14();
var ptdf = DcPowerFlow.Ptdf(net);           // [branch, bus]: ΔMW on branch per MW injected at bus (withdrawn at slack)
var lodf = DcPowerFlow.Lodf(net, ptdf);     // [l, k]: share of branch k's flow moving onto l when k trips
int bus14 = net.IndexOf(14);
Console.WriteLine($"Branch 0 picks up {ptdf[0, bus14]:P1} of an injection at bus 14");
```

LODF columns of radial (islanding) outages are `NaN`; the diagonal is −1. `DcModel.Build(net)` exposes the reduced B matrix for custom
angle/flow calculations (`Angles`, `BranchFlowsMw`). The tests check that PTDF predicts the DC flow change exactly.

## Validation

| Case | Reference | Electrical.Net |
|---|---|---|
| case9 (NR, FD, GS) | MATPOWER: P<sub>G1</sub> 71.64 MW, losses 4.641 MW, V<sub>9</sub> 0.9956 pu | identical |
| case14 (NR) | MATPOWER: P<sub>G1</sub> 232.39 MW, losses 13.393 MW, V<sub>14</sub> 1.036 ∠ −16.04° | identical |
| case33bw (NR and sweep) | Baran & Wu: 202.7 kW, V<sub>min</sub> 0.9131 pu at bus 18 | identical |
| DC case9 | Slack = load − fixed generation, zero losses | exact |
| MATPOWER round trip | `Matpower.Parse(Matpower.Write(net))` | same losses to 1e-6 |
| PSS/E RAW v33/v35 and IEEE CDF IEEE 14-bus ([interop](interop.md)) | MATPOWER case14, 13.393 MW losses | identical |
| Ideal 30° / 45° phase shifter, flat start | V<sub>3</sub> = V<sub>2</sub>∠−shift | converged in ≤ 6 it to the operational solution |

## Limitations

- Balanced positive-sequence model. For unbalanced three-phase feeders (phase frame, Carson line geometry, regulators) use
  [`Electrical.Distribution`](distribution.md).
- No in-loop transformer tap/OLTC or switched-shunt control in a single solve; time-series control loops (OLTC, LDC regulator, switched
  capacitors) run in [QSTS](simulation-hpc-native.md#voltage-control-in-qsts-v11), and `Electrical.SmartGrid.VoltVarOptimization` optimises set-points.
- The backward/forward sweep needs a radial network with a single source.

See also: [Stability](stability.md) (PV curves), [Optimization](optimization.md) (DC-OPF), [Simulation, HPC & native](simulation-hpc-native.md)
(batch and probabilistic load flow).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/aliran-daya.md)*
