# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

GeoPandas.Net: a GeoPandas-style geospatial platform for .NET 10 (brief: `GeoPandas.Net.txt`, full scope: `solution-design.md`). Phases 1–2 are implemented (v0.2: formats, databases, lazy queries, versioning). Remaining scope is tracked in `PLAN.md` (roadmap) and `Progress.md` (status, benchmarks, verification log). **Keep both updated when you add features.**

## Commands

```bash
dotnet build GeoPandas.Net.slnx                                   # whole solution (slnx format)
dotnet test tests/GeoPandas.Net.Tests                             # 128 xUnit tests (SQL Server LocalDB / DuckDB tests run live when available, else log "skipped")
dotnet test tests/GeoPandas.Net.Tests --filter "FullyQualifiedName~CrsTests.RoundTrip_IsAccurate"   # single test
GEOPANDAS_NET_DISABLE_NATIVE=1 dotnet test tests/GeoPandas.Net.Tests   # force managed SIMD kernels
cargo build --release --manifest-path native/geopandas_native/Cargo.toml   # optional Rust kernels (auto-copied to output if present)
cargo test --release --manifest-path native/geopandas_native/Cargo.toml
dotnet run --project tools/GeoPandas.Net.Cli -- help             # `gpd` CLI
dotnet run --project gallery/GeoPandas.Net.Gallery.Blazor         # http://localhost:5280, /sample/<id>?lang=id
dotnet run --project gallery/GeoPandas.Net.Gallery.Avalonia -- --sample route --screenshot out.png
dotnet run -c Release --project benchmarks/GeoPandas.Net.Benchmarks -- --filter "*" --job short
dotnet pack GeoPandas.Net.slnx -c Release -o artifacts/nupkg      # local packages (do NOT push without the owner's OK)
```

CI/release: `.github/workflows/geopandas-net.yml` at the Vibe_Components repo root builds Rust kernels for 5 RIDs (placed in `src/GeoPandas.Net.Geometry/runtimes/<rid>/native`), tests both kernel backends and packs. Pushing a tag `geopandas-net-vX.Y.Z` publishes to NuGet (`NUGET_API_KEY` secret) and creates a GitHub release. The base version is `VersionPrefix` in `Directory.Build.props`; bump the template `PackageReference` versions with it.

Package versions are centralised in `Directory.Packages.props`. Common metadata (version, icon, authors, credit line) lives in `Directory.Build.props`. Projects under `src/` are automatically packable and AOT-flagged.

## Architecture (layering; lower layers never reference upper ones)

