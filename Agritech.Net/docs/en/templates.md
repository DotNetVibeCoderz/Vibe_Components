# Project templates

```bash
dotnet new install Agritech.Templates
```

| Template | Command | What you get |
|---|---|---|
| Farm advisor (console) | `dotnet new agritech-console -n MyAdvisor --crop maize` | Soil health, fertilizer schedule, irrigation and the AI agent in one `Program.cs` |
| Farm dashboard (Blazor) | `dotnet new agritech-blazor -n MyDashboard` | Blazor Server page with NDVI map, LAI and soil-water charts and KPI cards |
| IoT edge gateway | `dotnet new agritech-iot-edge -n MyGateway --broker mqtt.local` | Worker service: MQTT or simulated sensors → edge analytics → digital twin → logged decisions |
| Field viewer (Avalonia) | `dotnet new agritech-avalonia -n MyViewer` | Cross-platform desktop NDVI viewer with zonal stats |

The IoT gateway reads `Simulate`, `IntervalSeconds` and `Mqtt:Host` from `appsettings.json`. Set `"Simulate": false` to use a real broker.
