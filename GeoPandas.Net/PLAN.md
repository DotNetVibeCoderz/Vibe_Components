# GeoPandas.Net — Roadmap / Rencana Pengembangan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. The scope comes from `solution-design.md`; live status is in [Progress.md](Progress.md).
> Cakupan mengacu pada `solution-design.md`; status terkini ada di [Progress.md](Progress.md).

## Phase 1 — Foundation (v0.1) ✅ / Fondasi

Core GeoDataFrame, geometry engine, CRS engine (EPSG/PROJ4/WKT/ESRI), spatial indexes (STR-tree, R-tree via STR, QuadTree, KD-tree, Octree, GeoHash), geoprocessing (intersection, union, difference, symmetric difference, buffer, dissolve, clip, merge), spatial joins (all predicates plus nearest), network analysis (Dijkstra, A*, Contraction Hierarchies, transport and utility networks), raster analytics (GeoTIFF/COG, DEM, slope, aspect, hillshade, viewshed), remote sensing (NDVI, NDBI, NDWI, change detection), spatial statistics (Moran's I, Geary's C, Getis-Ord Gi*, DBSCAN, KMeans, kriging), civil engineering (watershed analysis), CAD (DXF ⇄ GIS), Spatial AI (GeoLLM), vector formats (Shapefile, GeoJSON, GeoPackage, CSV), HPC (SIMD, Span, ArrayPool, Rust kernels, AOT-ready core), CLI, templates, Blazor and Avalonia gallery, and bilingual docs.

## Phase 2 — Formats & databases (v0.2) ✅ / Format & basis data

- [x] FlatGeobuf reader/writer with packed Hilbert R-tree (own FlatBuffers codec, memory-mapped bbox reads; GDAL-verified both ways)
- [x] GeoParquet 1.1 (WKB, PROJJSON, bbox covering, row-group pruning) and Arrow IPC / Feather (GeoArrow WKB, LZ4/ZSTD) ⇄ `DataFrame`
- [x] NetCDF raster reader: CDF-1/2/5 (own parser) and NetCDF-4/HDF5 (PureHDF), CF conventions; classic CF writer
- [x] LAS 1.0–1.4 reader/writer (formats 0–10) and LAZ (formats 0–5, laszip.net) on the `Octree`; DSM/DTM/nDSM
- [x] PostGIS, SQL Server Spatial, DuckDB Spatial, MySQL readers/writers over ADO.NET (`ReadSql`/`ReadTable`/`ToSql`, `read_postgis` equivalent)
- [x] Oracle Spatial (SDO_GEOMETRY via WKB) and BigQuery GIS (`GeoPandas.Net.Sql.BigQuery`)
- [x] Memory-mapped datasets (`MappedFile`) and lazy evaluation (`LazyGeoDataFrame` query plan with bbox/column/attribute/limit pushdown)
- [x] Dataset versioning: content-addressed snapshots, commits, tags, keyed and multiset diffs (`DatasetRepository`, `FrameDiff`)

Follow-ups (later releases) / Lanjutan:
- [ ] LAZ for LAS 1.4 point formats 6–10 (layered compression), COPC
- [ ] GeoParquet native GeoArrow encodings (point/linestring/polygon) and Parquet `GEOMETRY` logical type
- [ ] NetCDF chunked partial reads for NetCDF-4 (hyperslab selections), curvilinear grids
- [ ] Live PostGIS / Oracle CI services (currently SQL-level tests; SQL Server LocalDB and DuckDB run live)
- [ ] Sentinel-2 / Landsat / MODIS product readers (SAFE, MTL, HDF-EOS)

## Phase 3 — Projections & geodesy (v0.3) / Proyeksi & geodesi

- [ ] More projections: Lambert Azimuthal Equal-Area (EPSG:3035), Polar Stereographic, Oblique Mercator (RSO Malaysia), Equidistant Cylindrical, Robinson
- [ ] Full EPSG database import (generated registry) and grid-based datum shifts (NTv2, NADCON)
- [ ] Geoid models (EGM2008, InaGeoid) for orthometric heights
- [ ] Surveying: traverse adjustment, least squares network adjustment, GNSS (RINEX) processing

## Phase 4 — Engineering & BIM / Teknik & BIM

- [ ] `GeoPandas.Net.BIM`: IFC (STEP) reader, IFC ↔ GIS conversion, building-to-city twin
- [ ] DWG and DGN readers
- [ ] Civil: road alignment (horizontal/vertical curves), corridor modelling, earthwork volumes, parcel subdivision
- [ ] Utility network: topology rules, flow simulation, isolation-valve analysis
- [ ] Spatial statistics: Geographically Weighted Regression, spatial lag/error models, universal kriging

## Phase 5 — 3D, digital twin, AI, cloud / 3D, digital twin, AI, cloud

- [ ] `GeoPandas.Net.Scene`: terrain mesh, 3D Tiles export, Cesium.Net integration, point-cloud rendering (ThreeNet / OpenSceneGraph.Net)
- [ ] Digital twin: IoT streams (IoTComNet) on a live `QuadTree`, building, city and infrastructure twins
- [ ] AI: ONNX Runtime / ML.NET / TorchSharp land-cover segmentation, HF.Net embeddings, GeoLLM tool-calling over more operations
- [ ] H3 and S2 hexagonal and spherical indexes
- [ ] Distributed: Orleans grains for partitioned GeoDataFrames, Akka.NET pipelines, Spark connector
- [ ] Cloud: Azure Maps, Azure Digital Twins, AWS Location Service
- [ ] GPU computing (ILGPU / CUDA) for raster and kernel operations
- [ ] Interactive web map (`Explore()`) with MapLibre in Blazor

## Release & distribution / Rilis & distribusi

- [x] Publish the NuGet packages (v0.1.0 released via the `geopandas-net-v*` tag workflow)
- [x] CI (GitHub Actions): build, test (both kernel backends), cargo cross-compile for win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64
- [ ] Documentation site (DocFX), API reference
