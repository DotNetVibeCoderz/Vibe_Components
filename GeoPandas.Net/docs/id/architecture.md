# Arsitektur

Lapisan paket (dari bawah ke atas):

1. **GeoPandas.Net.Geometry**: factory geometri (`Geo`), engine CRS (proyeksi, datum, parser EPSG/PROJ/WKT), indeks spasial (STR-tree, KD-tree, quadtree, octree, GeoHash), geodesi (haversine, Vincenty, luas geodesik), `GeometryOps` (OverlayNG) dan `NativeKernels` (Rust/SIMD).
2. **GeoPandas.Net.Core**: `DataFrame`, `GeoSeries`, `GeoDataFrame`, spatial join, overlay, dissolve, clip, serta `MapScene` (rendering peta).
3. **Paket domain**: `Data` (IO, FlatGeobuf, `GeoIO.Scan`, versi dataset), `Arrow` (GeoParquet, Arrow IPC), `Sql` (PostGIS, SQL Server, DuckDB, Oracle, MySQL) + `Sql.BigQuery`, `PointCloud` (LAS/LAZ), `Analytics` (statistik spasial), `Network` (routing), `Raster` (raster/terrain/hidrologi/NetCDF), `CAD` (DXF), `AI` (GeoLLM), `Datasets` (data contoh).
   Core juga berisi `LazyGeoDataFrame` (rencana kueri lazy dengan pushdown), `FrameDiff` dan `MappedFile`.
4. **GeoPandas.Net**: meta-package beserta fasad `Gpd`.

## Keputusan desain

- **NetTopologySuite sebagai kernel geometri**, setara dengan GeoPandas yang memakai GEOS, sehingga predikat DE-9IM dan overlay robust.
- **Engine CRS sendiri** tanpa PROJ native, sehingga mendukung NativeAOT dan WebAssembly. Akurasinya divalidasi terhadap deret Krüger (< 1 mm).
- **Column-store immutable**: buffer dibagi antar-frame dan nilai null disimpan sebagai bitmap.
- **Rendering agnostik**: `MapScene` menghasilkan display list yang dipakai oleh SVG, Blazor dan Avalonia, sehingga peta web dan desktop identik.
- **Kernel native opsional**: Rust (Rayon) bila `geopandas_native` tersedia, selain itu SIMD `Vector<T>`. Set `GEOPANDAS_NET_DISABLE_NATIVE=1` untuk memaksa jalur managed.
- **AI yang aman**: LLM hanya menghasilkan data (rencana JSON), bukan kode.
- **Codec sendiri bila formatnya adalah produknya**: FlatGeobuf, LAS, NetCDF klasik dan PROJJSON ditulis sendiri. Parquet.Net, Apache.Arrow, PureHDF dan laszip.net dipakai bila implementasi ulang hanya menambah risiko. Semuanya diuji dua arah terhadap GDAL / geopandas / pyarrow / netCDF4 / laspy / pyproj.
- **Pushdown yang jujur**: sumber data menyatakan kemampuannya (`ScanPushdown`), sedangkan mesin lazy tetap memeriksa ulang setiap filter.
- **Basis data tanpa terikat provider**: ADO.NET biasa + WKB, perbedaan SQL dipisah ke `SpatialDialect`.

## Performa (mesin pengembangan)

| Operasi | Hasil |
|---|---|
| sjoin 100 ribu titik × 32 poligon | 28 ms (29× lebih cepat dari loop naif, alokasi 97% lebih kecil) |
| Transformasi 1 juta titik WGS 84 → UTM | 47 ms |
| Haversine 2 juta pasangan (Rust) | 34 ms |
| 200 rute: Dijkstra / A* / Contraction Hierarchies | 37 ms / 17 ms / 4 ms |

## Pengujian

Proyek `tests/GeoPandas.Net.Tests` berisi 128 tes xUnit, termasuk fixture interop dari GDAL/geopandas/pyarrow/netCDF4/laspy serta round trip langsung ke SQL Server LocalDB dan DuckDB Spatial: akurasi proyeksi terhadap nilai referensi, indeks dibandingkan dengan brute force, round-trip semua format file, statistik pada pola yang diketahui, kesesuaian hasil semua algoritma routing, algoritma raster pada permukaan analitik, dan GeoLLM dengan LLM tiruan.
