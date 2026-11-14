# Panduan modul

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/modules.md)

Setiap bagian berisi tujuan, tipe utama, contoh singkat, acuan validasi, dan batasan yang diketahui. Namespace sama dengan nama paket
(`Electrical.PowerFlow`, …); Core dibagi menjadi `Electrical.Core.Network`, `.Numerics`, `.Units`, `.Data`, dan `.Hosting`. Penjelasan lebih
panjang ada di panduan topik yang ditautkan di setiap bagian.

## Electrical.Core

**Tujuan.** Fondasi bersama: numerik, model jaringan, satuan, deret waktu, DI, dan event bus.

**Tipe utama.** `PowerNetwork`, `Bus`, `Load` (`ZipModel`), `Generator` (`PolynomialCost`, data mesin), `Branch`, `NetworkBuilder`, `LineLibrary`,
`AdmittanceMatrix`, `Topology`, `Matpower`, `TestCases`; `DenseMatrix<T>`, `SparseMatrix<T>`, `SparseBuilder<T>`, `SparseLU<T>`;
`PerUnitBase`, `ElectricalFormulas`, `Phasor`, `ThreePhase`, `SymmetricalComponents`; `TimeSeries`, `Stats`; `ElectricalBuilder`,
`IGridEventBus`; `ElectricalInfo` (nama, versi, kredit, daftar modul).

```csharp
var pu = new PerUnitBase(BaseMva: 100, BaseKv: 20);
double zb = pu.BaseOhm;                 // 4 Ω
double ib = pu.BaseKa;                  // 2,887 kA

var abc = new ThreePhase(Phasor.FromPolarDeg(1, 0), Phasor.FromPolarDeg(0.9, -125), Phasor.FromPolarDeg(1.05, 118));
SequenceSet s012 = SymmetricalComponents.FromPhases(abc);
double vuf = abc.UnbalanceFactor();     // |V2| / |V1|

var net = Matpower.Load("case30.m");    // MATPOWER .m (bus / gen / branch / gencost)
var islands = Topology.Islands(net);
```

