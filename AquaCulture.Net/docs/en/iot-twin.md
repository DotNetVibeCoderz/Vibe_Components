# IoT and digital twin

## Devices and readings

`IoTDevice` (ESP32, ESP8266, Arduino, Raspberry Pi, industrial PLC, gateway) and `SensorReading(DeviceId, PondId, Kind, Value, Timestamp)`.
`SensorKind` covers the 15 water parameters plus water level, weather, aerator current, feeder hopper, battery and RSSI.
`DeviceRegistry` tracks last-seen times and battery.

## Transports (`ISensorTransport`)

| Transport | Notes |
|---|---|
| `MqttSensorTransport` | MQTTnet 5. Telemetry `aquaculture/{farm}/{pond}/{device}/telemetry` with JSON `{"temp":29.1,"do":5.2,"ph":7.8}`; commands to `aquaculture/{farm}/cmd/{device}` |
| `RestSensorTransport` | Polls a URL returning one telemetry object or an array; POSTs commands |
| `SimulatedSensorTransport` | Deterministic diurnal DO, temperature, pH, TAN, ORP, turbidity; reacts to `aerator_on` |
| `ModbusTcpClient` + `Modbus` | RTU/TCP framing, CRC-16, register maps (`ModbusRegisterMap`, `Modbus.TypicalProbeMap`) |
| `LoRaPayload` | Cayenne LPP, compact 12-byte pond codec, The Things Stack webhook parser |
| `OpcUaSensorPoller` | Maps node ids to sensors through your `IOpcUaReader` (e.g. the OPC Foundation stack) |

ESP32 sketch (Arduino, PubSubClient): see `samples/firmware/esp32-pond-node.ino`.

## Edge analytics

`EdgeAnalytics.Process` flags outliers with a Hampel filter (median ± 3·MAD, at least 5 % of the level), detects stuck sensors, applies two-point calibration
and keeps an EMA. All values enter the window, so a genuine level shift is accepted after a few samples.

## Digital twin

```csharp
await using var twin = new FarmTwin(farm, new MqttSensorTransport(new MqttOptions("broker.local", FarmId: "farm1"))) { AutoActuate = true };
twin["K-01"].AeratorDeviceId = "aerator-k01";
twin.WeatherForecast = await new OpenMeteoClient().GetHourlyForecastAsync(lat, lon, 3);
await twin.StartAsync();

var decisions = await twin.EvaluateAsync(DateTime.Now);   // explained; aerator_on commands are sent automatically
var states = twin.States(DateTime.Now);                   // ABW, biomass, WQI, dawn-DO forecast, feed, disease risk, alerts
```

`PondTwin` keeps per-parameter history, runs the alert engine on every reading, advances a growth soft-sensor between samplings, forecasts dawn DO
assuming installed aerators run at the species' DO optimum, and evaluates: water-quality actions, overnight DO, next meal (with feeder command),
disease risk, weather impact and harvest readiness. `WhatIf` compares scenarios such as an aerator failure.

In Blazor, `<DigitalTwinViewer State="@state" Aerator="true" BiomassRatio="0.5" />` renders the 3D pond with Three.js; the desktop gallery renders the
same pond with ThreeNet.
