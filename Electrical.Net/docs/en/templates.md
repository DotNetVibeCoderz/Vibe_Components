# Project templates

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/templat.md)

```bash
dotnet new install Electrical.Templates
dotnet new list eln
```

| Short name | Template | Packages | Parameters |
|---|---|---|---|
| `eln-console` | Power-system study (console): power flow with Q limits, N-1 ranking, IEC 60909 scan, probabilistic load flow, single-line SVG | Electrical.HPC, .ShortCircuit, .Visualization | `--case case9\|case14\|case33bw` (default case14) |
| `eln-blazor` | Blazor Server grid dashboard: live single-line diagram, KPI cards and a load-scaling what-if slider | Electrical.Visualization.Blazor | `--case` |
| `eln-avalonia` | Avalonia desktop single-line viewer with power-flow overlay | Electrical.Visualization, Avalonia 12, Svg.Controls.Skia.Avalonia | `--case` |
| `eln-scada` | SCADA edge gateway: polls a Modbus TCP RTU, scales tags, evaluates ISA-18.2 alarms and historizes values (built-in RTU simulator when no RTU is given) | Electrical.SCADA | – |
| `eln-webapi` | ASP.NET Core minimal API exposing power flow, contingency, short circuit and study jobs | Electrical.Cloud | – |
| `eln-distribution` | Unbalanced three-phase feeder study (console): IEEE 13-node or an OpenDSS script, per-phase voltages, regulator control, Carson line impedances and a 24 h time series with controllers | Electrical.Distribution | – (pass a `.dss` path to `dotnet run`) |

```bash
dotnet new eln-console -n GridStudy --case case33bw
cd GridStudy && dotnet run          # prints results, writes single-line.svg

dotnet new eln-blazor -n GridDashboard
dotnet new eln-avalonia -n GridViewer --case case9
dotnet new eln-scada -n ScadaGateway
dotnet run --project ScadaGateway -- --rtu 127.0.0.1 --port 1502   # poll a real RTU (or `eln rtu --port 1502`)
dotnet new eln-webapi -n GridApi
dotnet new eln-distribution -n FeederStudy
cd FeederStudy && dotnet run -- feeder.dss   # or just `dotnet run` for the IEEE 13-node feeder
```

## What each template contains

**eln-console** (`Program.cs`): loads `TestCases.ByName("<case>")` (swap for `Matpower.Load("mycase.m")`), solves an AC power flow with
`EnforceQLimits`, prints bus results, ranks the five worst N-1 outages, prints the IEC 60909 scan, runs a 300-sample probabilistic load flow
(reporting the native/managed backend) and saves `single-line.svg` with `SvgTheme.Resolve(SldRenderer.Render(net, pf), dark: false)`.

**eln-blazor**: a Blazor Server app with `Dashboard.razor` using `KpiCard` and `SingleLineDiagram` from `Electrical.Visualization.Blazor`;
moving the slider scales all loads and re-solves the power flow.

**eln-avalonia**: a desktop window that renders the SLD SVG through `Svg.Controls.Skia.Avalonia`.

**eln-scada**: starts an `RtuSimulator` (IEEE 14-bus) on a loopback `ModbusTcpServer` unless `--rtu <host> --port <port>` is given, creates
a `TagDatabase`, an `AlarmManager` (HH 1.10 / H 1.06 / L 0.95 / LL 0.90 pu on the bus voltages) and a `Historian`, then scans five cycles
and prints alarms and trends.

**eln-webapi**: `WebApplication.CreateSlimBuilder` + `AddElectrical(b => b.AddPowerFlow().AddCloud(workers: 2))` + `MapElectricalApi()`;
try `curl -X POST http://localhost:5000/api/electrical/studies/powerflow -H "Content-Type: application/json" -d '{"case":"case14"}'`
(use the URL printed by `dotnet run`). See [Cloud API](cloud-api.md).

**eln-distribution** (`Program.cs`): loads `DistributionTestCases.Ieee13Node(automaticRegulator: true)` or `OpenDssReader.ParseFile(args[0])`,
prints reader warnings, solves `UnbalancedPowerFlow.Solve` (regulator taps, per-phase bus voltages, maximum voltage unbalance), computes the
configuration 601 impedance from conductor data with `LineConstants.Overhead`, and runs `DistributionTimeSeries.Run` over
`DailyLoadShape()` (energy, losses, voltage band, tap operations and capacitor switchings). See [Unbalanced distribution](distribution.md).

Every generated project carries the credit "built with Electrical.Net (Gravicode Studios, led by Kang Fadhil)".

## Building the template pack

The templates live in `templates/Electrical.Templates/content/<short-name>` with `.template.config/template.json` (`sourceName`
GridViewer, GridDashboard, ElectricalStudyApp, ScadaGateway, GridApi, FeederStudy). `pwsh build/pack.ps1` packs them as `Electrical.Templates`; install a
local build with `dotnet new install artifacts/packages/Electrical.Templates.1.1.0.nupkg`. The generated projects reference the
`Electrical.*` NuGet packages at version 1.1.0.

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/templat.md)*
