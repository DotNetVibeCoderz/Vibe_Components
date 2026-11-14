# Energi terbarukan dan smart grid

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/renewables-smartgrid.md)

Paket: `Electrical.Renewable`, `Electrical.SmartGrid`, dan `Electrical.Forecasting`. Studi deret waktu (QSTS) yang menggabungkannya dengan aliran
daya ada di [Simulasi, HPC & native](simulasi-hpc-native.md).

## PV surya

```csharp
var module = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT, idealityFactor: 1.3);
Console.WriteLine($"Rs = {module.Rs:0.000} Ω, Rp = {module.Rp:0.0} Ω");        // 0,221 Ω, 416,3 Ω
var mpp = module.MaximumPowerPoint(irradiance: 1000, cellTempC: 25);           // 200,15 W pada 26,35 V
var iv = module.IvCurve(600, 45, points: 60);

var sun = SolarPosition.Compute(new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.FromHours(7)), -6.2, 106.8);
var clear = Irradiance.IneichenClearSky(sun.ZenithDeg, dayOfYear: 79);
var poa = Irradiance.PlaneOfArray(clear, sun, tiltDeg: 10, surfaceAzimuthDeg: 0);
double tCell = CellTemperature.Noct(ambientC: 31, poaWm2: poa.Global);
double pdc = PvWatts.DcPower(poa.Global, tCell, pdc0: 5000);                  // W untuk array 5 kWp
double pac = PvWatts.InverterAc(pdc, pac0: 4600);
```

- `SolarPosition.Compute` (setara SPA; sesuai contoh NREL SPA dalam 0,05°) dan `Spencer` (aproksimasi cepat).
- `Irradiance`: radiasi normal ekstraterestrial, massa udara, clear-sky Haurwitz dan Ineichen, dekomposisi Erbs, sudut datang, plane of array
  (langit isotropik + pantulan tanah).
- `CellTemperature.Noct` dan `Faiman`.
- `SingleDiodeModel`: ekstraksi lima parameter dari datasheet (Villalva 2009), kurva I–V, MPP, ketergantungan suhu dan iradiasi.
- `PvWatts` (model DC v5, kurva inverter, `AcPower` ujung ke ujung), `InverterEfficiencyCurve`, `SandiaInverter`, `Mppt.PerturbAndObserve`.

## Angin

```csharp
var turbine = WindTurbine.Generic2MW;                                        // 2 MW, cut-in 3, rated 12, cut-out 25 m/s
var site = WeibullDistribution.Rayleigh(meanMs: 7.0);
var aep = WindEnergy.AnnualEnergyProduction(turbine, site, airDensity: AirDensity.AtAltitude(500), availability: 0.97, losses: 0.08);
Console.WriteLine($"AEP {aep.AepMwh:0} MWh, CF {aep.CapacityFactor:P1}");
double vHub = WindShear.PowerLaw(vRef: 6.2, hRefM: 10, hM: turbine.HubHeightM);
```

Dengan kerapatan udara standar, tanpa rugi, dan lokasi Rayleigh 7 m/s, turbin generik 2 MW menghasilkan 5127 MWh (faktor kapasitas 29,3 %).
`WindTurbine.FromTable` menerima kurva daya pabrikan; `WeibullDistribution.Fit` mencocokkan k dan c dengan data kecepatan terukur;
`WindEnergy.EnergyFromSeries` mengintegrasikan deret kecepatan.

## Penyimpanan baterai

```csharp
var bess = BatteryStorage.FromCRate(capacityMwh: 10, cRate: 0.5, initialSoc: 0.5);   // 10 MWh / 5 MW
double delivered = bess.Step(requestedMw: 4, hours: 1);    // + discharge, − charge; mengembalikan MW yang benar-benar tersalurkan
Console.WriteLine($"SoC {bess.Soc:P0}, SoH {bess.StateOfHealth:P2}");
```

