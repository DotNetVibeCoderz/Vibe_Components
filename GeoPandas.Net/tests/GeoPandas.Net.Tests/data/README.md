# Interop fixtures

Small files written by the reference Python stack (geopandas 1.2 / GDAL 3.12 / pyarrow / netCDF4 / laspy+lazrs / pyproj)
so the tests check GeoPandas.Net against independent implementations without needing Python in CI.

| File | Written by | Checks |
|---|---|---|
| `geopandas_points.parquet` | `GeoDataFrame.to_parquet(write_covering_bbox=True, row_group_size=50)` | GeoParquet 1.1 metadata, PROJJSON CRS, bbox covering, categorical + tz timestamps |
| `geopandas_points.feather` | `GeoDataFrame.to_feather()` (LZ4) | Arrow IPC with compression and `geo` metadata |
| `gdal_points.fgb`, `gdal_mixed.fgb` | GDAL FlatGeobuf driver | packed Hilbert R-tree, mixed geometry types, polygons with holes |
| `cf_grid_nc4.nc`, `cf_grid_cdf5.nc` | netCDF4-python (NetCDF-4/HDF5 and CDF-5) | CF `scale_factor`/`add_offset`/`_FillValue`, unlimited time |
| `laspy_f6.las`, `laspy_f3.laz` | laspy 2 (+ lazrs) | LAS 1.4 point format 6, LAZ 1.2 point format 3, GeoTIFF/WKT CRS VLRs |
| `reference.json` | pyproj / shapely | expected bbox count, PROJ's own PROJJSON, an LCC reference coordinate |

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
