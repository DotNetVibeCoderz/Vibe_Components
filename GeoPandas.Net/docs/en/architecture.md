# Architecture

```text
                       ┌───────────────────────── GeoPandas.Net (meta + Gpd facade) ─────────────────────────┐
                       │                                                                                       │
 GeoPandas.Net.AI ─────┤   GeoPandas.Net.CAD ── GeoPandas.Net.Data      GeoPandas.Net.Datasets                 │
 (GeoLLM)              │         (DXF)            (GeoJSON, SHP,           (sample data)                         │
                       │                           GPKG, CSV, GeoIO)            │                                │
 GeoPandas.Net.Analytics  GeoPandas.Net.Network   GeoPandas.Net.Raster ◄────────┘                                │
 (weights, LISA, Gi*,     (CSR graph, A*, CH,     (GeoTIFF/COG, terrain,                                         │
  DBSCAN, kriging)         isochrones, tracing)    hydrology, indices)                                           │
            └──────────────────────┬────────────────────┘                                                        │
                          GeoPandas.Net.Core  (DataFrame · GeoSeries · GeoDataFrame · sjoin/overlay · MapScene) │
                                   │                                                                              │
                          GeoPandas.Net.Geometry (Geo factory · CRS engine · STR/KD/Quad/Octree · Geodesy ·     │
                                   │              GeometryOps (OverlayNG) · NativeKernels)                       │
                         NetTopologySuite      native/geopandas_native (Rust, optional)
```

## Design decisions

1. **NetTopologySuite as the geometry kernel.** Robust DE-9IM predicates and overlay are hard to get right; GeoPandas makes the same choice by building on GEOS. GeoPandas.Net uses NTS `Geometry` directly, so it interoperates with existing .NET GIS code, and robust overlays go through `OverlayNGRobust`.
2. **Own CRS engine.** There is no native PROJ dependency, so NativeAOT and WebAssembly builds work. Projection formulas follow Snyder (USGS PP-1395) and are validated against an independent Krüger-series implementation. Datum shifts use the geocentric 7-parameter Helmert transform via WGS 84.
3. **Immutable column store.** Frames never mutate. Column buffers are shared across projections; value types use bit-packed validity masks, Arrow-style.
4. **Flat-array indexes.** The STR-tree stores nodes in parallel arrays, uses `stackalloc`/`ArrayPool` traversal stacks, and has a best-first k-NN that is exact for any geometry when given an exact distance function.
5. **Renderer-agnostic plotting.** `MapScene.Render()` emits a display list (paths, circles, text, images, gradients) that the SVG writer and the Avalonia `MapView` consume, so the web and desktop maps match pixel for pixel.
6. **Native kernels are optional.** `NativeKernels` probes for `geopandas_native` (Rust + Rayon) and otherwise uses `Vector<T>` SIMD with `Parallel.For`. Set `GEOPANDAS_NET_DISABLE_NATIVE=1` to force the managed path. Both paths are covered by tests.
7. **Safe AI.** GeoLLM produces data (a JSON plan), not code. Only whitelisted, deterministic operations run.

## Performance practices

- `Parallel.For` above about 2 000 rows for per-geometry operations and spatial joins (with thread-local result lists).
- Prepared geometries for repeated predicates, and envelope pre-filtering through the STR-tree.
- CSR adjacency for graphs, binary-heap Dijkstra/A*, and Contraction Hierarchies for repeated queries.
- Raster algorithms run row-parallel over contiguous `float[]` bands, and GeoTIFF tiles decode in parallel.
- Core packages are marked `IsAotCompatible`. Data uses `Microsoft.Data.Sqlite` and is not AOT-trimmed by default.

## Testing

`tests/GeoPandas.Net.Tests` (xUnit) has 92 tests that cover projections against reference values (Web Mercator, Krüger UTM, British National Grid with datum shift, Vincenty's Flinders Peak → Buninyong benchmark), indexes against brute force, joins and overlays against brute force and area conservation, IO round-trips for every format, statistics on known patterns, agreement between all routing algorithms, raster algorithms on analytic surfaces, and GeoLLM plan execution with a fake LLM.