- `src/GeoPandas.Net.Geometry`: `Geo` factory over **NetTopologySuite** (the geometry kernel, like Shapely/GEOS for GeoPandas); own **CRS engine** in `Crs/` (projections in `Projections.cs` / `Projections.Phase3.cs`, Helmert and NTv2 datum shifts, `GeoidModel`, `CrsRegistry` falling back to the generated `EpsgDatabase`, `CrsParser` for PROJ4/WKT1/ESRI/WKT2, `ProjJson`); `Index/` (flat-array STR-tree with exact k-NN, KD-tree, QuadTree, Octree, GeoHash); `GeometryOps` (OverlayNG robust overlay); `Native/NativeKernels` (P/Invoke to Rust `geopandas_native` with `Vector<T>` fallback).
- `src/GeoPandas.Net.Core` (namespace `GeoPandas.Net`): immutable column store (`Column<T>` with validity bitmap → `DataFrame` → `GeoSeries`/`GeoDataFrame`). `DataFrame.CreateLike` is the hook that keeps derived frames typed; `GeoDataFrame` re-declares typed wrappers with `new`. Spatial ops are in `GeoDataFrame.Spatial.cs` (`FindPairs` is the shared parallel join kernel). `Plotting/` holds `MapScene`, which renders to a display list (`RenderedMap` of `Shape` records) consumed by `SvgWriter` **and** the Avalonia `MapView`. Change the renderer once and both galleries follow.
- `Core/Lazy`: `LazyGeoDataFrame` (plan nodes → optimiser → streaming executor) over `IGeoScanSource`. Sources declare `ScanPushdown` flags; the engine re-checks every filter, so sources may return supersets, and `Limit` is pushed only when all earlier filters are exact. `Core/Versioning/FrameDiff` (row hashing, keyed/multiset diffs), `Core/IO/MappedFile` (mmap spans, `OpenStream()` with exact length).
- Domain packages on top of Core: `Data` (GeoJSON, Shapefile, GeoPackage, FlatGeobuf with own FlatBuffers codec + packed Hilbert R-tree, CSV, `GeoIO` driver registry + `GeoIO.Scan`, `DatasetRepository`), `Arrow` (GeoParquet via Parquet.Net, Arrow IPC via Apache.Arrow; registers itself into GeoIO), `PointCloud` (LAS codec, LAZ via the native `laz` codec or laszip.net, COPC), `Sql` (ADO.NET `SpatialDialect`s: PostGIS, SQL Server, DuckDB, Oracle, MySQL), `Sql.BigQuery`, `Analytics`, `Network` (CSR graph and Contraction Hierarchies), `Raster` (own GeoTIFF codec, NetCDF classic parser + NetCDF-4 via PureHDF, terrain, hydrology, indices), `Survey` (traverse, COGO, least-squares adjustment, RINEX, GNSS SPP), `RemoteSensing` (Landsat MTL, Sentinel-2 SAFE via CoreJ2K, STAC, MODIS/VIIRS HDF-EOS5), `CAD` (DXF; registers into `GeoIO` via `Dxf.Register()`), `AI` (GeoLLM: the LLM returns a JSON plan of whitelisted ops, executed by `GeoAssistant.Execute`; never execute model-generated code), `Datasets` (deterministic synthetic city around EPSG:32748).
- `src/GeoPandas.Net`: meta-package plus the `Gpd` facade (`Gpd.ReadFile`, which also registers DXF).
- `gallery/`: `Gallery.Shared/GalleryCatalog.cs` defines all samples (bilingual `L10n`, code snippet, `Build` func). Blazor and Avalonia are thin shells over it. Design tokens: paper `#EDF1EA`, ink `#1B2A33`, contour `#9A6B3F`, water `#2F6F9F`, magenta `#B0306A`.
- `templates/GeoPandas.Net.Templates/content/*`: `dotnet new` templates. They reference the `GeoPandas.Net` package by version, so bump them when the version changes. `gpd-avalonia` contains a copy of `MapView.cs`.

## Conventions and gotchas

