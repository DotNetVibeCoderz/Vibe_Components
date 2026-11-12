# Gallery

```bash
dotnet run --project gallery/AquaCulture.Gallery.Web --urls http://localhost:5390     # Blazor (light/dark, EN/ID)
dotnet run --project gallery/AquaCulture.Gallery.Desktop                              # Avalonia + ThreeNet 3D
```

All computation lives in `gallery/AquaCulture.Gallery.Shared` (`GalleryCatalog`, `DemoFarm`, `*Demo.Run(...)`), runs on a deterministic demo farm —
*Tambak Mina Sejahtera*, six lined whiteleg-shrimp ponds in Indramayu, West Java — and is covered by smoke tests.

| # | Use case | Modules |
|---|---|---|
| 1 | Water quality, alerts & actions | WaterQuality |
| 2 | Night oxygen forecast & aeration | Hydrodynamic, Forecasting |
| 3 | Pond circulation & oxygen field | Hydrodynamic, Native |
| 4 | AquaGrowth: growth & harvest forecast | Growth, Forecasting |
| 5 | Feeding plan, FCR & least-cost feed | Feeding |
| 6 | AI models & RL feed policy | AI |
| 7 | Disease diagnosis & risk | Disease |
| 8 | Computer vision: count, size & screen | ComputerVision |
| 9 | 3D digital twin of the farm | DigitalTwin, IoT, Blazor |
| 10 | IoT: MQTT, LoRaWAN, Modbus & edge | IoT |
| 11 | Farm GIS, sensors & drainage | GIS |
| 12 | Weather impact & climate risk | Environment |
| 13 | Stock assessment: MSY & Kobe | StockAssessment |
| 14 | Population dynamics | Population |
| 15 | KPIs & cycle economics | Analytics |
| 16 | Reports & GAP / CBIB / ASC / BAP | Reporting |
| 17 | AquaAgent | AI.Agent |

## Screenshots

Web (`pwsh build/screenshots-web.ps1` with the web gallery running) and desktop
(`dotnet run --project gallery/AquaCulture.Gallery.Desktop -- --screenshot docs/images en`, which renders the 3D pond with ThreeNet offscreen).

| | |
|---|---|
| ![](../images/web-home.png) | ![](../images/web-home-dark.png) |
| ![](../images/web-water.png) | ![](../images/web-oxygen.png) |
| ![](../images/web-hydro.png) | ![](../images/web-growth.png) |
| ![](../images/web-feeding.png) | ![](../images/web-ai.png) |
| ![](../images/web-disease.png) | ![](../images/web-vision.png) |
| ![](../images/web-twin.png) | ![](../images/web-iot.png) |
| ![](../images/web-gis.png) | ![](../images/web-weather.png) |
| ![](../images/web-stock.png) | ![](../images/web-population.png) |
| ![](../images/web-analytics.png) | ![](../images/web-reporting.png) |
| ![](../images/web-agent.png) | ![](../images/desktop-twin.png) |
| ![](../images/desktop-water.png) | ![](../images/desktop-growth.png) |
