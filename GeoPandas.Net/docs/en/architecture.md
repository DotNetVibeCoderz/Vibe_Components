# Architecture

```text
                     ┌──────────────────────────── GeoPandas.Net (meta + Gpd facade) ────────────────────────────┐
 GeoPandas.Net.AI    GeoPandas.Net.CAD    GeoPandas.Net.Arrow        GeoPandas.Net.Sql ── GeoPandas.Net.Sql.BigQuery
 (GeoLLM)            (DXF)                (GeoParquet, Arrow IPC)     (PostGIS, SQL Server, DuckDB, Oracle, MySQL)
                         └──────┬────────────────┘                            │
                        GeoPandas.Net.Data                                    │      GeoPandas.Net.Datasets
                        (GeoJSON, SHP, GPKG, FlatGeobuf, CSV,                 │      (sample data)
                         GeoIO + Scan, DatasetRepository)                     │            │
 GeoPandas.Net.Analytics   GeoPandas.Net.Network   GeoPandas.Net.Raster ◄── GeoPandas.Net.PointCloud
 (weights, LISA, Gi*,      (CSR graph, A*, CH,     (GeoTIFF/COG, NetCDF,     (LAS/LAZ, DSM/DTM)
  DBSCAN, kriging)          isochrones, tracing)    terrain, hydrology)
            └───────────────────────┬──────────────────────┘
                 GeoPandas.Net.Core  (DataFrame · GeoSeries · GeoDataFrame · sjoin/overlay · MapScene ·
                         │            LazyGeoDataFrame · FrameDiff · MappedFile)
                 GeoPandas.Net.Geometry (Geo factory · CRS engine + PROJJSON · STR/KD/Quad/Octree · Geodesy ·
                         │              GeometryOps (OverlayNG) · NativeKernels)
                 NetTopologySuite            native/geopandas_native (Rust, optional)
```

## Design decisions

1. **NetTopologySuite as the geometry kernel.** Robust DE-9IM predicates and overlay are hard to get right; GeoPandas makes the same choice by building on GEOS. GeoPandas.Net uses NTS `Geometry` directly, so it interoperates with existing .NET GIS code, and robust overlays go through `OverlayNGRobust`.
2. **Own CRS engine.** There is no native PROJ dependency, so NativeAOT and WebAssembly builds work. Projection formulas follow Snyder (USGS PP-1395) and are validated against an independent Krüger-series implementation. Datum shifts use the geocentric 7-parameter Helmert transform via WGS 84.
3. **Immutable column store.** Frames never mutate. Column buffers are shared across projections; value types use bit-packed validity masks, Arrow-style.
4. **Flat-array indexes.** The STR-tree stores nodes in parallel arrays, uses `stackalloc`/`ArrayPool` traversal stacks, and has a best-first k-NN that is exact for any geometry when given an exact distance function.
5. **Renderer-agnostic plotting.** `MapScene.Render()` emits a display list (paths, circles, text, images, gradients) that the SVG writer and the Avalonia `MapView` consume, so the web and desktop maps match pixel for pixel.
6. **Native kernels are optional.** `NativeKernels` probes for `geopandas_native` (Rust + Rayon) and otherwise uses `Vector<T>` SIMD with `Parallel.For`. Set `GEOPANDAS_NET_DISABLE_NATIVE=1` to force the managed path. Both paths are covered by tests.
7. **Safe AI.** GeoLLM produces data (a JSON plan), not code. Only whitelisted, deterministic operations run.
8. **Own codecs where the format is the product.** FlatGeobuf (with a minimal FlatBuffers builder/reader and the packed Hilbert R-tree), the LAS point codec, NetCDF classic and PROJJSON are implemented here, with no native dependencies. Mature managed libraries are used where re-implementing would add risk: Parquet.Net, Apache.Arrow, PureHDF (HDF5) and laszip.net (LAZ). Every codec is verified against GDAL / geopandas / pyarrow / netCDF4 / laspy / pyproj in both directions.
9. **Lazy plans with honest pushdown.** Sources declare what they can evaluate (`ScanPushdown`). The engine still re-checks every filter, and pushes `Limit` only past filters that are exact, so a coarse index can never change results.
10. **Databases without provider lock-in.** `GeoPandas.Net.Sql` speaks plain ADO.NET and moves geometry as WKB. A `SpatialDialect` holds the SQL differences (WKB functions, SRID, bbox operator, LIMIT syntax, DDL, spatial index).
11. **Versioning by content.** A dataset version is a content-addressed FlatGeobuf snapshot (SHA-256 of the schema plus row hashes), so unchanged commits are free and diffs are deterministic.

## Performance practices

- `Parallel.For` above about 2 000 rows for per-geometry operations and spatial joins (with thread-local result lists).
- Prepared geometries for repeated predicates, and envelope pre-filtering through the STR-tree.
- CSR adjacency for graphs, binary-heap Dijkstra/A*, and Contraction Hierarchies for repeated queries.
- Raster algorithms run row-parallel over contiguous `float[]` bands, and GeoTIFF tiles decode in parallel.
- Memory-mapped IO (`MappedFile`) for FlatGeobuf, LAS and Arrow IPC: opening a large file is O(1), and bbox reads only touch the pages they need.
- Core packages are marked `IsAotCompatible`. Data uses `Microsoft.Data.Sqlite` and is not AOT-trimmed by default.

## Testing

`tests/GeoPandas.Net.Tests` (xUnit) has 128 tests. They include interop fixtures written by GDAL, geopandas, pyarrow, netCDF4 and laspy, plus live SQL Server LocalDB and DuckDB Spatial round trips. The tests cover projections against reference values (Web Mercator, Krüger UTM, British National Grid with datum shift, Vincenty's Flinders Peak → Buninyong benchmark), indexes against brute force, joins and overlays against brute force and area conservation, IO round-trips for every format, statistics on known patterns, agreement between all routing algorithms, raster algorithms on analytic surfaces, and GeoLLM plan execution with a fake LLM.
