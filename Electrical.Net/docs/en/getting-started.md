# Getting started

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/memulai.md)

## Requirements

- .NET 10 SDK.
- Optional: Rust stable toolchain if you want to build the native kernels yourself. The `Electrical.Native` package ships prebuilt binaries
  for win-x64, linux-x64, linux-arm64, osx-x64 and osx-arm64, and every kernel has a managed fallback.

## Install

Packages follow the module names. Each one pulls its dependencies (for example `Electrical.Protection` brings `Electrical.ShortCircuit` and
`Electrical.Core`).

```bash
dotnet add package Electrical.PowerFlow
dotnet add package Electrical.ShortCircuit
dotnet add package Electrical.Protection
dotnet add package Electrical.Stability
dotnet add package Electrical.Visualization
# … Electrical.Circuit, .Harmonic, .Renewable, .SmartGrid, .Optimization, .StateEstimation, .SCADA, .IEC61850, .CIM,
#   .Forecasting, .GIS, .Simulation, .Cloud, .HPC, .Native, .Visualization.Blazor, .DigitalTwin, .AI
```

Tools:

```bash
dotnet tool install -g Electrical.Cli        # the "eln" command
dotnet new install Electrical.Templates      # eln-console, eln-blazor, eln-avalonia, eln-scada, eln-webapi
```

## First program

```csharp
using Electrical.Core.Network;
using Electrical.PowerFlow;

var net = TestCases.Case14();                 // IEEE 14-bus (MATPOWER data)
var pf = PowerFlowSolver.Solve(net);          // sparse Newton–Raphson
Console.WriteLine(pf);
// NewtonRaphson: converged in 3 it, mismatch 3.44E-009, gen 272.393 MW, load 259 MW, losses 13.393 MW, Vmin 1.0100 pu @ bus 3

foreach (var b in pf.Buses)
    Console.WriteLine($"Bus {b.BusId,2}: {b.Vm:0.0000} pu ∠ {b.VaDeg,8:0.000}°");
```

Built-in networks (`TestCases.ByName` accepts the names in `TestCases.Names`):

| Name | Content |
|---|---|
| `case9` | WSCC 3-machine 9-bus with the Anderson & Fouad operating point, MATPOWER costs, classical machine data |
| `case14` | IEEE 14-bus (MATPOWER) |
| `case33bw` | Baran & Wu 33-bus radial distribution feeder, 12.66 kV, base 10 MVA |
| `synthetic<N>` | Deterministic meshed grid with N buses and geographic locations (`TestCases.SyntheticGrid(n, seed)`) |

Load your own data with `Matpower.Load("case.m")`, `CimImporter.Load("grid.xml")`, `GeoJsonImporter.Import(json)` or
`NetworkJson.Parse(json)` (Electrical.Cloud).

## Building a network in physical units

`NetworkBuilder` takes kV, Ω, km, nameplate MVA and uk %, and converts to per unit on the system base:

```csharp
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.ShortCircuit;

var net = new NetworkBuilder("plant", baseMva: 100, frequencyHz: 50)
    .AddBus(1, 150, BusType.Slack, "Grid 150 kV")
    .AddBus(2, 20, name: "MV bus")
    .AddBus(3, 20, name: "Feeder end")
    .AddGenerator(1, 0, vset: 1.0)                                   // slack infeed
    .AddTransformer(1, 2, ratingMva: 60, ukPercent: 12, copperLossKw: 250)
    .AddLine(2, 3, 8, "NA2XS2Y 1x240 RM/25 12/20 kV")               // 8 km standard cable
    .AddLoad(3, 18, 6)                                               // 18 MW, 6 Mvar
    .AddShunt(3, 3)                                                  // 3 Mvar capacitor bank
    .Build();                                                        // validates the model

var pf = PowerFlowSolver.Solve(net);
Console.WriteLine($"V3 = {pf.Bus(3).Vm:0.000} pu, losses {pf.LossesMw:0.000} MW");

var sc = ShortCircuitAnalysis.Calculate(net, 3, FaultType.ThreePhase, new ShortCircuitOptions
{
    ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 3000, RxRatio = 0.1 }],
});
Console.WriteLine(sc);   // I"k, ip, Ib, Ith, S"k at bus 3
```

