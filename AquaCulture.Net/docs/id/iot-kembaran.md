# IoT dan kembaran digital

## Perangkat dan pembacaan

`IoTDevice` (ESP32, ESP8266, Arduino, Raspberry Pi, PLC industri, gateway) dan `SensorReading(DeviceId, PondId, Kind, Value, Timestamp)`.
`SensorKind` mencakup 15 parameter air serta tinggi air, cuaca, arus kincir, sisa pakan di hopper, baterai dan RSSI.

## Transport

| Transport | Catatan |
|---|---|
| `MqttSensorTransport` | Topik `aquaculture/{farm}/{pond}/{device}/telemetry`, JSON `{"temp":29.1,"do":5.2,"ph":7.8}`; perintah ke `aquaculture/{farm}/cmd/{device}` |
| `RestSensorTransport` | Polling URL JSON; perintah via POST |
| `SimulatedSensorTransport` | Data sensor sintetis harian yang deterministik, bereaksi pada `aerator_on` |
| `Modbus` / `ModbusTcpClient` | Frame RTU/TCP, CRC-16, peta register probe multi-parameter |
| `LoRaPayload` | Cayenne LPP, codec kolam 12 byte, parser webhook The Things Stack |
| `OpcUaSensorPoller` | Pemetaan node OPC-UA melalui `IOpcUaReader` Anda |

Contoh firmware ESP32: `samples/firmware/esp32-pond-node.ino`.

## Analitik edge

`EdgeAnalytics.Process`: filter Hampel (median ± 3·MAD, minimal 5 % dari level), deteksi sensor macet, kalibrasi dua titik dan EMA.

## Kembaran digital

```csharp
await using var kembaran = new FarmTwin(tambak, new MqttSensorTransport(new MqttOptions("broker.lokal", FarmId: "tambak1"))) { AutoActuate = true };
kembaran["K-01"].AeratorDeviceId = "kincir-k01";
await kembaran.StartAsync();
var keputusan = await kembaran.EvaluateAsync(DateTime.Now);
```

`PondTwin` menyimpan riwayat per parameter, menjalankan mesin peringatan, memperbarui soft-sensor pertumbuhan, memprakirakan DO subuh dengan asumsi kincir
terpasang menyala pada DO optimum spesies, lalu mengevaluasi tindakan kualitas air, DO malam, jadwal pakan berikutnya, risiko penyakit, dampak cuaca dan
kesiapan panen. Di Blazor, `<DigitalTwinViewer />` menampilkan kolam 3D dengan Three.js; di galeri desktop kolam yang sama dirender dengan ThreeNet.
