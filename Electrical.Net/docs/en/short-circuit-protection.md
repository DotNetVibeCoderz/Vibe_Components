# Short circuit and protection

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/hubung-singkat-proteksi.md)

Packages: `Electrical.ShortCircuit` (IEC 60909) and `Electrical.Protection` (relays, coordination, breaker duty, arc flash).

## IEC 60909 short circuit

`ShortCircuitStudy` builds the positive-, negative- and zero-sequence admittance matrices (branches plus source impedances; loads and
shunts neglected unless `IncludeShunts`), factorises each once with `SparseLU` and then computes a fault with one sparse solve per sequence
(one Z-bus column). The equivalent voltage source is c·U<sub>n</sub>/√3 at the fault location.

Sources:

- **Generators** in the network use `XdSubtransient`, `X2`, `X0`, `Ra` on `MBase`; `ApplyGeneratorCorrection` applies K<sub>G</sub>
  (IEC 60909-0 eq. 18).
- **External grids** (`ExternalGrid`) are network infeeds given by S″k [MVA], R/X, c and Z0/Z1.
- Transformer zero-sequence paths follow `Branch.Connection` (YNyn, YNd, Dyn, …).

### Worked example (validated by hand calculation)

```csharp
// 110 kV grid (S"k = 3000 MVA, R/X = 0.1) → 40 MVA 110/20 kV Dyn transformer (uk = 12 %) → 5 km line 0.2 + j0.35 Ω/km
var net = new NetworkBuilder("radial", 100, 50)
    .AddBus(1, 110, BusType.Slack).AddBus(2, 20).AddBus(3, 20)
    .AddTransformer(1, 2, 40, 12, 0, connection: WindingConnection.Dyn)
    .AddLine(2, 3, 5, 0.2, 0.35)
    .Build();

var options = new ShortCircuitOptions
{
    ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 3000, RxRatio = 0.1, C = 1.1 }],
    PeakMethod = PeakCurrentMethod.EquivalentFrequency,   // IEC 60909-0 method C (default)
};

var study = new ShortCircuitStudy(net, options);         // factorises Y1, Y2, Y0 once
FaultResult f3 = study.Calculate(3, FaultType.ThreePhase);
FaultResult f1 = study.Calculate(3, FaultType.SingleLineToGround);
Console.WriteLine(f3);
Console.WriteLine($"κ = {f3.Kappa:0.000}, ip = {f3.IpKa:0.00} kA, 3I0 = {f1.EarthCurrentKa:0.00} kA");

foreach (var row in study.ScanAllBuses())
    Console.WriteLine($"Bus {row.BusId}: I\"k3 {row.Ik3Ka:0.00} kA, I\"k1 {row.Ik1Ka:0.00} kA, S\"k {row.Sk3Mva:0} MVA");

var high = study.Calculate(3, FaultType.SingleLineToGround, faultImpedanceOhm: new Complex(10, 0));
```

Output of the two fault results:

```
ThreePhase fault at bus 3 (20 kV): I"k = 3.899 kA, ip = 7.645 kA, Ib = 3.899 kA, Ith = 3.919 kA, S"k = 135.1 MVA
SingleLineToGround fault at bus 3 (20 kV): I"k = 2.801 kA, ip = 5.492 kA, Ib = 2.801 kA, Ith = 2.815 kA, S"k = 97 MVA
```

Hand check (IEC 60909-0 eqs. 6, 7, 12, referred to 20 kV): Z<sub>Q</sub> = 1.1·20²/3000 = 0.1467 Ω (X<sub>Q</sub> = Z/√1.01,
R<sub>Q</sub> = 0.1·X<sub>Q</sub>), Z<sub>T</sub> = 0.12·20²/40 = j1.2 Ω, Z<sub>L</sub> = 1 + j1.75 Ω, so
I″k3 = 1.1·20 / (√3·|Z<sub>k</sub>|) = **3.899 kA**. The tests also check I″k2 = (√3/2)·I″k3, the SLG formula 3E/|Z1+Z2+Z0|, the LLG
earth current √3·c·U<sub>n</sub>/|Z1+2Z0|, branch contributions scaled by the transformer ratio, and κ for methods B and C.

