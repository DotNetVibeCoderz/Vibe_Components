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
| `projections_reference.json` | pyproj 3.7 / PROJ 9.8 | WKT1, PROJJSON and PROJ strings plus forward coordinates for LAEA, polar/oblique stereographic, Hotine A/B, Cassini, Equirectangular, CEA, Sinusoidal, Mollweide, Robinson, Equal Earth |
| `epsg_reference.json` | pyproj from PROJ `proj.db` (EPSG v12.029) | 704 projected EPSG codes: every non-TM/LCC method plus 250 random TM/LCC codes, two points each |
| `grids_reference.json` | PROJ `hgridshift` / `vgridshift` | NTv2 forward/inverse and GTX undulations for the grids the tests generate, and EGM96 (15') at seven places |
| `curv_nc4.nc`, `curv_cdf2.nc` | netCDF4-python | 2-D `lat(y,x)`/`lon(y,x)` curvilinear coordinates, hyperslab windows |
| `native_point.parquet`, `native_polygon.parquet`, `native_multipolygon.parquet` | `GeoDataFrame.to_parquet(geometry_encoding="geoarrow")` | native GeoArrow encodings, null geometries, polygons with holes |
| `1_4_w_evlr.laz`, `simple.copc.laz` | laspy test data (BSD-3-Clause) | LAZ for LAS 1.4 point format 6 with EVLRs; COPC hierarchy and bbox queries |
| `14601736.18o`, `14601736.18n` | georinex test data (MIT) | RINEX 2 observation and GPS navigation files for single point positioning |
| `S2B_MSIL2A_…SAFE/` | Pillow (OpenJPEG) + hand-written XML | synthetic Sentinel-2 L2A product: JPEG 2000 bands, baseline 05.10 offsets, SCL mask |
| `viirs_hdfeos5_subset.h5` | h5py | synthetic HDF-EOS5 grid (`StructMetadata.0`, sinusoidal projection, scale/fill attributes) |
| `stats_grid.geojson`, `stats_gwr.geojson`, `stats_reference.json` | numpy + PySAL spreg 1.9 / mgwr 2.2 / PyKrige 1.7 | synthetic spatial-lag process on a 15 × 15 queen grid, spatially varying GWR coefficients, universal kriging; reference OLS/LM/ML lag/ML error/GWR/UK results |
| `epanet_net1.inp`, `epanet_net3.inp` | EPANET example networks (US EPA, public domain), via WNTR 1.5 | `.inp` parsing, H-W hydraulics with pumps, tanks, controls |
| `epanet_net1_si_dw.inp`, `epanet_reference.json` | WNTR 1.5 + EPANET 2.2 engine | SI / Darcy-Weisbach variant with a check valve and a TCV; heads, flows, demands and status at t = 0 |
| `bim_house_ifc4.ifc`, `bim_site_ifc2x3.ifc`, `bim_reference.json` | IfcOpenShell 0.9 API (files are ours) | IFC4 in mm with IfcMapConversion, walls, slabs, mapped column type, triangulated face set, Psets/Qtos; IFC2x3 with site lat/lon; reference geometry from IfcOpenShell's OpenCascade kernel |
| `gdal_shapes.dgn` | GDAL 3.12 DGN driver | DGN v7 line strings, shape and point written by GDAL (0.01 m resolution seed) |

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
