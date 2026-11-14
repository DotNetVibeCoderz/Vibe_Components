# Memulai

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/getting-started.md)

## Kebutuhan

- .NET 10 SDK.
- Opsional: toolchain Rust stable bila ingin membangun kernel native sendiri. Paket `Electrical.Native` sudah berisi biner siap pakai untuk
  win-x64, linux-x64, linux-arm64, osx-x64, dan osx-arm64, dan setiap kernel punya fallback managed.

## Instalasi

Nama paket mengikuti nama modul. Setiap paket otomatis menarik dependensinya (misalnya `Electrical.Protection` membawa
`Electrical.ShortCircuit` dan `Electrical.Core`).

```bash
dotnet add package Electrical.PowerFlow
dotnet add package Electrical.ShortCircuit
dotnet add package Electrical.Protection
dotnet add package Electrical.Stability
dotnet add package Electrical.Visualization
# … Electrical.Circuit, .Harmonic, .Renewable, .SmartGrid, .Optimization, .StateEstimation, .SCADA, .IEC61850, .CIM,
#   .Forecasting, .GIS, .Simulation, .Cloud, .HPC, .Native, .Visualization.Blazor, .DigitalTwin, .AI,
#   .Markets, .Optimization.Highs, .Optimization.OrTools (v1.2)
```

Tool:

```bash
dotnet tool install -g Electrical.Cli        # perintah "eln"
dotnet new install Electrical.Templates      # eln-console, eln-blazor, eln-avalonia, eln-scada, eln-webapi
```

## Program pertama

```csharp
using Electrical.Core.Network;
using Electrical.PowerFlow;

var net = TestCases.Case14();                 // IEEE 14-bus (data MATPOWER)
var pf = PowerFlowSolver.Solve(net);          // Newton–Raphson sparse
Console.WriteLine(pf);
// NewtonRaphson: converged in 3 it, mismatch 3.44E-009, gen 272.393 MW, load 259 MW, losses 13.393 MW, Vmin 1.0100 pu @ bus 3

foreach (var b in pf.Buses)
    Console.WriteLine($"Bus {b.BusId,2}: {b.Vm:0.0000} pu ∠ {b.VaDeg,8:0.000}°");
```

Jaringan bawaan (`TestCases.ByName` menerima nama-nama di `TestCases.Names`):

| Nama | Isi |
|---|---|
| `case9` | WSCC 3 mesin 9-bus dengan titik operasi Anderson & Fouad, biaya MATPOWER, data mesin klasik |
| `case14` | IEEE 14-bus (MATPOWER) |
| `case33bw` | Penyulang distribusi radial Baran & Wu 33-bus, 12,66 kV, basis 10 MVA |
| `synthetic<N>` | Jaringan mesh deterministik dengan N bus dan lokasi geografis (`TestCases.SyntheticGrid(n, seed)`) |

Muat data Anda sendiri dengan `Matpower.Load("case.m")`, `CimImporter.Load("grid.xml")`, `GeoJsonImporter.Import(json)`, atau
`NetworkJson.Parse(json)` (Electrical.Cloud).

## Membangun jaringan dalam satuan fisik

`NetworkBuilder` menerima kV, Ω, km, MVA pelat nama, dan uk %, lalu mengonversinya ke per-unit pada basis sistem:

```csharp
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.ShortCircuit;

var net = new NetworkBuilder("plant", baseMva: 100, frequencyHz: 50)
    .AddBus(1, 150, BusType.Slack, "Grid 150 kV")
    .AddBus(2, 20, name: "MV bus")
    .AddBus(3, 20, name: "Feeder end")
    .AddGenerator(1, 0, vset: 1.0)                                   // infeed slack
    .AddTransformer(1, 2, ratingMva: 60, ukPercent: 12, copperLossKw: 250)
    .AddLine(2, 3, 8, "NA2XS2Y 1x240 RM/25 12/20 kV")               // kabel standar 8 km
    .AddLoad(3, 18, 6)                                               // 18 MW, 6 Mvar
    .AddShunt(3, 3)                                                  // bank kapasitor 3 Mvar
    .Build();                                                        // memvalidasi model

var pf = PowerFlowSolver.Solve(net);
Console.WriteLine($"V3 = {pf.Bus(3).Vm:0.000} pu, rugi {pf.LossesMw:0.000} MW");

var sc = ShortCircuitAnalysis.Calculate(net, 3, FaultType.ThreePhase, new ShortCircuitOptions
{
    ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 3000, RxRatio = 0.1 }],
});
Console.WriteLine(sc);   // I"k, ip, Ib, Ith, S"k di bus 3
```