`BatteryStorage` menerapkan batas daya, jendela SoC, dan efisiensi pengisian/pengosongan, serta melacak siklus penuh ekivalen dan kerusakan
setengah siklus (aturan Miner dengan umur siklus Wöhler N(DoD) = N₁₀₀·DoD^−k) untuk state of health.

## Grid code dan hosting capacity

- `VoltVarCurve.Ieee1547CategoryA/B`, `VoltWattCurve.Ieee1547`, `FrequencyDroop.Ieee1547` (default IEEE 1547-2018).
- `HostingCapacity.Analyze` menambahkan PV di sebuah bus secara bertahap dan mencari injeksi terbesar yang menjaga tegangan ≤ `vmaxPu` dan
  pembebanan cabang ≤ `loadingLimitPercent`.

```csharp
var hc = HostingCapacity.Analyze(TestCases.Case33BW(), busId: 18, stepMw: 0.25, maxMw: 6);
Console.WriteLine($"{hc.HostingCapacityMw:0.00} MW, dibatasi oleh {hc.LimitingFactor}");   // 2,09 MW, dibatasi oleh Overvoltage
```

## Smart grid

### Otomasi distribusi

```csharp
var recon = FeederReconfiguration.Optimize(FeederReconfiguration.Case33WithTies());
Console.WriteLine($"Buka {string.Join(", ", recon.OpenBranches)}: {recon.LossesBeforeMw * 1000:0.0} → {recon.LossesAfterMw * 1000:0.0} kW");
// Buka 14-15, 25-29, 32-33, 7-8, 9-10: 202,7 → 139,6 kW

var vvo = VoltVarOptimization.Optimize(TestCases.Case33BW(), VoltVarControls.Case33Default());
Console.WriteLine($"Rugi VVO {vvo.LossesAfterMw * 1000:0.0} kW, V [{vvo.VminAfter:0.000}, {vvo.VmaxAfter:0.000}] pu");
```

- Rekonfigurasi: pembukaan saklar berurutan (Shirmohammadi & Hong 1989) ditambah branch exchange (Civanlar dkk. 1988). Pada penyulang Baran–Wu
  dengan lima saklar tie, hasilnya membuka himpunan rugi minimum yang dikenal (7-8, 9-10, 14-15, 32-33, 25-29): 202,7 → 139,6 kW, V<sub>min</sub>
  0,9131 → 0,9378 pu.
- Optimasi Volt/VAR: coordinate descent diskrit atas langkah kapasitor, set-point gardu induk, dan daya reaktif inverter. Dengan kontrol
  `Case33Default`: 202,7 → 118,6 kW dan semua tegangan dalam [0,95; 1,05] pu.

### Indeks keandalan

```csharp
var net = TestCases.Case33BW();
var idx = RadialReliability.Evaluate(net, RadialReliability.Uniform(net, failureRatePerYear: 0.1, repairHours: 4));
Console.WriteLine($"SAIFI {idx.Saifi:0.00}, SAIDI {idx.Saidi:0.00} jam, ENS {idx.EnsMwh:0.0} MWh/thn");
```

λ, U, dan r titik beban serta indeks SAIFI/SAIDI/CAIDI/ASAI/ENS mereproduksi Billinton & Allan §7.2 (Tabel 7.1: λ<sub>C</sub> 0,45 f/thn,
U<sub>C</sub> 2,9 jam). Fuse (`ProtectedByFuse`), jumlah pelanggan, dan waktu sectionalizing didukung.

### Demand response, EV, microgrid, dan VPP