- `GeoIO`'s static constructor probes for optional driver assemblies (`GeoPandas.Net.Arrow`, `GeoPandas.Net.CAD`) by reflection, so `.parquet`/`.arrow`/`.dxf` work without calling `Register()`. New optional format packages should be added there.
- Parquet.Net is async-only: sync wrappers must go through `Arrow.Sync` (runs on the thread pool). `GetAwaiter().GetResult()` / `ToBlockingEnumerable()` on a UI thread deadlocks (this happened in the Avalonia gallery).
- Parquet.Net 6 reports string/binary columns as `ReadOnlyMemory<char>`/`ReadOnlyMemory<byte>` (`GeoParquet.ClrOf`).
- FlatGeobuf with a spatial index stores rows in Hilbert order (like GDAL). DateTimes are written as UTC ISO strings, because GDAL rejects numeric offsets.
- LAZ: the native kernel (Rust `laz` crate, `lazcodec.rs`) decodes all point formats 0–10 and COPC chunks; without it, laszip.net (LGPL) covers formats 0–5 only. Tests that need formats 6–10 / COPC must return early when `!NativeKernels.SupportsLaz` (the managed CI run). Bump `gpn_version` when adding native exports.
- DuckDB `GEOMETRY` has no SRID, so pass `crs:`. SQL Server tables get an identity `fid` PK when a spatial index is created.
- Oracle identifiers: `SpatialDialect.Quote` uppercases and `NormalizeName` lowercases all-uppercase names on the way out (like SQLAlchemy). `SDO_SRID` attribute access needs a table alias (`t.geom.SDO_SRID`). Live PostGIS/Oracle tests run only when `GPD_POSTGIS_CS` / `GPD_ORACLE_CS` are set (the `sql-services` CI job).
- The EPSG table (`Crs/Data/epsg.tsv.gz`) is generated from PROJ's `proj.db` by `tools/epsg/generate_epsg.py`; never edit it by hand. Its columns are code, kind, name, datum, ellipsoid, a, rf, towgs84, method, params, unit, unitname, extent (index 12). New projection methods need a case in `ProjectionFactory.FromEpsgMethod`, `Projection.Create` (PROJ string), `CrsParser` (WKT names/parameter aliases) and `ProjJson`, plus `Method`/`Proj4Body` overrides so they serialise.
- Embedded resource names must not look like culture names: MSBuild treated `egm96_1deg.bin.gz` as culture "bin" and moved it to a satellite assembly. Use `WithCulture="false"` and names without a culture-like segment.
- Inside `GeoPandas.Net.*` namespaces, `Raster` resolves to the namespace; use `using RasterGrid = GeoPandas.Net.Raster.Raster;`.
- Hotine Oblique Mercator: compute γ0 with the closed form (`gTan`), not asin, which is ill-conditioned at α = 90° (Swiss/Hungarian grids drifted by 4 cm).
- Interop fixtures in `tests/GeoPandas.Net.Tests/data` were written by the Python reference stack (see its README). To re-verify, use a venv with geopandas, pyogrio, pyarrow, laspy[lazrs], netCDF4, pyproj, pillow and h5py. Projection/EPSG/grid expectations (`*_reference.json`) come from PROJ 9.8; compare geodesy against those, not against values from memory.

- Inside the `GeoPandas.Net.Geometry` namespace, `Geometry` is ambiguous (namespace vs NTS type). Use the alias `using NtsGeometry = NetTopologySuite.Geometries.Geometry;`.
- `CoordinateReferenceSystem.WebMercator` is a static property, so pattern-match the projection type with its fully qualified name.
- Don't add LINQ-like instance methods named `Where`/`Select` to columns (they shadow LINQ). The mask helper is `Column<T>.Mask`.
- Planar `Area()`/`GeomLength()` on geographic CRSs return degrees. Reproject (`EstimateUtmCrs`) or use `Geodesic*`.
- Validate geodesy against independent references (see the Krüger check in `CrsTests`), not against values from memory.
- Shapefile DBF field names are truncated to 10 characters (format limit).

## Requirements from the brief

- Docs are bilingual (EN in `docs/en` and `README.md`, ID in `docs/id` and `README.id.md`), with screenshots in `docs/screenshots` (Edge headless for Blazor, `--screenshot` for Avalonia).
- Every doc and app must carry the credit "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" (`Gpd.Credits`, `GalleryCatalog.Credit`).
- Use the `frontend-design` skill (`.claude/skills/`) for gallery or UI work. The Blender and Rodin MCP servers in `.mcp.json` are available for 3D/2D art.

## Secrets (outside the repo; never copy or commit them)

- NuGet API key: `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt`. Publishing is an outward action, so ask before pushing.
- LLM keys: `C:\Users\mifma\Documents\CodeSandbox\testkey.txt` (Azure OpenAI, DeepSeek, Anthropic), HF token in `HFToken.txt`. Pass them to `gpd ask` through the environment variables `GEOPANDAS_LLM_PROVIDER/ENDPOINT/API_KEY/MODEL`.

## Supporting libraries (for later phases)

DotNetVibeCoderz repos listed in `GeoPandas.Net.txt` (RLNet, Visual.Net, ThreeNet, GravicodeScience, HF.Net, MediaPipeNet, IoTComNet, GDAL.Net, OpenSceneGraph.Net, Cesium.Net), plus TorchSharp, OpenCvSharp and ML.NET.
