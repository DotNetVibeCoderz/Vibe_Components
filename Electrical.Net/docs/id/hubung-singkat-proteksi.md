# Hubung singkat dan proteksi

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/short-circuit-protection.md)

Paket: `Electrical.ShortCircuit` (IEC 60909) dan `Electrical.Protection` (relai, koordinasi, breaker duty, arc flash).

## Hubung singkat IEC 60909

`ShortCircuitStudy` membangun matriks admitansi urutan positif, negatif, dan nol (cabang ditambah impedansi sumber; beban dan shunt diabaikan
kecuali `IncludeShunts`), memfaktorkannya sekali dengan `SparseLU`, lalu menghitung setiap gangguan dengan satu solve sparse per urutan (satu
kolom Z-bus). Sumber tegangan ekivalen adalah c·U<sub>n</sub>/√3 di lokasi gangguan.

Sumber arus gangguan:

- **Generator** di jaringan memakai `XdSubtransient`, `X2`, `X0`, `Ra` pada `MBase`; `ApplyGeneratorCorrection` menerapkan K<sub>G</sub>
  (IEC 60909-0 pers. 18).
- **Jaringan eksternal** (`ExternalGrid`) adalah infeed yang ditentukan oleh S″k [MVA], R/X, c, dan Z0/Z1.
- Jalur urutan nol trafo mengikuti `Branch.Connection` (YNyn, YNd, Dyn, …).

### Contoh (divalidasi dengan perhitungan tangan)

```csharp
// Jaringan 110 kV (S"k = 3000 MVA, R/X = 0,1) → trafo 40 MVA 110/20 kV Dyn (uk = 12 %) → saluran 5 km 0,2 + j0,35 Ω/km
var net = new NetworkBuilder("radial", 100, 50)
    .AddBus(1, 110, BusType.Slack).AddBus(2, 20).AddBus(3, 20)
    .AddTransformer(1, 2, 40, 12, 0, connection: WindingConnection.Dyn)
    .AddLine(2, 3, 5, 0.2, 0.35)
    .Build();

var options = new ShortCircuitOptions
{
    ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 3000, RxRatio = 0.1, C = 1.1 }],
    PeakMethod = PeakCurrentMethod.EquivalentFrequency,   // IEC 60909-0 metode C (default)
};

var study = new ShortCircuitStudy(net, options);         // memfaktorkan Y1, Y2, Y0 sekali
FaultResult f3 = study.Calculate(3, FaultType.ThreePhase);
FaultResult f1 = study.Calculate(3, FaultType.SingleLineToGround);
Console.WriteLine(f3);
Console.WriteLine($"κ = {f3.Kappa:0.000}, ip = {f3.IpKa:0.00} kA, 3I0 = {f1.EarthCurrentKa:0.00} kA");

foreach (var row in study.ScanAllBuses())
    Console.WriteLine($"Bus {row.BusId}: I\"k3 {row.Ik3Ka:0.00} kA, I\"k1 {row.Ik1Ka:0.00} kA, S\"k {row.Sk3Mva:0} MVA");

var high = study.Calculate(3, FaultType.SingleLineToGround, faultImpedanceOhm: new Complex(10, 0));
```

Keluaran dua hasil gangguan:

```
ThreePhase fault at bus 3 (20 kV): I"k = 3.899 kA, ip = 7.645 kA, Ib = 3.899 kA, Ith = 3.919 kA, S"k = 135.1 MVA
SingleLineToGround fault at bus 3 (20 kV): I"k = 2.801 kA, ip = 5.492 kA, Ib = 2.801 kA, Ith = 2.815 kA, S"k = 97 MVA
```

Pemeriksaan tangan (IEC 60909-0 pers. 6, 7, 12, dirujuk ke 20 kV): Z<sub>Q</sub> = 1,1·20²/3000 = 0,1467 Ω (X<sub>Q</sub> = Z/√1,01,
R<sub>Q</sub> = 0,1·X<sub>Q</sub>), Z<sub>T</sub> = 0,12·20²/40 = j1,2 Ω, Z<sub>L</sub> = 1 + j1,75 Ω, sehingga
I″k3 = 1,1·20 / (√3·|Z<sub>k</sub>|) = **3,899 kA**. Tes juga memeriksa I″k2 = (√3/2)·I″k3, rumus SLG 3E/|Z1+Z2+Z0|, arus tanah LLG
√3·c·U<sub>n</sub>/|Z1+2Z0|, kontribusi cabang yang diskalakan rasio trafo, serta κ untuk metode B dan C.

