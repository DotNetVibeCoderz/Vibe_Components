# Renewables and smart grid

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/energi-terbarukan-smartgrid.md)

Packages: `Electrical.Renewable`, `Electrical.SmartGrid` and `Electrical.Forecasting`. Time-series studies (QSTS) that combine them with power
flow are in [Simulation, HPC & native](simulation-hpc-native.md).

## Solar PV

```csharp
var module = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT, idealityFactor: 1.3);
Console.WriteLine($"Rs = {module.Rs:0.000} Ω, Rp = {module.Rp:0.0} Ω");        // 0.221 Ω, 416.3 Ω
var mpp = module.MaximumPowerPoint(irradiance: 1000, cellTempC: 25);           // 200.15 W at 26.35 V
var iv = module.IvCurve(600, 45, points: 60);

var sun = SolarPosition.Compute(new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.FromHours(7)), -6.2, 106.8);
var clear = Irradiance.IneichenClearSky(sun.ZenithDeg, dayOfYear: 79);
var poa = Irradiance.PlaneOfArray(clear, sun, tiltDeg: 10, surfaceAzimuthDeg: 0);
double tCell = CellTemperature.Noct(ambientC: 31, poaWm2: poa.Global);
double pdc = PvWatts.DcPower(poa.Global, tCell, pdc0: 5000);                  // W for a 5 kWp array
double pac = PvWatts.InverterAc(pdc, pac0: 4600);
```

- `SolarPosition.Compute` (SPA-grade; matches the NREL SPA example within 0.05°) and `Spencer` (fast approximation).
- `Irradiance`: extraterrestrial normal, air mass, Haurwitz and Ineichen clear-sky, Erbs decomposition, angle of incidence, plane of array
  (isotropic sky + ground reflection).
- `CellTemperature.Noct` and `Faiman`.
- `SingleDiodeModel`: five-parameter extraction from a datasheet (Villalva 2009), I–V curves, MPP, temperature and irradiance dependence.
- `PvWatts` (v5 DC model, inverter curve, `AcPower` end to end), `InverterEfficiencyCurve`, `SandiaInverter`, `Mppt.PerturbAndObserve`.

## Wind

```csharp
var turbine = WindTurbine.Generic2MW;                                        // 2 MW, cut-in 3, rated 12, cut-out 25 m/s
var site = WeibullDistribution.Rayleigh(meanMs: 7.0);
var aep = WindEnergy.AnnualEnergyProduction(turbine, site, airDensity: AirDensity.AtAltitude(500), availability: 0.97, losses: 0.08);
Console.WriteLine($"AEP {aep.AepMwh:0} MWh, CF {aep.CapacityFactor:P1}");
double vHub = WindShear.PowerLaw(vRef: 6.2, hRefM: 10, hM: turbine.HubHeightM);
```

With standard air density, no losses and a Rayleigh 7 m/s site the generic 2 MW turbine yields 5127 MWh (capacity factor 29.3 %).
`WindTurbine.FromTable` takes a manufacturer power curve; `WeibullDistribution.Fit` fits k and c to measured speeds; `WindEnergy.EnergyFromSeries`
integrates a speed series.

## Battery storage

```csharp
var bess = BatteryStorage.FromCRate(capacityMwh: 10, cRate: 0.5, initialSoc: 0.5);   // 10 MWh / 5 MW
double delivered = bess.Step(requestedMw: 4, hours: 1);    // + discharge, − charge; returns the MW actually delivered
Console.WriteLine($"SoC {bess.Soc:P0}, SoH {bess.StateOfHealth:P2}");
```