**Acuan / batasan.** Format kasus dan data MATPOWER untuk case9/case14; Baran & Wu (1989) untuk case33bw. Model berupa bus-cabang urutan positif
seimbang; untuk penyulang tiga fasa tak seimbang (phase-frame) lihat [Electrical.Distribution](#electricaldistribution), dan untuk berkas
PSS/E RAW serta IEEE CDF [Electrical.Interop](#electricalinterop).

## Electrical.Circuit

**Tujuan.** Simulasi rangkaian terpusat dengan analisis nodal termodifikasi (MNA). Panduan: [Simulasi, HPC & native](simulasi-hpc-native.md#simulasi-rangkaian).

**Tipe utama.** `ElectricCircuit` (fluent `AddResistor/AddCapacitor/AddInductor/AddVoltageSource/AddCurrentSource/AddDiode/AddSwitch/AddCoupling/
AddIdealTransformer/AddVcvs/AddVccs`), `Waveform` (`Dc`, `Sine`, `Pulse`, `PiecewiseLinear`), `Netlist.Parse`, `DcResult`, `AcResult` (`Bode`),
`TransientResult`, `IntegrationMethod` (Trapezoidal, BackwardEuler), `ThreePhaseCircuits` (konversi bintang/segitiga, beban bintang/segitiga
tak seimbang).

```csharp
var rc = new ElectricCircuit()
    .AddVoltageSource("V1", "in", "0", Waveform.Pulse(0, 1, delayS: 0, riseS: 1e-9))
    .AddResistor("R1", "in", "out", 1e3)
    .AddCapacitor("C1", "out", "0", 1e-6);
var tr = rc.Transient(stopTimeS: 5e-3, stepS: 1e-6);
double vTau = tr.At(tr.Voltage("out"), 1e-3);   // 0,632 V (τ = RC = 1 ms)
```

**Acuan / batasan.** Tes memeriksa konstanta waktu RC/RL, resonansi RLC seri (f₀ = 1591,55 Hz, Q = 10), konsistensi dioda Shockley, dan
rasio trafo. Langkah waktu tetap; tidak ada model divais selain dioda/saklar/sumber terkendali ideal; parser netlist menerima subset SPICE
(dot-directive diabaikan).

## Electrical.PowerFlow

**Tujuan.** Aliran daya AC dan DC, penyaringan kontingensi, dan sensitivitas. Panduan: [Aliran daya](aliran-daya.md).

**Tipe utama.** `PowerFlowSolver.Solve(net, PowerFlowOptions)`, `PowerFlowMethod` (NewtonRaphson, GaussSeidel, FastDecoupled, DC,
BackwardForwardSweep), `PowerFlowResult` (`Buses`, `Branches`, `Generators`, `LossesMw`, `VoltageViolations()`, `Overloads()`, `ApplyTo`),
`ContingencyAnalysis.RunN1`, `ContingencyResult`, `DcPowerFlow.Ptdf/Lodf`, `DcModel`, `IPowerFlowService`.

```csharp
var feeder = PowerFlowSolver.Solve(TestCases.Case33BW(),
    new PowerFlowOptions { Method = PowerFlowMethod.BackwardForwardSweep });
// rugi 202,7 kW, Vmin 0,9131 pu @ bus 18
```

**Acuan / batasan.** Hasil MATPOWER case9 dan case14; Baran & Wu 33-bus; flat start berawal DC divalidasi pada pergeseran fasa besar.
Hanya urutan positif seimbang (tiga fasa: [Electrical.Distribution](#electricaldistribution)); tidak ada loop kendali tap atau shunt dalam
satu kali penyelesaian (loop kendali deret waktu berjalan di [Electrical.Simulation](#electricalsimulation)).

## Electrical.Distribution

**Tujuan.** Penyulang distribusi tiga fasa tak seimbang dalam kerangka fasa (v1.1). Panduan: [Distribusi tak seimbang](distribusi.md).

**Tipe utama.** `DistributionNetwork`, `UnbalancedPowerFlow` (`Solve`, ladder sweep atau injeksi arus), `DistributionPowerFlowOptions`,
`DistributionPowerFlowResult` (`Bus(name).VmPu/VaDeg/VoltageUnbalanceFactor/NemaUnbalance`, `Branch(name)`, `RegulatorTaps`, `LossesKw`),
`LineConstants` (`Overhead`, `ConcentricNeutral`, `TapeShielded`), `OverheadConfiguration`, `ConductorLibrary`, `LineImpedance`, `LineCode`,
`VoltageRegulator`, `VoltageRegulatorController`, `OltcController`, `CapacitorController`, `DistributionTimeSeries`, `OpenDssReader`,
`DistributionTestCases` (IEEE 4 dan 13 node), `ToBalanced`, `AddDistribution`.

```csharp
var r = UnbalancedPowerFlow.Solve(DistributionTestCases.Ieee13Node());
var b = r.Bus("675");   // 0,9831 / 1,0559 / 0,9772 pu (IEEE: 0,9835 / 1,0553 / 0,9758)
```

**Acuan / batasan.** Penyulang uji IEEE PES 4 dan 13 node (galat tegangan terbesar 0,026 % dan 0,148 %), Kersting Contoh 4.1–4.3 dan
5.1–5.2; konfigurasi 601–607 dihitung ulang dari geometri dalam 0,0001 Ω/mil. Sumber ideal; subset OpenDSS (trafo dua belitan, beban,
kapasitor, regulator, kode dan geometri saluran); belum ada analisis gangguan atau harmonik pada model fasa (gunakan `ToBalanced`).

## Electrical.ShortCircuit

**Tujuan.** Arus hubung singkat IEC 60909. Panduan: [Hubung singkat & proteksi](hubung-singkat-proteksi.md).

**Tipe utama.** `ShortCircuitStudy` (jaringan urutan terfaktorkan, `Calculate`, `ScanAllBuses`, `TheveninImpedance`), `ShortCircuitAnalysis`
(helper statis), `FaultType`, `ShortCircuitOptions` (faktor c, `PeakCurrentMethod`, `ExternalGrid`, koreksi generator K<sub>G</sub>, impedansi
gangguan), `FaultResult` (I″k, ip, Ib, Ith, Idc, S″k, κ, tegangan, kontribusi cabang dan sumber), `BusShortCircuitSummary`, `Iec60909` (c, κ, m,
μ, K<sub>G</sub>, komponen DC).

```csharp
var r = ShortCircuitAnalysis.Calculate(TestCases.Case14(), 4, FaultType.SingleLineToGround);
Console.WriteLine(r);
// SingleLineToGround fault at bus 4 (138 kV): I"k = 4.625 kA, ip = 11.234 kA, Ib = 4.625 kA, Ith = 4.682 kA, S"k = 1105.4 MVA
```

**Acuan / batasan.** Persamaan IEC 60909-0 (contoh radial dihitung tangan: I″k3 = 3,899 kA; rumus κ, μ, K<sub>G</sub>). Pergeseran fasa trafo
diabaikan secara default; arus pemutusan mengasumsikan gangguan jauh dari generator (I<sub>b</sub> = I″k; pakai `Iec60909.DecayFactorMu` dekat
generator); arus beban pra-gangguan tidak ditumpangkan (metode IEC 60909). Faktor pengali ANSI/IEEE C37 tidak diimplementasikan.

## Electrical.Protection

**Tujuan.** Penyetelan dan studi proteksi. Panduan: [Hubung singkat & proteksi](hubung-singkat-proteksi.md#proteksi).

**Tipe utama.** `OvercurrentRelay`, `CurveType`, `InverseTimeCurves`, `Fuse`, `IProtectiveDevice`, `Tcc`, `Coordination`
(`Check`, `GradeTimeMultipliers`, `GradeRadialFeeder`), `DistanceRelay` (zona mho), `PercentDifferentialRelay`, `BreakerDuty`, `BreakerRating`,
`ArcFlash`, `ArcFlashInput`, `ArcEquipment`.

```csharp
var relay = new OvercurrentRelay("R1", pickupA: 400, CurveType.IecStandardInverse, timeMultiplier: 0.1);
double t = relay.OperatingTime(4000);     // 0,297 s
```

**Acuan / batasan.** Konstanta kurva IEC 60255-151 dan IEEE C37.112; IEEE 1584-2002 (perhitungan tangan 480 V dan 13,8 kV tervalidasi) dengan
metode Lee di atas 15 kV; kategori PPE NFPA 70E. Model arc flash adalah edisi **2002**, cocok untuk penyaringan dan pengajaran; gunakan tool
IEEE 1584-2018 tervalidasi untuk label peralatan.

## Electrical.Stability

**Tujuan.** Stabilitas sudut rotor, tegangan, dan frekuensi. Panduan: [Stabilitas](stabilitas.md).

**Tipe utama.** `ClassicalModel`, `TransientStability` (`Simulate`, `SimulateFault`, `CriticalClearingTime`, `BranchIndex`), `StabilityEvent`,
`TransientStabilityOptions/Result`, `Smib` / `SmibSystem` (kriteria luas sama), `SmallSignal` (`OscillationMode`, faktor partisipasi),
`EigenSolver`, `VoltageStability` (`PvCurve`, `QvCurve`, `LIndex`), `FrequencyResponse`.

```csharp
var ss = SmallSignal.Analyze(TestCases.Case9());
foreach (var mode in ss.OscillatoryModes)
    Console.WriteLine($"{mode.FrequencyHz:0.000} Hz, ζ = {mode.DampingRatio:P2}");   // 1,383 Hz dan 2,126 Hz
```

**Acuan / batasan.** Anderson & Fouad 9-bus (sudut awal, CCT ≈ 0,16 s), Sauer & Pai (mode), kriteria luas sama bentuk tertutup, transfer
maksimum dua bus analitis. Mesin memakai **model klasik** (E′ di belakang X′d, beban admitansi konstan); model generator, eksiter, governor,
dan inverter yang detail ada di peta jalan (v1.3).

## Electrical.Harmonic

**Tujuan.** Analisis harmonik dan kualitas daya. Panduan: [Harmonik](harmonik.md).

**Tipe utama.** `Fft`, `HarmonicSpectrum` (THD, TDD, faktor-K, sintesis), `PowerQuality` (RMS, crest factor, ketidakseimbangan, derating NEMA),
`Ieee519`, `Iec61000`, `ComplianceResult`, `TypicalSpectra`, `HarmonicInjection`, `HarmonicLoadFlow`, `HarmonicLoadFlowResult`,
`FrequencyScan`, `SingleTunedFilter`.

```csharp
var hlf = HarmonicLoadFlow.Run(TestCases.Case14(),
    [new HarmonicInjection(9, TypicalSpectra.SixPulseRectifier(), FundamentalCurrentPu: 0.1)]);
var (bus, thd) = hlf.WorstBus;
```

**Acuan / batasan.** Tabel IEEE 519-2014, IEC 61000-2-2 dan 61000-3-2 Kelas A, THD 6-pulsa ideal, resonansi √(S<sub>sc</sub>/Q<sub>c</sub>).
Aliran daya harmonik memakai metode injeksi arus pada jaringan seimbang (jaringan urutan per orde harmonik); tidak ada model konverter
domain harmonik.

## Electrical.Renewable

**Tujuan.** Model PV, angin, penyimpanan, dan grid code inverter. Panduan: [Energi terbarukan & smart grid](energi-terbarukan-smartgrid.md).

**Tipe utama.** `SolarPosition`, `Irradiance` (Haurwitz, clear-sky Ineichen, Erbs, plane of array), `CellTemperature`, `PvDatasheet`,
`SingleDiodeModel`, `PvWatts`, `InverterEfficiencyCurve`, `SandiaInverter`, `Mppt`; `WindTurbine`, `WeibullDistribution`, `WindShear`,
`AirDensity`, `WindEnergy`; `BatteryStorage`; `VoltVarCurve`, `VoltWattCurve`, `FrequencyDroop`; `HostingCapacity`.

```csharp
var module = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT, idealityFactor: 1.3);
var mpp = module.MaximumPowerPoint(irradiance: 1000, cellTempC: 25);   // 200,15 W pada 26,35 V
```

**Acuan / batasan.** Contoh NREL SPA (Reda & Andreas 2008), Villalva dkk. 2009, PVWatts v5, Masters (faktor pola energi Rayleigh), kurva default
IEEE 1547-2018. Hosting capacity adalah sapuan deterministik satu bus (batas tegangan dan pembebanan), bukan studi stokastik.

## Electrical.SmartGrid

**Tujuan.** Operasi tepi jaringan dan distribusi. Panduan: [Energi terbarukan & smart grid](energi-terbarukan-smartgrid.md#smart-grid).

**Tipe utama.** `DemandResponse` (`PriceBased`, `PeakShaving`, `OptimalPeakShaving`), `EvCharging` (`Uncontrolled`, `ValleyFilling`,
`SyntheticFleet`), `MicrogridConfig`, `MicrogridEms` (`RuleBased`, `Optimal`), `VirtualPowerPlant`, `DerResource`, `VoltVarOptimization`,
`VoltVarControls`, `FeederReconfiguration`, `RadialReliability`, `ReliabilityIndices`.

```csharp
var recon = FeederReconfiguration.Optimize(FeederReconfiguration.Case33WithTies());
// buka 14-15, 25-29, 32-33, 7-8, 9-10: 202,7 → 139,6 kW
```

**Acuan / batasan.** Shirmohammadi & Hong (1989) dan Civanlar dkk. (1988) untuk rekonfigurasi; Billinton & Allan untuk keandalan radial.
Optimizer berupa heuristik (coordinate descent, branch exchange, dynamic programming) yang setiap kandidatnya dievaluasi dengan aliran daya AC;
tidak menjamin optimum global.

## Electrical.Optimization

**Tujuan.** Optimasi dispatch dan perencanaan. Panduan: [Optimasi](optimasi.md).

**Tipe utama.** `EconomicDispatch`, `DispatchUnit`, `LossCoefficients`, `DcOpf` (`Solve`, `SolveSecurityConstrained`), `DcOpfOptions`,
`DcOpfResult` (LMP, kendala aktif, congestion rent), `UnitCommitment`, `UcUnit`, `CapacitorPlacement`, `QuadraticProgram`, `QpSolver`,
`NelderMead`, `ParticleSwarm`.

```csharp
var ed = EconomicDispatch.Solve(TestCases.Case9());   // λ = 24,044 $/MWh; 86,56 / 134,38 / 94,06 MW
```

**Acuan / batasan.** Biaya MATPOWER case9; Wood & Wollenberg Contoh 4A; literatur penempatan kapasitor Baran–Wu. OPF berbasis DC (jaringan
linear, biaya kuadratik); AC-OPF dan unit commitment MILP ada di peta jalan (v1.2). Unit commitment adalah dynamic programming daftar prioritas
(W&W §4.2), bukan MILP.

## Electrical.StateEstimation

**Tujuan.** Estimasi keadaan AC statis. Panduan: [Estimasi keadaan](estimasi-keadaan.md).

**Tipe utama.** `Measurement`, `MeasurementType`, `BranchEnd`, `MeasurementPlan`, `MeasurementGenerator`, `WlsEstimator`, `StateEstimationResult`,
`Observability`, `BadDataDetector`, `Distributions` (kuantil χ²).

```csharp
var z = MeasurementGenerator.FromPowerFlow(PowerFlowSolver.Solve(TestCases.Case14()));
var est = WlsEstimator.Estimate(TestCases.Case14(), z);
```

**Acuan / batasan.** Abur & Expósito, *Power System State Estimation*, bab 2 dan 5; nilai tabel χ². Hanya WLS (tanpa LAV/estimator robust);
cek observabilitas numerik, tanpa optimasi penempatan meter.

## Electrical.SCADA

**Tujuan.** Telemetri, alarm, dan historisasi. Panduan: [SCADA, IEC 61850 & CIM](scada-iec61850-cim.md).

**Tipe utama.** `ModbusTcpServer`, `ModbusTcpClient`, `ModbusRegisterMap`, `ModbusPdu`, `ModbusTcpAdu` (`Electrical.SCADA.Modbus`); `TagDatabase`,
`Tag`, `TagAddress`, `TagScaling`, `TagQuality`; `AlarmManager`, `AlarmDefinition`, `AlarmEvent`; `Historian`, `CompressionMode`; `RtuSimulator`;
`Iec104Codec`, `Apdu`, `Asdu`, `Cp56Time2a` (`Electrical.SCADA.Iec104`).

**Acuan / batasan.** Contoh Modbus Application Protocol v1.1b3, state alarm ISA-18.2, swinging-door trending (Bristol 1990), byte frame
IEC 60870-5-104. Hanya Modbus TCP (tanpa RTU serial); IEC 104 berupa codec (encode/decode APDU/ASDU), bukan sesi klien/server lengkap.

## Electrical.IEC61850

**Tujuan.** Data dan pesan otomasi gardu induk. Panduan: [SCADA, IEC 61850 & CIM](scada-iec61850-cim.md#iec-61850).

**Tipe utama.** `SclParser`, `SclDocument`, `SclIed`, `ObjectReference`, `FunctionalConstraint`, `LogicalNodeCatalog`, keluarga `MmsData`, `Ber`,
`GoosePdu`, `GooseFrame`, `GooseCodec`, `GooseRetransmission`, `GoosePublisherState`, `SvAsdu`, `SvFrame`, `SampledValuesCodec`.

**Acuan / batasan.** IEC 61850-6 (SCL), -7-2 (referensi, FC), -8-1 (GOOSE), 9-2LE (SV); vektor uji BER. Hanya encoding dan decoding frame:
tidak ada publisher/subscriber raw-socket atau klien/server MMS.

## Electrical.CIM

**Tujuan.** Pertukaran model CIM / CGMES. Panduan: [SCADA, IEC 61850 & CIM](scada-iec61850-cim.md#cim--cgmes).

**Tipe utama.** `CimExporter` (`Export`, `ExportString`, `Write`, `ExportProfiles`), `CimProfiles`, `CimImporter` (`Import`, `Load`,
`ImportDetailed`), `CimImportOptions`, `CimImportResult`.

**Acuan / batasan.** IEC 61970-301, profil gaya CGMES 2.4.15 (EQ, TP, SSH, SV). Hanya trafo dua belitan; kelas yang didukung: ACLineSegment,
PowerTransformer, SynchronousMachine, ExternalNetworkInjection, EnergyConsumer, LinearShuntCompensator, saklar (untuk pemrosesan topologi),
dan SvVoltage. Cakupan dan validasi CGMES 3.0 penuh ada di peta jalan (v1.4).

## Electrical.Interop

**Tujuan.** Pertukaran model dengan perangkat perencanaan (v1.1). Panduan: [Pertukaran model](pertukaran-model.md).

**Tipe utama.** `NetworkFormats` (`Detect`, `Load`, `Parse`, `FromExtension`), `ImportResult` (`Network`, `Format`, `Warnings`),
`NetworkFormat`, `PsseRaw` (`Read`, `Parse`, `Load`, `Write`, `Save`; revisi 33 dan 35), `IeeeCdf` (`Read`, `Parse`), `InteropSamples`
(IEEE 14 bus sebagai RAW v33, RAW v35, dan CDF), `INetworkImporter`, `AddInterop`.

```csharp
var import = NetworkFormats.Load("grid.raw");           // PSS/E RAW, IEEE CDF, MATPOWER, atau JSON Cloud, dideteksi dari isinya
string raw35 = PsseRaw.Write(import.Network, version: 35);
```

**Acuan / batasan.** IEEE 14 bus RAW v33/v35 dan CDF mereproduksi MATPOWER case14 (13,393 MW). Trafo tiga belitan menjadi ekuivalen bintang,
shunt terswitch dan trafo pengatur dibekukan, bagian HVDC/FACTS dilewati dengan peringatan; penulis hanya menghasilkan trafo dua belitan;
CDF hanya dapat dibaca.

## Electrical.DigitalTwin

**Tujuan.** Kembaran jaringan langsung yang disinkronkan dari pengukuran SCADA melalui estimasi keadaan, simulasi what-if, kesehatan aset, dan
alarm yang dijelaskan. Panduan: [AI & digital twin](ai-digital-twin.md#digital-twin-gridtwin).

**Tipe utama.** `GridTwin` (`Ingest`, `IngestTags`, `Update`, `WhatIf`, `Summary`, `AssetHealth`), `GridTwinOptions`, `TwinState`, `GridKpis`,
event `TwinStateUpdated` / `TwinAlarmRaised` / `TwinAlarmCleared` / `TwinBadMeasurement`, `TagMeasurementMapper`, `WhatIfSimulator` dan aksi
`OpenBranch`, `CloseBranch`, `ScaleLoad`, `AddPv`, `AddShuntCapacitor`, `SetGeneration`; `AlarmExplainer`, `ExplainedAlarm`;
`AssetHealthTracker`, `TransformerThermalParameters`; `TwinSimulator`, `TwinSimulatorOptions`; `IGridTwinFactory` (`AddDigitalTwin()`).

```csharp
var sim = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions { Steps = 24 });
TwinSimulationResult run = sim.Run();          // SCADA → WLS → alarm → kesehatan aset, 24 siklus per jam
var w = sim.Twin.WhatIf(new OpenBranch(6), new ScaleLoad(1.1));
```

**Acuan / batasan.** Hot spot dan faktor percepatan penuaan IEEE C57.91; WLS Abur & Expósito. Model bus-cabang seimbang, estimasi statis per
siklus, model termal tunak.

## Electrical.Forecasting

**Tujuan.** Prakiraan beban dan energi terbarukan. Panduan: [Energi terbarukan & smart grid](energi-terbarukan-smartgrid.md#prakiraan).

**Tipe utama.** `LoadForecast.Forecast(history, horizon, LoadForecastMethod)`, `HoltWinters`, `RegressionForecaster`, `SimilarDayForecaster`,
`EnsembleForecaster`, `ForecastMetrics`, `SolarForecast`, `WindForecast`, `WindPowerCurve`, `SyntheticProfiles`, `LoadProfileKind`.

```csharp
var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
var history = SyntheticProfiles.Load(LoadProfileKind.Commercial, start, days: 14, TimeSpan.FromHours(1), peakMw: 100, noise: 0.02);
var dayAhead = LoadForecast.Forecast(history, horizon: 24, LoadForecastMethod.HoltWinters);
```

**Batasan.** Hanya model statistik; model ML (ML.NET, TorchSharp) ada di peta jalan (v1.4).

## Electrical.AI

**Tujuan.** Agen operator jaringan LLM dengan tool calling (OpenAI, Azure OpenAI, Hugging Face, Ollama, offline), deteksi anomali, klasifikasi
dan lokasi gangguan, klasterisasi profil beban, dan lingkungan RL untuk kendali Volt/VAR. Panduan: [AI & digital twin](ai-digital-twin.md).

**Tipe utama.** `GridAgent`, `GridAgentContext`, `AgentAnswer`, `GridTools` (10 tool), `IChatModel`, `OpenAiCompatibleChatModel`,
`RuleBasedChatModel`, `LlmOptions` (`FromEnvironment`: `ELECTRICAL_LLM_PROVIDER/_ENDPOINT/_API_KEY/_MODEL`), `AddAI()`; `RobustZScore`,
`EwmaDetector`, `CusumDetector`, `Anomaly`; `FaultClassifier`, `FaultLocator`, `LoadProfileClustering`; `VoltVarEnvironment`,
`TabularQLearning`, `VoltVarPolicies`.

```csharp
var agent = new GridAgent(new GridAgentContext { CaseName = "case14" });   // LLM dari ELECTRICAL_LLM_*, atau aturan offline
AgentAnswer a = await agent.AskAsync("Berapa arus hubung singkat 3 fasa di bus 4?");
```

**Acuan / batasan.** Blackburn (klasifikasi gangguan), Arthur & Vassilvitskii (k-means++), Q-learning tabular (Watkins) pada penyulang Baran–Wu;
tes live lulus di Azure OpenAI `gpt-5-mini`. Jawaban hanya selengkap tool yang tersedia; model offline memiliki kosakata dwibahasa terbatas.

## Electrical.GIS

**Tujuan.** Jaringan geografis dan peta. Panduan: [GIS & visualisasi](gis-visualisasi.md).

**Tipe utama.** `GeoJsonExporter`, `GeoJsonImporter`, `GisSvgRenderer`, `GisSvgOptions`, `GisColors`, `GeoLayout`, `BusSpatialIndex`, `OutageArea`,
`Geo`, `WebMercator`, `GeoBounds`, `LineLengths`.

**Acuan / batasan.** Batas EPSG:3857, penomoran tile OSM, jarak haversine. Tidak ada unduhan raster/tile dan tidak ada reproyeksi selain
WGS-84 ↔ Web Mercator.

## Electrical.Visualization dan Electrical.Visualization.Blazor

**Tujuan.** Diagram dan grafik SVG untuk UI apa pun, ditambah komponen Blazor. Panduan: [GIS & visualisasi](gis-visualisasi.md#visualisasi).

**Tipe utama.** `SldLayout`, `SldRenderer`, `SldOptions`, `SvgCharts` (`Line`, `TimeSeries`, `Bar`, `StackedArea`, `Heatmap`, `Gauge`,
`Sparkline`, `KpiTile`, `Phasor`, `Polar`, `Tcc`), `SvgTheme`, `ColorRamp`, `PngEncoder`, `IDiagramRenderer`. Blazor: `ElectricalTheme`,
`SingleLineDiagram`, `LineChart`, `BarChart`, `Heatmap`, `PhasorDiagram`, `TccChart`, `Gauge`, `KpiCard`, `SvgMarkup`.

```csharp
string svg = SldRenderer.Render(net, pf, new SldOptions { Title = "IEEE 14-bus" });
File.WriteAllText("sld.svg", SvgTheme.Resolve(svg, dark: false));
```

## Electrical.Simulation

**Tujuan.** Studi operasional domain waktu dan kecukupan pembangkitan. Panduan: [Simulasi, HPC & native](simulasi-hpc-native.md).

**Tipe utama.** `QstsSimulation`, `QstsResult`, `PvSystem`, `SimulationEvent`, `SimulationEventKind`, `ScenarioManager`, `ScenarioSummary`,
`AdequacyAssessment` (`Analytical`, `SequentialMonteCarlo`, `CapacityOutageTable`), `GeneratingUnit`, `AdequacyIndices`; loop kendali v1.1:
`QstsController`, `OltcController`, `VoltageRegulatorController` (LDC), `CapacitorController` (`CapacitorControlMode` Voltage / ReactivePower),
`QstsSimulation.Controllers`, `QstsSimulation.Case33ControlDemo`.

**Acuan / batasan.** Tabel capacity outage Billinton & Allan; Monte-Carlo sesuai dengan LOLE analitis dalam 10 % di tes.
Case33ControlDemo: OLTC + kapasitor terswitch menurunkan langkah pelanggaran tegangan dari 64 menjadi 1 dari 96 (V<sub>min</sub> 0,9237 → 0,9617 pu)
dengan 4 langkah tap dan 2 pensaklaran. Pengendali bersifat diskret dan kuasi-statis (tanpa dinamika mekanis); pengendali per fasa untuk
penyulang tak seimbang ada di [Electrical.Distribution](#electricaldistribution).

## Electrical.Cloud

**Tujuan.** Menjalankan studi di balik API HTTP. Panduan: [API cloud](api-cloud.md).

**Tipe utama.** `MapElectricalApi`, `AddCloud`, `StudyRunner`, `StudyJob`, `StudyCatalog`, `NetworkJson`, `IScenarioStore`,
`InMemoryScenarioStore`, `FileScenarioStore`.

## Electrical.HPC

**Tujuan.** Banyak aliran daya dengan cepat. Panduan: [Simulasi, HPC & native](simulasi-hpc-native.md#hpc).

**Tipe utama.** `BatchPowerFlow` (`Run`, `LoadSweep`), `ProbabilisticLoadFlow`, `ProbabilisticLoadFlowOptions/Result`, `BusVoltageStatistics`,
`BranchLoadingStatistics`, `PowerFlowBenchmark`.

**Batasan.** Paralelisme CPU (`Parallel.For`); eksekusi GPU dan terdistribusi ada di peta jalan.

## Electrical.Native

**Tujuan.** Kernel Rust dengan fallback managed. Panduan: [Simulasi, HPC & native](simulasi-hpc-native.md#kernel-native).

**Tipe utama.** `GridKernels` (`Fft`, `Harmonics`, `Thd`, `Rms`, `PowerInjections`, `NormalSamples`, `Backend`, `IsNativeAvailable`,
`PreferNative`).

**Batasan.** `Fft` membutuhkan panjang pangkat dua (gunakan `Electrical.Harmonic.Fft` untuk panjang sembarang, Bluestein).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/modules.md)*