Other builder methods: `AddLine(from, to, lengthKm, rOhmPerKm, xOhmPerKm, cNanoFPerKm, rateMva)`, `AddLineOhm(...)` for total Ω/µF,
`AddGenerator(..., cost: new PolynomialCost(c2, c1, c0))`. `LineLibrary.All` lists the standard conductor and cable types
(NAYY, NA2XS2Y, AL/ST, ACSR Hawk/Drake, AAAC) and `LineLibrary.Register` adds your own.

You can also edit the model directly: `PowerNetwork.Buses / Loads / Generators / Branches` are mutable lists, `net.Clone()` makes a deep copy for
what-if studies and `net.Validate()` returns human-readable data problems.

## Dependency injection

Every module has an `AddX()` extension on `ElectricalBuilder`. Registration is idempotent.

```csharp
using Electrical.Core.Hosting;
using Electrical.PowerFlow;
using Electrical.ShortCircuit;
using Electrical.Stability;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddElectrical(b => b.AddPowerFlow().AddShortCircuit().AddStability())
    .BuildServiceProvider();

var pf = services.GetRequiredService<IPowerFlowService>().Solve(TestCases.Case9());
var modules = services.GetRequiredService<ElectricalModuleRegistry>().Modules; // Core, PowerFlow, ShortCircuit, Stability
```

| Extension | Registers |
|---|---|
| `AddElectrical()` | `IGridEventBus` (in-memory), `TimeProvider.System`, `ElectricalModuleRegistry` |
| `AddCircuit()`, `AddPowerFlow()`, `AddShortCircuit()`, `AddStability()`, `AddHarmonic()`, `AddProtection()`, `AddRenewable()`, `AddSmartGrid()`, `AddOptimization()`, `AddStateEstimation()`, `AddForecasting()`, `AddSimulation()`, `AddIec61850()`, `AddCim()`, `AddGis()` | `ICircuitService`, `IPowerFlowService`, `IShortCircuitService`, … (one facade interface per module) |
| `AddScada(historianDefaults)` | `TagDatabase`, `AlarmManager`, `Historian` wired through the event bus |
| `AddVisualization()` | `IDiagramRenderer` |
| `AddCloud(workers)` | `StudyRunner`, `IScenarioStore` (in memory) |
| `AddHpc()` | module marker (the HPC API is static) |
| `AddDigitalTwin()` | `IGridTwinFactory` (plus StateEstimation, SCADA, Forecasting) |
| `AddAI(llmOptions?)` | `IChatModel` (environment or offline), `GridAgentContext`, transient `GridAgent` (plus the modules its tools use) |

The static entry points (`PowerFlowSolver.Solve`, `ShortCircuitAnalysis.Calculate`, …) need no DI. `ServiceCollection` comes from the
`Microsoft.Extensions.DependencyInjection` package.

## Units and conventions

- Power in MW / Mvar / MVA, voltage in kV (line-to-line) or pu, current in kA (A for relays), impedance in Ω or pu on the network
  `BaseMva`, angles in degrees at the API (radians internally), time in seconds, frequency in Hz.
- `Branch` follows MATPOWER: π model in pu, `B` is total line charging, a non-zero `Tap` (≠ 1) or a phase shift marks a transformer.
- Bus shunts `Gs`/`Bs` are MW/Mvar at 1 pu (capacitor = positive `Bs`).
- Generator machine data (`Xd`, `XdPrime`, `XdSubtransient`, `X2`, `X0`, `H`, `D`) is in pu on `MBase`.
- Synthetic data (test grids, profiles, noise, Monte-Carlo) is seeded and identical across processes.

## Next

- [Module guide](modules.md) for every package.
- [CLI](cli.md) to try studies without writing code.
- [Gallery](gallery.md) to see the library running.

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/memulai.md)*