`BatteryStorage` enforces power limits, SoC window and charge/discharge efficiencies and tracks equivalent full cycles and half-cycle
damage (Miner's rule with Wöhler cycle life N(DoD) = N₁₀₀·DoD^−k) for state of health.

## Grid codes and hosting capacity

- `VoltVarCurve.Ieee1547CategoryA/B`, `VoltWattCurve.Ieee1547`, `FrequencyDroop.Ieee1547` (IEEE 1547-2018 defaults).
- `HostingCapacity.Analyze` adds PV at a bus in steps and finds the largest injection that keeps voltages ≤ `vmaxPu` and branch loading
  ≤ `loadingLimitPercent`.

```csharp
var hc = HostingCapacity.Analyze(TestCases.Case33BW(), busId: 18, stepMw: 0.25, maxMw: 6);
Console.WriteLine($"{hc.HostingCapacityMw:0.00} MW, limited by {hc.LimitingFactor}");   // 2.09 MW, limited by Overvoltage
```

## Smart grid

### Distribution automation

```csharp
var recon = FeederReconfiguration.Optimize(FeederReconfiguration.Case33WithTies());
Console.WriteLine($"Open {string.Join(", ", recon.OpenBranches)}: {recon.LossesBeforeMw * 1000:0.0} → {recon.LossesAfterMw * 1000:0.0} kW");
// Open 14-15, 25-29, 32-33, 7-8, 9-10: 202.7 → 139.6 kW

var vvo = VoltVarOptimization.Optimize(TestCases.Case33BW(), VoltVarControls.Case33Default());
Console.WriteLine($"VVO losses {vvo.LossesAfterMw * 1000:0.0} kW, V [{vvo.VminAfter:0.000}, {vvo.VmaxAfter:0.000}] pu");
```

- Reconfiguration: sequential switch opening (Shirmohammadi & Hong 1989) plus branch exchange (Civanlar et al. 1988). On the Baran–Wu
  feeder with its five tie switches it opens the known loss-minimal set (7-8, 9-10, 14-15, 32-33, 25-29): 202.7 → 139.6 kW, V<sub>min</sub>
  0.9131 → 0.9378 pu.
- Volt/VAR optimization: discrete coordinate descent over capacitor steps, substation set-point and inverter reactive power. With
  `Case33Default` controls: 202.7 → 118.6 kW and all voltages within [0.95, 1.05] pu.

### Reliability indices

```csharp
var net = TestCases.Case33BW();
var idx = RadialReliability.Evaluate(net, RadialReliability.Uniform(net, failureRatePerYear: 0.1, repairHours: 4));
Console.WriteLine($"SAIFI {idx.Saifi:0.00}, SAIDI {idx.Saidi:0.00} h, ENS {idx.EnsMwh:0.0} MWh/yr");
```

Load-point λ, U and r and the SAIFI/SAIDI/CAIDI/ASAI/ENS indices reproduce Billinton & Allan §7.2 (Table 7.1: λ<sub>C</sub> 0.45 f/yr,
U<sub>C</sub> 2.9 h). Fuses (`ProtectedByFuse`), customer counts and sectionalizing time are supported.

### Demand response, EVs, microgrids and VPPs

```csharp
double[] load = [52, 50, 49, 50, 55, 63, 75, 84, 88, 90, 92, 91, 90, 89, 90, 93, 97, 100, 98, 92, 82, 72, 63, 56];
var shave = DemandResponse.OptimalPeakShaving(load, BatteryStorage.FromCRate(20, 0.5));
Console.WriteLine($"Peak {shave.OriginalPeakMw} → {shave.ThresholdMw:0.0} MW");

var mg = MicrogridConfig.Demo();
var rules = MicrogridEms.RuleBased(mg);
var dp = MicrogridEms.Optimal(mg);                  // dynamic programming over battery SoC
Console.WriteLine($"Cost: rules {rules.TotalCost:0} vs DP {dp.TotalCost:0}");

var fleet = EvCharging.SyntheticFleet(count: 40);
double[] baseKw = Enumerable.Repeat(300.0, 24).ToArray();
var dumb = EvCharging.Uncontrolled(fleet, baseKw);
var smart = EvCharging.ValleyFilling(fleet, baseKw);
Console.WriteLine($"EV peak {dumb.PeakKw:0} → {smart.PeakKw:0} kW");

var vpp = new VirtualPowerPlant()
    .Add(new DerResource("PV", DerKind.Solar, 0, 5, MarginalCost: 0))
    .Add(new DerResource("CHP", DerKind.Chp, 0, 3, MarginalCost: 70));
var bids = vpp.BidCurve();
var (setpoints, cost, delivered) = vpp.Dispatch(targetMw: 6);
```

- `DemandResponse.PriceBased` (self/cross elasticity), `PeakShaving` (fixed threshold) and `OptimalPeakShaving` (lowest holdable peak).
- `EvCharging.ValleyFilling` schedules sessions into the valleys of the base load while meeting energy needs.
- `MicrogridEms.Optimal` beats the rule-based strategy on the demo microgrid (tests assert lower cost and power balance).
- `VirtualPowerPlant`: flexibility envelope per step, merit-order bid curve and dispatch to a target.

## Forecasting

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

(The MAPE line above compares against the first history day only to show the API; use a hold-out day in practice.)

Methods: `HoltWinters` (additive, optional second season, parameters optimised), `RegressionForecaster` (calendar and temperature features,
ridge), `SimilarDayForecaster` (k-NN), `EnsembleForecaster` (inverse-error weights). `eln forecast` evaluates them on a held-out day:

```
$ eln forecast
Day-ahead load forecast (HoltWinters, 14 days history): MAPE 1.62 %, RMSE 1.59 MW, bias -0.72 MW
$ eln forecast --method ens
Day-ahead load forecast (Ensemble, 14 days history): MAPE 1.74 %, RMSE 1.55 MW, bias -0.57 MW
```

## Limitations

Hosting capacity is deterministic (one bus, one operating point); smart-grid optimizers are heuristics evaluated with AC power flows;
forecasting models are statistical (ML-based models are on the roadmap, v1.4).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/energi-terbarukan-smartgrid.md)*
