# GeoPandas.Net — Roadmap / Rencana Pengembangan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. The scope comes from `solution-design.md`; live status is in [Progress.md](Progress.md).
> Cakupan mengacu pada `solution-design.md`; status terkini ada di [Progress.md](Progress.md).

## Phase 1 — Foundation (v0.1) ✅ / Fondasi

Core GeoDataFrame, geometry engine, CRS engine (EPSG/PROJ4/WKT/ESRI), spatial indexes (STR-tree, R-tree via STR, QuadTree, KD-tree, Octree, GeoHash), geoprocessing (intersection, union, difference, symmetric difference, buffer, dissolve, clip, merge), spatial joins (all predicates plus nearest), network analysis (Dijkstra, A*, Contraction Hierarchies, transport and utility networks), raster analytics (GeoTIFF/COG, DEM, slope, aspect, hillshade, viewshed), remote sensing (NDVI, NDBI, NDWI, change detection), spatial statistics (Moran's I, Geary's C, Getis-Ord Gi*, DBSCAN, KMeans, kriging), civil engineering (watershed analysis), CAD (DXF ⇄ GIS), Spatial AI (GeoLLM), vector formats (Shapefile, GeoJSON, GeoPackage, CSV), HPC (SIMD, Span, ArrayPool, Rust kernels, AOT-ready core), CLI, templates, Blazor and Avalonia gallery, and bilingual docs.

## Phase 2 — Formats & databases (v0.2–0.3) ✅ / Format & basis data

- [x] FlatGeobuf reader/writer with packed Hilbert R-tree (own FlatBuffers codec, memory-mapped bbox reads; GDAL-verified both ways)
- [x] GeoParquet 1.1 (WKB, PROJJSON, bbox covering, row-group pruning) and Arrow IPC / Feather (GeoArrow WKB, LZ4/ZSTD) ⇄ `DataFrame`
- [x] NetCDF raster reader: CDF-1/2/5 (own parser) and NetCDF-4/HDF5 (PureHDF), CF conventions; classic CF writer
- [x] LAS 1.0–1.4 reader/writer (formats 0–10) and LAZ (formats 0–5, laszip.net) on the `Octree`; DSM/DTM/nDSM
- [x] PostGIS, SQL Server Spatial, DuckDB Spatial, MySQL readers/writers over ADO.NET (`ReadSql`/`ReadTable`/`ToSql`, `read_postgis` equivalent)
- [x] Oracle Spatial (SDO_GEOMETRY via WKB) and BigQuery GIS (`GeoPandas.Net.Sql.BigQuery`)
- [x] Memory-mapped datasets (`MappedFile`) and lazy evaluation (`LazyGeoDataFrame` query plan with bbox/column/attribute/limit pushdown)
- [x] Dataset versioning: content-addressed snapshots, commits, tags, keyed and multiset diffs (`DatasetRepository`, `FrameDiff`)

Follow-ups (v0.3) / Lanjutan:
- [x] LAZ for LAS 1.4 point formats 6–10 (layered compression, native `laz` crate) and COPC (hierarchy pages, bbox/depth queries); laspy-verified
- [x] GeoParquet native GeoArrow encodings (point, linestring, polygon and multi-*), read and write; geopandas-verified both ways
- [ ] Parquet `GEOMETRY` logical type (waiting for Parquet.Net support)
- [x] NetCDF hyperslab windows (`ReadWindow`, chunked NetCDF-4 and classic files) and curvilinear grids (`ToPoints`, `Regrid`); netCDF4-verified
- [x] Live PostGIS / Oracle CI services (`sql-services` job: postgis/postgis 17-3.5 and Oracle Free 23)
- [x] Satellite product readers (`GeoPandas.Net.RemoteSensing`): Landsat C2 MTL, Sentinel-2 SAFE (JPEG 2000), STAC items, MODIS/VIIRS sinusoidal grid and HDF-EOS5

## Phase 3 — Projections & geodesy (v0.3) ✅ / Proyeksi & geodesi

- [x] More projections: Lambert Azimuthal Equal-Area, Polar Stereographic (A/B, UPS), Oblique Stereographic, Hotine Oblique Mercator A/B (RSO, Swiss), Cassini-Soldner, Equidistant Cylindrical, Cylindrical Equal-Area, Sinusoidal, Mollweide, Robinson, Equal Earth; Krüger-series Transverse Mercator (nm-level against PROJ)
- [x] Full EPSG import: 5 804 CRSs generated from PROJ `proj.db` (EPSG v12.029) with area of use, search and lookup by location
- [x] Grid datum shifts: NTv2 (`+nadgrids`, `GridCatalog`), matched to PROJ `hgridshift`
- [ ] NADCON (.las/.los) grids
- [x] Geoid models: GTX, ISG (ITRF/ISG ASCII, the InaGeoid and EGM2008 distribution formats), GeoTIFF; embedded coarse EGM96; `+geoidgrids` in CRS transforms
- [ ] Bundled full-resolution EGM2008 / InaGeoid grids (users load them from BIG / NGA files)
- [x] Surveying: traverses (Bowditch/Transit), COGO, least-squares 2D and leveling network adjustment (error ellipses, χ² global test, data snooping), RINEX 2/3 and GPS single point positioning (Klobuchar, Saastamoinen, FDE)

