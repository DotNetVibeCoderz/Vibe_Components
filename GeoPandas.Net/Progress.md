# GeoPandas.Net — Progress / Progres Pengembangan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. Roadmap: [PLAN.md](PLAN.md)

Last updated / Terakhir diperbarui: **2026-11-18** · Version **0.4.0** (Phase 4 complete / Fase 4 selesai)

## Status by module / Status per modul

| Module (solution-design §) | Status | Notes / Catatan |
|---|---|---|
| Core / GeoDataFrame (§3) | ✅ | Typed column store with null bitmaps, immutable, groupby/merge/concat/describe |
| Geometry engine (§4) | ✅ (2D/2.5D) | NTS kernel; 3D/BIM/survey geometry → Phase 4 |
| CRS engine (§5) | ✅ | Full EPSG dataset (5 804 CRSs), 20 projection methods matching PROJ, PROJ4/WKT1/ESRI/WKT2/PROJJSON, Helmert + NTv2, geoids · NADCON → later |
| Geoprocessing (§6) | ✅ | intersection, union, difference, sym. difference, buffer, dissolve, clip, merge/concat |
| Spatial join (§7) | ✅ | all predicates plus nearest (exact) |
| Spatial index (§8) | 🟡 | STR-tree, QuadTree, KD-tree, Octree, GeoHash ✅ · H3/S2 → Phase 5 |
| Network analysis (§9) | ✅ | Dijkstra, A*, bidirectional, Contraction Hierarchies, isochrones, utility tracing |
| Raster analytics (§10) | ✅ | GeoTIFF/COG, NetCDF (CDF-1/2/5 + NetCDF-4, CF) ✅, DEM/slope/aspect/hillshade/viewshed ✅ |
| Remote sensing (§11) | ✅ | NDVI/NDWI/NDBI/SAVI, change detection, Landsat C2, Sentinel-2 SAFE, STAC, MODIS/VIIRS HDF-EOS5 ✅ |
| Spatial statistics (§12) | ✅ | Moran's I, Geary's C, LISA, Gi*, DBSCAN, KMeans, ordinary/universal kriging, IDW, OLS + LM tests, ML spatial lag/error, GWR |
| Surveying & geodesy (§13) | ✅ | Vincenty, geodesic area, geoids, traverse, COGO, least-squares 2D/leveling adjustment, RINEX 2/3, GPS SPP ✅ · carrier-phase/RTK → later |
| Civil engineering (§14) | ✅ | watershed/streams/fill, alignments with spirals, vertical curves, corridors, earthwork & mass haul, level pads, subdivision ✅ |
| BIM (§15) | ✅ | IFC2x3/IFC4/IFC4x3 reader, IFC → GIS in map coordinates, LOD1 footprints, GIS → IFC4 ✅ · openings/NURBS, CityGML → later |
| CAD (§16) | ✅ | DXF, DWG (R14–2018), DGN v7 read/write and georeferencing ✅ · DGN v8 → later |
| Utility network (§17) | ✅ | tracing, topology rules, EPANET-compatible hydraulics, valve-isolation segments ✅ · PRVs and extended-period simulation → later |
| Digital twin (§18) | ⏳ | dynamic QuadTree ready; IoT twin → Phase 5 |
| AI & ML (§19) | 🟡 | GeoLLM (OpenAI/Azure/DeepSeek) ✅ · ONNX/ML.NET/TorchSharp → Phase 5 |
| 3D GIS (§20) | 🟡 | Octree, LAS/LAZ (formats 0–10) and COPC point clouds, DSM/DTM/nDSM ✅ · Scene/Cesium → Phase 5 |
| Distributed (§21) / Cloud (§22) | ⏳ | Phase 5 |
| File formats (§23) | 🟡 | Shapefile, GeoJSON, GeoPackage, FlatGeobuf, GeoParquet (WKB + native GeoArrow), Arrow IPC/Feather, CSV, GeoTIFF, COG, NetCDF, LAS/LAZ/COPC, RINEX, JPEG 2000 (Sentinel-2), DXF ✅ · IFC, DWG/DGN → Phase 4 |
| Enterprise DBs (§24) | ✅ | GeoPackage, PostGIS, SQL Server, DuckDB Spatial, Oracle Spatial, MySQL (ADO.NET), BigQuery GIS ✅ |
| HPC (§25) | 🟡 | SIMD, Span, ArrayPool, parallel, Rust kernels, AOT-compatible core ✅ · GPU → Phase 5 |
| NuGet ecosystem (§26) | 🟡 | Core, Data, Geometry, Analytics, Network, Raster, CAD, AI, Datasets, Arrow, PointCloud, Sql, Sql.BigQuery, Survey, RemoteSensing, Civil, BIM, meta, CLI, Templates ✅ · 0.1.0, 0.2.0 and 0.3.0 published · BIM/Scene/Cloud → later |
| Gallery (Blazor + Avalonia) | ✅ | 30 samples (new in 0.4: GWR, water pressure simulation, road corridor, BIM ⇄ GIS), EN/ID, shared renderer |
| Lazy evaluation & versioning (Phase 2) | ✅ | `LazyGeoDataFrame` with bbox/column/attribute/limit pushdown, memory-mapped readers, `DatasetRepository` + `FrameDiff` |
| CLI `gpd` | ✅ | 32 commands incl. regress, water, topology, ifc, crs search/at, geoid, survey, gnss, copc, s2, landsat, stac, scan, las, netcdf, repo; DWG/DGN via convert |
| Templates | ✅ | gpd-console, gpd-blazor, gpd-avalonia |
| Docs EN/ID | ✅ | README, README.id, docs/en, docs/id, screenshots |