Metode builder lain: `AddLine(from, to, lengthKm, rOhmPerKm, xOhmPerKm, cNanoFPerKm, rateMva)`, `AddLineOhm(...)` untuk Ω/µF total,
`AddGenerator(..., cost: new PolynomialCost(c2, c1, c0))`. `LineLibrary.All` berisi tipe penghantar dan kabel standar (NAYY, NA2XS2Y, AL/ST,
ACSR Hawk/Drake, AAAC), dan `LineLibrary.Register` menambahkan tipe Anda sendiri.

Model juga bisa diubah langsung: `PowerNetwork.Buses / Loads / Generators / Branches` adalah list yang bisa diubah, `net.Clone()` membuat
salinan dalam untuk studi what-if, dan `net.Validate()` mengembalikan masalah data dalam bentuk teks.

## Dependency injection

Setiap modul punya extension `AddX()` pada `ElectricalBuilder`. Registrasi bersifat idempoten.

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

| Extension | Mendaftarkan |
|---|---|
| `AddElectrical()` | `IGridEventBus` (in-memory), `TimeProvider.System`, `ElectricalModuleRegistry` |
| `AddCircuit()`, `AddPowerFlow()`, `AddShortCircuit()`, `AddStability()`, `AddHarmonic()`, `AddProtection()`, `AddRenewable()`, `AddSmartGrid()`, `AddOptimization()`, `AddMarkets()`, `AddHighs()`, `AddOrTools()`, `AddStateEstimation()`, `AddForecasting()`, `AddSimulation()`, `AddIec61850()`, `AddCim()`, `AddGis()` | `ICircuitService`, `IPowerFlowService`, `IShortCircuitService`, … (satu interface fasad per modul) |
| `AddScada(historianDefaults)` | `TagDatabase`, `AlarmManager`, `Historian` yang terhubung lewat event bus |
| `AddVisualization()` | `IDiagramRenderer` |
| `AddCloud(workers)` | `StudyRunner`, `IScenarioStore` (in-memory) |
| `AddHpc()` | penanda modul (API HPC bersifat statis) |
| `AddDigitalTwin()` | `IGridTwinFactory` (ditambah StateEstimation, SCADA, Forecasting) |
| `AddAI(llmOptions?)` | `IChatModel` (dari environment atau offline), `GridAgentContext`, `GridAgent` transient (ditambah modul yang dipakai tool-nya) |

Titik masuk statis (`PowerFlowSolver.Solve`, `ShortCircuitAnalysis.Calculate`, …) tidak membutuhkan DI. `ServiceCollection` berasal dari paket
`Microsoft.Extensions.DependencyInjection`.

## Satuan dan konvensi

- Daya dalam MW / Mvar / MVA, tegangan dalam kV (antar-fasa) atau pu, arus dalam kA (A untuk relai), impedansi dalam Ω atau pu pada
  `BaseMva` jaringan, sudut dalam derajat di API (radian di dalam), waktu dalam detik, frekuensi dalam Hz.
- `Branch` mengikuti MATPOWER: model π dalam pu, `B` adalah total charging saluran, `Tap` tidak nol (≠ 1) atau pergeseran fasa menandai trafo.
- Shunt bus `Gs`/`Bs` dalam MW/Mvar pada 1 pu (kapasitor = `Bs` positif).
- Data mesin generator (`Xd`, `XdPrime`, `XdSubtransient`, `X2`, `X0`, `H`, `D`) dalam pu pada `MBase`.
- Data sintetis (jaringan uji, profil, derau, Monte-Carlo) memakai seed tetap dan identik antar proses.

## Selanjutnya

- [Panduan modul](modul.md) untuk setiap paket.
- [CLI](cli.md) untuk mencoba studi tanpa menulis kode.
- [Galeri](galeri.md) untuk melihat pustaka berjalan.

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/getting-started.md)*
