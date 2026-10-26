# Matriks fitur

Halaman ini memetakan setiap kemampuan GDAL dari brief proyek (`features.md`) ke API, driver, atau opsi GDAL.Net yang menyediakannya.

Legenda: ✅ API khusus GDAL.Net · 🟢 didukung lewat driver/opsi atau utilitas generik · ⚪ tidak tersedia di build native bawaan (butuh SDK berlisensi atau build GDAL khusus)

## 1. Format data

### Raster

| Format | Status | Cara |
|---|---|---|
| GeoTIFF, COG | ✅ | `Gdal.OpenRaster`, `Driver.Create`, `GdalUtils.Translate(... Format = "COG")` |
| JPEG2000 | 🟢 | Driver `JP2OpenJPEG` |
| PNG, JPEG, BMP, GIF, WebP | 🟢 | Driver + `RgbaImage.EncodePng/Jpeg/Webp` |
| NetCDF, HDF4/HDF5 | ✅ | API raster klasik atau `Gdal.OpenMultidimensional` |
| GRIB | 🟢 | Driver `GRIB` |
| ECW, MrSID | ⚪ | SDK berlisensi, tidak ada di build bawaan |
| Arc/Info Grid, BIL, DEM, SRTM | 🟢 | Driver `AIG`, `AAIGrid`, `EHdr`, `USGSDEM`, `SRTMHGT` |
| Sentinel SAFE, format NASA EarthData | 🟢 | Driver `SAFE`, `SENTINEL2`, HDF/netCDF + `/vsicurl/` |

Fitur raster:

| Fitur | Status | API |
|---|---|---|
| Baca/tulis raster | ✅ | `RasterBand.Read/Write/ReadAll/WriteAll/ReadResampled/ProcessRows`, `Dataset.ReadInterleaved` |
| Citra multi-band | ✅ | `Dataset.Bands`, `RenderOptions.Bands`, `BuildVrtOptions.Separate` |
| Metadata | ✅ | `GetMetadata`, `GetMetadataItem`, `SetMetadataItem`, `MetadataDomains` |
| Tabel warna | ✅ | `ColorTable`, `RasterBand.ColorTable` |
| Piramida/overview | ✅ | `Dataset.BuildOverviews`, `GetOverview`, `GetBestOverview` |
| Kompresi | ✅ | Creation option (`COMPRESS=DEFLATE/LZW/ZSTD/JPEG/WEBP`) |
| NoData | ✅ | `RasterBand.NoDataValue`, `MaskBand`, `MaskFlags` |
| Statistik | ✅ | `ComputeStatistics`, `GetStatistics`, `ComputeMinMax`, `Kernels.Statistics` |
| Histogram | ✅ | `GetHistogram`, `Kernels.Histogram` |
| Raster Attribute Table | ✅ | `RasterAttributeTable` |

### Vektor (OGR)

| Format | Status | Driver |
|---|---|---|
| ESRI Shapefile, GeoJSON, GeoPackage, KML/KMZ, CSV, GML, DXF, FlatGeobuf, MapInfo TAB, SQLite, OpenStreetMap | 🟢 | `ESRI Shapefile`, `GeoJSON`, `GPKG`, `KML`/`LIBKML`, `CSV`, `GML`, `DXF`, `FlatGeobuf`, `MapInfo File`, `SQLite`, `OSM` |
| PostgreSQL/PostGIS | 🟢 | Driver `PostgreSQL` (connection string `PG:`) |
| MSSQL Spatial | 🟢 | Driver `MSSQLSpatial` (connection string `MSSQL:`, butuh driver ODBC) |
| Oracle Spatial | ⚪ | Butuh Oracle client (driver `OCI`) |

| Fitur | Status | API |
|---|---|---|
| Manajemen layer | ✅ | `Dataset.Layers`, `CreateLayer`, `CopyLayer`, `DeleteLayer` |
| Manajemen fitur | ✅ | `Layer.GetNextFeature/GetFeature/CreateFeature/SetFeature/DeleteFeature/AddFeature`, `ForEach`, `ReadRecords` |
| Pengelolaan atribut | ✅ | `FieldDefinition`, `Layer.CreateField`, getter/setter dan indexer `Feature` |
| Edit geometri | ✅ | Factory `Geometry`, `AddPoint`, `AddGeometry`, `Segmentize`, `CloseRings`, `FlattenTo2D` |
| Indeks spasial | 🟢 | Bawaan GPKG/FlatGeobuf/SQLite; `CREATE SPATIAL INDEX` via `ExecuteSql`; dipakai oleh `SetSpatialFilter` |
| Transaksi | ✅ | `Dataset.BeginTransaction`, `Layer.StartTransaction` |