## Phase 4 — Engineering & BIM (v0.4) ✅ / Teknik & BIM

- [x] `GeoPandas.Net.BIM`: STEP (ISO 10303-21) reader/writer, IFC2x3/IFC4/IFC4x3 model (spatial structure, Psets/Qtos, extrusions, B-reps, face sets, mapped items), georeferencing (IfcMapConversion, ePSet_MapConversion, site lat/lon), IFC → GeoDataFrame, LOD1 building footprints for city twins, GIS → georeferenced IFC4; IfcOpenShell-verified
- [ ] IFC opening/boolean subtraction and NURBS geometry; CityGML/CityJSON export of the LOD1 twin
- [x] DWG read/write (R14–2018 via ACadSharp, ezdxf-verified) and MicroStation DGN v7 read/write (GDAL-verified both ways)
- [ ] DGN v8 (OLE container)
- [x] Civil: horizontal alignments with clothoid spirals, vertical parabolic curves, corridors daylighted to a DEM, cross-sections, average-end-area volumes, mass haul, design surface, grid earthwork, level pads, parcel subdivision
- [x] Utility network: topology rules, EPANET-compatible steady-state hydraulics (`.inp` read/write; heads within 0.05 mm of EPANET 2.2), isolation-valve segments and unintended isolation
- [ ] Pressure-regulating valves (PRV/PSV/FCV/PBV) and extended-period simulation
- [x] Spatial statistics: OLS with LM diagnostics, ML spatial lag/error (spreg-verified), GWR (mgwr-verified), universal kriging (PyKrige-verified)

## Phase 5 — 3D, digital twin, AI, cloud / 3D, digital twin, AI, cloud

- [x] `GeoPandas.Net.Scene`: local ENU frames, terrain/extrusion/point/line meshes, glTF with EXT_mesh_features + EXT_structural_metadata, 3D Tiles 1.1 (buildings quadtree, terrain pyramid; validator-clean); `GeoPandas.Net.Cesium` for Cesium.Net
- [ ] Native point-cloud rendering (ThreeNet / OpenSceneGraph.Net); point clouds go to glTF / Cesium for now
- [x] Digital twin: `LiveLayer` (IoT streams on a dynamic quadtree, geofence and threshold events with hysteresis, stale sensors, trajectories, zone statistics), `DigitalTwin` roll-ups; Azure Digital Twins and AWS Location tracker sync
- [ ] IoTComNet transport adapters (package not on NuGet); readings arrive through `IAsyncEnumerable` / channels / `TwinJson`
- [x] AI: ONNX Runtime segmentation (tiled, onnxruntime-verified), ML.NET land-cover classification with accuracy assessment, GeoLLM ops `h3_bin`, `within_distance`, `simplify`, `unique`
- [ ] TorchSharp training and HF.Net embeddings (HF.Net not on NuGet)
- [x] H3 (port of Uber H3 v4, exact against h3-py 4.5) and S2 (exact against s2sphere) indexes, `H3Frame` binning
- [x] Distributed core: `PartitionedGeoDataFrame` (k-d tree / grid / H3 / column partitions, parallel map/reduce, partition-aware sjoin) and partitioned GeoParquet datasets with partition pruning
- [ ] Orleans, Akka.NET and Spark adapters over the partition model
- [x] Cloud: Azure Maps, Azure Digital Twins, AWS Location Service (`GeoPandas.Net.Cloud`, SigV4 botocore-verified)
- [x] GPU computing (ILGPU: CUDA → OpenCL → CPU) for slope, aspect, hillshade, normalized difference, convolution, IDW
- [x] Interactive web map (`Explore()`) with MapLibre, `ExploreView` / `MapSceneView` Blazor components

## Release & distribution / Rilis & distribusi

- [x] Publish the NuGet packages (v0.1.0 and v0.2.0 released via the `geopandas-net-v*` tag workflow)
- [x] CI (GitHub Actions): build, test (both kernel backends), cargo cross-compile for win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64
- [ ] Documentation site (DocFX), API reference
