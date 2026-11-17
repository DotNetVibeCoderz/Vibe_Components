# GeoPandas.Net — Progress / Progres Pengembangan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. Roadmap: [PLAN.md](PLAN.md)

Last updated / Terakhir diperbarui: **2026-11-17** · Version **0.1.0**

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
| Raster analytics (§10) | 🟡 | GeoTIFF/COG ✅, DEM/slope/aspect/hillshade/viewshed ✅ · NetCDF → Phase 2 |
| Remote sensing (§11) | 🟡 | NDVI/NDWI/NDBI/SAVI, change detection ✅ · Sentinel/Landsat/MODIS product readers → Phase 2 |
| Spatial statistics (§12) | ✅ | Moran's I, Geary's C, LISA, Gi*, DBSCAN, KMeans, kriging, IDW |
| Surveying & geodesy (§13) | 🟡 | Vincenty, haversine, geodesic area ✅ · adjustment/GNSS/geoid → Phase 3 |
| Civil engineering (§14) | 🟡 | watershed/streams/fill ✅ · road design/corridor → Phase 4 |
| BIM (§15) | ⏳ | Phase 4 |
| CAD (§16) | 🟡 | DXF read/write and georeferencing ✅ · DWG/DGN → Phase 4 |
| Utility network (§17) | 🟡 | tracing with valves ✅ · flow simulation → Phase 4 |
| Digital twin (§18) | ⏳ | dynamic QuadTree ready; IoT twin → Phase 5 |
| AI & ML (§19) | 🟡 | GeoLLM (OpenAI/Azure/DeepSeek) ✅ · ONNX/ML.NET/TorchSharp → Phase 5 |
| 3D GIS (§20) | ⏳ | Octree ✅ · Scene/Cesium → Phase 5 |
| Distributed (§21) / Cloud (§22) | ⏳ | Phase 5 |
| File formats (§23) | 🟡 | Shapefile, GeoJSON, GeoPackage, CSV, GeoTIFF, COG, DXF ✅ · FlatGeobuf, GeoParquet, IFC, DWG, LAS → later phases |
| Enterprise DBs (§24) | 🟡 | GeoPackage (SQLite) ✅ · PostGIS/SQL Server/DuckDB → Phase 2 |
| HPC (§25) | 🟡 | SIMD, Span, ArrayPool, parallel, Rust kernels, AOT-compatible core ✅ · GPU → Phase 5 |
| NuGet ecosystem (§26) | 🟡 | Core, Data, Geometry, Analytics, Network, Raster, CAD, AI, Datasets, meta, CLI, Templates ✅ · BIM/Scene/Cloud → later · not yet published |
| Gallery (Blazor + Avalonia) | ✅ | 18 samples, EN/ID, shared renderer |
| CLI `gpd` | ✅ | 17 commands incl. raster, routing, GeoLLM |
| Templates | ✅ | gpd-console, gpd-blazor, gpd-avalonia |
| Docs EN/ID | ✅ | README, README.id, docs/en, docs/id, screenshots |

## Verification log / Log verifikasi

- `dotnet test`: **92/92 passing**. Both kernel backends were tested (Rust native, and managed with `GEOPANDAS_NET_DISABLE_NATIVE=1`).
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

## Changelog

- **0.1.0** (2026-11-17): first complete foundation release (Phase 1). Optimised sjoin for point-in-polygon (prepared right geometries per thread), Otsu change detection, map-sheet frame rendering.
