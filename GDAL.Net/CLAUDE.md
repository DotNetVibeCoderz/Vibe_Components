# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

GDAL.Net is a .NET 10 wrapper of GDAL/OGR/OSR (GDAL 3.13.3, PROJ 9.8.1) with Blazor and Avalonia components and two gallery apps. It lives as a subfolder of the `Vibe_Components` monorepo. The spec files are `GDAL.Net.txt` (brief, in Indonesian) and `features.md` (feature scope). The roadmap is in `PLAN.md` and status tracking in `Progress.md`.

## Commands

```bash
dotnet build GDAL.Net.slnx
dotnet test --project tests/GDAL.Net.Tests                                   # Microsoft Testing Platform (global.json), xUnit v3
dotnet test --project tests/GDAL.Net.Tests -- --filter-class "GDAL.Net.Tests.VectorTests"
dotnet test --project tests/GDAL.Net.Tests -- --filter-method "*Viewshed*"
dotnet run --project samples/GDAL.Net.Gallery.Blazor                         # http://localhost:5280
dotnet run --project samples/GDAL.Net.Gallery.Avalonia
dotnet run --project samples/GDAL.Net.Gallery.Avalonia -- --screenshot docs/images   # headless screenshots (GALLERY_SHOTS=id,id to limit)
pwsh build/build-native.ps1          # cargo test + build Rust kernels → src/GDAL.Net/runtimes/<rid>/native
pwsh build/pack.ps1 -Version x.y.z   # tests + dotnet pack → ./artifacts
pwsh build/publish.ps1 -Version x.y.z   # push to nuget.org (key from credentials file / NUGET_API_KEY; never print it)
```

From the bash tool, run the `.ps1` scripts with `powershell -NoProfile -ExecutionPolicy Bypass -File ...`. Windows PowerShell treats cargo's stderr as an error; `build-native.ps1` wraps native calls to handle this.

## Architecture