## Verification log / Log verifikasi

- **v0.4** `dotnet test`: **250/250 passing** on both kernel backends.
- Phase 4 checks against independent implementations:
  - **Spatial regression:** OLS coefficients/SE/t/R²/log L/AIC and all five LM tests match spreg 1.9 to 1e-9 (p-values to 1e-32 via upper-tail gamma); ML lag/error β, SE, z, log L, AIC, pseudo R² to 1e-6 (λ optimiser tolerance 1e-7, as spreg).
  - **GWR:** mgwr 2.2 picks the same adaptive bandwidth (71); local parameters, SEs and local R² agree to 1e-15; the fixed Gaussian bandwidth search lands within 0.01 m of mgwr's.
  - **Universal kriging:** PyKrige 1.7 `regional_linear` estimates and variances to 1e-12.
  - **Hydraulics:** EPANET 2.2 (WNTR EpanetSimulator) on Net1 (pump, tank), Net3 (2 pumps, 3 tanks, level controls at t = 0) and an SI Darcy-Weisbach variant (check valve, TCV): heads within 4.6e-5 m, flows within 3e-7 m³/s, identical link status. EPANET's constants matter: RQTOL gradient floor, viscosity 1.1e-5 ft²/s, g = 32.2 ft/s² in the D-W resistance.
  - **IFC:** IfcOpenShell 0.9 (OpenCascade tessellation) bounding boxes, footprint areas, map coordinates (auto_xyz2enh) and Pset counts identical to ~1e-15 for all 14 elements; our IFC4 writer output passes IfcOpenShell schema validation with 0 issues; compressed GUIDs match `ifcopenshell.guid.compress`.
  - **DWG:** on ACadSharp's AC1018 sample, our DWG read matches ezdxf reading the same drawing's DXF: identical counts for every comparable entity type, line/circle coordinates to 2e-13, all 29 TEXT strings identical.
  - **DGN v7:** we read GDAL-written files exactly as GDAL does; GDAL reads our files with the right element types (3/4/6/12/17), levels, colours and text; complex chains within the 0.5 mm grid.
  - **Civil:** clothoid coordinates vs SciPy Fresnel integrals to 1e-9; spiral–curve–spiral layouts close on the forward tangent to 1e-6 m; analytic trapezoidal prism (39 m² × 500 m) exact by average end area and within 1 % by the grid method.
- Bugs found while building the Phase 4 gallery: `SampleData.Districts(count)` looped forever when the 1.2 km seed spacing could not fit `count` districts (spacing now scales with the count; default output unchanged).
- CLI smoke test (v0.4): `regress` (OLS/lag/GWR), `water solve` (Net3 with a closed pipe), `topology`, `ifc info/elements/footprint/from`, DGN → GeoJSON via `convert`.

