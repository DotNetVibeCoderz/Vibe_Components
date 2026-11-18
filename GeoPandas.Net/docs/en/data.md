# Data formats, databases, lazy queries & versioning

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · Made by Gravicode Studios, led by Kang Fadhil · [🇮🇩 Bahasa Indonesia](../id/data.md)

Phase 2 (v0.2) adds cloud-native and scientific formats, spatial databases, a lazy query engine and dataset versioning. Every format here is checked against the reference implementation (GDAL 3.12, geopandas 1.2, pyarrow, netCDF4-python, laspy, pyproj) in both directions; small fixtures from those tools live in `tests/GeoPandas.Net.Tests/data`.

| Format / source | Read | Write | Lazy pushdown | Package |
|---|---|---|---|---|
| FlatGeobuf `.fgb` | ✅ memory-mapped | ✅ packed Hilbert R-tree | bbox (R-tree), columns, limit | `GeoPandas.Net.Data` |
| GeoParquet 1.1 `.parquet` | ✅ | ✅ WKB or native GeoArrow, PROJJSON, bbox covering | bbox (row-group stats), columns, `Where`, limit | `GeoPandas.Net.Arrow` |
| Arrow IPC / Feather v2 `.arrow` `.feather` | ✅ (LZ4/ZSTD) | ✅ GeoArrow WKB | – | `GeoPandas.Net.Arrow` |
| GeoPackage `.gpkg` | ✅ | ✅ | bbox (`rtree_*`), columns, limit | `GeoPandas.Net.Data` |
| NetCDF CDF-1/2/5, NetCDF-4 (HDF5) | ✅ CF conventions | ✅ classic CF-1.8 | – | `GeoPandas.Net.Raster` |
| LAS 1.0–1.4 (formats 0–10) | ✅ memory-mapped | ✅ formats 0–3, 6–8 | bbox, class, step | `GeoPandas.Net.PointCloud` |
| LAZ (formats 0–10) | ✅ (6–10 via the native codec) | ✅ formats 0–5 | bbox, class, step | `GeoPandas.Net.PointCloud` |
| COPC `.copc.laz` | ✅ octree hierarchy | – | bbox, depth | `GeoPandas.Net.PointCloud` |
| PostGIS, SQL Server, DuckDB, Oracle, MySQL | ✅ `ReadSql` / `ReadTable` | ✅ `ToSql` | bbox, columns, `Where`, limit → SQL | `GeoPandas.Net.Sql` |
| BigQuery GIS | ✅ `ReadQuery` | ✅ `ToBigQuery` | – | `GeoPandas.Net.Sql.BigQuery` |

All file formats go through `GeoIO`/`Gpd`, so `Gpd.ReadFile("x.parquet")`, `gdf.ToFile("x.fgb")` and `gpd convert a.shp b.parquet` just work.

## FlatGeobuf

```csharp
roads.ToFile("roads.fgb");                                     // Hilbert-sorted + packed R-tree (GDAL-compatible)
var info = FlatGeobuf.ReadInfo("roads.fgb");                    // count, CRS, geometry type, columns, index size
var window = FlatGeobuf.Read("roads.fgb", bbox: new Envelope(700_000, 702_000, 9_310_000, 9_312_000));
foreach (var batch in FlatGeobuf.ReadBatches("big.fgb", batchSize: 50_000)) { /* streaming */ }
FlatGeobuf.Write(gdf, "ordered.fgb", new FlatGeobufWriteOptions { SpatialIndex = false }); // keep row order
```

The reader memory-maps the file: a bbox query reads only the R-tree nodes it visits and the matching features. With an index, rows are stored in Hilbert order (as GDAL does); set `SpatialIndex = false` to keep the input order. Datetimes are written as UTC ISO strings, because GDAL cannot parse numeric offsets.

## GeoParquet and Apache Arrow

```csharp
GeoParquet.Write(parcels, "parcels.parquet", new GeoParquetWriteOptions { RowGroupSize = 100_000 });
var p = GeoParquet.Read("parcels.parquet", bbox: box, columns: ["owner", "area_m2"]);
var info = GeoParquet.ReadInfo("parcels.parquet");              // rows, row groups, CRS, covering column, "geo" JSON

var batch = parcels.ToRecordBatch();                            // Apache.Arrow RecordBatch (geoarrow.wkb + PROJJSON)
parcels.WriteArrowFile("parcels.arrow");                        // Feather v2 / Arrow IPC file
var back = ArrowInterop.ReadGeoArrowFile("parcels.feather");    // also reads pyarrow's LZ4-compressed files
```

