# Optimization

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/optimasi.md)

Package: `Electrical.Optimization` (depends on PowerFlow). Costs are polynomial `C(P) = C2·P² + C1·P + C0` [$/h] with P in MW
(`PolynomialCost` on each `Generator`).

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

CLI: `eln opf --case case9 [--limit 5-6:60]` (the limit override is in MW).

## Unit commitment

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

## Limitations

DC network model for OPF (no voltages, reactive power or losses); unit commitment is a priority-list DP, not a MILP; no network constraints
in UC. AC-OPF, SCUC with an external MILP solver and market clearing are planned for v1.2.

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/optimasi.md)*