- **v0.3** `dotnet test`: **213/213 passing** on both kernel backends (Rust native; managed with `GEOPANDAS_NET_DISABLE_NATIVE=1`, where the LAZ 1.4/COPC test no-ops).
- Phase 3 checks against PROJ 9.8 / pyproj:
  - **Projections:** 22 CRSs × (WKT1, PROJJSON, PROJ string) forward/inverse to < 0.1 mm (nm in practice). Krüger TM replaced the Snyder series, which was up to 11 cm off on NZTM.
  - **EPSG dataset:** 5 216 of 5 217 projected codes within 1 mm of PROJ; Palestine Cassini (28191) at 9 mm. A 704-code sample is in the test suite.
  - **NTv2:** forward and inverse within 10 µm of `hgridshift`; `+nadgrids` CRS transforms match pyproj.
  - **Geoids:** GTX exact, ISG to 4e-5 m, compound-CRS heights match pyproj; embedded EGM96 1° is 0.44 m RMS against the 15′ PROJ grid.
  - **Surveying:** least squares recovers synthetic truth to 1e-6 m; the χ² test passes on simulated noise and a 0.5 m blunder is flagged; leveling loop closes as expected.
  - **GNSS:** real RINEX 2 (georinex fixture) positions within 1.7–5.4 m of the receiver's header position after FDE.
- Format follow-ups:
  - **LAZ 1.4 / COPC:** formats 6/7 and COPC (including paged hierarchies) match laspy exactly; a COPC bbox query equals brute force.
  - **GeoParquet native GeoArrow:** all 6 geometry types round-trip with geopandas both ways.
  - **NetCDF:** `ReadWindow`, `ToPoints` and `Regrid` match netCDF4 for NetCDF-4 and CDF-2.
  - **Remote sensing:** Sentinel-2 (JPEG 2000 via Pillow/OpenJPEG) and VIIRS HDF-EOS5 (h5py) fixtures decode to the values they were written with.
- CLI smoke test (v0.3): `crs search/at`, `transform --z`, `geoid`, `survey traverse/adjust`, `gnss spp`, `copc info/read`, `s2 info/ndvi`.
- Gallery bug found while building the adjustment sample: `TraverseResult.LegLines()` crashed on loop traverses (start station listed twice); legs are now matched by position.

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

- **0.4.0** (2026-11-18): Phase 4 (engineering & BIM).
  - Spatial regression: OLS with LM diagnostics, ML spatial lag and error models, GWR with AICc bandwidth selection, universal kriging.
  - Utility networks: EPANET `.inp` read/write, EPANET-compatible steady-state hydraulics, topology rules, isolation-valve segments.
  - New package `GeoPandas.Net.Civil`: alignments with clothoids, vertical curves, corridors, earthwork, mass haul, level pads, subdivision.
  - New package `GeoPandas.Net.BIM`: STEP reader/writer, IFC → GIS in map coordinates, LOD1 footprints, GIS → georeferenced IFC4.
  - CAD: DWG read/write (ACadSharp) and MicroStation DGN v7 read/write.
  - Gallery: GWR, water pressure simulation, road corridor and BIM ⇄ GIS samples; CLI: `regress`, `water`, `topology`, `ifc`.
  - Fix: `SampleData.Districts` no longer hangs for large counts.

- **0.3.0** (2026-11-18): Phase 3 (projections & geodesy) and the Phase 2 follow-ups.
  - Projections: LAEA, polar & oblique stereographic, Hotine Oblique Mercator A/B, Cassini-Soldner, Equidistant Cylindrical, Cylindrical Equal-Area, Sinusoidal, Mollweide, Robinson, Equal Earth; Krüger-series Transverse Mercator.
  - Full EPSG dataset (5 804 CRSs, generated from PROJ `proj.db`) with `CrsRegistry.Search` / `AtLocation` and areas of use.
  - NTv2 grid datum shifts (`+nadgrids`, `GridCatalog`) and geoid models (GTX, ISG, GeoTIFF, embedded EGM96; `+geoidgrids`, `WithGeoid`).
  - New package `GeoPandas.Net.Survey`: traverses, COGO, least-squares network and leveling adjustment, RINEX 2/3, GPS single point positioning.
  - New package `GeoPandas.Net.RemoteSensing`: Landsat C2, Sentinel-2 SAFE, STAC, MODIS/VIIRS grids and HDF-EOS5.
  - LAZ for LAS 1.4 formats 6–10 and COPC (native `laz` codec), native GeoArrow encodings in GeoParquet, NetCDF hyperslab windows and curvilinear grids.
  - Live PostGIS and Oracle CI services; Oracle identifier normalisation.
  - TIFF floating-point predictor (3).
  - 3 new gallery samples, 7 new CLI commands, docs page "Projections, geodesy, surveying & satellites" (EN/ID).

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