```csharp
double[] load = [52, 50, 49, 50, 55, 63, 75, 84, 88, 90, 92, 91, 90, 89, 90, 93, 97, 100, 98, 92, 82, 72, 63, 56];
var shave = DemandResponse.OptimalPeakShaving(load, BatteryStorage.FromCRate(20, 0.5));
Console.WriteLine($"Puncak {shave.OriginalPeakMw} → {shave.ThresholdMw:0.0} MW");

var mg = MicrogridConfig.Demo();
var rules = MicrogridEms.RuleBased(mg);
var dp = MicrogridEms.Optimal(mg);                  // dynamic programming atas SoC baterai
Console.WriteLine($"Biaya: aturan {rules.TotalCost:0} vs DP {dp.TotalCost:0}");

var fleet = EvCharging.SyntheticFleet(count: 40);
double[] baseKw = Enumerable.Repeat(300.0, 24).ToArray();
var dumb = EvCharging.Uncontrolled(fleet, baseKw);
var smart = EvCharging.ValleyFilling(fleet, baseKw);
Console.WriteLine($"Puncak EV {dumb.PeakKw:0} → {smart.PeakKw:0} kW");

var vpp = new VirtualPowerPlant()
    .Add(new DerResource("PV", DerKind.Solar, 0, 5, MarginalCost: 0))
    .Add(new DerResource("CHP", DerKind.Chp, 0, 3, MarginalCost: 70));
var bids = vpp.BidCurve();
var (setpoints, cost, delivered) = vpp.Dispatch(targetMw: 6);
```

- `DemandResponse.PriceBased` (elastisitas sendiri/silang), `PeakShaving` (ambang tetap), dan `OptimalPeakShaving` (puncak terendah yang dapat
  dipertahankan).
- `EvCharging.ValleyFilling` menjadwalkan sesi ke lembah beban dasar sambil memenuhi kebutuhan energi.
- `MicrogridEms.Optimal` mengungguli strategi berbasis aturan pada microgrid demo (tes memastikan biaya lebih rendah dan keseimbangan daya).
- `VirtualPowerPlant`: amplop fleksibilitas per langkah, kurva penawaran merit order, dan dispatch ke target.

## Prakiraan

```csharp
var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
var history = SyntheticProfiles.Load(LoadProfileKind.Commercial, start, days: 14, TimeSpan.FromHours(1), peakMw: 100, noise: 0.02);
var dayAhead = LoadForecast.Forecast(history, horizon: 24, LoadForecastMethod.HoltWinters);

var hw = HoltWinters.FitOptimized(history.Values, seasonLength: 24, secondSeasonLength: 168);
var err = ForecastMetrics.Evaluate(history.Values.AsSpan(0, 24), dayAhead.Values);
Console.WriteLine($"MAPE {err.Mape:0.00} %");

var clouds = SyntheticProfiles.CloudCover(start, 2, TimeSpan.FromHours(1));
var pv = SolarForecast.Forecast(clouds, latitudeDeg: -6.2, longitudeDeg: 106.8, capacityMw: 10);
var wind = WindForecast.Forecast(SyntheticProfiles.WindSpeed(start, 2, TimeSpan.FromHours(1)), new WindPowerCurve(), turbines: 10);
```

(Baris MAPE di atas membandingkan dengan hari pertama riwayat hanya untuk menunjukkan API; dalam praktik gunakan hari hold-out.)

Metode: `HoltWinters` (aditif, musim kedua opsional, parameter dioptimasi), `RegressionForecaster` (fitur kalender dan suhu, ridge),
`SimilarDayForecaster` (k-NN), `EnsembleForecaster` (bobot inverse-error). `eln forecast` mengevaluasinya pada satu hari hold-out:

```
$ eln forecast
Day-ahead load forecast (HoltWinters, 14 days history): MAPE 1.62 %, RMSE 1.59 MW, bias -0.72 MW
$ eln forecast --method ens
Day-ahead load forecast (Ensemble, 14 days history): MAPE 1.74 %, RMSE 1.55 MW, bias -0.57 MW
```

## Batasan

Hosting capacity bersifat deterministik (satu bus, satu titik operasi); optimizer smart grid berupa heuristik yang dievaluasi dengan aliran
daya AC; model prakiraan bersifat statistik (model berbasis ML ada di peta jalan, v1.4).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/renewables-smartgrid.md)*
