# Harmonics and power quality

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/harmonik.md)

Package: `Electrical.Harmonic` (depends on PowerFlow). For time-domain circuit waveforms see `Electrical.Circuit` in
[Simulation, HPC & native](simulation-hpc-native.md#circuit-simulation); for the Rust FFT/Goertzel kernels see `Electrical.Native`.

## Spectra from samples

`Fft.Forward` handles any length (radix-2 for powers of two, Bluestein otherwise). `HarmonicSpectrum.FromSamples` returns RMS magnitudes
and phases per harmonic order (index 0 = DC, 1 = fundamental).

```csharp
double fs = 6400, f0 = 50;
var x = Enumerable.Range(0, 256)
    .Select(i => 100 * Math.Cos(2 * Math.PI * f0 * i / fs) + 20 * Math.Cos(2 * Math.PI * 5 * f0 * i / fs + Math.PI / 6))
    .ToArray();
var s = HarmonicSpectrum.FromSamples(x, fs, f0, maxOrder: 40);
Console.WriteLine($"V1 = {s.Fundamental:0.00} rms, V5 = {s[5]:0.00} rms ∠{s.Phase(5):0}°, THD = {s.ThdPercent:0.00} %, K = {s.KFactor:0.00}");
// V1 = 70.71 rms, V5 = 14.14 rms ∠30°, THD = 20.00 %
```

Other indices: `TddPercent(maximumDemandCurrent)`, `KFactor`, `TotalRms`, `Synthesize`. `PowerQuality` adds `Rms`, `CrestFactor`,
`VoltageUnbalancePercent` (|V2|/|V1|), `NemaUnbalancePercent` and `NemaDerating`. `HarmonicSpectrum.FromPercent` builds a spectrum from a
datasheet table.

## Standards

```csharp
var limits = Ieee519.CheckVoltage(s, busKv: 13.8);        // 3 % individual, 5 % THD for 1 kV < V ≤ 69 kV
foreach (var row in limits.Rows.Where(r => r.Measured > r.Limit))
    Console.WriteLine($"h{row.Order}: {row.Measured:0.00} % > {row.Limit:0.00} %");
```

- `Ieee519`: voltage limits by bus voltage (Table 1), current limits by I<sub>sc</sub>/I<sub>L</sub> and bus voltage, even harmonics at
  25 % of odd limits, `CheckCurrent` with TDD.
- `Iec61000`: IEC 61000-2-2 compatibility levels (`VoltageCompatibilityLevelPercent`, `CheckVoltage`) and IEC 61000-3-2 Class A limits
  (`ClassACurrentLimitA`, `CheckClassA`).
- `ComplianceResult` lists `Rows` (order, measured, limit), totals and `Violations`.

## Harmonic sources

`TypicalSpectra` provides ideal 6- and 12-pulse converters (1/h magnitudes), a typical 6-pulse rectifier, PWM drives with and without a DC
choke and an LED driver without PFC. The ideal 6-pulse THD approaches √(π²/9 − 1) = 31.08 %.

## Harmonic load flow

The current-injection method: a fundamental power flow fixes the source phase angles, then for each order h the network
Y(h)·V(h) = I(h) is solved. Loads are parallel R–L impedances (R = V²/P, X = h·V²/Q), generators use X″d (negative-sequence reactance for
h = 3k − 1), and triplen orders use the zero-sequence network when `UseSequenceNetworks` is on.

```csharp
var net = TestCases.Case14();
var hlf = HarmonicLoadFlow.Run(net, [new HarmonicInjection(9, TypicalSpectra.SixPulseRectifier(), FundamentalCurrentPu: 0.1)]);
var (bus, thd) = hlf.WorstBus;
Console.WriteLine($"Worst THDv {thd:0.00} % at bus {bus}");
foreach (var (busId, check) in hlf.CheckIeee519())
    Console.WriteLine($"Bus {busId}: THD {check.TotalMeasured:0.00} % (limit {check.TotalLimit} %)");
```

`HarmonicLoadFlowResult` exposes `Voltage(bus, h)`, `VoltageSpectrum(bus)`, `ThdPercent(bus)`, `BranchCurrent(branch, h)` and the fundamental
`PowerFlowResult`.

```
$ eln harmonic --case case14 --bus 9
Harmonic load flow on case14: 6-pulse rectifier (typical) at bus 9
  Worst voltage THD: 2.96 % at bus 9
  Bus 9    THDv   2.96 %
  Bus 10   THDv   2.62 %
  Bus 14   THDv   2.21 %
  ...
```

With `--source vfd` (PWM drive without choke) the worst THD at bus 9 is 10.15 %.

## Frequency scan, resonance and filters

```csharp
var scan = FrequencyScan.DrivingPoint(TestCases.Case14(), busId: 9, hMin: 1, hMax: 25, step: 0.05);
foreach (var r in FrequencyScan.FindResonances(scan))
    Console.WriteLine($"{(r.Parallel ? "Parallel" : "Series")} resonance at h = {r.Order:0.00} ({r.FrequencyHz:0} Hz), |Z| = {r.ImpedanceOhm:0.0} Ω");

double hr = FrequencyScan.ParallelResonanceOrder(shortCircuitMva: 500, capacitorMvar: 20);   // √(500/20) = 5.0

var filter = SingleTunedFilter.Design(systemKv: 13.8, effectiveMvar: 5, tunedOrder: 4.7, qualityFactor: 30, frequencyHz: 60);
Console.WriteLine($"XC = {filter.CapacitorReactanceOhm:0.000} Ω, XL = {filter.ReactorReactanceOhm:0.000} Ω");   // 39.894 Ω, 1.806 Ω
```

The filter design uses X<sub>eff</sub> = kV²/Q, X<sub>C</sub> = X<sub>eff</sub>·h²/(h² − 1), X<sub>L</sub> = X<sub>C</sub>/h²,
R = h·X<sub>L</sub>/Q. `Impedance(h)`, `CapacitorVoltageRise`, `FundamentalMvar` and `CapacitanceMicroF` are available on the result. A
filter is added to a network as a shunt; the tests show that replacing a 6 Mvar bank by a 6 Mvar 4.7th filter moves most 5th-harmonic current
into the filter.

## Validation

| Check | Reference | Result |
|---|---|---|
| FFT vs. direct DFT, lengths 64 / 100 / 37 | DFT definition | equal, inverse round trip |
| 100 cos ωt + 20 cos(5ωt + 30°) | THD 20 %, RMS 70.71 / 14.14 | exact |
| K-factor, TDD | Definitions | exact |
| IEEE 519 limits (I<sub>sc</sub>/I<sub>L</sub> = 30, 13.8 kV) | Table 2: 7 % / 3.5 %, TDD 8 % | exact |
| IEC 61000-2-2 / 3-2 tables | Standard values | exact |
| Frequency scan, 500 MVA source, 20 Mvar bank | h<sub>r</sub> = 5.0 (300 Hz) | 5.0 |
| Single-tuned filter 13.8 kV, 5 Mvar, h = 4.7 | X<sub>C</sub> 39.894 Ω, X<sub>L</sub> 1.806 Ω | exact |

## Limitations

Balanced networks; no frequency-dependent line models (lines scale X with h, R constant unless `SkinEffect` for generators); no flicker
(P<sub>st</sub>) meter; converter sources are fixed current spectra.

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/harmonik.md)*
