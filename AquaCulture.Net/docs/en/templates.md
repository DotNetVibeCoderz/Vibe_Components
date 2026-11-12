# Templates

```bash
dotnet new install AquaCulture.Templates
```

| Short name | Project | Parameters |
|---|---|---|
| `aqua-console` | Pond advisor: water quality, feeding plan, harvest forecast and the AquaAgent | `--species` |
| `aqua-blazor` | Farm dashboard with live simulated sensors, `SensorGrid`, `FeedScheduler` and the 3D `DigitalTwinViewer` | `--species` |
| `aqua-iot-edge` | Worker service for a Raspberry Pi / industrial PC: MQTT (or simulator) → `FarmTwin` → aerator commands; configure in `appsettings.json` | `--species` |
| `aqua-avalonia` | Desktop pond monitor with WQI, DO chart and decisions | `--species` |

```bash
dotnet new aqua-iot-edge -n TambakGateway --species vaname
cd TambakGateway && dotnet run
```

All four templates are verified to build from the packed NuGet feed.
