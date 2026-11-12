# Data dan konektor (v1.1)

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/data-connectors.md)

Versi 1.1 menghubungkan AquaCulture.Net ke penyimpanan data, data cuaca dan laut publik, bus lapangan, serta raster satelit.

| Paket | Fitur baru |
|---|---|
| `AquaCulture.Data` | Penyimpanan EF Core (SQLite secara bawaan) untuk tambak, siklus beserta seluruh catatannya, pembacaan sensor, dan keputusan kembaran digital; `RecordingTransport`; ekspor CSV dan Apache Parquet |
| `AquaCulture.Environment` | `BmkgClient` (prakiraan cuaca per desa), `NasaPowerClient` (riwayat harian sejak 1981), `MarineForecastClient` (gelombang, arus, suhu permukaan laut) |
| `AquaCulture.IoT` | `ModbusRtuClient` lewat serial RS-485 (System.IO.Ports) atau stream apa pun; `ChirpStack.ParseUplink` untuk integrasi HTTP ChirpStack v4 |
| `AquaCulture.IoT.OpcUa` | `OpcUaReader` (stack OPC Foundation) untuk PLC/SCADA, serta `PondOpcUaSimulator` untuk uji tanpa perangkat keras |
| `AquaCulture.GIS.Gdal` | `GdalRaster`: baca/tulis GeoTIFF lewat GDAL.Net (otomatis diproyeksikan ke WGS-84); `WaterColour`: NDWI, MNDWI, klorofil-a dari NDCI, kekeruhan, dan status trofik |

## Penyimpanan

```csharp
using AquaCulture.Data;

using var store = AquaStore.OpenSqlite("tambak.db");         // skema dibuat otomatis
await store.SaveFarmAsync(farm);                              // tambak + kolam + siklus (sampling, pakan, kematian, panen, biaya)
var lagi = await store.LoadFarmAsync(farm.Id);

await store.AppendReadingsAsync(readings);
var doSubuh = await store.SeriesAsync("P-01", SensorKind.DissolvedOxygen, dari, sampai);  // TimeSeries → peramalan
await store.PurgeReadingsAsync(DateTime.UtcNow.AddDays(-365));                           // retensi

// rekam semua data dari transport langsung (ditulis per batch)
await using var rec = new RecordingTransport(new MqttSensorTransport(mqtt), store, batchSize: 200);
```

Untuk basis data lain (PostgreSQL, SQL Server), buat `DbContextOptions<AquaDbContext>` dengan provider EF Core yang diinginkan, lalu teruskan ke `new AquaStore(options)`.
Dengan DI: `services.AddAquaCulture(b => b.AddSqliteStore("tambak.db"))`.

### Ekspor deret waktu

```csharp
await using var fs = File.Create("p01.parquet");
await TimeSeriesExport.WriteParquetAsync(readings, fs);       // Snappy; kolom timestamp_utc, pond_id, device_id, kind, value, unit
File.WriteAllText("p01.csv", TimeSeriesExport.WriteCsv(readings));
```

Berkas Parquet bisa langsung dibuka di pandas (`pd.read_parquet`), DuckDB, Spark, dan Power BI. Penulisannya memakai ParquetSharp. Parquet.Net tidak dipakai
karena `Parquet.dll` miliknya bentrok di Windows dengan `parquet.dll` native milik GDAL.

## Cuaca, iklim, dan laut

| Klien | Sumber | Catatan |
|---|---|---|
| `BmkgClient.GetForecastAsync(adm4)` | `api.bmkg.go.id/publik/prakiraan-cuaca` | Per 3 jam untuk sekitar 3 hari, per desa (kode `adm4` Kemendagri, misalnya `31.71.03.1001`). Angin km/jam → m/s. Radiasi matahari diperkirakan dari tutupan awan (langit cerah × (1 − 0,75·awan)). `ToHourly` menginterpolasi data untuk `WeatherImpactPredictor`. |
| `NasaPowerClient.GetDailyAsync(lat, lon, mulai, akhir)` | NASA POWER (komunitas AG) | Suhu min/maks/rata-rata, hujan terkoreksi, angin 2 m, radiasi, RH, tekanan. Hasilnya diteruskan ke `ClimateRiskAnalysis.Analyze`. |
| `MarineForecastClient.GetHourlyAsync(lat, lon)` | Open-Meteo Marine | Tinggi dan periode gelombang, suhu permukaan laut, arus. `Assess(sea, species)` menandai gelombang > 1,5 m (berbahaya > 2,5 m), arus > 0,5 m/s, dan suhu di luar rentang optimum spesies untuk KJA. |

