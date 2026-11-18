# GeoPandas.Net documentation

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · Made by Gravicode Studios, led by Kang Fadhil · [🇮🇩 Bahasa Indonesia](../id/index.md)

| Guide | Covers |
|---|---|
| [Getting started](getting-started.md) | Install, first GeoDataFrame, reading/writing files, first map |
| [Vector data, CRS & IO](vector.md) | `DataFrame`/`GeoSeries`/`GeoDataFrame`, spatial joins, overlay, dissolve, CRS engine, file formats, plotting |
| [Formats, databases, lazy & versioning](data.md) | FlatGeobuf, GeoParquet, Arrow, NetCDF, LAS/LAZ, PostGIS/SQL Server/DuckDB/Oracle/BigQuery, `LazyGeoDataFrame`, `DatasetRepository` |
| [Analysis](analysis.md) | Spatial statistics, clustering, interpolation, networks & routing, raster & terrain, hydrology, remote sensing |
| [Projections, geodesy, surveying & satellites](geodesy.md) | 20 projections, the EPSG dataset, NTv2, geoids, traverse & least-squares adjustment, RINEX/GNSS, Landsat / Sentinel-2 / STAC / MODIS |
| [Engineering, BIM & spatial econometrics](engineering.md) | Spatial lag/error regression, GWR, universal kriging, EPANET-compatible hydraulics, topology rules, valve isolation, road alignments & corridors, earthwork, subdivision, IFC ⇄ GIS, DWG, DGN |
| [Scale, 3D, digital twins & cloud](scale-3d-twins.md) | H3 and S2 indexes, partitioned frames and GeoParquet datasets, glTF and 3D Tiles, Cesium.Net, `Explore()` with MapLibre and Blazor, live IoT layers, Azure Maps, AWS Location, Azure Digital Twins, ONNX and ML.NET, ILGPU |
| [CAD & AI integrations](integrations.md) | DXF ⇄ GIS, georeferencing, GeoLLM assistant |
| [Tools](tools.md) | `gpd` CLI, gallery apps, `dotnet new` templates, benchmarks |
| [Architecture](architecture.md) | Package layering, design decisions, performance, native kernels |

## Mapping from Python GeoPandas

| GeoPandas (Python) | GeoPandas.Net (C#) |
|---|---|
| `gpd.read_file(p)` | `Gpd.ReadFile(p)` / `GeoIO.Read(p)` |
| `gdf.to_file(p)` | `gdf.ToFile(p)` |
| `gpd.points_from_xy(x, y, crs)` | `GeoSeries.PointsFromXY(x, y, crs)` / `GeoDataFrame.FromXY(df, "x", "y", crs)` |
| `gdf.set_crs(4326)` / `gdf.to_crs(3857)` | `gdf.SetCrs(4326)` / `gdf.ToCrs(3857)` |
| `gdf.estimate_utm_crs()` | `gdf.EstimateUtmCrs()` |
| `gdf.area`, `gdf.length` | `gdf.Area()`, `gdf.GeomLength()` |
| `gdf.buffer(d)`, `gdf.centroid` | `gdf.Buffer(d)`, `gdf.Centroid()` |
| `gdf.total_bounds`, `gdf.sindex` | `gdf.TotalBounds`, `gdf.SIndex` |
| `gdf.cx[x0:x1, y0:y1]` | `gdf.Cx(x0, y0, x1, y1)` |
| `gpd.sjoin(a, b, predicate="within")` | `a.SJoin(b, SpatialPredicate.Within)` |
| `gpd.sjoin_nearest(a, b, distance_col="d")` | `a.SJoinNearest(b, distanceColumn: "d")` |
| `gpd.overlay(a, b, how="union")` | `a.Overlay(b, OverlayHow.Union)` |
| `gdf.dissolve(by="r", aggfunc="sum")` | `gdf.Dissolve("r", AggFunc.Sum)` |
| `gpd.clip(gdf, mask)` | `gdf.Clip(mask)` |
| `gdf.explode()` | `gdf.Explode()` |
| `gdf.union_all()` | `gdf.UnionAll()` |
| `gdf.plot(column="c", cmap="viridis")` | `gdf.Plot(new LayerStyle { Column = "c", ColorMap = ColorMaps.Viridis })` |
| `df.groupby("k").agg(...)` | `df.GroupBy("k").Agg(("col", AggFunc.Sum, "out"))` |
| `df.merge(other, on="k", how="left")` | `df.Merge(other, "k", JoinHow.Left)` |
| `gpd.read_file(p, bbox=b)` | `FlatGeobuf.Read(p, bbox)` / `Gpd.Scan(p).FilterBbox(b).Collect()` |
| `gpd.read_parquet(p)` / `gdf.to_parquet(p)` | `GeoParquet.Read(p)` / `GeoParquet.Write(gdf, p)` |
| `gpd.read_feather(p)` / `gdf.to_arrow()` | `ArrowInterop.ReadGeoArrowFile(p)` / `gdf.ToRecordBatch()` |
| `gpd.read_postgis(sql, con)` / `gdf.to_postgis(name, con)` | `SpatialSql.ReadSql(con, sql)` / `gdf.ToSql(con, name)` |
| `dask_geopandas.read_parquet(...)` (lazy) | `Gpd.Scan(p)…Collect()` |
| `xarray.open_dataset("x.nc")` | `NetCdfDataset.Open("x.nc").ToRaster(var, t)` |
| `laspy.read("x.laz")` | `Las.Read("x.laz")` |