## 2. Transformasi koordinat

| Fitur | Status | API |
|---|---|---|
| Reproyeksi raster | ✅ | `GdalUtils.Warp(... TargetSrs)` |
| Reproyeksi vektor | ✅ | `VectorTranslateOptions.TargetSrs`, `Geometry.TransformTo`, `VectorData.Load(layer, target)` |
| Transformasi datum | ✅ | `CoordinateTransformation` + `TransformationOptions` (AOI, pipeline, OnlyBest) |
| Konversi koordinat | ✅ | `Transform`, `TransformInPlace`, `TransformBounds` |
| EPSG, WKT, PROJJSON | ✅ | `SpatialReference.FromEpsg/FromWkt/FromProjJson`, `ToWkt`, `ToProjJson` |

## 3. Pemrosesan raster

| Fitur | Status | API |
|---|---|---|
| Resampling: nearest, bilinear, cubic, cubic spline, Lanczos, average, mode (+ gauss, RMS) | ✅ | `ResampleAlgorithm` pada baca, overview, warp, translate, VRT |
| Warping, ortorektifikasi, georeferensi | ✅ | `GdalUtils.Warp` dengan `WarpOptions` (`Rpc`, `RpcDem`, `ThinPlateSpline`, `PolynomialOrder`), `Dataset.SetGcps` |
| Mosaik | ✅ | `GdalUtils.BuildVrt`, `Warp` dengan beberapa sumber, `WarpInto` |
| Pemotongan | ✅ | `WarpOptions.Cutline*` + `CropToCutline`, `TranslateOptions.ProjectionWindow/SourceWindow` |
| Band math | ✅ | `BandMath` |
| NDVI, NDWI | ✅ | `SpectralIndices.Ndvi/Ndwi` (+ MNDWI, NDBI, NBR, SAVI, EVI) |

## 4. Pemrosesan vektor

| Fitur | Status | API |
|---|---|---|
| Buffer, union, intersection, difference, symdifference | ✅ | `Geometry.Buffer/Union/Intersection/Difference/SymDifference`, `Layer.Overlay` |
| Dissolve | ✅ | `Geometry.UnionAll`, `UnaryUnion`, `UnionCascaded`, `Layer.Dissolve` |
| Simplify | ✅ | `Simplify`, `SimplifyPreserveTopology`, `VectorTranslateOptions.Simplify` |
| Convex hull, centroid | ✅ | `ConvexHull`, `ConcaveHull`, `Centroid`, `PointOnSurface` |
| Spatial join | 🟢 | Dialek SQLite (join `ST_Intersects`), `Layer.Overlay(Identity/Intersection)` |
| Attribute join | 🟢 | SQL `JOIN` (OGR SQL atau dialek SQLite) |

## 5. Mesin SQL

| Fitur | Status | API |
|---|---|---|
| Filter, pengurutan, join, agregasi, kueri atribut | ✅ | `Dataset.ExecuteSql` (OGR SQL / `dialect: "SQLITE"`), `Layer.SetAttributeFilter` |
| Filter spasial | ✅ | `ExecuteSql(spatialFilter: …)`, `Layer.SetSpatialFilter`, fungsi SpatiaLite |

## 6. Virtual raster (VRT)

| Fitur | Status | API |
|---|---|---|
| Mosaik virtual | ✅ | `GdalUtils.BuildVrt` |
| Penumpukan band | ✅ | `BuildVrtOptions { Separate = true }` |
| Transformasi on-the-fly | ✅ | `WarpOptions { Format = "VRT" }` (dipakai `TileGenerator`) |
| Tanpa duplikasi data | ✅ | VRT hanya mereferensikan sumbernya |

## 7. Cloud dan big data

| Fitur | Status | API |
|---|---|---|
| `/vsis3/`, `/vsiaz/`, `/vsigs/`, `/vsicurl/`, `/vsizip/`, `/vsitar/` | ✅ | Path builder dan helper kredensial `VirtualFileSystem`, `VsiStream` |
| Amazon S3, Azure Blob, Google Cloud Storage, HTTPS, FTP | ✅ | `ConfigureS3/ConfigureAzure/ConfigureGoogleCloud`, `Gdal.SetPathSpecificOption` |

