# Data and connectors (v1.1)

*Made by Gravicode Studios, led by Kang Fadhil.* · [Bahasa Indonesia](../id/data-konektor.md)

Version 1.1 connects AquaCulture.Net to storage, public weather and ocean data, field buses and satellite rasters.

| Package | What it adds |
|---|---|
| `AquaCulture.Data` | EF Core store (SQLite by default) for farms, cycles with every log, sensor readings and twin decisions; `RecordingTransport`; CSV and Apache Parquet export |
| `AquaCulture.Environment` | `BmkgClient` (Indonesian village forecasts), `NasaPowerClient` (daily history since 1981), `MarineForecastClient` (waves, currents, SST) |
| `AquaCulture.IoT` | `ModbusRtuClient` over serial RS-485 (System.IO.Ports) or any stream; `ChirpStack.ParseUplink` for ChirpStack v4 HTTP integrations |
| `AquaCulture.IoT.OpcUa` | `OpcUaReader` (OPC Foundation stack) for PLC/SCADA, plus `PondOpcUaSimulator` for testing without hardware |
| `AquaCulture.GIS.Gdal` | `GdalRaster` GeoTIFF read/write through GDAL.Net (warps to WGS-84); `WaterColour` NDWI, MNDWI, NDCI chlorophyll-a, turbidity and trophic state |

## Persistence

```csharp
using AquaCulture.Data;

using var store = AquaStore.OpenSqlite("farm.db");          // creates the schema on first use
await store.SaveFarmAsync(farm);                              // farm + ponds + cycles (samplings, feed, mortality, harvests, expenses)
var again = await store.LoadFarmAsync(farm.Id);

await store.AppendReadingsAsync(readings);
var dawnDo = await store.SeriesAsync("P-01", SensorKind.DissolvedOxygen, from, to);   // TimeSeries → forecasting
await store.PurgeReadingsAsync(DateTime.UtcNow.AddDays(-365));                        // retention

// record everything a live transport delivers (batched writes)
await using var rec = new RecordingTransport(new MqttSensorTransport(mqtt), store, batchSize: 200);
```

Other databases: build `DbContextOptions<AquaDbContext>` with any EF Core provider (PostgreSQL, SQL Server) and pass it to `new AquaStore(options)`.
With DI: `services.AddAquaCulture(b => b.AddSqliteStore("farm.db"))`.

### Exporting time series

```csharp
await using var fs = File.Create("p01.parquet");
await TimeSeriesExport.WriteParquetAsync(readings, fs);       // Snappy, columns timestamp_utc, pond_id, device_id, kind, value, unit
File.WriteAllText("p01.csv", TimeSeriesExport.WriteCsv(readings));
```

The Parquet file opens directly in pandas (`pd.read_parquet`), DuckDB, Spark and Power BI. Parquet is written with ParquetSharp. Parquet.Net is not used
because its `Parquet.dll` collides on Windows with GDAL's native `parquet.dll`.

## Weather, climate and ocean

| Client | Source | Notes |
|---|---|---|
| `BmkgClient.GetForecastAsync(adm4)` | `api.bmkg.go.id/publik/prakiraan-cuaca` | 3-hourly, about 3 days, per village (Kemendagri `adm4` code such as `31.71.03.1001`). Wind km/h → m/s. Solar radiation is estimated from cloud cover (clear-sky × (1 − 0.75·cloud)). `ToHourly` interpolates for `WeatherImpactPredictor`. |
| `NasaPowerClient.GetDailyAsync(lat, lon, start, end)` | NASA POWER (AG community) | Tmin/Tmax/Tmean, corrected rain, 2 m wind, solar, RH, pressure. Feed it to `ClimateRiskAnalysis.Analyze`. |
| `MarineForecastClient.GetHourlyAsync(lat, lon)` | Open-Meteo Marine | Wave height and period, SST, current. `Assess(sea, species)` flags waves > 1.5 m (severe > 2.5 m), currents > 0.5 m/s and SST outside the species optimum for floating net cages (KJA). |

BMKG's maritime endpoints are not documented as a stable public API, so marine forecasts use Open-Meteo Marine instead. Every client has a static `Parse`
method, so you can parse cached responses offline; the tests run on recorded fixtures.

## Field buses

```csharp
// RS-485 probe on COM3 (9600 8N1)
await using var probe = ModbusRtuClient.OpenSerial("COM3");
var r = await probe.ReadProbeAsync(unit: 7, "probe-7", "P-01", Modbus.TypicalProbeMap);

// ChirpStack v4 HTTP integration (minimal API)
app.MapPost("/chirpstack", async (HttpRequest req, string @event) =>
{
    if (!ChirpStack.IsUplink(@event)) return Results.Ok();
    var readings = ChirpStack.ParseUplink(await new StreamReader(req.Body).ReadToEndAsync());   // uses deviceInfo.tags.pond
    foreach (var x in readings) twin.Ingest(x);
    return Results.Ok();
});

// OPC UA (PLC / SCADA)
await using var opc = new OpcUaReader(new OpcUaReaderOptions("opc.tcp://plc:4840") { UserName = "aqua", Password = pwd });
await opc.ConnectAsync();
var poller = new OpcUaSensorPoller(opc, nodeMap);
var values = await poller.PollAsync();
await opc.WriteAsync("ns=2;s=Ponds/P-01/Aerator", true);
```

`PondOpcUaSimulator` publishes `Ponds/<id>/{Temperature, DissolvedOxygen, Ph, Salinity, Ammonia, Aerator}` with diurnal dynamics; `Aerator` is
writable and raises DO. Try `aqua opcua`. Security: the reader accepts untrusted server certificates by default (`AcceptUntrustedCertificates`) and uses
no message security unless you set `UseSecurity = true`. Turn on both for production networks.

The IoTComNet adapters are deferred until IoTCom.Net is published on NuGet.

## Satellite water colour (GDAL)

```csharp
using AquaCulture.GIS.Gdal;

var b = GdalRaster.ReadAllBands("S2_L2A_reservoir.tif");     // reflectance 0–1; UTM tiles are warped to WGS-84
GeoRaster green = b[2], red = b[3], redEdge = b[4], nir = b[7];
var water = WaterColour.WaterMask(WaterColour.Ndwi(green, nir));
var chl = WaterColour.Chlorophyll(WaterColour.Ndci(redEdge, red), water);   // µg/L
var s = WaterColour.Summarize(chl, water);
Console.WriteLine($"{s.AreaHa:0} ha, median Chl-a {s.Median:0} µg/L: {WaterColour.TrophicState(s.Median).En}");
GdalRaster.WriteGeoTiff("chl.tif", chl);
```

- NDCI → chlorophyll-a uses Mishra & Mishra (2012): Chl = 14.039 + 86.115·NDCI + 194.325·NDCI². Turbidity uses Dogliotti et al. (2015) at 665 nm.
  Both are global calibrations, so calibrate against local samples before using the values for permits.
- `GdalRaster.Read` handles any GDAL raster format, NoData (→ NaN) and band scale/offset.

## CLI

```
aqua bmkg --adm4 31.71.03.1001
aqua history --lat -6.33 --lon 108.32 --start 2015-01-01 --end 2024-12-31
aqua marine --lat -5.85 --lon 106.6 --species kerapu
aqua export --db aqua.db --demo --out readings.parquet
aqua opcua --seconds 30
```