Endpoint maritim BMKG tidak terdokumentasi sebagai API publik yang stabil, jadi prakiraan laut memakai Open-Meteo Marine. Setiap klien punya metode statis `Parse`,
sehingga respons yang disimpan bisa diurai secara offline; tes dijalankan dengan fixture rekaman.

## Bus lapangan

```csharp
// probe RS-485 di COM3 (9600 8N1)
await using var probe = ModbusRtuClient.OpenSerial("COM3");
var r = await probe.ReadProbeAsync(unit: 7, "probe-7", "P-01", Modbus.TypicalProbeMap);

// integrasi HTTP ChirpStack v4 (minimal API)
app.MapPost("/chirpstack", async (HttpRequest req, string @event) =>
{
    if (!ChirpStack.IsUplink(@event)) return Results.Ok();
    var readings = ChirpStack.ParseUplink(await new StreamReader(req.Body).ReadToEndAsync());   // memakai deviceInfo.tags.pond
    foreach (var x in readings) twin.Ingest(x);
    return Results.Ok();
});

// OPC UA (PLC / SCADA)
await using var opc = new OpcUaReader(new OpcUaReaderOptions("opc.tcp://plc:4840") { UserName = "aqua", Password = pwd });
await opc.ConnectAsync();
var poller = new OpcUaSensorPoller(opc, nodeMap);
var nilai = await poller.PollAsync();
await opc.WriteAsync("ns=2;s=Ponds/P-01/Aerator", true);
```

`PondOpcUaSimulator` memublikasikan `Ponds/<id>/{Temperature, DissolvedOxygen, Ph, Salinity, Ammonia, Aerator}` dengan dinamika harian. Node `Aerator`
bisa ditulis dan menaikkan DO. Coba jalankan `aqua opcua`. Soal keamanan: secara bawaan reader menerima sertifikat server yang belum dipercaya (`AcceptUntrustedCertificates`)
dan tidak memakai keamanan pesan kecuali `UseSecurity = true`. Aktifkan keduanya di jaringan produksi.

Adaptor IoTComNet ditunda sampai IoTCom.Net terbit di NuGet.

## Warna air dari satelit (GDAL)

```csharp
using AquaCulture.GIS.Gdal;

var b = GdalRaster.ReadAllBands("S2_L2A_waduk.tif");         // reflektansi 0–1; tile UTM diproyeksikan ke WGS-84
GeoRaster green = b[2], red = b[3], redEdge = b[4], nir = b[7];
var air = WaterColour.WaterMask(WaterColour.Ndwi(green, nir));
var chl = WaterColour.Chlorophyll(WaterColour.Ndci(redEdge, red), air);   // µg/L
var s = WaterColour.Summarize(chl, air);
Console.WriteLine($"{s.AreaHa:0} ha, median Chl-a {s.Median:0} µg/L: {WaterColour.TrophicState(s.Median).Id}");
GdalRaster.WriteGeoTiff("chl.tif", chl);
```

- Konversi NDCI → klorofil-a memakai Mishra & Mishra (2012): Chl = 14,039 + 86,115·NDCI + 194,325·NDCI². Kekeruhan memakai Dogliotti dkk. (2015) pada 665 nm.
  Keduanya kalibrasi global, jadi kalibrasi dulu dengan sampel lokal sebelum nilainya dipakai untuk perizinan.
- `GdalRaster.Read` menangani semua format raster GDAL, NoData (→ NaN), serta skala/offset band.

## CLI

```
aqua bmkg --adm4 31.71.03.1001 --lang id
aqua history --lat -6.33 --lon 108.32 --start 2015-01-01 --end 2024-12-31 --lang id
aqua marine --lat -5.85 --lon 106.6 --species kerapu
aqua export --db aqua.db --demo --out readings.parquet
aqua opcua --seconds 30
```
