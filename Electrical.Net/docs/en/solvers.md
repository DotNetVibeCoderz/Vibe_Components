# LP / MILP solvers

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/solver.md)

New in v1.2. Linear and mixed-integer models are written once with `LinearModel` (package `Electrical.Optimization`) and solved by any
`ILpSolver` / `IMilpSolver`. Electrical.Net ships a dependency-free built-in solver pair and two optional adapters for external solvers:

| Package | Solver | Interfaces | Native code | AOT |
|---|---|---|---|---|
| `Electrical.Optimization` | `SimplexSolver` (bounded dual simplex) and `BranchAndBoundSolver` | `ILpSolver`, `IMilpSolver` + `ILpSolver` | none | yes |
| `Electrical.Optimization.Highs` | `HighsSolver` (HiGHS 1.16.0 via `Highs.Native`) | `IMilpSolver` + `ILpSolver` | HiGHS (`highs-native.*`) | no |
| `Electrical.Optimization.OrTools` | `OrToolsSolver` (Google OR-Tools 9.15.6755: SCIP, CBC, GLOP, PDLP …) | `IMilpSolver` + `ILpSolver` | OR-Tools runtime | no |

Every solver returns a `LinearSolution` with the same conventions, so results (including LP duals) are interchangeable. Unit commitment
and market clearing in [Electrical.Markets](markets.md) take the solver through `ScucOptions.Solver`.

## Building a model

`LinearModel` holds continuous, integer and binary variables with bounds, linear rows (`≤`, `=`, `≥`) and a linear objective. Expressions
are built with operators (`3 * x + 2 * y`) or incrementally with `LinearExpression.Add`.

```csharp
using Electrical.Optimization;

// Hillier & Lieberman "Wyndor Glass": max 3x + 5y, x ≤ 4, 2y ≤ 12, 3x + 2y ≤ 18
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

A 0/1 knapsack as a MILP:

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

Other model operations: `SetBounds`, `SetType`, `SetRhs`, `SetObjectiveCoefficient`, `Clone`, `FixIntegers` (the LP with the integers fixed
at a solution, used for pricing), `EvaluateObjective` and `MaxViolation`.

## Solution conventions

`LinearSolution` carries `Status` (`Optimal`, `Feasible`, `Infeasible`, `Unbounded`, `TimeLimit`, `IterationLimit`, `Error`), `Objective`,
`Values`, `BestBound`, `MipGap`, `Iterations`, `Nodes`, `SolveTime` and the solver name. For LPs it also has `Duals` and `ReducedCosts`
with one solver-independent convention: **dual = ∂objective/∂rhs** in the model's own objective sense. For a minimisation, a binding `≤` row
has a dual ≤ 0 and a binding `≥` row a dual ≥ 0. The adapters convert HiGHS and OR-Tools duals to this convention, so a market model priced
with any solver gives the same LMPs.

`SolverOptions`: `TimeLimit`, `RelativeGap` (default 1e-6), `Threads` (external solvers), `Verbose`, `MaxNodes` (built-in).

## Built-in solvers

- **`SimplexSolver`**: bounded dual simplex (one logical per row carrying the row bounds), explicit dense basis inverse with product-form
  updates and periodic refactorisation, dual steepest-edge pricing, Harris' two-pass ratio test and Bland's rule as an anti-cycling
  fallback. Returns duals and reduced costs. Validated on textbook LPs (Wyndor, mixed senses, free variables) and against HiGHS / OR-Tools.
- **`BranchAndBoundSolver`**: LP-based branch and bound that warm-starts the dual simplex from the parent basis, dives depth-first for an
  early incumbent and resumes the best-bound open node when a dive ends, branches on pseudo-costs (product score) and prunes against the
  incumbent. It implements both `IMilpSolver` and `ILpSolver` (LPs go to the simplex).
  Brute-force checks: knapsacks and a 3-hour unit commitment enumerated over all 512 commitments.

They are fully managed, AOT-compatible and deterministic. They are the defaults of `AddMarkets()` and of `ScucOptions` when no solver is
given.

## External solvers: HiGHS and OR-Tools

```bash
dotnet add package Electrical.Optimization.Highs      # HiGHS (MIT), native library for win/linux/osx x64/arm64
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