- Geometry is WKB. The CRS is written as **PROJJSON** (`crs.ToProjJson()`), which pyproj, GDAL and DuckDB all read; `ProjJson.Parse` reads PROJ's own output, with or without an EPSG id.
- The GeoParquet 1.1 `bbox` covering struct column is written by default. Readers (ours, GDAL, DuckDB, Sedona) use its statistics to skip row groups.
- Strings, int64/int32/…, double, bool, timestamps (UTC, µs), categoricals/dictionaries and binary map to typed columns.

## NetCDF (climate, ocean, weather)

```csharp
using var nc = NetCdfDataset.Open("era5_t2m.nc");           // CDF-1/2/5 (own parser) or NetCDF-4/HDF5 (PureHDF)
Console.WriteLine(nc.Describe());                             // ncdump -h
DateTime[] times = nc.Times()!;                               // CF "hours since 1900-01-01"
Raster t0  = nc.ToRaster("t2m", 0);                           // one time step, north-up, WGS 84
Raster all = nc.ToRaster("t2m");                              // 3-D variable → one band per time step
double[] v = nc.ReadSlice("t2m", 3);                          // CF-unpacked values of step 3
NetCdf.WriteClassic(raster, "out.nc", "elevation", units: "m");
```

CF handling: `_FillValue`, `missing_value` and `valid_range`/`valid_min`/`valid_max` become NaN. `scale_factor`/`add_offset` are applied. Ascending latitudes are flipped to north-up. The CRS comes from lat/lon coordinates or from the `grid_mapping` (`crs_wkt`, `spatial_ref`, `epsg_code`). Grids must be regular.

## LiDAR point clouds (LAS / LAZ)

```csharp
var cloud = Las.Read("tile.laz", new LasReadOptions { Classes = [LasClass.Ground, LasClass.Building], Step = 1 });
Console.WriteLine(Las.ReadInfo("tile.laz"));                  // version, point format, count, CRS, bounds, VLRs
var dsm  = cloud.Dsm(cellSize: 1);                            // highest return per cell
var dtm  = cloud.Dtm(cellSize: 1);                            // ground (class 2), gaps filled
var hag  = cloud.HeightAboveGround(dtm);                      // normalised heights
var near = cloud.Radius(x, y, z, 2.5);                        // Octree query
var trees = cloud.ByClass(LasClass.HighVegetation).Thin(0.5).ToGeoDataFrame();
Las.Write(cloud, "out.las");                                  // or .laz (formats 0–3)
```