### `FaultResult`

| Property | Meaning |
|---|---|
| `IkKa` | Initial symmetrical short-circuit current I″k |
| `IpKa`, `Kappa`, `RxRatio` | Peak current ip = κ·√2·I″k (method per `PeakMethod`) |
| `IbKa`, `IbAsymKa`, `IdcKa` | Symmetrical / asymmetrical breaking current, DC component at `MinimumTimeDelayS` |
| `IthKa` | Thermal equivalent current for `FaultDurationS` |
| `EarthCurrentKa` | 3I₀ |
| `SkMva` | S″k = √3·U<sub>n</sub>·I″k |
| `PhaseCurrentsKa`, `SequenceCurrentsPu` | Fault currents per phase / sequence |
| `Voltages` | Phase voltages at every bus during the fault (pu, `VaKv` …) |
| `Branches`, `Sources` | Branch currents and source contributions |

`Iec60909` exposes the formulas: `VoltageFactor(kV, maximum)`, `Kappa(R/X)`, `ThermalFactorM`, `DecayFactorMu`, `GeneratorCorrectionFactor`,
`DcComponent`.

### All-bus scan from the CLI

```
$ eln sc --case case14
IEC 60909 short-circuit scan (cmax) on case14
  Bus     kV     I"k3[kA] I"k2[kA] I"k1[kA]  ip[kA]   S"k[MVA]   R/X
  1        138     5.775    5.002    6.075   14.719     1380.4  0.075
  2        138     6.701    5.803    7.035   17.399     1601.7  0.061
  ...
  14      13.8    16.100   13.943   10.925   31.907      384.8  0.315
```

### Limitations

Transformer phase shifts are ignored by default (they do not change magnitudes at the fault location); breaking current assumes
far-from-generator faults (I<sub>b</sub> = I″k; use `Iec60909.DecayFactorMu` near generators); pre-fault load currents are not superimposed,
as prescribed by the IEC 60909 method. ANSI/IEEE C37 multiplying factors are not implemented.

## Protection

### Inverse-time overcurrent relays and fuses

Curves follow t = TMS·(A/(M<sup>P</sup> − 1) + B) with the IEC 60255-151 (SI, VI, EI, LTI) and IEEE C37.112 (MI, VI, EI) constants, plus
definite time. At M = 10 and TMS = 1 the IEC curves give 2.971 s (SI), 1.5 s (VI), 0.808 s (EI) and 13.33 s (LTI), as the tests assert.

```csharp
var relay = new OvercurrentRelay("R1", pickupA: 400, CurveType.IecStandardInverse, timeMultiplier: 0.1)
{
    InstantaneousPickupA = 8000,
    CtRatio = 400.0 / 1,
};
double t = relay.OperatingTime(4000);     // 0.297 s (10× pickup, SI curve, TMS 0.1)

var fuse = Fuse.FromI2t("100 A fuse", 100, meltingI2tA2s: 30_000);   // t = I²t / I² above 1.6·In
var check = Coordination.Check(fuse, relay, downstreamCurrentA: 1500);
var tcc = Tcc.Curve(relay, 400, 20_000);   // points for a log-log TCC chart
```

`Fuse` can also be built from a tabulated time-current curve (log-log interpolation).

### Grading

`Coordination.GradeTimeMultipliers` sets the most downstream relay to the minimum TMS and each upstream relay to the smallest TMS (rounded to
`tmsStep`) that keeps the coordination time interval at the coordinating current. `GradeRadialFeeder` takes the currents from an IEC 60909
study along a bus path, converting to amperes at each relay's voltage, so transformers on the path are handled.

```csharp
var r1 = new OvercurrentRelay("Feeder", 200);
var r2 = new OvercurrentRelay("Incomer", 400);
var r3 = new OvercurrentRelay("HV side", 600);
var grading = Coordination.GradeTimeMultipliers(
[
    new GradingStage(r1, MaxFaultCurrentA: 3000, CoordinationCurrentA: 3000),
    new GradingStage(r2, 5000, 3000),
    new GradingStage(r3, 7000, 5000),
], ctiS: 0.3);
Console.WriteLine($"All coordinated: {grading.AllCoordinated}; TMS = {r1.TimeMultiplier}, {r2.TimeMultiplier}, {r3.TimeMultiplier}");
```