- `HighsSolver { LpAlgorithm = "simplex" | "ipm" | "pdlp" | "choose" }`: LPs with HiGHS' dual simplex by default, MILPs with its branch and
  cut.
- `OrToolsSolver { MipBackend = "SCIP" | "CBC" | "SAT", LpBackend = "GLOP" | "PDLP" | "CLP" }`: MILPs with SCIP by default, LPs with GLOP
  (duals from `DualValue()`).

The same 24 h Wood & Wollenberg unit commitment (72 binaries, 4-segment costs, 5 % reserve) with each solver:

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

(Times from one run on a desktop PC; the test suite asserts equal objectives and equal LMPs across the three solvers.)

### Dependency injection

`AddMarkets()` registers the built-in `BranchAndBoundSolver` as `IMilpSolver` and `SimplexSolver` as `ILpSolver` unless a solver is already
registered. `AddHighs()` / `AddOrTools()` replace them, before or after `AddMarkets()`:

```csharp
using var sp = new ServiceCollection()
    .AddElectrical(b => b.AddMarkets().AddHighs())            // or .AddOrTools("SCIP", "GLOP"); omit for the built-in solvers
    .BuildServiceProvider();
var market = sp.GetRequiredService<IMarketService>();
Console.WriteLine(market.Solver.Name);                        // HiGHS
```

## Which solver to use

| Situation | Recommendation |
|---|---|
| LPs (DC-OPF-like pricing runs, small dispatch problems), teaching, AOT / trimmed / WebAssembly apps, no native binaries allowed | Built-in `SimplexSolver` |
| Small MILPs: a few units over a day, single-period market clearing, studies that must run anywhere | Built-in `BranchAndBoundSolver` |
| Day-ahead SCUC with a network, N-1 security rows, tens of units or more, tight MIP gaps, time limits that matter | `HighsSolver` (one native library, MIT licence, fast LP and MIP) |
| You already use OR-Tools, want SCIP/CBC or PDLP for very large LPs | `OrToolsSolver` |

**The built-in MILP solver is for small models and teaching.** It has no cutting planes or presolve, and the LP relaxation of unit
commitment is weak, so its run time grows quickly with the number of binaries and network/contingency rows. On the 24-hour case9 SCUC of
`eln scuc` (units from the network generators, 10 % reserve) it stopped at an 18.6 % gap after 20 s, while HiGHS proved the 92 701.61 $
optimum in about one second. **Use HiGHS or OR-Tools for real SCUC and day-ahead markets**; `eln scuc` uses HiGHS by default
(`--solver builtin` to compare). Because all three share `LinearModel` and the dual convention, you can develop with the built-in solver and
switch with one line.

## Native library note

- `Electrical.Optimization.Highs` and `Electrical.Optimization.OrTools` are **not AOT-compatible** (`IsAotCompatible=false`) and pull native
  binaries; the core `Electrical.Optimization` and `Electrical.Markets` packages stay managed-only.
- Both `Highs.Native` and `Google.OrTools.runtime.*` ship a file named `runtimes/<rid>/native/highs.dll` (different HiGHS builds with
  different ABIs). In one output folder they overwrite each other and one solver fails to load. The HiGHS adapter therefore excludes the
  package's native asset, re-ships it as `runtimes/<rid>/native/highs-native.{dll,so,dylib}` and resolves it with a `DllImport` resolver.
  Both adapters can be referenced by the same application; nothing needs to be configured.
- Supported RIDs: win-x64/x86/arm64, linux-x64/arm64, osx-x64/arm64 (HiGHS); the platforms of the Google.OrTools runtime packages.

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/solver.md)*
