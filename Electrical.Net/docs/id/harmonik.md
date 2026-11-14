# Harmonik dan kualitas daya

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/harmonics.md)

Paket: `Electrical.Harmonic` (bergantung pada PowerFlow). Untuk bentuk gelombang rangkaian domain waktu lihat `Electrical.Circuit` di
[Simulasi, HPC & native](simulasi-hpc-native.md#simulasi-rangkaian); untuk kernel FFT/Goertzel Rust lihat `Electrical.Native`.

## Spektrum dari sampel

`Fft.Forward` menangani panjang berapa pun (radix-2 untuk pangkat dua, Bluestein untuk lainnya). `HarmonicSpectrum.FromSamples` mengembalikan
magnitudo RMS dan fasa per orde harmonik (indeks 0 = DC, 1 = fundamental).

```csharp
double fs = 6400, f0 = 50;
var x = Enumerable.Range(0, 256)
    .Select(i => 100 * Math.Cos(2 * Math.PI * f0 * i / fs) + 20 * Math.Cos(2 * Math.PI * 5 * f0 * i / fs + Math.PI / 6))
    .ToArray();
var s = HarmonicSpectrum.FromSamples(x, fs, f0, maxOrder: 40);
Console.WriteLine($"V1 = {s.Fundamental:0.00} rms, V5 = {s[5]:0.00} rms ∠{s.Phase(5):0}°, THD = {s.ThdPercent:0.00} %, K = {s.KFactor:0.00}");
// V1 = 70.71 rms, V5 = 14.14 rms ∠30°, THD = 20.00 %
```

Indeks lain: `TddPercent(maximumDemandCurrent)`, `KFactor`, `TotalRms`, `Synthesize`. `PowerQuality` menambahkan `Rms`, `CrestFactor`,
`VoltageUnbalancePercent` (|V2|/|V1|), `NemaUnbalancePercent`, dan `NemaDerating`. `HarmonicSpectrum.FromPercent` membangun spektrum dari tabel
datasheet.

## Standar

```csharp
var limits = Ieee519.CheckVoltage(s, busKv: 13.8);        // 3 % individual, THD 5 % untuk 1 kV < V ≤ 69 kV
foreach (var row in limits.Rows.Where(r => r.Measured > r.Limit))
    Console.WriteLine($"h{row.Order}: {row.Measured:0.00} % > {row.Limit:0.00} %");
```

- `Ieee519`: batas tegangan menurut tegangan bus (Tabel 1), batas arus menurut I<sub>sc</sub>/I<sub>L</sub> dan tegangan bus, harmonik genap
  25 % dari batas ganjil, `CheckCurrent` dengan TDD.
- `Iec61000`: tingkat kompatibilitas IEC 61000-2-2 (`VoltageCompatibilityLevelPercent`, `CheckVoltage`) dan batas Kelas A IEC 61000-3-2
  (`ClassACurrentLimitA`, `CheckClassA`).
- `ComplianceResult` berisi `Rows` (orde, terukur, batas), total, dan `Violations`.

## Sumber harmonik

`TypicalSpectra` menyediakan konverter 6- dan 12-pulsa ideal (magnitudo 1/h), penyearah 6-pulsa tipikal, drive PWM dengan dan tanpa choke DC,
serta driver LED tanpa PFC. THD 6-pulsa ideal mendekati √(π²/9 − 1) = 31,08 %.

## Aliran daya harmonik

Metode injeksi arus: aliran daya fundamental menentukan sudut fasa sumber, lalu untuk setiap orde h jaringan Y(h)·V(h) = I(h) diselesaikan.
Beban dimodelkan sebagai impedansi R–L paralel (R = V²/P, X = h·V²/Q), generator memakai X″d (reaktansi urutan negatif untuk h = 3k − 1), dan
orde kelipatan tiga memakai jaringan urutan nol bila `UseSequenceNetworks` aktif.

```csharp
var net = TestCases.Case14();
var hlf = HarmonicLoadFlow.Run(net, [new HarmonicInjection(9, TypicalSpectra.SixPulseRectifier(), FundamentalCurrentPu: 0.1)]);
var (bus, thd) = hlf.WorstBus;
Console.WriteLine($"THDv terburuk {thd:0.00} % di bus {bus}");
foreach (var (busId, check) in hlf.CheckIeee519())
    Console.WriteLine($"Bus {busId}: THD {check.TotalMeasured:0.00} % (batas {check.TotalLimit} %)");
```

`HarmonicLoadFlowResult` menyediakan `Voltage(bus, h)`, `VoltageSpectrum(bus)`, `ThdPercent(bus)`, `BranchCurrent(branch, h)`, dan
`PowerFlowResult` fundamental.

```
$ eln harmonic --case case14 --bus 9
Harmonic load flow on case14: 6-pulse rectifier (typical) at bus 9
  Worst voltage THD: 2.96 % at bus 9
  Bus 9    THDv   2.96 %
  Bus 10   THDv   2.62 %
  Bus 14   THDv   2.21 %
  ...
```

Dengan `--source vfd` (drive PWM tanpa choke) THD terburuk di bus 9 menjadi 10,15 %.

## Frequency scan, resonansi, dan filter

```csharp
var scan = FrequencyScan.DrivingPoint(TestCases.Case14(), busId: 9, hMin: 1, hMax: 25, step: 0.05);
foreach (var r in FrequencyScan.FindResonances(scan))
    Console.WriteLine($"Resonansi {(r.Parallel ? "paralel" : "seri")} pada h = {r.Order:0.00} ({r.FrequencyHz:0} Hz), |Z| = {r.ImpedanceOhm:0.0} Ω");

double hr = FrequencyScan.ParallelResonanceOrder(shortCircuitMva: 500, capacitorMvar: 20);   // √(500/20) = 5,0

var filter = SingleTunedFilter.Design(systemKv: 13.8, effectiveMvar: 5, tunedOrder: 4.7, qualityFactor: 30, frequencyHz: 60);
Console.WriteLine($"XC = {filter.CapacitorReactanceOhm:0.000} Ω, XL = {filter.ReactorReactanceOhm:0.000} Ω");   // 39,894 Ω, 1,806 Ω
```

Desain filter memakai X<sub>eff</sub> = kV²/Q, X<sub>C</sub> = X<sub>eff</sub>·h²/(h² − 1), X<sub>L</sub> = X<sub>C</sub>/h², R = h·X<sub>L</sub>/Q.
`Impedance(h)`, `CapacitorVoltageRise`, `FundamentalMvar`, dan `CapacitanceMicroF` tersedia pada hasilnya. Filter ditambahkan ke jaringan sebagai
shunt; tes menunjukkan bahwa mengganti bank 6 Mvar dengan filter 6 Mvar orde 4,7 mengalihkan sebagian besar arus harmonik ke-5 ke filter.

## Validasi

| Pemeriksaan | Acuan | Hasil |
|---|---|---|
| FFT vs. DFT langsung, panjang 64 / 100 / 37 | Definisi DFT | sama, round trip invers |
| 100 cos ωt + 20 cos(5ωt + 30°) | THD 20 %, RMS 70,71 / 14,14 | eksak |
| Faktor-K, TDD | Definisi | eksak |
| Batas IEEE 519 (I<sub>sc</sub>/I<sub>L</sub> = 30, 13,8 kV) | Tabel 2: 7 % / 3,5 %, TDD 8 % | eksak |
| Tabel IEC 61000-2-2 / 3-2 | Nilai standar | eksak |
| Frequency scan, sumber 500 MVA, bank 20 Mvar | h<sub>r</sub> = 5,0 (300 Hz) | 5,0 |
| Filter single-tuned 13,8 kV, 5 Mvar, h = 4,7 | X<sub>C</sub> 39,894 Ω, X<sub>L</sub> 1,806 Ω | eksak |

## Batasan

Jaringan seimbang; tidak ada model saluran yang bergantung frekuensi (X saluran berskala h, R konstan kecuali `SkinEffect` untuk generator);
tidak ada flickermeter (P<sub>st</sub>); sumber konverter berupa spektrum arus tetap.

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/harmonics.md)*