- **`src/GDAL.Net`** (package `Gravicode.GDAL.Net`; the id `GDAL.Net` is taken on nuget.org).
  - **Native interop.** `Native/GdalNative.*.cs` holds raw `LibraryImport` declarations against the GDAL **C API**. It does not use SWIG.
  - **Native binaries.** The natives come from the `MaxRev.Gdal.*Runtime.Minimal` package dependencies. `NativeLibraryLoader` (a module initializer) resolves `gdal` per RID, including versioned sonames, and also resolves `gdalnet_kernels`.
  - **Bootstrap.** `GdalNative`'s static constructor calls `GdalRuntime.Bootstrap()` on the first native call. Bootstrap:
    - installs the error handler and forwards messages to the `Gdal.Log` event
    - sets `GDAL_DATA`, `OSRSetPROJSearchPaths` (proj.db is under `runtimes/<rid>/native/maxrev.gdal.core.libshared`) and `CURL_CA_BUNDLE` (the shipped `gdal-data/cacert.pem`)
    - sets `OSR_DEFAULT_AXIS_MAPPING_STRATEGY=TRADITIONAL_GIS_ORDER` (lon/lat everywhere)
    - calls `GDALAllRegister`
  - **Handle model (important when adding wrappers).** Every native object is a `GdalSafeHandle`:
    - *Owned* handles carry a release function pointer.
    - *Borrowed* handles (bands, layers, a feature's geometry, field definitions) carry no release, but keep a reference to their `Parent`.
    - `GdalObject.Handle` throws `ObjectDisposedException` if the object or any ancestor is closed.
    - Imports take `GdalSafeHandle` (not `nint`), so the marshaller keeps the chain alive during the call. Use `GdalSafeHandle.Borrowed(ptr, parentHandle)` for borrowed results.
    - SRS getters return clones (`SpatialReference.CloneFrom`).
    - `SqlResultLayer` releases via its parent dataset (`releaseWithParent`).
  - **Errors.** Check `CPLErr` results with `GdalNative.Check` and `OGRErr` results with `GdalNative.CheckOgr`. Use `GdalNative.LastError(...)` when a call returned null.
  - **Progress and cancellation.** `ProgressBridge` maps `GDALProgressFunc` to `IProgress<GdalProgress>` + `CancellationToken` through a single `UnmanagedCallersOnly` callback.
  - **`Utilities/`**:
    - `GdalUtils` wraps the gdal_utils.h API (`*OptionsNew(argv)`), with typed option records in `UtilityOptions.cs` (`ToArgs()`).
    - `GdalCli` runs the GDAL ≥ 3.11 algorithm API (`gdal raster ...`).
    - `RasterAlgorithms` wraps gdal_alg.h.
  - **`Processing/`**:
    - `BandMath` is a recursive-descent parser that evaluates over row chunks with `TensorPrimitives`.
    - `SpectralIndices`.
    - `Kernels` P/Invokes the Rust library when it is present, with a managed fallback that must stay bit-identical (tested by `NativeAndManagedKernelsAgree`).
  - **`Rendering/`**: the shared UI pipeline, used by both component libraries.
    - `RasterRenderer`: view-window floating-point RasterIO, stretch cache, hillshade.
    - `ColorRamp`, `RgbaImage`.
    - `VectorData`: an immutable snapshot plus hit testing.
    - `ScreenTransform`, `TileGenerator`.
- **`native/gdalnet-kernels`**: Rust cdylib using rayon, with `extern "C"` `gnk_*` functions. Only win-x64 is prebuilt and committed (`src/GDAL.Net/runtimes/win-x64/native`). Other RIDs use the managed fallback.
- **`src/GDAL.Net.Blazor`**:
  - Interactive Server only. `GdalMap` renders rasters server-side to PNG and draws vectors as SVG.
  - The JS module is at `_content/Gravicode.GDAL.Net.Blazor/gdalMap.js`; the static asset path follows the PackageId.
- **`src/GDAL.Net.Avalonia`**:
  - `GdalMapView` renders on a background thread. Any code touching a displayed dataset must hold `GdalUiSync.Lock`.
  - The theme is at `avares://GDAL.Net.Avalonia/Themes/GdalNetTheme.axaml`.
  - Avalonia also has a `RenderOptions` type, so the code aliases GDAL's as `GdalRenderOptions`.
- **`samples/GDAL.Net.Gallery.Shared`**: the shared gallery content.
  - `SampleContext`/`SampleDataGenerator` procedurally generate the sample data into `gallery-data/`. Bump `DataVersion` when the generators change.
  - `Catalog*.cs` holds 32 `GallerySample`s, each with bilingual `Text`, `SampleParameter`s, a `Run` function and the displayed `Code`.
  - `Branding` holds the palette tokens and `SignatureContours`.
  - The Blazor and Avalonia gallery apps render this catalog generically.
  - Every sample is exercised by `GallerySamplesTests` (defaults plus every choice option), so a new sample must run offline unless it has `RequiresNetwork`.
- **Packaging**:
  - `Directory.Build.targets` adds `build/icon.png` and `build/PACKAGE.md` to every packable project.
  - The core package ships `gdal-data/` and a `buildTransitive` targets file that copies it to consumers' output.
  - Package versions are managed centrally in `Directory.Packages.props`.

## Gotchas

- The minimal GDAL build has no LERC codec. WEBP in GTiff/COG needs 3 or 4 Byte bands. ECW, MrSID and OCI are absent.
- GeoTIFF cannot be written directly into `/vsizip/`. Write it to `/vsimem/` and then use `VirtualFileSystem.CopyFile`.
- In netCDF, set nodata before writing array data. netCDF drops dimension type/direction on reopen.
- For streaming formats such as GeoJSON, `GdalUtils.VectorTranslate` returns a dataset whose feature count isn't final. Dispose it and reopen.
- `Progress<T>` posts callbacks asynchronously. In tests, use a synchronous `IProgress<T>`.
- Docs are bilingual (`docs/en`, `docs/id`, with mirrored file names). Every doc index page and every app must show the credit "Created by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil".
