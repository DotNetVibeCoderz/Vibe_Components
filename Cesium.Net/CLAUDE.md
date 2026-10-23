# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Cesium.Net is a .NET 10 wrapper for CesiumJS with hosts for Blazor (web) and Avalonia (desktop). The original brief is in `Cesium.Net.txt` and the target feature list in `features.md`. The roadmap is `PLAN.md` and status tracking is in `Progress.md`; keep `Progress.md` current when finishing work.

## Commands

```bash
dotnet build Cesium.Net.slnx
dotnet test tests/Cesium.Net.Tests                                   # xunit v3 on Microsoft.Testing.Platform (global.json "test.runner")
dotnet test tests/Cesium.Net.Tests -- --filter-method "*Vincenty*"   # single test / pattern (MTP filter syntax)
dotnet run --project samples/Cesium.Net.Gallery.Blazor --urls http://localhost:5180
dotnet run --project samples/Cesium.Net.Gallery.Avalonia -- --sample hello-globe   # optional start sample
./build/pack.ps1 [-Version 1.0.1]                                    # tests + NuGet packages → ./artifacts
```

Publishing goes through CI (GitHub Actions in the `DotNetVibeCoderz/Vibe_Components` repo, where this solution lives under `Cesium.Net/`). The workflow uses the `NUGET_API_KEY` repository secret and runs on `cesium-net-v*` tags. Never print, log or commit API keys. `build/publish.ps1` is a local fallback that reads the key without echoing it.

## Architecture

- **`src/Cesium.Net`**, the core package (namespace `CesiumNet`), is host-agnostic. `CesiumController` exposes sub-APIs (`Camera`, `Entities`, `Imagery`, `Terrain`, `Tilesets`, `DataSources`, `Primitives`, `Clock`, `Scene`, `Drawing`). Every call becomes `ICesiumTransport.InvokeAsync(method, argsJson)`: the method is a string like `"entities.add"` and the args are a JSON **array** built with `JsArgs`. Results come back as JSON strings. Events from JS enter through `CesiumController.HandleHostEvent(name, json)`. Calls made before the `"ready"` event are queued.
- **JSON** uses the source-generated `Interop/CesiumJsonContext` (camelCase, nulls omitted, enums as PascalCase strings). Every new option or result type must be added there with `[JsonSerializable]`. `GeoPoint` serializes as `[lon,lat,h]` and `CesiumColor` as `#RRGGBBAA`. Option properties are nullable, meaning "unchanged / Cesium default".
- **JS bridge**: `src/Cesium.Net.Blazor/wwwroot/cesium-net.js` is the *single source of truth* for both hosts. Blazor serves it as a static web asset, and the core project embeds the same file (`CesiumScripts.BridgeScript`) for Avalonia. Its `M` method table must match the method strings used in `src/Cesium.Net/Api/*.cs`. JS compares enum strings case-insensitively via `lc()`. Compass heading is converted for models with `orientationFor` (heading − 90°).
- **Blazor host** (`src/Cesium.Net.Blazor`): `CesiumViewer` imports `cesium-net-blazor.js`, creates the viewer, and forwards events through `[JSInvokable] OnCesiumEvent`. It needs an interactive render mode.
- **Avalonia host** (`src/Cesium.Net.Avalonia`): `CesiumView` wraps the official `Avalonia.Controls.WebView` `NativeWebView`. The page is served by `CesiumLocalServer`, a loopback `HttpListener` that also serves folders mapped with `MapFolder`. C#→JS uses `InvokeScript("CesiumNet.dispatchFromHost(...)")`, and results and events return via `invokeCSharpAction` messages correlated by `callId`. Windows apps **must** ship an `app.manifest` with the Windows 10 `supportedOS` id, or NativeWebView crashes. Overlays cannot be drawn on top of the native WebView (airspace).
- **Gallery**: `samples/Cesium.Net.Gallery.Shared` holds one `GallerySample` subclass per use case, discovered by reflection and ordered by `[SampleOrder]` within the categories in `SampleCatalog`. Both `Cesium.Net.Gallery.Blazor` and `Cesium.Net.Gallery.Avalonia` render the same catalog. The code tab shows the text between `// <snippet>` and `// </snippet>` in the embedded sample source. All UI text is bilingual via `Text(En, Id)` / `GalleryText`.
- **Design tokens** ("survey station" theme) are defined in `samples/Cesium.Net.Gallery.Blazor/wwwroot/app.css` and mirrored in `samples/Cesium.Net.Gallery.Avalonia/App.axaml`. Fonts are Chakra Petch, IBM Plex Sans and IBM Plex Mono. The live camera "reticle" strip is the gallery's signature element. Use the project skill `frontend-design` for gallery UI work.

## Conventions

- Samples and defaults must work without a Cesium ion token: use `ImageryLayerOptions.Satellite()` (Esri) and `TerrainOptions.ArcGis()`. Gate ion-only content on `ctx.HasIonToken`.
- Docs are bilingual: `docs/en/*.md` and `docs/id/*.md` use matching file names and link to each other. Screenshots live in `docs/images/`.
- Docs, READMEs and apps carry the credit: "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" (EN: "Made by Gravicode Studios, led by Kang Fadhil").
- Verify UI changes in a real browser: the Blazor gallery renders in headless Chromium with `--use-angle=swiftshader --enable-unsafe-swiftshader`.
