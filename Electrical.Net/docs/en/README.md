# Electrical.Net documentation (English)

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/README.md)

Electrical.Net is a power-system framework for .NET 10: 22 library modules plus Rust native kernels, a CLI tool, project templates and a
Blazor/Avalonia gallery. Start with [Getting started](getting-started.md), then read the guide for the area you work in.

| Guide | Contents |
|---|---|
| [Getting started](getting-started.md) | Install, first program, building networks, dependency injection, units and conventions |
| [Architecture](architecture.md) | Dependency graph, network model, numerics, design rules, performance, native interop |
| [Module guide](modules.md) | Every package: purpose, key types, short example, references and limitations |
| [Power flow](power-flow.md) | Newton–Raphson, fast-decoupled, Gauss–Seidel, DC, backward/forward sweep, ZIP loads, Q limits, N-1, PTDF/LODF |
| [Short circuit & protection](short-circuit-protection.md) | IEC 60909, relays, grading, distance and differential protection, breaker duty, arc flash |
| [Stability](stability.md) | Transient stability, CCT, equal-area criterion, small-signal, PV/QV curves, L-index, frequency response |
| [Harmonics & power quality](harmonics.md) | FFT and spectra, IEEE 519 / IEC 61000, harmonic load flow, frequency scan, filters |
| [Renewables & smart grid](renewables-smartgrid.md) | PV, wind, batteries, grid codes, hosting capacity, DR, EV, microgrid, VPP, VVO, reconfiguration, reliability, forecasting |
| [Optimization](optimization.md) | Economic dispatch, DC-OPF and LMPs, security constraints, unit commitment, capacitor placement |
| [State estimation](state-estimation.md) | WLS estimator, measurements, observability, bad-data detection |
| [SCADA, IEC 61850 & CIM](scada-iec61850-cim.md) | Modbus TCP, tags, alarms, historian, RTU simulator, IEC 104, GOOSE, SV, SCL, CGMES |
| [GIS & visualization](gis-visualization.md) | GeoJSON, maps, spatial index, outage areas, single-line diagrams, charts, Blazor components |
| [Simulation, HPC & native](simulation-hpc-native.md) | Circuit simulation, QSTS, adequacy, batch and probabilistic load flow, Rust kernels |
| [Cloud API](cloud-api.md) | REST endpoints, study jobs, scenario stores, JSON network contract |
| [AI & digital twin](ai-digital-twin.md) | Grid-operator LLM agent and tools, anomaly detection, fault analytics, RL Volt/VAR, live digital twin, what-if, asset health |
| [CLI](cli.md) | `eln` command reference with sample output |
| [Templates](templates.md) | `dotnet new eln-*` templates |
| [Gallery](gallery.md) | Blazor and Avalonia showcase apps, screenshots, adding a demo |

![Gallery](../images/web-home.png)

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/README.md)*
