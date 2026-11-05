# Vibe_Components

Collection of Vibed Components: open-source .NET 10 frameworks and UI components by **Gravicode Studios**, led by **Kang Fadhil**.
Each component is a self-contained folder with its own solution, docs (English and Bahasa Indonesia), gallery apps and CI workflow.

*Kumpulan komponen .NET 10 open-source dari Gravicode Studios dipimpin oleh Kang Fadhil. Setiap komponen memiliki solusi, dokumentasi (Inggris & Indonesia), aplikasi galeri, dan workflow CI sendiri.*

## Components

| Component | Description | NuGet | CI |
|---|---|---|---|
| [**Agritech.Net**](Agritech.Net) | The ultimate open-source agriculture technology framework for .NET: crops, soil, weather, GIS, remote sensing (NDVI), drones, IoT/LoRaWAN, AI farm agent, crop simulation, genomics, livestock, fisheries, robotics, supply chain, marketplace and farm digital twin. Rust kernels, Blazor and Avalonia gallery, CLI and templates. | [`Agritech.*`](https://www.nuget.org/packages?q=Agritech) · 1.0.0 | [![Agritech.Net](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/agritech-net.yml/badge.svg)](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/agritech-net.yml) |
| [**Blazor.AI**](Blazor.AI) | AI components for Blazor: smart forms, copilots, cited document chat, agents, decision engines, RAG, local models, MediaPipe and 3D. | [`Blazor.AI.*`](https://www.nuget.org/packages?q=Blazor.AI) · 1.2.0 | [![Blazor.AI](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/blazor-ai.yml/badge.svg)](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/blazor-ai.yml) |
| [**Cesium.Net**](Cesium.Net) | CesiumJS for .NET: 3D globes, maps, terrain, 3D Tiles, time-dynamic data and geospatial analysis from C#, for Blazor and Avalonia. | [`Cesium.Net*`](https://www.nuget.org/packages?q=Cesium.Net) · 1.0.0 | [![Cesium.Net](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/cesium-net.yml/badge.svg)](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/cesium-net.yml) |
| [**GDAL.Net**](GDAL.Net) | GDAL for modern .NET: read, write, convert, reproject and analyse 200+ raster and vector formats, with Rust kernels and Blazor/Avalonia components. | [`Gravicode.GDAL.Net*`](https://www.nuget.org/packages?q=Gravicode.GDAL.Net) · 1.0.0 | [![GDAL.Net](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/gdal-net.yml/badge.svg)](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/gdal-net.yml) |
| [**GnuRadio.Net**](GnuRadio.Net) | Software-defined radio and DSP for .NET modelled on GNU Radio: flowgraphs, filters, FFT, modulation, FEC, SDR hardware and live instruments. | [`GnuRadio.Net*`](https://www.nuget.org/packages?q=GnuRadio.Net) · 0.1.0 | [![GnuRadio.Net](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/gnuradio-net.yml/badge.svg)](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/gnuradio-net.yml) |
| [**OpenSceneGraph.Net**](OpenSceneGraph.Net) | .NET scene graph for OpenSceneGraph: managed core, native OSG 3.6.5 runtime, OpenGL/WebGL renderers, Avalonia and Blazor components. | [`OpenSceneGraph.Net*`](https://www.nuget.org/packages?q=OpenSceneGraph.Net) · 0.1.0 | [![OpenSceneGraph.Net](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/openscenegraph-net.yml/badge.svg)](https://github.com/DotNetVibeCoderz/Vibe_Components/actions/workflows/openscenegraph-net.yml) |

## Agritech.Net

<p align="center"><img src="Agritech.Net/assets/icon-512.png" width="96" alt="Agritech.Net logo" /></p>

![Agritech.Net Gallery](Agritech.Net/docs/images/web-home.png)

```bash
dotnet add package Agritech.AI                 # crops, soil, weather and the AI farm agent
dotnet new install Agritech.Templates          # agritech-console / -blazor / -iot-edge / -avalonia
dotnet tool install -g Agritech.Cli            # agritech ask "Berapa dosis pupuk terbaik minggu ini?"
```

See the [Agritech.Net README](Agritech.Net/README.md) ([Bahasa Indonesia](Agritech.Net/README.id.md)) and its [documentation](Agritech.Net/docs/en/README.md).

## Repository conventions

- One folder per component, with its own `.slnx`, `CLAUDE.md`, `PLAN.md` (roadmap) and `Progress.md` (tracking).
- One workflow per component in `.github/workflows/<component>.yml`. CI runs on changes under that folder.
- **Publishing to NuGet:** push a tag `<component>-vX.Y.Z` (for example `agritech-net-v1.0.1`) or run the workflow manually with *publish* ticked.
  It uses the `NUGET_API_KEY` repository secret.

## License

[MIT](LICENSE)
