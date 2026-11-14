# Optimization

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/optimasi.md)

Package: `Electrical.Optimization` (depends on PowerFlow). Costs are polynomial `C(P) = C2·P² + C1·P + C0` [$/h] with P in MW
(`PolynomialCost` on each `Generator`).

New in v1.2: [AC optimal power flow](#ac-optimal-power-flow-v12) with a primal-dual interior point, [LMP decomposition](#lmp-decomposition)
into energy, loss, congestion and voltage components, a more robust DC-OPF start, and the `LinearModel` LP/MILP layer with built-in and
external solvers ([LP / MILP solvers](solvers.md)). Security-constrained unit commitment and market clearing live in the new
[Electrical.Markets](markets.md) package.

## Economic dispatch

Equal-incremental-cost (λ-iteration) dispatch with unit limits; optional B-coefficient losses.

```csharp
var net = TestCases.Case9();
var ed = EconomicDispatch.Solve(net);                      // lossless, equal incremental cost
Console.WriteLine($"λ = {ed.Lambda:0.000} $/MWh, P = {string.Join(" / ", ed.OutputMw.Select(p => p.ToString("0.00")))} MW");
// λ = 24.044 $/MWh, P = 86.56 / 134.38 / 94.06 MW

var lossy = EconomicDispatch.SolveWithLosses(net);        // B-coefficients from AC power flow, iterated
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

`LossCoefficients.FromPowerFlow` derives the Kron loss formula at the operating point (exact there, as the tests check). For reference,
MATPOWER's AC-OPF optimum for case9 is 5296.69 $/h; the B-loss dispatch is within 0.5 % of it.

## DC optimal power flow and LMPs

`DcOpf.Solve` is a PTDF-formulation DC-OPF with quadratic costs, generator limits and branch limits, solved with the interior-point
`QpSolver`. LMPs follow from the duals: LMP<sub>i</sub> = λ − Σ μ<sub>c</sub>·PTDF<sub>c,i</sub>.

```csharp
var net = TestCases.Case9();
int line = net.Branches.FindIndex(b => (b.From == 5 && b.To == 6) || (b.From == 6 && b.To == 5));
var opf = DcOpf.Solve(net, new DcOpfOptions { RatingOverrides = new Dictionary<int, double> { [line] = 60 } });
Console.WriteLine($"Cost {opf.TotalCost:0.00} $/h, λ {opf.Lambda:0.00} $/MWh");
foreach (var id in opf.BusIds) Console.WriteLine($"LMP bus {id}: {opf.LmpAt(id):0.00} $/MWh");
foreach (var c in opf.Binding) Console.WriteLine($"{c.Kind} {c.Description}: μ = {c.ShadowPrice:0.00} $/MWh");

var scopf = DcOpf.SolveSecurityConstrained(net, contingencies: [0, 3, 6], emergencyRatingFactor: 1.2);
```

- Without congestion the DC-OPF equals the lossless economic dispatch (5216.03 $/h, uniform LMP 24.04 $/MWh on case9).
- Under congestion the LMPs separate; the tests check that the LMP at a bus equals the finite-difference marginal cost of serving ±0.5 MW there.
- `DcOpfResult` also reports `FlowsMw`, `DispatchMw`, `Binding` constraints ("Flow", "ContingencyFlow", "Pmax", "Pmin") and `CongestionRent`.
- Security-constrained mode adds post-contingency limits f<sub>l</sub> + LODF<sub>l,k</sub>·f<sub>k</sub> ≤ emergency rating for each
  listed outage; the tests verify each secured outage with a post-contingency DC power flow.

- **v1.2 robustness fix**: the interior-point `QpSolver` used to start from x = 0, which stalled on larger grids (`SyntheticGrid(300)`: 100
  iterations and 205 062 $/h instead of the economic-dispatch optimum 186 364 $/h). DC-OPF now starts from a Pmax-proportional dispatch that
  meets the power balance, with central-path slacks and duals; the 300-bus grid converges in 8 iterations to the economic-dispatch cost
  (regression test for 100 and 300 buses). `QpSolver.Solve(qp, initialX: ...)` accepts your own starting point.

CLI: `eln opf --case case9 [--limit 5-6:60]` (the limit override is in MW).

## AC optimal power flow (v1.2)

`AcOpf.Solve` is the polar AC-OPF of MATPOWER's `runopf`: minimise Σ C<sub>i</sub>(P<sub>g,i</sub>) [$/h] subject to the full AC power balance
(P and Q) at every bus, bus voltage limits, generator P/Q limits and branch apparent-power limits |S| ≤ RateA at both ends (squared form).
Loads are constant power. It is solved by `InteriorPointSolver`, a primal-dual interior-point method with exact Hessians (MATPOWER MIPS
style) and a sparse KKT factorisation whose fill-reducing ordering is reused between iterations.

```csharp
using Electrical.Core.Network;
using Electrical.Optimization;

var net = TestCases.Case9();
var opf = AcOpf.Solve(net);                                   // MATPOWER runopf formulation, flat start
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

The cost and dispatch are MATPOWER's (5296.69 $/h; 89.80 / 134.32 / 94.19 MW). The DC-OPF of the same case costs 5216.03 $/h: the AC
optimum pays for 3.31 MW of losses and respects the voltage limits (buses 1, 6 and 8 at |V| = 1.1 pu).

`AcOpfOptions`: `Tolerance` (1e-6), `MaxIterations` (150), `Start` (`Flat` like MATPOWER, `PowerFlow` warm start from Newton–Raphson, or
`Network` for the stored set-points), `EnforceLineLimits`, `RatingFactor`, `RatingOverrides` [MVA per branch index], `StepControl`,
`CostScaling`, `BindingThreshold`.

`AcOpfResult` holds `TotalCost` [$/h], `PgMw`, `QgMvar`, `Vm` [pu], `VaDeg`, `Lmp` [$/MWh] (the P-balance multipliers), `LmpQ` [$/Mvarh],
`Branches` (flows at both ends, enforced rating), branch shadow prices (`BranchShadowPriceFrom/To`, $/MVAh), voltage shadow prices
(`VmaxShadowPrice`/`VminShadowPrice`), generator limit shadow prices (`Pmax/Pmin/Qmax/QminShadowPrice`), `Binding` (kinds "Flow", "Vmax",
"Vmin", "Pmax", "Pmin", "Qmax", "Qmin"), `LossesMw`, `MaxMismatchMw`, `Iterations`, `Status` and the raw `Solver` result with its
convergence `History`.

### Congestion, decomposition and the single-line diagram

```csharp
var net = TestCases.Case9();
int line = net.Branches.FindIndex(b => b.From == 5 && b.To == 6);   // index 2
var opf = AcOpf.Solve(net, new AcOpfOptions { RatingOverrides = new Dictionary<int, double> { [line] = 42 } });  // MVA
var lmp = opf.Decompose();                                    // reference bus = OPF slack (bus 1)
for (int i = 0; i < lmp.BusIds.Length; i++)
    Console.WriteLine($"bus {lmp.BusIds[i]}: {lmp.Lmp[i]:0.00} = {lmp.Energy[i]:0.00} + {lmp.Loss[i]:0.00} (loss) " +
                      $"+ {lmp.Congestion[i]:0.00} (congestion) + {lmp.Voltage[i]:0.00} (voltage)");
Console.WriteLine($"shadow price 5-6: {opf.BranchShadowPriceFrom[line]:0.00} / {opf.BranchShadowPriceTo[line]:0.00} $/MVAh");

var pf = opf.ToPowerFlowResult();                             // PowerFlowResult view of the optimum
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

Holding line 5–6 at 42 MVA raises the cost to 5472.70 $/h and splits the prices from 18.84 to 33.82 $/MWh; congestion dominates the spread,
losses move prices by less than 1 $/MWh. `ToPowerFlowResult()` converts the optimum into a regular `PowerFlowResult` (bus voltages, branch
flows, generator outputs), so single-line diagrams, reports and `ApplyTo` warm starts work unchanged. The gallery demo `acopf` draws it.

AC vs DC on case14 (MATPOWER: 8081.53 $/h, λ<sub>P</sub>(bus 1) = 36.724 $/MWh), with a power-flow warm start:

```csharp
var net = TestCases.Case14();
var ac = AcOpf.Solve(net, new AcOpfOptions { Start = AcOpfStart.PowerFlow, Tolerance = 1e-8 });
var dc = DcOpf.Solve(net);
Console.WriteLine($"AC {ac.TotalCost:0.00} $/h ({ac.Iterations} it, {ac.LossesMw:0.000} MW losses), DC {dc.TotalCost:0.00} $/h");
Console.WriteLine($"LMP bus 1: AC {ac.LmpAt(1):0.000}, DC {dc.LmpAt(1):0.000} $/MWh");
var dcParts = LmpDecomposition.Decompose(dc);                 // energy = λ, congestion = LMP − λ, loss = 0
```

```
AC 8081.53 $/h (12 it, 9.287 MW losses), DC 7642.59 $/h
LMP bus 1: AC 36.724, DC 39.016 $/MWh
```

With dependency injection, `AddOptimization()` registers `IAcOpfService` (`Solve`, `Decompose`):

```csharp
using var sp = new ServiceCollection().AddElectrical(b => b.AddOptimization()).BuildServiceProvider();
var acopf = sp.GetRequiredService<IAcOpfService>();
var r = acopf.Solve(TestCases.Case14());
Console.WriteLine($"{r.TotalCost:0.00} $/h, LMP bus 1 {r.LmpAt(1):0.000} $/MWh");   // 8081.53 $/h, LMP bus 1 36.724 $/MWh
```

## LMP decomposition

`LmpDecomposition.Decompose(AcOpfResult, referenceBusId?)` (also `opf.Decompose()`) splits every AC LMP into
**LMP = energy + loss + congestion + voltage** (`LmpComponents`):

- **energy**: the price at the reference bus (default the OPF slack), equal at every bus;
- **loss**: −LF<sub>i</sub>·λ<sub>ref</sub>, with marginal loss factors LF<sub>i</sub> = ∂P<sub>loss</sub>/∂P<sub>i</sub> taken from the AC
  Jacobian at the optimum (`LossFactors`, `DeliveryFactors` = 1 − LF);
- **congestion**: the contribution of binding branch MVA limits;
- **voltage**: the contribution of binding |V| limits (zero when no voltage limit binds), which AC prices carry and DC prices cannot.

It solves the stationarity condition of the OPF Lagrangian with respect to the voltages, J<sub>V</sub>ᵀ·λ + J<sub>h,V</sub>ᵀ·μ + ν<sub>V</sub> = 0,
for λ with the reference price as parameter. `MaxResidual` reports |LMP − Σ components| (below 1e-3 $/MWh in the tests). The tests also
check every LMP against the finite-difference marginal cost of ±0.5 MW of load (within 1 %), congested and uncongested.

`LmpDecomposition.Decompose(DcOpfResult)` gives the DC split: energy = λ, congestion = LMP − λ, loss = 0. Market results carry the same DC
split as `DcLmpComponents` ([Electricity markets](markets.md#lmp-components)).

## Interior-point solver for your own NLPs

`InteriorPointSolver.Solve(NonlinearProgram, x0, InteriorPointOptions?)` solves min f(x) s.t. g(x) = 0, h(x) ≤ 0, x<sub>min</sub> ≤ x ≤ x<sub>max</sub>
from callbacks: objective + gradient, constraints, sparse Jacobians and the Hessian of the Lagrangian ∇²(σ·f + λᵀg + μᵀh). Return matrices
with a constant sparsity pattern so the KKT ordering is reused.

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

The exact optimum is (0, 1) with x0 on its lower bound; the solver stops at the 1e-6 default tolerances (tighten
`InteriorPointOptions.FeasibilityTolerance` and the other tolerances for more digits). `InteriorPointResult` gives `X`, `Lambda`
(equalities), `Mu` (inequalities), `MuLower`/`MuUpper` (bounds), `History` and `Status`.

## Unit commitment

For a MILP unit commitment with network constraints, N-1 security, ramps and prices, use `SecurityConstrainedUnitCommitment` in
[Electrical.Markets](markets.md) (v1.2). The priority-list DP below stays as a fast heuristic and reference.

Forward dynamic programming over priority-list states (Wood & Wollenberg §4.2), with start-up costs, minimum up/down times and a spinning
reserve requirement.

```csharp
var units = UnitCommitment.WoodWollenbergThreeUnits();
double[] demand = [450, 420, 400, 400, 450, 550, 700, 850, 950, 1000, 1050, 1050, 1000, 1000, 950, 900, 950, 1050, 1100, 1000, 850, 700, 550, 480];
var uc = UnitCommitment.Solve(units, demand, reserveFraction: 0.05);
Console.WriteLine($"Feasible {uc.Feasible}, total {uc.TotalCost:0} $, priority {string.Join(",", uc.PriorityOrder.Select(i => i + 1))}");
```

The W&W Example 4A units have full-load average costs 9.79, 9.40 and 11.19 $/MWh, giving priority 2, 1, 3. `UnitCommitmentResult` holds
`Status[unit, hour]`, `DispatchMw`, `Lambda`, `SpinningReserveMw`, production and start-up costs. `eln uc` prints a 24-hour schedule.

## Capacitor placement and general optimizers

```csharp
var caps = CapacitorPlacement.Optimize(TestCases.Case33BW(), maxCapacitors: 3);
Console.WriteLine($"{caps.LossesBeforeMw * 1000:0.0} → {caps.LossesAfterMw * 1000:0.0} kW");

var rosen = NelderMead.Minimize(x => Math.Pow(1 - x[0], 2) + 100 * Math.Pow(x[1] - x[0] * x[0], 2), [-1.2, 1.0]);
```

Capacitor placement ranks candidate buses by loss sensitivity and sizes banks in `stepMvar` steps with AC power flows. `eln caps` gives
bus 13 400 kvar, bus 24 550 kvar, bus 30 1150 kvar: 202.7 → 132.2 kW (the literature reports 132–145 kW for three banks).

`QuadraticProgram`/`QpSolver` (primal-dual interior point, equality and inequality constraints, duals), `NelderMead` and `ParticleSwarm`
(deterministic seed) are public for your own problems.

## Validation (v1.2)

| Check | Reference | Result |
|---|---|---|
| AC-OPF case9 | MATPOWER 7 `runopf` (MIPS): 5296.69 $/h, Pg 89.80 / 134.32 / 94.19 MW | identical (±0.01) |
| AC-OPF case14 | MATPOWER: 8081.53 $/h, Pg 194.33 / 36.72 / 28.74 / 0 / 8.50 MW, λ<sub>P</sub>(1) 36.724 $/MWh | identical; < 1 s including JIT |
| AC LMPs, congested and uncongested | Finite difference of the optimal cost, ±0.5 MW of load | within 1 % |
| AC LMP decomposition | Sum of components; binding Vmax at buses 1, 6, 8 (as MATPOWER) | residual < 1e-3 $/MWh |
| Flat start, warm start, step control | Same optimum | equal to 0.01 $/h |
| DC-OPF `SyntheticGrid(300)` | Economic dispatch (uncongested) | equal cost in 8 iterations (was 100, not converged) |

## Limitations

AC-OPF: polar formulation with constant-power loads, no discrete controls (taps, switched shunts), no security constraints and no
piecewise-linear generator costs; a flat start can fail on very large or badly scaled cases (try `Start = AcOpfStart.PowerFlow` or
`StepControl = true`). MILP unit commitment and markets are in [Electrical.Markets](markets.md).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/optimasi.md)*
