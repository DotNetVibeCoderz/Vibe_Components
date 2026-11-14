# Module guide

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/modul.md)

Each section gives the purpose, the main types, a short example, the references the implementation is validated against and its known
limitations. Namespaces match the package names (`Electrical.PowerFlow`, …); Core is split into `Electrical.Core.Network`, `.Numerics`,
`.Units`, `.Data` and `.Hosting`. Longer walkthroughs are in the topic guides linked from each section.

## Electrical.Core

**Purpose.** Shared foundation: numerics, network model, units, time series, DI and the event bus.

**Key types.** `PowerNetwork`, `Bus`, `Load` (`ZipModel`), `Generator` (`PolynomialCost`, machine data), `Branch`, `NetworkBuilder`, `LineLibrary`,
`AdmittanceMatrix`, `Topology`, `Matpower`, `TestCases`; `DenseMatrix<T>`, `SparseMatrix<T>`, `SparseBuilder<T>`, `SparseLU<T>`;
`PerUnitBase`, `ElectricalFormulas`, `Phasor`, `ThreePhase`, `SymmetricalComponents`; `TimeSeries`, `Stats`; `ElectricalBuilder`,
`IGridEventBus`; `ElectricalInfo` (name, version, credit, module list).

```csharp
var pu = new PerUnitBase(BaseMva: 100, BaseKv: 20);
double zb = pu.BaseOhm;                 // 4 Ω
double ib = pu.BaseKa;                  // 2.887 kA

var abc = new ThreePhase(Phasor.FromPolarDeg(1, 0), Phasor.FromPolarDeg(0.9, -125), Phasor.FromPolarDeg(1.05, 118));
SequenceSet s012 = SymmetricalComponents.FromPhases(abc);
double vuf = abc.UnbalanceFactor();     // |V2| / |V1|

var net = Matpower.Load("case30.m");    // MATPOWER .m (bus / gen / branch / gencost)
var islands = Topology.Islands(net);
```

**References / limitations.** MATPOWER case format and data for case9/case14; Baran & Wu (1989) for case33bw. The model is balanced
positive-sequence bus-branch; three-phase unbalanced (phase-frame) modelling is on the roadmap (v1.1).

## Electrical.Circuit

