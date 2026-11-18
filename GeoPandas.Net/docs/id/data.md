# Format data, basis data, kueri lazy & versi dataset

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [🇬🇧 English](../en/data.md)

Fase 2 (v0.2) menambahkan format cloud-native dan ilmiah, basis data spasial, mesin kueri lazy, serta versi dataset. Setiap format diuji dua arah terhadap implementasi rujukan (GDAL 3.12, geopandas 1.2, pyarrow, netCDF4-python, laspy, pyproj). Fixture kecil dari tools tersebut ada di `tests/GeoPandas.Net.Tests/data`.

| Format / sumber | Baca | Tulis | Pushdown lazy | Paket |
|---|---|---|---|---|
| FlatGeobuf `.fgb` | ✅ memory-mapped | ✅ packed Hilbert R-tree | bbox (R-tree), kolom, limit | `GeoPandas.Net.Data` |
| GeoParquet 1.1 `.parquet` | ✅ | ✅ WKB atau GeoArrow native, PROJJSON, bbox covering | bbox (statistik row group), kolom, `Where`, limit | `GeoPandas.Net.Arrow` |
| Arrow IPC / Feather v2 | ✅ (LZ4/ZSTD) | ✅ GeoArrow WKB | – | `GeoPandas.Net.Arrow` |
| GeoPackage `.gpkg` | ✅ | ✅ | bbox (`rtree_*`), kolom, limit | `GeoPandas.Net.Data` |
| NetCDF CDF-1/2/5, NetCDF-4 (HDF5) | ✅ konvensi CF | ✅ klasik CF-1.8 | – | `GeoPandas.Net.Raster` |
| LAS 1.0–1.4 (format 0–10) | ✅ memory-mapped | ✅ format 0–3, 6–8 | bbox, kelas, step | `GeoPandas.Net.PointCloud` |
| LAZ (format 0–10) | ✅ (6–10 lewat codec native) | ✅ format 0–5 | bbox, kelas, step | `GeoPandas.Net.PointCloud` |
| COPC `.copc.laz` | ✅ hierarki octree | – | bbox, kedalaman | `GeoPandas.Net.PointCloud` |
| PostGIS, SQL Server, DuckDB, Oracle, MySQL | ✅ `ReadSql` / `ReadTable` | ✅ `ToSql` | bbox, kolom, `Where`, limit → SQL | `GeoPandas.Net.Sql` |
| BigQuery GIS | ✅ `ReadQuery` | ✅ `ToBigQuery` | – | `GeoPandas.Net.Sql.BigQuery` |

Semua format file melalui `GeoIO`/`Gpd`, jadi `Gpd.ReadFile("x.parquet")`, `gdf.ToFile("x.fgb")` dan `gpd convert a.shp b.parquet` langsung bisa dipakai.

## FlatGeobuf

```csharp
jalan.ToFile("jalan.fgb");                                     // diurutkan Hilbert + packed R-tree (kompatibel GDAL)
var info = FlatGeobuf.ReadInfo("jalan.fgb");                    // jumlah, CRS, tipe geometri, kolom, ukuran indeks
var jendela = FlatGeobuf.Read("jalan.fgb", bbox: new Envelope(700_000, 702_000, 9_310_000, 9_312_000));
FlatGeobuf.Write(gdf, "urut.fgb", new FlatGeobufWriteOptions { SpatialIndex = false }); // pertahankan urutan baris
```

Reader memetakan file ke memori. Kueri bbox hanya membaca node R-tree yang dikunjungi dan fitur yang cocok. Dengan indeks, baris disimpan dalam urutan Hilbert (seperti GDAL). Datetime ditulis sebagai string ISO UTC agar terbaca oleh GDAL.

## GeoParquet dan Apache Arrow

```csharp
GeoParquet.Write(persil, "persil.parquet", new GeoParquetWriteOptions { RowGroupSize = 100_000 });
var p = GeoParquet.Read("persil.parquet", bbox: kotak, columns: ["pemilik", "luas_m2"]);
var batch = persil.ToRecordBatch();                              // RecordBatch Apache.Arrow (geoarrow.wkb + PROJJSON)
persil.WriteArrowFile("persil.arrow");                           // Feather v2 / Arrow IPC
var kembali = ArrowInterop.ReadGeoArrowFile("persil.feather");   // termasuk file LZ4 dari pyarrow
```