## 8. Data multidimensi

| Fitur | Status | API |
|---|---|---|
| NetCDF, HDF5, dataset iklim, data cube, dimensi waktu | ✅ | `Gdal.OpenMultidimensional`, `MdGroup`, `MdArray`, `MdDimension`, `AsClassicDataset`, `GdalUtils.MultiDimInfo` |

## 9. Manajemen metadata

| Fitur | Status | API |
|---|---|---|
| EXIF, XMP, tag GeoTIFF, RPC, GCP, metadata per domain | ✅ | `GetMetadata("EXIF")`, `GetMetadataList("xml:XMP")`, `GetMetadata("RPC")`, `Gcps`, `SetGcps`, `MetadataDomains` |

## 10. Sistem referensi spasial

| Fitur | Status | API |
|---|---|---|
| Database EPSG | ✅ | `SpatialReference.FromEpsg`, `CrsDatabase.GetAll/Search/At` |
| WKT1, WKT2, PROJJSON, ESRI WKT | ✅ | `ToWkt(WktFormat…)`, `ToProjJson`, `ToEsriWkt`, `FromEsri` |
| CRS kustom | ✅ | `FromProj4`, `FromWkt`, `FromUserInput` |

## 11. Utilitas command line

| Utilitas | Status | API |
|---|---|---|
| gdalinfo, gdal_translate, gdalwarp, gdaldem, gdalbuildvrt, gdal_rasterize | ✅ | `GdalUtils.Info/Translate/Warp/DemProcessing/BuildVrt/Rasterize` |
| gdal_contour | ✅ | `RasterAlgorithms.GenerateContours` |
| gdal_merge.py | ✅ | `GdalUtils.Warp(sources…)` atau `BuildVrt` + `Translate` |
| ogrinfo, ogr2ogr | ✅ | `GdalUtils.VectorInfo`, `GdalUtils.VectorTranslate` |
| ogrmerge.py | ✅ | `VectorTranslate` dengan beberapa sumber atau `VectorTranslateOptions.Append` |
| gdal (CLI terpadu 3.11+) | ✅ | `GdalCli.Run` |

## 12. API pemrograman

GDAL.Net adalah binding C# modern di atas GDAL C API (terpisah dari binding SWIG resmi), dilengkapi paket komponen Blazor dan Avalonia.

## 13. Kemampuan GIS lanjutan

| Fitur | Status | API |
|---|---|---|
| Georeferensi, GCP, transformasi RPC | ✅ | `SetGcps`, `WarpOptions` (`PolynomialOrder`, `ThinPlateSpline`, `Rpc`, `RpcDem`) |
| Analisis terrain, pemrosesan DEM, hillshade | ✅ | `GdalUtils.DemProcessing`, `RenderOptions.Hillshade` |
| Pembuatan kontur | ✅ | `RasterAlgorithms.GenerateContours` |
| Analisis viewshed | ✅ | `RasterAlgorithms.Viewshed` |
| Poligonisasi / vektorisasi raster | ✅ | `RasterAlgorithms.Polygonize`, `Sieve` |
| Pembuatan tile | ✅ | `TileGenerator`, `GdalCli.Run("raster", "tile", …)` |

## 14. Kasus penggunaan GIS enterprise

| Area | Komponen GDAL.Net |
|---|---|
| Integrasi data (200+ format) | `Gdal.Open`, `Gdal.Drivers` |
| ETL spasial | `GdalUtils.VectorTranslate`, `GdalUtils.Translate`, pipeline `GdalCli` |
| Cloud storage | `VirtualFileSystem` |
| Analitik | `BandMath`, `SpectralIndices`, `DemProcessing`, `Kernels` |
| Visualisasi | `RasterRenderer`, `TileGenerator`, komponen Blazor/Avalonia |
| Pra-pemrosesan AI/ML | `Read<T>` zero-copy ke tensor, `ReadResampled`, `Warp` ke grid bersama |
| Big data | VRT, streaming `/vsicurl/`, pembacaan sadar overview, `ProcessRows` |
| Database | Driver PostGIS / MSSQL, `ExecuteSql`, transaksi |
| CRS | `SpatialReference`, `CoordinateTransformation`, `CrsDatabase` |
| Otomasi | `GdalUtils`, `GdalCli`, progres dan pembatalan |

**Berikutnya:** [Pemecahan masalah](troubleshooting.md)
