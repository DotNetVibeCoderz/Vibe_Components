# Electrical.Net: development roadmap / peta jalan pengembangan

> Made by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

**Vision / Visi:** the ML.NET of electrical engineering and the ASP.NET Core of power systems & smart grid. It is meant as a base for EMS, DMS, ADMS,
DERMS, VPP, SCADA, digital twins and smart grids on .NET.
*Menjadi ML.NET untuk dunia teknik elektro dan ASP.NET Core untuk sistem tenaga & smart grid.*

## v1.0: Foundation ✅
- Core: sparse/dense complex linear algebra, per-unit, phasors and symmetrical components, bus-branch network model, MATPOWER I/O, IEEE test cases
- 22 modules from `solution-design.md`: Circuit, PowerFlow, ShortCircuit, Stability, Harmonic, Protection, Renewable, SmartGrid, Optimization,
  StateEstimation, SCADA, IEC61850, CIM, DigitalTwin, Forecasting, AI, GIS, Visualization, Simulation, Cloud, HPC, plus Native (Rust)
- Gallery (Blazor + Avalonia), CLI tool `eln`, project templates, bilingual docs, NuGet packages and CI

## v1.1: Distribution depth (current) ✅
- Electrical.Distribution: unbalanced three-phase power flow (ladder sweep + current injection) with single/two-phase laterals, line geometry
  (modified Carson's equations, Kron reduction, concentric-neutral and tape-shielded cables), three-phase transformers, step-voltage regulators
  with line-drop compensation, OLTC and capacitor controllers, daily time series, IEEE 4-node and 13-node feeders, OpenDSS `.dss` reader
- Regulators, OLTC control loops and capacitor controllers in balanced QSTS (Electrical.Simulation)
- Electrical.Interop: PSS/E `.raw` (v33/v35) reader/writer, IEEE CDF reader, format auto-detection
- Newton–Raphson flat start initialised from a DC power flow (robust with large phase shifters)
- Still open from this theme: three-winding / centre-tap / open-delta transformers, PSS/E `.raw` writer for three-winding units, OpenDSS
  `XfmrCode`/energy meters, PSS/E `.dyr` dynamics

## v1.2: Optimization and markets
- AC optimal power flow (primal-dual interior point with exact Hessians)
- Security-constrained unit commitment (MILP via an external solver adapter: HiGHS / OR-Tools)
- Market clearing with LMP decomposition (energy, congestion, losses)

## v1.3: Dynamics
- Detailed generator models (GENROU/GENSAL), exciters (IEEE ST1A, AC4A), governors (TGOV1, HYGOV), PSS (PSS2B)
- Inverter-based resource models (REGC/REEC/REPC) and grid-forming inverters
- EMT kernel for switching transients (Rust)

## v1.4: Data and AI
- PMU / IEEE C37.118 stream reader; DNP3 outstation codec; full IEC 60870-5-104 client
- CGMES 3.0 profile coverage (EQ/TP/SSH/SV) and validation
- ML models (GravicodeScience / ML.NET / TorchSharp) for load forecasting, fault location and topology identification; RL Volt/VAR agents (Gravicode.RLNet)
- 3D substation digital twin with ThreeNet

## Platform
- Native binaries for win-x64, linux-x64/arm64 and osx-x64/arm64 through CI (in the workflow)
- GPU batch power flow (CUDA via Rust) and distributed Monte-Carlo on Kubernetes (Electrical.Cloud workers)
- BenchmarkDotNet project with performance budgets in CI
