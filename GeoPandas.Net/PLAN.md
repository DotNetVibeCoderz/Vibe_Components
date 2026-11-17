# GeoPandas.Net — Roadmap / Rencana Pengembangan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. The scope comes from `solution-design.md`; live status is in [Progress.md](Progress.md).
> Cakupan mengacu pada `solution-design.md`; status terkini ada di [Progress.md](Progress.md).

## Phase 1 — Foundation (v0.1) ✅ / Fondasi

Core GeoDataFrame, geometry engine, CRS engine (EPSG/PROJ4/WKT/ESRI), spatial indexes (STR-tree, R-tree via STR, QuadTree, KD-tree, Octree, GeoHash), geoprocessing (intersection, union, difference, symmetric difference, buffer, dissolve, clip, merge), spatial joins (all predicates plus nearest), network analysis (Dijkstra, A*, Contraction Hierarchies, transport and utility networks), raster analytics (GeoTIFF/COG, DEM, slope, aspect, hillshade, viewshed), remote sensing (NDVI, NDBI, NDWI, change detection), spatial statistics (Moran's I, Geary's C, Getis-Ord Gi*, DBSCAN, KMeans, kriging), civil engineering (watershed analysis), CAD (DXF ⇄ GIS), Spatial AI (GeoLLM), vector formats (Shapefile, GeoJSON, GeoPackage, CSV), HPC (SIMD, Span, ArrayPool, Rust kernels, AOT-ready core), CLI, templates, Blazor and Avalonia gallery, and bilingual docs.

## Phase 2 — Formats & databases (v0.2) / Format & basis data

- [ ] FlatGeobuf reader/writer (with packed Hilbert R-tree)
- [ ] GeoParquet (Apache Arrow / Parquet) and Arrow IPC export of `DataFrame`
- [ ] NetCDF raster reader
- [ ] LAS/LAZ point cloud reader on the `Octree`
- [ ] PostGIS, SQL Server Spatial and DuckDB Spatial readers/writers (`read_postgis` equivalent)
- [ ] Oracle Spatial, BigQuery GIS
- [ ] Memory-mapped datasets and lazy evaluation (`LazyGeoDataFrame` query plan)
- [ ] Dataset versioning (snapshot and diff of immutable frames)

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

- [ ] Publish the NuGet packages (needs owner approval and the API key from `PackageCredentials.txt`)
- [ ] CI (GitHub Actions): build, test, cargo cross-compile for linux-x64, osx-arm64 and win-arm64 native kernels
- [ ] Documentation site (DocFX), API reference
