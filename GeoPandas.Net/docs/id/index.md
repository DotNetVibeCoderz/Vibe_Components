# Dokumentasi GeoPandas.Net

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [🇬🇧 English](../en/index.md)

| Panduan | Isi |
|---|---|
| [Memulai](getting-started.md) | Instalasi, GeoDataFrame pertama, baca/tulis file, peta pertama |
| [Data vektor, CRS & IO](vector.md) | `DataFrame`/`GeoSeries`/`GeoDataFrame`, spatial join, overlay, dissolve, engine CRS, format file, peta |
| [Format, basis data, lazy & versi](data.md) | FlatGeobuf, GeoParquet, Arrow, NetCDF, LAS/LAZ, PostGIS/SQL Server/DuckDB/Oracle/BigQuery, `LazyGeoDataFrame`, `DatasetRepository` |
| [Proyeksi, geodesi, survei & satelit](geodesy.md) | 20 proyeksi, dataset EPSG, NTv2, geoid, poligon & perataan kuadrat terkecil, RINEX/GNSS, Landsat / Sentinel-2 / STAC / MODIS |
| [Teknik, BIM & ekonometrika spasial](engineering.md) | Regresi lag/error spasial, GWR, kriging universal, hidraulika kompatibel EPANET, aturan topologi, isolasi katup, alinyemen & koridor jalan, galian-timbunan, pemecahan persil, IFC ⇄ GIS, DWG, DGN |
| [Skala besar, 3D, kembaran digital & cloud](scale-3d-twins.md) | Indeks H3 dan S2, frame terpartisi dan dataset GeoParquet, glTF dan 3D Tiles, Cesium.Net, `Explore()` dengan MapLibre dan Blazor, live layer IoT, Azure Maps, AWS Location, Azure Digital Twins, ONNX dan ML.NET, ILGPU |
| [Analisis](analysis.md) | Statistik spasial, klasterisasi, interpolasi, jaringan & routing, raster & terrain, hidrologi, penginderaan jauh |
| [Integrasi CAD & AI](integrations.md) | DXF ⇄ GIS, georeferensi, asisten GeoLLM |
| [Tools](tools.md) | CLI `gpd`, aplikasi galeri, template `dotnet new`, benchmark |
| [Arsitektur](architecture.md) | Lapisan paket, keputusan desain, performa, kernel native |

## Padanan dengan GeoPandas Python

| GeoPandas (Python) | GeoPandas.Net (C#) |
|---|---|
| `gpd.read_file(p)` | `Gpd.ReadFile(p)` |
| `gdf.to_file(p)` | `gdf.ToFile(p)` |
| `gdf.to_crs(32748)` | `gdf.ToCrs(32748)` |
| `gdf.estimate_utm_crs()` | `gdf.EstimateUtmCrs()` |
| `gdf.area`, `gdf.length` | `gdf.Area()`, `gdf.GeomLength()` |
| `gpd.sjoin(a, b, predicate="within")` | `a.SJoin(b, SpatialPredicate.Within)` |
| `gpd.sjoin_nearest(a, b)` | `a.SJoinNearest(b)` |
| `gpd.overlay(a, b, how="intersection")` | `a.Overlay(b, OverlayHow.Intersection)` |
| `gdf.dissolve(by="wilayah", aggfunc="sum")` | `gdf.Dissolve("wilayah", AggFunc.Sum)` |
| `gpd.clip(gdf, mask)` | `gdf.Clip(mask)` |
| `gdf.plot(column="c")` | `gdf.Plot(new LayerStyle { Column = "c" })` |
| `df.groupby("k").agg(...)` | `df.GroupBy("k").Agg(("kolom", AggFunc.Sum, "hasil"))` |
| `gpd.read_parquet(p)` / `gdf.to_parquet(p)` | `GeoParquet.Read(p)` / `GeoParquet.Write(gdf, p)` |
| `gpd.read_postgis(sql, con)` / `gdf.to_postgis(nama, con)` | `SpatialSql.ReadSql(con, sql)` / `gdf.ToSql(con, nama)` |
| `gpd.read_file(p, bbox=b)` | `Gpd.Scan(p).FilterBbox(b).Collect()` |
| `xarray.open_dataset("x.nc")` | `NetCdfDataset.Open("x.nc").ToRaster(var, t)` |
| `laspy.read("x.laz")` | `Las.Read("x.laz")` |