LAS is decoded by our own memory-mapped codec. LAZ is decoded by the native kernel (the Rust `laz` crate) for every point format 0–10, including LAS 1.4 layered compression. Without the native kernel, [laszip.net](https://www.nuget.org/packages/laszip.net) (LGPL-2.1) covers formats 0–5. COPC files are queried by octree node: `Copc.Read(path, bbox, maxDepth)` decompresses only the chunks that intersect the box. The CRS comes from the OGC WKT VLR (2112) or the GeoTIFF GeoKeys (34735).

## Spatial databases (`read_postgis` / `to_postgis`)

`GeoPandas.Net.Sql` works over plain ADO.NET: bring Npgsql, Microsoft.Data.SqlClient, DuckDB.NET, Oracle.ManagedDataAccess or MySqlConnector. Geometry crosses the wire as WKB, so you don't need provider geometry types.

```csharp
await using var con = new NpgsqlConnection(cs); con.Open();
var parcels = SpatialSql.ReadSql(con, "SELECT id, owner, geom FROM parcels WHERE kecamatan = @p0", parameters: ["Menteng"]);
var subset  = SpatialSql.ReadTable(con, "parcels", bbox: box, columns: ["owner"], limit: 1000);
districts.ToSql(con, "districts", IfExists.Replace);          // CREATE TABLE + typed columns + GIST index
var tables  = SpatialSql.ListGeometryColumns(con);
```

| Dialect | Geometry → WKB | Insert | Bbox filter | Index |
|---|---|---|---|---|
| PostGIS | `ST_AsBinary` | `ST_GeomFromWKB(…, srid)` | `&&` + `ST_MakeEnvelope` | GiST |
| SQL Server | `.STAsBinary()` | `geometry::STGeomFromWKB` | `STEnvelope().STIntersects` | spatial index (+ `fid` PK) |
| DuckDB Spatial | `ST_AsWKB` | `ST_GeomFromWKB` | `ST_Intersects_Extent` | R-tree |
| Oracle Spatial | `SDO_UTIL.TO_WKBGEOMETRY` | `SDO_GEOMETRY(blob, srid)` | `SDO_FILTER` | `SPATIAL_INDEX_V2` |
| MySQL 8 | `ST_AsWKB(…, 'axis-order=long-lat')` | `ST_GeomFromWKB` | `MBRIntersects` | `SPATIAL INDEX` |

The SRID is read back as the CRS (DuckDB geometries carry no SRID, so pass `crs:`). SQL Server LocalDB and DuckDB run live in the test suite. BigQuery uses `BigQueryGis.ReadQuery(client, sql)` and `gdf.ToBigQuery(client, dataset, table)`, which reprojects to WGS 84 and loads WKT `GEOGRAPHY`.

## Lazy queries: `LazyGeoDataFrame`

```csharp
var plan = Gpd.Scan("buildings.parquet")                       // nothing is read yet
    .FilterBbox(700_000, 9_300_000, 705_000, 9_305_000)
    .Where("levels", CompareOp.Ge, 3)
    .Select("name", "levels")
    .ToCrs(4326)
    .Limit(1_000);
Console.WriteLine(plan.Explain());                              // what was pushed into the reader
var result = plan.Collect();                                   // or plan.Stream() / plan.Count()
```

- **Pushdown:** the optimiser pushes bbox filters (also the envelope of `Filter(SpatialPredicate, geom)`, and bboxes written after `ToCrs`, transformed back), column pruning, `Where` comparisons and `Limit` into the source. The source can be a FlatGeobuf R-tree, GeoParquet row groups, the GeoPackage `rtree_*` index, or SQL `WHERE`/`LIMIT`.
- **Correctness first:** the engine re-checks every filter, so a source may return a superset. `Limit` is pushed only when every earlier filter is evaluated exactly by the source.
- **Bounded memory:** execution streams batches (`WithBatchSize`) and stops reading once a limit is satisfied.
- Sources: `GeoIO.Scan(path)`, `SpatialSql.ScanTable(con, table)`, `gdf.Lazy()`, or your own `IGeoScanSource`.

## Dataset versioning

```csharp
var repo = DatasetRepository.Open("parcels.repo", key: "parcel_id");
repo.Commit(parcels, "Import from BPN", author: "survey team");
repo.Commit(edited, "Merge 12 + 13");                          // returns the head unchanged if content is identical
repo.Tag("2026-Q4");
var diff = repo.Diff("HEAD~1", "HEAD");                        // +0 added, -1 removed, ~1 modified
foreach (var m in diff.Modified) Console.WriteLine($"{m.Key}: {string.Join(", ", m.ChangedColumns)}");
diff.ToChangeFrame().Plot(new LayerStyle { Column = "_change", Categorical = true }).SaveSvg("changes.svg");
var old = repo.Checkout("2026-Q4");
```

Snapshots are content-addressed FlatGeobuf files (`snapshots/<sha256>.fgb`), so identical versions are stored once. `FrameDiff.Compare(a, b, key)` also works without a repository. Without a key it compares rows as a multiset. Geometries compare by exact WKB, and numbers by value (an `int` 3 equals a `long` 3 and a `double` 3.0).

## CLI

```bash
gpd scan roads.parquet --bbox 700000,9308000,704000,9312000 --where "highway=primary" --select name --explain
gpd convert parcels.gpkg parcels.parquet
gpd las info tile.laz --classes
gpd las ndsm tile.las heights.tif --cell 1
gpd netcdf info era5.nc && gpd netcdf raster era5.nc t2m t2m.tif --index 0
gpd repo commit parcels.repo parcels.gpkg --m "weekly sync" && gpd repo diff parcels.repo --out changes.geojson
```