### Distance and differential protection

```csharp
var zLine = new Complex(1, 10);   // Ω primary
var relay = DistanceRelay.Mho("21-L1", zLine, shortestNextLineOhm: zLine * 0.6, longestNextLineOhm: zLine * 1.2);
var zone = relay.OperatingZone(new Complex(0.5, 5));   // zone 1 (50 % of the line)
double t = relay.OperatingTime(1.1 * zLine);           // zone-2 delay

var diff = new PercentDifferentialRelay { MinimumPickupPu = 0.3, Slope1 = 0.25, Slope2 = 0.5 };
var decision = diff.Evaluate(new Complex(10, 0), new Complex(-9.5, 0));   // through fault → restrain
```

Mho settings: Z1 = 80 % of the line, Z2 = max(120 % of the line, line + 50 % of the shortest next line), Z3 = 120 % of (line + longest next
line). `DistanceRelay.ForBranch(network, branchIndex)` builds a relay from a network branch; `ResidualCompensation`,
`ApparentImpedanceGround/Phase` and `ToSecondary` help with measured impedances. The differential relay is dual-slope with an unrestrained
stage and second-harmonic blocking.

### Breaker duty

```csharp
var study = new ShortCircuitStudy(TestCases.Case14());
var duty = BreakerDuty.CheckAll(study, [(4, new BreakerRating("CB-4", 145, BreakingCurrentKa: 31.5))]);
foreach (var d in duty)
    Console.WriteLine($"{d.Breaker}: breaking {d.BreakingDutyPercent:0}% making {d.MakingDutyPercent:0}% adequate={d.Adequate}");
```

Each breaker is checked against the worst of the 3φ, SLG and LLG faults: breaking (I<sub>b</sub>), making (ip vs. 2.5/2.6·I<sub>sc</sub>
unless given) and short-time (I²t) duties, plus the voltage rating.

### Arc flash (IEEE 1584-2002)

```csharp
var r = ArcFlash.Calculate(new ArcFlashInput(SystemKv: 0.48, BoltedFaultKa: 30, ArcDurationS: 0.1,
    WorkingDistanceMm: 610, Equipment: ArcEquipment.Switchgear, GapMm: 32));
Console.WriteLine($"Ia = {r.ArcingCurrentKa:0.00} kA, E = {r.IncidentEnergyCalCm2:0.00} cal/cm², " +
                  $"AFB = {r.ArcFlashBoundaryMm:0} mm, PPE {r.PpeCategory}");
// Ia = 15.72 kA, E = 3.43 cal/cm², AFB = 1245 mm, PPE 1

// Arc duration from a protective device (with the 85 % arcing-current check)
var incomer = new OvercurrentRelay("MCC incomer", 1600, CurveType.IecExtremelyInverse, 0.2);
var withDevice = ArcFlash.Calculate(new ArcFlashInput(0.48, 30, 0, 455, ArcEquipment.MotorControlCenter), incomer, breakerOpeningS: 0.05);
```

| Case | IEEE 1584-2002 hand calculation | Electrical.Net |
|---|---|---|
| 480 V switchgear, 30 kA, 0.1 s, 610 mm, G = 32 mm | I<sub>a</sub> 15.72 kA, E 3.434 cal/cm², AFB 1245 mm, PPE 1 | 15.72 kA, 3.434 cal/cm², 1245 mm, 1 |
| 13.8 kV switchgear, 20 kA, 0.2 s, 910 mm | I<sub>a</sub> 19.18 kA, E 5.23 cal/cm², PPE 2 | 19.18 kA, 5.226 cal/cm², 2 |

The model is the **2002** empirical model (0.208–15 kV, Lee method above 15 kV) with NFPA 70E PPE categories. It is not the IEEE 1584-2018
model (electrode configurations, enclosure correction); use a validated 2018 tool for equipment labels.

CLI: `eln arcflash --kv 0.48 --ika 30 --time 0.2` and `eln relay --pickup 400 --tms 0.1 --current 4000` (see [CLI](cli.md)).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/hubung-singkat-proteksi.md)*
