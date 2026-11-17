# GeoPandas.Net — Progress / Progres Pengembangan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. Roadmap: [PLAN.md](PLAN.md)

Last updated / Terakhir diperbarui: **2026-11-18** · Version **0.2.0** (Phase 2 complete / Fase 2 selesai)

## Status by module / Status per modul

| Module (solution-design §) | Status | Notes / Catatan |
|---|---|---|
| Core / GeoDataFrame (§3) | ✅ | Typed column store with null bitmaps, immutable, groupby/merge/concat/describe |
| Geometry engine (§4) | ✅ (2D/2.5D) | NTS kernel; 3D/BIM/survey geometry → Phase 4 |
| CRS engine (§5) | ✅ | EPSG subset, PROJ4, WKT1, ESRI, WKT2; TM/Merc/WebMerc/LCC/Albers; Helmert |
| Geoprocessing (§6) | ✅ | intersection, union, difference, sym. difference, buffer, dissolve, clip, merge/concat |
| Spatial join (§7) | ✅ | all predicates plus nearest (exact) |
| Spatial index (§8) | 🟡 | STR-tree, QuadTree, KD-tree, Octree, GeoHash ✅ · H3/S2 → Phase 5 |
| Network analysis (§9) | ✅ | Dijkstra, A*, bidirectional, Contraction Hierarchies, isochrones, utility tracing |
| Raster analytics (§10) | ✅ | GeoTIFF/COG, NetCDF (CDF-1/2/5 + NetCDF-4, CF) ✅, DEM/slope/aspect/hillshade/viewshed ✅ |
| Remote sensing (§11) | 🟡 | NDVI/NDWI/NDBI/SAVI, change detection ✅ · Sentinel/Landsat/MODIS product readers → Phase 2 |
| Spatial statistics (§12) | ✅ | Moran's I, Geary's C, LISA, Gi*, DBSCAN, KMeans, kriging, IDW |
| Surveying & geodesy (§13) | 🟡 | Vincenty, haversine, geodesic area ✅ · adjustment/GNSS/geoid → Phase 3 |
| Civil engineering (§14) | 🟡 | watershed/streams/fill ✅ · road design/corridor → Phase 4 |
| BIM (§15) | ⏳ | Phase 4 |
| CAD (§16) | 🟡 | DXF read/write and georeferencing ✅ · DWG/DGN → Phase 4 |
| Utility network (§17) | 🟡 | tracing with valves ✅ · flow simulation → Phase 4 |
| Digital twin (§18) | ⏳ | dynamic QuadTree ready; IoT twin → Phase 5 |
| AI & ML (§19) | 🟡 | GeoLLM (OpenAI/Azure/DeepSeek) ✅ · ONNX/ML.NET/TorchSharp → Phase 5 |
| 3D GIS (§20) | 🟡 | Octree, LAS/LAZ point clouds, DSM/DTM/nDSM ✅ · Scene/Cesium → Phase 5 |
| Distributed (§21) / Cloud (§22) | ⏳ | Phase 5 |
| File formats (§23) | 🟡 | Shapefile, GeoJSON, GeoPackage, FlatGeobuf, GeoParquet, Arrow IPC/Feather, CSV, GeoTIFF, COG, NetCDF, LAS/LAZ, DXF ✅ · IFC, DWG/DGN → Phase 4 |
| Enterprise DBs (§24) | ✅ | GeoPackage, PostGIS, SQL Server, DuckDB Spatial, Oracle Spatial, MySQL (ADO.NET), BigQuery GIS ✅ |
| HPC (§25) | 🟡 | SIMD, Span, ArrayPool, parallel, Rust kernels, AOT-compatible core ✅ · GPU → Phase 5 |
| NuGet ecosystem (§26) | 🟡 | Core, Data, Geometry, Analytics, Network, Raster, CAD, AI, Datasets, Arrow, PointCloud, Sql, Sql.BigQuery, meta, CLI, Templates ✅ · 0.1.0 published · BIM/Scene/Cloud → later |
| Gallery (Blazor + Avalonia) | ✅ | 23 samples (new: FlatGeobuf, lazy GeoParquet, versioning, LiDAR, NetCDF), EN/ID, shared renderer |
| Lazy evaluation & versioning (Phase 2) | ✅ | `LazyGeoDataFrame` with bbox/column/attribute/limit pushdown, memory-mapped readers, `DatasetRepository` + `FrameDiff` |
| CLI `gpd` | ✅ | 21 commands incl. scan, las, netcdf, repo, raster, routing, GeoLLM |
| Templates | ✅ | gpd-console, gpd-blazor, gpd-avalonia |
| Docs EN/ID | ✅ | README, README.id, docs/en, docs/id, screenshots |

