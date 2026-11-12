# Memulai

Kebutuhan: .NET 10 SDK. Kernel Rust sudah dikemas di `AquaCulture.Native` untuk win-x64, linux-x64/arm64 dan osx-x64/arm64;
di platform lain fallback managed berjalan otomatis.

## Instalasi

```bash
dotnet add package AquaCulture.WaterQuality
dotnet add package AquaCulture.Growth
dotnet add package AquaCulture.Feeding
dotnet add package AquaCulture.AI
```

## 1. Periksa kualitas air

```csharp
var spesies = SpeciesCatalog.Get("vaname");                    // atau "Udang vaname"
var sampel = new WaterSample { TemperatureC = 30.5, DissolvedOxygen = 3.8, Ph = 8.4, Ammonia = 1.6, SalinityPpt = 18, Alkalinity = 95 };
var hasil = WaterQualityIndex.Assess(sampel, spesies);
Console.WriteLine($"WQI {hasil.Wqi} {WaterQualityAssessment.ClassName(hasil.Class).Id}, NH3 {hasil.UnionizedAmmonia} mg/L");
foreach (var masalah in hasil.Issues) Console.WriteLine(masalah.Id);

foreach (var t in RecommendationEngine.Recommend(hasil, new PondContext(VolumeM3: 2600, AreaM2: 2000, BiomassKg: 3000, AerationKw: 4.4)))
    Console.WriteLine($"{t.Priority}. {t.Action.Id} — {t.Rationale.Id}");
```

Semua teks untuk pengguna berupa `Text2` dengan `.En` dan `.Id`.

## 2. Pantau peringatan dari aliran sensor

```csharp
var peringatan = AlertEngine.ForSpecies(spesies);
foreach (var s in snapshotSensor)
    foreach (var a in peringatan.Evaluate(s)) Console.WriteLine($"{a.Severity}: {a.Message.Id}");
```

Aturan memakai histeresis sehingga sensor yang berisik di dekat ambang hanya memunculkan satu peringatan; aturan tren memperingatkan bila DO turun lebih dari 0,6 mg/L per jam.

## 3. Rencana pakan hari ini

```csharp
var rencana = FeedingCalculator.Plan(spesies, abwG: 12, population: 220_000, tempC: 30, dawnDo: 4.2, nh3: hasil.UnionizedAmmonia);
foreach (var m in rencana.Meals) Console.WriteLine($"{m.Time} {m.Kg} kg");
```

## 4. Prakiraan pertumbuhan dan panen

```csharp
var sim = new GrowthSimulator(spesies);
var awal = new StockState(spesies, DateOnly.FromDateTime(DateTime.Today), AbwG: 1, Count: 250_000, VolumeM3: 2600);
var lintasan = sim.Simulate(awal, 140, new DailyEnvironment(29, 5, 0.03, 18));
var terbaik = HarvestPredictor.Optimal(lintasan, 2000, new HarvestEconomics(17_500, 200_000));
var mc = HarvestForecaster.Forecast(spesies, DateOnly.FromDateTime(DateTime.Today), 1, 250_000, 20, Enumerable.Repeat(29.0, 160).ToArray());
Console.WriteLine($"Panen terbaik DOC {terbaik.DayOfCulture}; P50 {mc.DayP50} hari (P10–P90 {mc.DayP10}–{mc.DayP90})");
```

## 5. Dependency injection

```csharp
services.AddAquaCulture(b => b.AddWaterQuality().AddGrowth().AddFeeding().AddIoT().AddDigitalTwin());
```

## Selanjutnya

[Modul](modul.md) · [IoT dan kembaran digital](iot-kembaran.md) · [AI dan AquaAgent](agen-ai.md)