### `FaultResult`

| Properti | Arti |
|---|---|
| `IkKa` | Arus hubung singkat simetris awal I″k |
| `IpKa`, `Kappa`, `RxRatio` | Arus puncak ip = κ·√2·I″k (metode sesuai `PeakMethod`) |
| `IbKa`, `IbAsymKa`, `IdcKa` | Arus pemutusan simetris / asimetris, komponen DC pada `MinimumTimeDelayS` |
| `IthKa` | Arus ekivalen termal untuk `FaultDurationS` |
| `EarthCurrentKa` | 3I₀ |
| `SkMva` | S″k = √3·U<sub>n</sub>·I″k |
| `PhaseCurrentsKa`, `SequenceCurrentsPu` | Arus gangguan per fasa / urutan |
| `Voltages` | Tegangan fasa di setiap bus selama gangguan (pu, `VaKv` …) |
| `Branches`, `Sources` | Arus cabang dan kontribusi sumber |

`Iec60909` menyediakan rumus: `VoltageFactor(kV, maximum)`, `Kappa(R/X)`, `ThermalFactorM`, `DecayFactorMu`, `GeneratorCorrectionFactor`,
`DcComponent`.

### Pemindaian semua bus dari CLI

```
$ eln sc --case case14
IEC 60909 short-circuit scan (cmax) on case14
  Bus     kV     I"k3[kA] I"k2[kA] I"k1[kA]  ip[kA]   S"k[MVA]   R/X
  1        138     5.775    5.002    6.075   14.719     1380.4  0.075
  2        138     6.701    5.803    7.035   17.399     1601.7  0.061
  ...
  14      13.8    16.100   13.943   10.925   31.907      384.8  0.315
```

### Batasan

Pergeseran fasa trafo diabaikan secara default (tidak mengubah magnitudo di lokasi gangguan); arus pemutusan mengasumsikan gangguan jauh dari
generator (I<sub>b</sub> = I″k; gunakan `Iec60909.DecayFactorMu` dekat generator); arus beban pra-gangguan tidak ditumpangkan, sesuai metode
IEC 60909. Faktor pengali ANSI/IEEE C37 tidak diimplementasikan.

## Proteksi

### Relai arus lebih waktu-terbalik dan fuse

Kurva mengikuti t = TMS·(A/(M<sup>P</sup> − 1) + B) dengan konstanta IEC 60255-151 (SI, VI, EI, LTI) dan IEEE C37.112 (MI, VI, EI), ditambah
waktu tertentu (definite time). Pada M = 10 dan TMS = 1 kurva IEC memberi 2,971 s (SI), 1,5 s (VI), 0,808 s (EI), dan 13,33 s (LTI), sesuai
yang diuji.

```csharp
var relay = new OvercurrentRelay("R1", pickupA: 400, CurveType.IecStandardInverse, timeMultiplier: 0.1)
{
    InstantaneousPickupA = 8000,
    CtRatio = 400.0 / 1,
};
double t = relay.OperatingTime(4000);     // 0,297 s (10× pickup, kurva SI, TMS 0,1)

var fuse = Fuse.FromI2t("100 A fuse", 100, meltingI2tA2s: 30_000);   // t = I²t / I² di atas 1,6·In
var check = Coordination.Check(fuse, relay, downstreamCurrentA: 1500);
var tcc = Tcc.Curve(relay, 400, 20_000);   // titik untuk grafik TCC log-log
```

`Fuse` juga bisa dibangun dari kurva waktu-arus berbentuk tabel (interpolasi log-log).

### Koordinasi

`Coordination.GradeTimeMultipliers` menyetel relai paling hilir ke TMS minimum dan setiap relai hulu ke TMS terkecil (dibulatkan ke `tmsStep`)
yang menjaga coordination time interval pada arus koordinasi. `GradeRadialFeeder` mengambil arus dari studi IEC 60909 di sepanjang jalur bus
dan mengonversinya ke ampere pada tegangan setiap relai, sehingga trafo di jalur ikut ditangani.

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
Console.WriteLine($"Semua terkoordinasi: {grading.AllCoordinated}; TMS = {r1.TimeMultiplier}, {r2.TimeMultiplier}, {r3.TimeMultiplier}");
```

### Proteksi jarak dan diferensial

```csharp
var zLine = new Complex(1, 10);   // Ω primer
var relay = DistanceRelay.Mho("21-L1", zLine, shortestNextLineOhm: zLine * 0.6, longestNextLineOhm: zLine * 1.2);
var zone = relay.OperatingZone(new Complex(0.5, 5));   // zona 1 (50 % saluran)
double t = relay.OperatingTime(1.1 * zLine);           // tunda zona 2