## Verification log / Log verifikasi

- `dotnet test`: **128/128 passing** on both kernel backends (Rust native, and managed with `GEOPANDAS_NET_DISABLE_NATIVE=1`), including live SQL Server LocalDB and DuckDB Spatial round trips.
- Phase 2 interop, checked in both directions against GDAL 3.12 / geopandas 1.2 / pyarrow / netCDF4-python / laspy+lazrs / pyproj:
  - **FlatGeobuf:** GDAL reads our files, including bbox reads through our R-tree, and we read GDAL's. Mixed types, holes, Z, nulls and datetimes all survive.
  - **GeoParquet:** geopandas reads our files with types and CRS intact, and its bbox read returns the same 363 rows as ours.
  - **Arrow/Feather:** we read pyarrow's LZ4-compressed files with dictionary columns.
  - **PROJJSON:** pyproj parses our output for 9 EPSG codes, and a custom LCC in US feet matches to < 1 µm.
  - **NetCDF:** CDF-1/2/5 and NetCDF-4 values match the expected values; netCDF4-python reads our CF writer's output.
  - **LAS/LAZ:** formats 1/3/6/8 and LAZ 3 from laspy read correctly, and laspy reads our LAS 1.2/1.4 and LAZ output.
- Bug found by the gallery: blocking on Parquet.Net's async API deadlocked under a UI SynchronizationContext. Sync wrappers now run on the thread pool (`Arrow.Sync`).
- CLI smoke test (Phase 2):
  - `sample roads → .parquet`, then `scan --bbox --where --select --explain`
  - `sample lidar`, then `las info` / `las ndsm`
  - `sample climate → .nc`, then `netcdf info` / `netcdf raster`
  - `repo init/commit/log`: re-committing the same rows in Hilbert order is a no-op.
- Rust crate: `cargo test --release` 2/2 passing.
- UTM accuracy: Eiffel Tower result matches an independent 6th-order Krüger series to < 1 cm.
- CLI smoke test: sample → info → sjoin → convert (EPSG:4326) → dissolve → plot → route → raster info/hillshade/contours → crs → transform.
- Real LLM test: Azure OpenAI `gpt-5-mini` via `gpd ask`. The Indonesian question was answered with a valid 5-step plan.
- Gallery: all 18 samples render in both Blazor and Avalonia (screenshots in `docs/screenshots`).

## Benchmarks (BenchmarkDotNet, short job, dev machine, Release)

| Benchmark | Result |
|---|---|
| sjoin 10 000 points × 32 polygons | 3.2 ms (naive nested loop 79 ms; **24×**) |
| sjoin 100 000 points × 32 polygons | 28 ms (naive 810 ms; **29×**, 3 % of the allocations) |
| 1 M points WGS 84 → UTM 48S | 47 ms |
| 1 M points WGS 84 → BNG (7-param datum shift) | 376 ms |
| Haversine, 2 M pairs (Rust/Rayon) | 34 ms |
| BBox filter, 2 M points | 3.3 ms |
| 200 routes: Dijkstra / A* / CH | 37 ms / 17 ms / 4.2 ms |

Phase 2 IO benchmarks (`FormatBench`: FlatGeobuf/GeoParquet bbox vs full read, lazy count, LAS read) are written but not measured yet: the run was stopped because the dev machine ran low on memory. Run them with `dotnet run -c Release --project benchmarks/GeoPandas.Net.Benchmarks -- --filter "*FormatBench*" --job short`.

## Changelog

- **0.2.0** (2026-11-18): Phase 2 (formats & databases).
  - FlatGeobuf with a packed Hilbert R-tree.
  - GeoParquet 1.1 and Arrow IPC/Feather, with PROJJSON.
  - NetCDF (classic + NetCDF-4, CF conventions).
  - LAS/LAZ point clouds (DSM/DTM/nDSM).
  - PostGIS, SQL Server, DuckDB, Oracle, MySQL and BigQuery readers/writers.
  - `LazyGeoDataFrame` with pushdown, memory-mapped readers, `DatasetRepository`/`FrameDiff` versioning.
  - New packages: Arrow, PointCloud, Sql, Sql.BigQuery.
  - 5 new gallery samples and the `scan`/`las`/`netcdf`/`repo` CLI commands.

- **0.1.0** (2026-11-17): first complete foundation release (Phase 1). Optimised sjoin for point-in-polygon (prepared right geometries per thread), Otsu change detection, map-sheet frame rendering.
