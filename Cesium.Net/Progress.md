# Cesium.Net — Progress / Progres

Status against [PLAN.md](PLAN.md). ✅ done · 🟡 partial · ⏳ planned.
*Status terhadap [PLAN.md](PLAN.md). ✅ selesai · 🟡 sebagian · ⏳ direncanakan.*

_Last updated / Terakhir diperbarui: 2026-10-24 — v1.0.0 (M1)._

## M1 — Foundation (v1.0)

| Item | Status | Notes |
|---|---|---|
| Solution layout (.NET 10, central package management, `Cesium.Net.slnx`) | ✅ | `src/`, `samples/`, `tests/`, `docs/`, `build/` |
| Core API `CesiumController` + 10 sub-APIs | ✅ | Camera, Entities, Imagery, Terrain, Tilesets, DataSources, Primitives, Clock, Scene, Drawing |
| JS bridge `cesium-net.js` (CesiumJS 1.146.0) | ✅ | Single source shared by both hosts |
| Source-generated JSON protocol, queued calls before ready | ✅ | Trim/AOT-safe |
| Blazor `CesiumViewer` component + `AddCesiumNet` | ✅ | Server/WASM/Auto; large results (screenshots) streamed past the SignalR limit |
| Avalonia `CesiumView` control (official NativeWebView) + loopback server | ✅ | Verified on Windows/WebView2; macOS/Linux not yet tested on hardware |
| Geodesy, coordinate conversion, Keplerian orbits, realtime feed, ion REST client | ✅ | |
| Unit tests | ✅ | 58 tests (geodesy reference values, conversions, protocol, events, feed, orbits) |
| Gallery — shared catalog | ✅ | 33 samples in 12 categories, all verified rendering in headless Chromium |
| Gallery — Blazor front-end | ✅ | "Survey station" design; EN/ID; code view; event log; live reticle |
| Gallery — Avalonia front-end | ✅ | Same catalog and design tokens; verified on Windows |
| Documentation EN + ID | ✅ | 20 pages per language; English snippets compile-checked against the API |
| README (EN/ID) with screenshots, PLAN, Progress, LICENSE | ✅ | |
| NuGet packaging + icon | ✅ | `Cesium.Net`, `Cesium.Net.Blazor`, `Cesium.Net.Avalonia` pack cleanly |
| CI workflow & NuGet publish | 🟡 | Workflow prepared for `Vibe_Components` (tag `cesium-net-v*`, secret `NUGET_API_KEY`); not yet run |

## features.md coverage (summary)

Full mapping: [docs/en/feature-matrix.md](docs/en/feature-matrix.md).

| Section | Status |
|---|---|
| 1 Rendering engine | ✅ (LOD/culling/instancing are CesiumJS internals; exposed via 3D Tiles SSE and point primitives) |
| 2 Terrain & elevation | ✅ |
| 3 Imagery & map layers | ✅ |
| 4 OGC 3D Tiles | ✅ (BIM/photogrammetry via any tileset URL or ion asset) |
| 5 3D models & formats | ✅ |
| 6 Time-dynamic visualization | ✅ |
| 7 Aerospace & satellite | 🟡 Keplerian propagation; SGP4/TLE planned (M2) |
| 8 GIS & spatial analysis | ✅ |
| 9 Entity system | ✅ |
| 10 Camera system | ✅ |
| 11 Interactivity | ✅ |
| 12 Drawing & editing | ✅ |
| 13 Visual effects | ✅ |
| 14 Sensor & military visualization | 🟡 Volumes, zones, COP sample; MIL-STD-2525 symbology planned (M4) |
| 15 Real-time streaming | ✅ Transport-agnostic `RealtimeEntityFeed` (SignalR/MQTT/WebSocket examples in docs) |
| 16 Coordinate systems | ✅ |
| 17 Developer features | ✅ Blazor; custom shaders & materials. React/Angular/Vue/Svelte not applicable to .NET |
| 18 Cesium ion integration | 🟡 Token, ion assets, REST asset listing; upload/tiling pipeline not wrapped |
| 19 Enterprise use cases | ✅ Demonstrated by gallery samples |

## Known limitations / Keterbatasan diketahui

- Avalonia: native WebViews are drawn above Avalonia content (no overlays on the globe); Windows apps need an `app.manifest` declaring Windows 10/11.
- Avalonia on macOS/Linux is untested on real hardware.
- `Geodesy.Area`/`Contains` assume polygons that do not cross the antimeridian.
- Terrain sampling returns height 0 where no terrain data exists (no null).
- Rendering in headless/software GL is slow (terrain and weather samples take 40–60 s to settle); real GPUs are unaffected.

## Changelog

### 1.0.0 — 2026-10-24
- Initial release: core, Blazor and Avalonia packages; gallery (33 samples); bilingual docs; tests.

---
Made by Gravicode Studios, led by Kang Fadhil. · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
