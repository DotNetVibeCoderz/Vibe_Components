# Memulai

## Kebutuhan

- .NET 10 SDK
- Opsional: Rust 1.80+ untuk membangun ulang kernel native (biner `win-x64` sudah disertakan; platform lain otomatis memakai SIMD terkelola)

## Instalasi

Pasang hanya paket yang dibutuhkan:

```bash
dotnet add package Agritech.AI              # Core + Crops + Soil + Weather + agen
dotnet add package Agritech.RemoteSensing   # Core + Native
dotnet add package Agritech.DigitalTwin
dotnet add package Agritech.Visualization.Blazor
```

## Program pertama

```csharp
var lokasi = new GeoCoordinate(-6.3045, 107.3012);   // Karawang
var tanah = new SoilSample { Ph = 5.4, OrganicCarbonPct = 1.2, AvailablePMgKg = 9, SandPct = 22, SiltPct = 38, ClayPct = 40 };

// Tanaman apa yang cocok?
var terbaik = new CropRecommender().Recommend(new SiteConditions(MeanTempC: 27, SeasonalRainfallMm: 1600, SoilPh: tanah.Ph), top: 3);

// Kesehatan tanah dan jadwal pemupukan
var kesehatan = SoilHealth.Assess(tanah);
var rencana = FertilizerAdvisor.Advise(CropCatalog.Default.Get("padi"), tanah, areaHa: 2, targetYieldTonHa: 6);

// Cuaca (langsung, gratis, tanpa kunci) dan evapotranspirasi acuan
var prakiraan = await new OpenMeteoWeatherProvider().GetForecastAsync(lokasi, days: 7);
var et0 = Evapotranspiration.Compute(prakiraan, new WeatherStation(lokasi.Latitude, 20));
```

Katalog tanaman menerima nama Indonesia: `padi`, `jagung`, `kedelai`, `singkong`, `kentang`, `tomat`, `cabai`, `bawang merah`, `tebu`, `kelapa sawit`, `kopi robusta`, `kakao`, `kacang tanah`, `kacang hijau`, `pisang`.

## Dependency injection

```csharp
builder.Services.AddAgritech(b => b
    .AddCrops().AddSoil().AddWeather()
    .AddRemoteSensing()
    .AddIoT(sp => new MqttSensorTransport(new MqttOptions("broker.lokal")))
    .AddAI()
    .AddDigitalTwin(kebun));
```

## Data luring yang deterministik

Semua contoh dan pengujian dapat berjalan tanpa internet berkat `SyntheticWeatherProvider` (generator cuaca, hasilnya identik antarproses),
`SyntheticScene` (citra multispektral), `RgbImage.SyntheticRowCrop` (foto drone), serta dataset sintetis untuk hasil panen, pemuliaan, dan harga.

## Satuan dan konvensi

- Koordinat WGS-84 derajat desimal; luas dalam m² dan ha.
- Air dalam mm; hara dalam kg/ha sebagai N, P₂O₅, K₂O; hasil dalam t/ha; suhu dalam °C.
- Raster `float` reflektansi 0–1, `NaN` = tanpa data.
- DAP (*days after planting*) = HST (hari setelah tanam).

Berikutnya: [panduan modul](modul.md).
