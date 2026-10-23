# Cesium.Net — Development Plan / Rencana Pengembangan

Roadmap for Cesium.Net, the .NET wrapper for CesiumJS. Status of each item is tracked in [Progress.md](Progress.md).
*Peta jalan Cesium.Net. Status tiap butir dicatat di [Progress.md](Progress.md).*

## Architecture decisions / Keputusan arsitektur

| Decision | Why |
|---|---|
| One host-agnostic core (`Cesium.Net`) + thin hosts (`Cesium.Net.Blazor`, `Cesium.Net.Avalonia`) | The same C# drives web and desktop; hosts only move JSON strings. |
| A tiny string protocol (`method` + JSON args array → JSON result) behind `ICesiumTransport` | Works over Blazor JS interop *and* native WebView script injection; easy to test with a fake transport. |
| One JS bridge (`cesium-net.js`) shared by both hosts | A fix lands once for every platform. Lives in `src/Cesium.Net.Blazor/wwwroot`, embedded into the core for Avalonia. |
| Source-generated System.Text.Json, compact `[lon,lat,h]` positions, nulls omitted | Trim/AOT-safe, small payloads, no reflection. |
| Official `Avalonia.Controls.WebView` (MIT, v12) + loopback HTTP server | Native engines (WebView2/WKWebView/WebKitGTK), no bundled Chromium; a real origin for workers, storage and local files. |
| No Cesium ion dependency by default (OSM/Esri imagery, Esri terrain) | Works out of the box; ion is opt-in via token. |
| Geodesy & orbit math in C# (Vincenty, ENU/ECEF, Kepler+J2) | Testable, usable server-side, no round-trips for measurements. |

## Milestones / Tonggak

### M1 — Foundation (v1.0)
- Core API: camera, entities (all graphics types), imagery, terrain, 3D Tiles, data sources, primitives, clock, scene/effects, drawing/editing/measuring, picking & events.
- Blazor `CesiumViewer` component; Avalonia `CesiumView` control.
- Geodesy, coordinate conversion, Keplerian orbits, realtime feed batching, Cesium ion REST client.
- Gallery (Blazor + Avalonia) sharing one sample catalog; bilingual docs (EN/ID); unit tests; NuGet packaging & icon.

### M2 — Depth (v1.1)
- SGP4/TLE propagation for real satellites (CelesTrak feeds) and access/visibility windows.
- Viewshed / line-of-sight analysis and terrain-aware coverage.
- `CustomDataSource` grouping, clustering of billboards/points, heat maps.
- Model node transforms & articulations, glTF animation control.
- Typed CZML builder in C#.
- Timeline/animation widgets restyled to match host themes.

### M3 — Scale & offline (v1.2)
- `dotnet tool` to download and self-host a CesiumJS build (`cesium-net offline`), plus Blazor static-asset helper.
- Binary transport (MessagePack / typed arrays) for very large bulk updates.
- Blazor WebAssembly gallery deployable to static hosting.
- Avalonia browser target (WASM) and mobile (Android/iOS) WebView hosts.

### M4 — Enterprise (v2.0)
- Digital-twin helpers: IoT binding, 3D Tiles metadata querying, BIM feature trees.
- MIL-STD-2525 symbology for COP views.
- Collaborative sessions (shared camera/annotations via SignalR).
- Accessibility pass on the gallery and docs site generation (DocFX).

## Quality gates / Gerbang kualitas
- Every public API has XML docs and is covered by docs in both languages.
- Core math and protocol covered by unit tests; every gallery sample verified rendering in a headless browser before release.
- No warnings in Release builds; packages include icon, README, license, symbols.

---
Made by Gravicode Studios, led by Kang Fadhil. · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