CRS ditulis sebagai **PROJJSON** (`crs.ToProjJson()`), yang terbaca oleh pyproj, GDAL dan DuckDB. Kolom bbox covering GeoParquet 1.1 ditulis secara default sehingga pembaca bisa melewati row group yang tidak relevan.

## NetCDF (iklim, laut, cuaca)

```csharp
using var nc = NetCdfDataset.Open("era5_t2m.nc");      // CDF-1/2/5 (parser sendiri) atau NetCDF-4/HDF5 (PureHDF)
Console.WriteLine(nc.Describe());                        // seperti ncdump -h
var waktu = nc.Times();                                  // CF "hours since …" → DateTime
var t0 = nc.ToRaster("t2m", 0);                          // satu langkah waktu, north-up, WGS 84
NetCdf.WriteClassic(raster, "keluar.nc", "elevasi", units: "m");
```

`_FillValue`, `missing_value` dan `valid_range` menjadi NaN. `scale_factor`/`add_offset` diterapkan. Lintang menaik dibalik menjadi north-up. CRS diambil dari koordinat lat/lon atau dari `grid_mapping`.

## Point cloud LiDAR (LAS / LAZ)

```csharp
var awan = Las.Read("tile.laz", new LasReadOptions { Classes = [LasClass.Ground, LasClass.Building] });
var dsm = awan.Dsm(1);  var dtm = awan.Dtm(1);            // permukaan & terrain
var tinggi = awan.HeightAboveGround(dtm);                 // tinggi di atas tanah
var pohon = awan.ByClass(LasClass.HighVegetation).Thin(0.5).ToGeoDataFrame();
```

LAS dibaca oleh codec memory-mapped milik sendiri. LAZ didekode oleh kernel native (crate Rust `laz`) untuk semua format titik 0–10, termasuk kompresi berlapis LAS 1.4. Tanpa kernel native, laszip.net (LGPL-2.1) menangani format 0–5. Berkas COPC dikueri per node octree: `Copc.Read(path, bbox, maxDepth)` hanya mendekompresi chunk yang beririsan dengan kotak.

## Basis data spasial (`read_postgis` / `to_postgis`)

```csharp
using var con = new NpgsqlConnection(cs); con.Open();
var persil = SpatialSql.ReadSql(con, "SELECT id, pemilik, geom FROM persil WHERE kecamatan = @p0", parameters: ["Menteng"]);
distrik.ToSql(con, "distrik", IfExists.Replace);          // CREATE TABLE + kolom bertipe + indeks spasial
```

Berjalan di atas ADO.NET biasa: PostGIS (Npgsql), SQL Server, DuckDB Spatial, Oracle Spatial dan MySQL 8. Geometri dikirim sebagai WKB. Untuk BigQuery gunakan `BigQueryGis.ReadQuery` dan `gdf.ToBigQuery`.

## Kueri lazy: `LazyGeoDataFrame`

```csharp
var rencana = Gpd.Scan("bangunan.parquet")
    .FilterBbox(700_000, 9_300_000, 705_000, 9_305_000)
    .Where("lantai", CompareOp.Ge, 3)
    .Select("nama", "lantai")
    .Limit(1_000);
Console.WriteLine(rencana.Explain());                    // apa saja yang di-push ke reader
var hasil = rencana.Collect();
```

Optimizer mendorong (push down) filter bbox, pemangkasan kolom, perbandingan `Where` dan `Limit` ke sumber data: R-tree FlatGeobuf, row group GeoParquet, indeks `rtree_*` GeoPackage, atau `WHERE`/`LIMIT` SQL. Mesin tetap memeriksa ulang setiap filter. Eksekusi berjalan per batch sehingga memori tetap terbatas.

## Versi dataset

```csharp
var repo = DatasetRepository.Open("persil.repo", key: "id_persil");
repo.Commit(persil, "Impor dari BPN");
repo.Commit(diedit, "Gabung persil 12 + 13");
repo.Tag("2026-Q4");
var selisih = repo.Diff("HEAD~1", "HEAD");               // +0 ditambah, -1 dihapus, ~1 diubah
var lama = repo.Checkout("2026-Q4");
```

Snapshot disimpan sebagai FlatGeobuf berbasis konten (`snapshots/<sha256>.fgb`), sehingga versi yang identik hanya disimpan sekali. `FrameDiff.Compare(a, b, key)` juga bisa dipakai tanpa repositori.