var diff = new PercentDifferentialRelay { MinimumPickupPu = 0.3, Slope1 = 0.25, Slope2 = 0.5 };
var decision = diff.Evaluate(new Complex(10, 0), new Complex(-9.5, 0));   // gangguan lewat → restrain
```

Setelan mho: Z1 = 80 % saluran, Z2 = maks(120 % saluran, saluran + 50 % saluran berikut terpendek), Z3 = 120 % dari (saluran + saluran berikut
terpanjang). `DistanceRelay.ForBranch(network, branchIndex)` membangun relai dari cabang jaringan; `ResidualCompensation`,
`ApparentImpedanceGround/Phase`, dan `ToSecondary` membantu menghitung impedansi terukur. Relai diferensial memakai dua slope dengan tahap
unrestrained dan blocking harmonik kedua.

### Breaker duty

```csharp
var study = new ShortCircuitStudy(TestCases.Case14());
var duty = BreakerDuty.CheckAll(study, [(4, new BreakerRating("CB-4", 145, BreakingCurrentKa: 31.5))]);
foreach (var d in duty)
    Console.WriteLine($"{d.Breaker}: breaking {d.BreakingDutyPercent:0}% making {d.MakingDutyPercent:0}% adequate={d.Adequate}");
```

Setiap pemutus diperiksa terhadap gangguan terburuk dari 3φ, SLG, dan LLG: duty pemutusan (I<sub>b</sub>), penutupan (ip vs. 2,5/2,6·I<sub>sc</sub>
bila tidak diberikan), dan waktu singkat (I²t), ditambah rating tegangan.

### Arc flash (IEEE 1584-2002)

```csharp
var r = ArcFlash.Calculate(new ArcFlashInput(SystemKv: 0.48, BoltedFaultKa: 30, ArcDurationS: 0.1,
    WorkingDistanceMm: 610, Equipment: ArcEquipment.Switchgear, GapMm: 32));
Console.WriteLine($"Ia = {r.ArcingCurrentKa:0.00} kA, E = {r.IncidentEnergyCalCm2:0.00} cal/cm², " +
                  $"AFB = {r.ArcFlashBoundaryMm:0} mm, PPE {r.PpeCategory}");
// Ia = 15.72 kA, E = 3.43 cal/cm², AFB = 1245 mm, PPE 1

// Durasi busur dari perangkat proteksi (dengan pemeriksaan arus busur 85 %)
var incomer = new OvercurrentRelay("MCC incomer", 1600, CurveType.IecExtremelyInverse, 0.2);
var withDevice = ArcFlash.Calculate(new ArcFlashInput(0.48, 30, 0, 455, ArcEquipment.MotorControlCenter), incomer, breakerOpeningS: 0.05);
```

| Kasus | Perhitungan tangan IEEE 1584-2002 | Electrical.Net |
|---|---|---|
| Switchgear 480 V, 30 kA, 0,1 s, 610 mm, G = 32 mm | I<sub>a</sub> 15,72 kA, E 3,434 cal/cm², AFB 1245 mm, PPE 1 | 15,72 kA, 3,434 cal/cm², 1245 mm, 1 |
| Switchgear 13,8 kV, 20 kA, 0,2 s, 910 mm | I<sub>a</sub> 19,18 kA, E 5,23 cal/cm², PPE 2 | 19,18 kA, 5,226 cal/cm², 2 |

Modelnya adalah model empiris **2002** (0,208–15 kV, metode Lee di atas 15 kV) dengan kategori PPE NFPA 70E. Ini bukan model IEEE 1584-2018
(konfigurasi elektroda, koreksi enklosur); gunakan tool 2018 yang tervalidasi untuk label peralatan.

CLI: `eln arcflash --kv 0.48 --ika 30 --time 0.2` dan `eln relay --pickup 400 --tms 0.1 --current 4000` (lihat [CLI](cli.md)).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/short-circuit-protection.md)*