**Purpose.** Lumped circuit simulation by modified nodal analysis (MNA). Guide: [Simulation, HPC & native](simulation-hpc-native.md#circuit-simulation).

**Key types.** `ElectricCircuit` (fluent `AddResistor/AddCapacitor/AddInductor/AddVoltageSource/AddCurrentSource/AddDiode/AddSwitch/AddCoupling/
AddIdealTransformer/AddVcvs/AddVccs`), `Waveform` (`Dc`, `Sine`, `Pulse`, `PiecewiseLinear`), `Netlist.Parse`, `DcResult`, `AcResult` (`Bode`),
`TransientResult`, `IntegrationMethod` (Trapezoidal, BackwardEuler), `ThreePhaseCircuits` (star/delta conversion, unbalanced star/delta loads).

```csharp
var rc = new ElectricCircuit()
    .AddVoltageSource("V1", "in", "0", Waveform.Pulse(0, 1, delayS: 0, riseS: 1e-9))
    .AddResistor("R1", "in", "out", 1e3)
    .AddCapacitor("C1", "out", "0", 1e-6);
var tr = rc.Transient(stopTimeS: 5e-3, stepS: 1e-6);
double vTau = tr.At(tr.Voltage("out"), 1e-3);   // 0.632 V (τ = RC = 1 ms)
```

**References / limitations.** Tests check RC/RL time constants, series RLC resonance (f₀ = 1591.55 Hz, Q = 10), Shockley diode consistency and
transformer ratios. Fixed time step; no device models beyond the ideal diode/switch/controlled sources; the netlist parser accepts a
SPICE subset (dot-directives are ignored).

## Electrical.PowerFlow

**Purpose.** AC and DC power flow, contingency screening and sensitivities. Guide: [Power flow](power-flow.md).

**Key types.** `PowerFlowSolver.Solve(net, PowerFlowOptions)`, `PowerFlowMethod` (NewtonRaphson, GaussSeidel, FastDecoupled, DC,
BackwardForwardSweep), `PowerFlowResult` (`Buses`, `Branches`, `Generators`, `LossesMw`, `VoltageViolations()`, `Overloads()`, `ApplyTo`),
`ContingencyAnalysis.RunN1`, `ContingencyResult`, `DcPowerFlow.Ptdf/Lodf`, `DcModel`, `IPowerFlowService`.

```csharp
var feeder = PowerFlowSolver.Solve(TestCases.Case33BW(),
    new PowerFlowOptions { Method = PowerFlowMethod.BackwardForwardSweep });
// 202.7 kW losses, Vmin 0.9131 pu @ bus 18
```

**References / limitations.** MATPOWER case9 and case14 results; Baran & Wu 33-bus. Balanced positive-sequence only; no automatic
transformer tap or shunt control loops inside the power flow (QSTS events can switch shunts).

## Electrical.ShortCircuit

**Purpose.** IEC 60909 short-circuit currents. Guide: [Short circuit & protection](short-circuit-protection.md).

**Key types.** `ShortCircuitStudy` (factorised sequence networks, `Calculate`, `ScanAllBuses`, `TheveninImpedance`), `ShortCircuitAnalysis`
(static helpers), `FaultType`, `ShortCircuitOptions` (c factor, `PeakCurrentMethod`, `ExternalGrid`s, generator correction K<sub>G</sub>,
fault impedance), `FaultResult` (I″k, ip, Ib, Ith, Idc, S″k, κ, voltages, branch and source contributions), `BusShortCircuitSummary`,
`Iec60909` (c, κ, m, μ, K<sub>G</sub>, DC component).

```csharp
var r = ShortCircuitAnalysis.Calculate(TestCases.Case14(), 4, FaultType.SingleLineToGround);
Console.WriteLine(r);
// SingleLineToGround fault at bus 4 (138 kV): I"k = 4.625 kA, ip = 11.234 kA, Ib = 4.625 kA, Ith = 4.682 kA, S"k = 1105.4 MVA
```

**References / limitations.** IEC 60909-0 equations (hand-calculated radial example: I″k3 = 3.899 kA; κ, μ, K<sub>G</sub> formulas).
Transformer phase shifts are ignored by default; the breaking current assumes far-from-generator conditions (I<sub>b</sub> = I″k; use
`Iec60909.DecayFactorMu` near generators); pre-fault load currents are not superimposed (the IEC 60909 method). ANSI/IEEE C37 multiplying
factors are not implemented.

## Electrical.Protection

**Purpose.** Protection settings and studies. Guide: [Short circuit & protection](short-circuit-protection.md#protection).

**Key types.** `OvercurrentRelay`, `CurveType`, `InverseTimeCurves`, `Fuse`, `IProtectiveDevice`, `Tcc`, `Coordination`
(`Check`, `GradeTimeMultipliers`, `GradeRadialFeeder`), `DistanceRelay` (mho zones), `PercentDifferentialRelay`, `BreakerDuty`,
`BreakerRating`, `ArcFlash`, `ArcFlashInput`, `ArcEquipment`.

```csharp
var relay = new OvercurrentRelay("R1", pickupA: 400, CurveType.IecStandardInverse, timeMultiplier: 0.1);
double t = relay.OperatingTime(4000);     // 0.297 s
```

**References / limitations.** IEC 60255-151 and IEEE C37.112 curve constants; IEEE 1584-2002 (validated 480 V and 13.8 kV hand
calculations) with the Lee method above 15 kV; NFPA 70E PPE categories. The arc-flash model is the **2002** edition, suitable for screening
and teaching; use a validated IEEE 1584-2018 tool for labels.

## Electrical.Stability

**Purpose.** Rotor-angle, voltage and frequency stability. Guide: [Stability](stability.md).

**Key types.** `ClassicalModel`, `TransientStability` (`Simulate`, `SimulateFault`, `CriticalClearingTime`, `BranchIndex`), `StabilityEvent`,
`TransientStabilityOptions/Result`, `Smib` / `SmibSystem` (equal-area criterion), `SmallSignal` (`OscillationMode`, participation factors),
`EigenSolver`, `VoltageStability` (`PvCurve`, `QvCurve`, `LIndex`), `FrequencyResponse`.

```csharp
var ss = SmallSignal.Analyze(TestCases.Case9());
foreach (var mode in ss.OscillatoryModes)
    Console.WriteLine($"{mode.FrequencyHz:0.000} Hz, ζ = {mode.DampingRatio:P2}");   // 1.383 Hz and 2.126 Hz
```

**References / limitations.** Anderson & Fouad 9-bus (initial angles, CCT ≈ 0.16 s), Sauer & Pai (modes), closed-form equal-area criterion,
analytic two-bus maximum transfer. Machines use the **classical model** (E′ behind X′d, constant-admittance loads); detailed generator,
exciter, governor and inverter models are on the roadmap (v1.3).

## Electrical.Harmonic

**Purpose.** Harmonic analysis and power quality. Guide: [Harmonics](harmonics.md).

**Key types.** `Fft`, `HarmonicSpectrum` (THD, TDD, K-factor, synthesis), `PowerQuality` (RMS, crest factor, unbalance, NEMA derating),
`Ieee519`, `Iec61000`, `ComplianceResult`, `TypicalSpectra`, `HarmonicInjection`, `HarmonicLoadFlow`, `HarmonicLoadFlowResult`,
`FrequencyScan`, `SingleTunedFilter`.

```csharp
var hlf = HarmonicLoadFlow.Run(TestCases.Case14(),
    [new HarmonicInjection(9, TypicalSpectra.SixPulseRectifier(), FundamentalCurrentPu: 0.1)]);
var (bus, thd) = hlf.WorstBus;
```

**References / limitations.** IEEE 519-2014 tables, IEC 61000-2-2 and 61000-3-2 Class A, ideal 6-pulse THD, √(S<sub>sc</sub>/Q<sub>c</sub>)
resonance. The harmonic load flow uses the current-injection method on a balanced network (sequence networks per harmonic order); no
harmonic-domain converter models.

## Electrical.Renewable

**Purpose.** Models for PV, wind, storage and inverter grid codes. Guide: [Renewables & smart grid](renewables-smartgrid.md).

**Key types.** `SolarPosition`, `Irradiance` (Haurwitz, Ineichen clear sky, Erbs, plane of array), `CellTemperature`, `PvDatasheet`,
`SingleDiodeModel`, `PvWatts`, `InverterEfficiencyCurve`, `SandiaInverter`, `Mppt`; `WindTurbine`, `WeibullDistribution`, `WindShear`,
`AirDensity`, `WindEnergy`; `BatteryStorage`; `VoltVarCurve`, `VoltWattCurve`, `FrequencyDroop`; `HostingCapacity`.

```csharp
var module = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT, idealityFactor: 1.3);
var mpp = module.MaximumPowerPoint(irradiance: 1000, cellTempC: 25);   // 200.15 W at 26.35 V
```

**References / limitations.** NREL SPA example (Reda & Andreas 2008), Villalva et al. 2009, PVWatts v5, Masters (Rayleigh energy pattern
factor), IEEE 1547-2018 default curves. Hosting capacity is a deterministic single-bus sweep (voltage and loading limits), not a stochastic
study.

## Electrical.SmartGrid

**Purpose.** Grid-edge and distribution operations. Guide: [Renewables & smart grid](renewables-smartgrid.md#smart-grid).

**Key types.** `DemandResponse` (`PriceBased`, `PeakShaving`, `OptimalPeakShaving`), `EvCharging` (`Uncontrolled`, `ValleyFilling`,
`SyntheticFleet`), `MicrogridConfig`, `MicrogridEms` (`RuleBased`, `Optimal`), `VirtualPowerPlant`, `DerResource`, `VoltVarOptimization`,
`VoltVarControls`, `FeederReconfiguration`, `RadialReliability`, `ReliabilityIndices`.

```csharp
var recon = FeederReconfiguration.Optimize(FeederReconfiguration.Case33WithTies());
// open 14-15, 25-29, 32-33, 7-8, 9-10: 202.7 → 139.6 kW
```

**References / limitations.** Shirmohammadi & Hong (1989) and Civanlar et al. (1988) for reconfiguration; Billinton & Allan for radial
reliability. Optimizers are heuristics (coordinate descent, branch exchange, dynamic programming), each candidate evaluated with an AC power
flow; they do not prove global optimality.

## Electrical.Optimization

**Purpose.** Dispatch and planning optimization. Guide: [Optimization](optimization.md).

**Key types.** `EconomicDispatch`, `DispatchUnit`, `LossCoefficients`, `DcOpf` (`Solve`, `SolveSecurityConstrained`), `DcOpfOptions`,
`DcOpfResult` (LMPs, binding constraints, congestion rent), `UnitCommitment`, `UcUnit`, `CapacitorPlacement`, `QuadraticProgram`, `QpSolver`,
`NelderMead`, `ParticleSwarm`.

```csharp
var ed = EconomicDispatch.Solve(TestCases.Case9());   // λ = 24.044 $/MWh; 86.56 / 134.38 / 94.06 MW
```

**References / limitations.** MATPOWER case9 costs; Wood & Wollenberg Example 4A; Baran–Wu capacitor-placement literature. OPF is DC
(linear network, quadratic costs); AC-OPF and MILP unit commitment are on the roadmap (v1.2). Unit commitment is a priority-list dynamic
program (W&W §4.2), not a MILP.

## Electrical.StateEstimation

**Purpose.** Static AC state estimation. Guide: [State estimation](state-estimation.md).

**Key types.** `Measurement`, `MeasurementType`, `BranchEnd`, `MeasurementPlan`, `MeasurementGenerator`, `WlsEstimator`, `StateEstimationResult`,
`Observability`, `BadDataDetector`, `Distributions` (χ² quantiles).

```csharp
var z = MeasurementGenerator.FromPowerFlow(PowerFlowSolver.Solve(TestCases.Case14()));
var est = WlsEstimator.Estimate(TestCases.Case14(), z);
```

**References / limitations.** Abur & Expósito, *Power System State Estimation*, ch. 2 and 5; χ² table values. WLS only (no LAV/robust
estimators); numerical observability check, no measurement-placement optimization.

## Electrical.SCADA

**Purpose.** Telemetry, alarming and historization. Guide: [SCADA, IEC 61850 & CIM](scada-iec61850-cim.md).

**Key types.** `ModbusTcpServer`, `ModbusTcpClient`, `ModbusRegisterMap`, `ModbusPdu`, `ModbusTcpAdu` (`Electrical.SCADA.Modbus`); `TagDatabase`,
`Tag`, `TagAddress`, `TagScaling`, `TagQuality`; `AlarmManager`, `AlarmDefinition`, `AlarmEvent`; `Historian`, `CompressionMode`;
`RtuSimulator`; `Iec104Codec`, `Apdu`, `Asdu`, `Cp56Time2a` (`Electrical.SCADA.Iec104`).

**References / limitations.** Modbus Application Protocol v1.1b3 examples, ISA-18.2 alarm states, swinging-door trending (Bristol 1990),
IEC 60870-5-104 frame bytes. Modbus TCP only (no serial RTU); IEC 104 is a codec (APDU/ASDU encode/decode), not a full client/server session.

## Electrical.IEC61850

**Purpose.** Substation automation data and messages. Guide: [SCADA, IEC 61850 & CIM](scada-iec61850-cim.md#iec-61850).

**Key types.** `SclParser`, `SclDocument`, `SclIed`, `ObjectReference`, `FunctionalConstraint`, `LogicalNodeCatalog`, `MmsData` family, `Ber`,
`GoosePdu`, `GooseFrame`, `GooseCodec`, `GooseRetransmission`, `GoosePublisherState`, `SvAsdu`, `SvFrame`, `SampledValuesCodec`.

**References / limitations.** IEC 61850-6 (SCL), -7-2 (references, FCs), -8-1 (GOOSE), 9-2LE (SV); BER known vectors. Frame encoding and
decoding only: there is no raw-socket publisher/subscriber or MMS client/server.

## Electrical.CIM

**Purpose.** CIM / CGMES model exchange. Guide: [SCADA, IEC 61850 & CIM](scada-iec61850-cim.md#cim--cgmes).

**Key types.** `CimExporter` (`Export`, `ExportString`, `Write`, `ExportProfiles`), `CimProfiles`, `CimImporter` (`Import`, `Load`,
`ImportDetailed`), `CimImportOptions`, `CimImportResult`.

**References / limitations.** IEC 61970-301, CGMES 2.4.15-style profiles (EQ, TP, SSH, SV). Two-winding transformers only; supported
classes are ACLineSegment, PowerTransformer, SynchronousMachine, ExternalNetworkInjection, EnergyConsumer, LinearShuntCompensator, switches
(for topology processing) and SvVoltage. Full CGMES 3.0 coverage and validation are on the roadmap (v1.4).

## Electrical.DigitalTwin

**Purpose.** Live grid twin synchronised from SCADA measurements via state estimation, what-if simulation, asset health and explained alarms.
Guide: [AI & digital twin](ai-digital-twin.md#digital-twin-gridtwin).

**Key types.** `GridTwin` (`Ingest`, `IngestTags`, `Update`, `WhatIf`, `Summary`, `AssetHealth`), `GridTwinOptions`, `TwinState`, `GridKpis`,
`TwinStateUpdated` / `TwinAlarmRaised` / `TwinAlarmCleared` / `TwinBadMeasurement` events, `TagMeasurementMapper`, `WhatIfSimulator` and the
actions `OpenBranch`, `CloseBranch`, `ScaleLoad`, `AddPv`, `AddShuntCapacitor`, `SetGeneration`; `AlarmExplainer`, `ExplainedAlarm`;
`AssetHealthTracker`, `TransformerThermalParameters`; `TwinSimulator`, `TwinSimulatorOptions`; `IGridTwinFactory` (`AddDigitalTwin()`).

```csharp
var sim = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions { Steps = 24 });
TwinSimulationResult run = sim.Run();          // SCADA → WLS → alarms → asset health, 24 hourly cycles
var w = sim.Twin.WhatIf(new OpenBranch(6), new ScaleLoad(1.1));
```

**References / limitations.** IEEE C57.91 hot-spot and aging acceleration factor; Abur & Expósito WLS. Balanced bus-branch model, static
estimation per cycle, steady-state thermal model.

## Electrical.Forecasting

**Purpose.** Load and renewable forecasting. Guide: [Renewables & smart grid](renewables-smartgrid.md#forecasting).

**Key types.** `LoadForecast.Forecast(history, horizon, LoadForecastMethod)`, `HoltWinters`, `RegressionForecaster`, `SimilarDayForecaster`,
`EnsembleForecaster`, `ForecastMetrics`, `SolarForecast`, `WindForecast`, `WindPowerCurve`, `SyntheticProfiles`, `LoadProfileKind`.

```csharp
var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
var history = SyntheticProfiles.Load(LoadProfileKind.Commercial, start, days: 14, TimeSpan.FromHours(1), peakMw: 100, noise: 0.02);
var dayAhead = LoadForecast.Forecast(history, horizon: 24, LoadForecastMethod.HoltWinters);
```

**Limitations.** Statistical models only; ML models (ML.NET, TorchSharp) are on the roadmap (v1.4).

## Electrical.AI

**Purpose.** LLM grid-operator agent with tool calling (OpenAI, Azure OpenAI, Hugging Face, Ollama, offline), anomaly detection, fault
classification and location, load-profile clustering and an RL environment for Volt/VAR control. Guide: [AI & digital twin](ai-digital-twin.md).

**Key types.** `GridAgent`, `GridAgentContext`, `AgentAnswer`, `GridTools` (10 tools), `IChatModel`, `OpenAiCompatibleChatModel`,
`RuleBasedChatModel`, `LlmOptions` (`FromEnvironment`: `ELECTRICAL_LLM_PROVIDER/_ENDPOINT/_API_KEY/_MODEL`), `AddAI()`; `RobustZScore`,
`EwmaDetector`, `CusumDetector`, `Anomaly`; `FaultClassifier`, `FaultLocator`, `LoadProfileClustering`; `VoltVarEnvironment`,
`TabularQLearning`, `VoltVarPolicies`.

```csharp
var agent = new GridAgent(new GridAgentContext { CaseName = "case14" });   // LLM from ELECTRICAL_LLM_*, else offline rules
AgentAnswer a = await agent.AskAsync("What is the 3-phase short-circuit current at bus 4?");
```

**References / limitations.** Blackburn (fault classification), Arthur & Vassilvitskii (k-means++), tabular
Q-learning (Watkins) on the Baran–Wu feeder; live test passed on Azure OpenAI `gpt-5-mini`. Answers are only as complete as the tools; the offline model
has a limited bilingual vocabulary.

## Electrical.GIS

**Purpose.** Geographic networks and maps. Guide: [GIS & visualization](gis-visualization.md).

**Key types.** `GeoJsonExporter`, `GeoJsonImporter`, `GisSvgRenderer`, `GisSvgOptions`, `GisColors`, `GeoLayout`, `BusSpatialIndex`, `OutageArea`,
`Geo`, `WebMercator`, `GeoBounds`, `LineLengths`.

**References / limitations.** EPSG:3857 extents, OSM tile numbering, haversine distances. No raster/tiles download and no reprojection
beyond WGS-84 ↔ Web Mercator.

## Electrical.Visualization and Electrical.Visualization.Blazor

**Purpose.** SVG diagrams and charts for any UI, plus Blazor components. Guide: [GIS & visualization](gis-visualization.md#visualization).

**Key types.** `SldLayout`, `SldRenderer`, `SldOptions`, `SvgCharts` (`Line`, `TimeSeries`, `Bar`, `StackedArea`, `Heatmap`, `Gauge`,
`Sparkline`, `KpiTile`, `Phasor`, `Polar`, `Tcc`), `SvgTheme`, `ColorRamp`, `PngEncoder`, `IDiagramRenderer`. Blazor: `ElectricalTheme`,
`SingleLineDiagram`, `LineChart`, `BarChart`, `Heatmap`, `PhasorDiagram`, `TccChart`, `Gauge`, `KpiCard`, `SvgMarkup`.

```csharp
string svg = SldRenderer.Render(net, pf, new SldOptions { Title = "IEEE 14-bus" });
File.WriteAllText("sld.svg", SvgTheme.Resolve(svg, dark: false));
```

## Electrical.Simulation

**Purpose.** Time-domain operational studies and adequacy. Guide: [Simulation, HPC & native](simulation-hpc-native.md).

**Key types.** `QstsSimulation`, `QstsResult`, `PvSystem`, `SimulationEvent`, `SimulationEventKind`, `ScenarioManager`, `ScenarioSummary`,
`AdequacyAssessment` (`Analytical`, `SequentialMonteCarlo`, `CapacityOutageTable`), `GeneratingUnit`, `AdequacyIndices`.

**References / limitations.** Billinton & Allan capacity outage tables; Monte-Carlo agrees with the analytical LOLE within 10 % in tests.
QSTS has no controller dynamics (regulators/OLTC loops are on the roadmap, v1.1).

## Electrical.Cloud

**Purpose.** Run studies behind an HTTP API. Guide: [Cloud API](cloud-api.md).

**Key types.** `MapElectricalApi`, `AddCloud`, `StudyRunner`, `StudyJob`, `StudyCatalog`, `NetworkJson`, `IScenarioStore`,
`InMemoryScenarioStore`, `FileScenarioStore`.

## Electrical.HPC

**Purpose.** Many power flows fast. Guide: [Simulation, HPC & native](simulation-hpc-native.md#hpc).

**Key types.** `BatchPowerFlow` (`Run`, `LoadSweep`), `ProbabilisticLoadFlow`, `ProbabilisticLoadFlowOptions/Result`, `BusVoltageStatistics`,
`BranchLoadingStatistics`, `PowerFlowBenchmark`.

**Limitations.** CPU parallelism (`Parallel.For`); GPU and distributed execution are on the roadmap.

## Electrical.Native

**Purpose.** Rust kernels with managed fallbacks. Guide: [Simulation, HPC & native](simulation-hpc-native.md#native-kernels).

**Key types.** `GridKernels` (`Fft`, `Harmonics`, `Thd`, `Rms`, `PowerInjections`, `NormalSamples`, `Backend`, `IsNativeAvailable`,
`PreferNative`).

**Limitations.** `Fft` requires a power-of-two length (use `Electrical.Harmonic.Fft` for arbitrary lengths, Bluestein).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/modul.md)*
