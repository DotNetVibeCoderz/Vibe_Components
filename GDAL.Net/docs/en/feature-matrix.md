# Feature matrix

This page maps every GDAL capability from the project brief (`features.md`) to the GDAL.Net API, driver or option that provides it.

Legend: ✅ dedicated GDAL.Net API · 🟢 supported through drivers/options or the generic utilities · ⚪ not available in the bundled native build (needs a proprietary SDK or a custom GDAL build)

## 1. Data formats

### Raster

| Format | Status | How |
|---|---|---|
| GeoTIFF, COG | ✅ | `Gdal.OpenRaster`, `Driver.Create`, `GdalUtils.Translate(... Format = "COG")` |
| JPEG2000 | 🟢 | `JP2OpenJPEG` driver |
| PNG, JPEG, BMP, GIF, WebP | 🟢 | Drivers + `RgbaImage.EncodePng/Jpeg/Webp` |
| NetCDF, HDF4/HDF5 | ✅ | Classic raster API or `Gdal.OpenMultidimensional` |
| GRIB | 🟢 | `GRIB` driver |
| ECW, MrSID | ⚪ | Proprietary SDKs, not in the bundled build |
| Arc/Info Grid, BIL, DEM, SRTM | 🟢 | `AIG`, `AAIGrid`, `EHdr`, `USGSDEM`, `SRTMHGT` drivers |
| Sentinel SAFE, NASA EarthData formats | 🟢 | `SAFE`, `SENTINEL2`, HDF/netCDF drivers + `/vsicurl/` |

Raster features:

| Feature | Status | API |
|---|---|---|
| Read/write rasters | ✅ | `RasterBand.Read/Write/ReadAll/WriteAll/ReadResampled/ProcessRows`, `Dataset.ReadInterleaved` |
| Multi-band imagery | ✅ | `Dataset.Bands`, `RenderOptions.Bands`, `BuildVrtOptions.Separate` |
| Metadata | ✅ | `GetMetadata`, `GetMetadataItem`, `SetMetadataItem`, `MetadataDomains` |
| Color table | ✅ | `ColorTable`, `RasterBand.ColorTable` |
| Pyramid/overview | ✅ | `Dataset.BuildOverviews`, `GetOverview`, `GetBestOverview` |
| Compression | ✅ | Creation options (`COMPRESS=DEFLATE/LZW/ZSTD/JPEG/WEBP`) |
| NoData | ✅ | `RasterBand.NoDataValue`, `MaskBand`, `MaskFlags` |
| Statistics | ✅ | `ComputeStatistics`, `GetStatistics`, `ComputeMinMax`, `Kernels.Statistics` |
| Histogram | ✅ | `GetHistogram`, `Kernels.Histogram` |
| Raster Attribute Table | ✅ | `RasterAttributeTable` |

### Vector (OGR)

| Format | Status | Driver |
|---|---|---|
| ESRI Shapefile, GeoJSON, GeoPackage, KML/KMZ, CSV, GML, DXF, FlatGeobuf, MapInfo TAB, SQLite, OpenStreetMap | 🟢 | `ESRI Shapefile`, `GeoJSON`, `GPKG`, `KML`/`LIBKML`, `CSV`, `GML`, `DXF`, `FlatGeobuf`, `MapInfo File`, `SQLite`, `OSM` |
| PostgreSQL/PostGIS | 🟢 | `PostgreSQL` driver (`PG:` connection strings) |
| MSSQL Spatial | 🟢 | `MSSQLSpatial` driver (`MSSQL:` connection strings, ODBC driver required) |
| Oracle Spatial | ⚪ | Needs the Oracle client (`OCI` driver) |

| Feature | Status | API |
|---|---|---|
| Layer management | ✅ | `Dataset.Layers`, `CreateLayer`, `CopyLayer`, `DeleteLayer` |
| Feature management | ✅ | `Layer.GetNextFeature/GetFeature/CreateFeature/SetFeature/DeleteFeature/AddFeature`, `ForEach`, `ReadRecords` |
| Attribute handling | ✅ | `FieldDefinition`, `Layer.CreateField`, `Feature` getters/setters and indexer |
| Geometry editing | ✅ | `Geometry` factories, `AddPoint`, `AddGeometry`, `Segmentize`, `CloseRings`, `FlattenTo2D` |
| Spatial indexing | 🟢 | Built into GPKG/FlatGeobuf/SQLite; `CREATE SPATIAL INDEX` via `ExecuteSql`; `SetSpatialFilter` uses it |
| Transactions | ✅ | `Dataset.BeginTransaction`, `Layer.StartTransaction` |

## 2. Coordinate transformation

| Feature | Status | API |
|---|---|---|
| Raster reprojection | ✅ | `GdalUtils.Warp(... TargetSrs)` |
| Vector reprojection | ✅ | `VectorTranslateOptions.TargetSrs`, `Geometry.TransformTo`, `VectorData.Load(layer, target)` |
| Datum transformation | ✅ | `CoordinateTransformation` + `TransformationOptions` (AOI, pipelines, OnlyBest) |
| Coordinate conversion | ✅ | `Transform`, `TransformInPlace`, `TransformBounds` |
| EPSG, WKT, PROJJSON | ✅ | `SpatialReference.FromEpsg/FromWkt/FromProjJson`, `ToWkt`, `ToProjJson` |

## 3. Raster processing

| Feature | Status | API |
|---|---|---|
| Resampling: nearest, bilinear, cubic, cubic spline, Lanczos, average, mode (+ gauss, RMS) | ✅ | `ResampleAlgorithm` in reads, overviews, warp, translate, VRT |
| Warping, orthorectification, georeferencing | ✅ | `GdalUtils.Warp` with `WarpOptions` (`Rpc`, `RpcDem`, `ThinPlateSpline`, `PolynomialOrder`), `Dataset.SetGcps` |
| Mosaicking | ✅ | `GdalUtils.BuildVrt`, `Warp` with several sources, `WarpInto` |
| Clipping | ✅ | `WarpOptions.Cutline*` + `CropToCutline`, `TranslateOptions.ProjectionWindow/SourceWindow` |
| Band math | ✅ | `BandMath` |
| NDVI, NDWI | ✅ | `SpectralIndices.Ndvi/Ndwi` (+ MNDWI, NDBI, NBR, SAVI, EVI) |

## 4. Vector processing

| Feature | Status | API |
|---|---|---|
| Buffer, union, intersection, difference, symdifference | ✅ | `Geometry.Buffer/Union/Intersection/Difference/SymDifference`, `Layer.Overlay` |
| Dissolve | ✅ | `Geometry.UnionAll`, `UnaryUnion`, `UnionCascaded`, `Layer.Dissolve` |
| Simplify | ✅ | `Simplify`, `SimplifyPreserveTopology`, `VectorTranslateOptions.Simplify` |
| Convex hull, centroid | ✅ | `ConvexHull`, `ConcaveHull`, `Centroid`, `PointOnSurface` |
| Spatial join | 🟢 | SQLite dialect (`ST_Intersects` joins), `Layer.Overlay(Identity/Intersection)` |
| Attribute join | 🟢 | SQL `JOIN` (OGR SQL or SQLite dialect) |

## 5. SQL engine

| Feature | Status | API |
|---|---|---|
| Filtering, sorting, join, aggregation, attribute querying | ✅ | `Dataset.ExecuteSql` (OGR SQL / `dialect: "SQLITE"`), `Layer.SetAttributeFilter` |
| Spatial filtering | ✅ | `ExecuteSql(spatialFilter: …)`, `Layer.SetSpatialFilter`, SpatiaLite functions |

## 6. Virtual raster (VRT)

| Feature | Status | API |
|---|---|---|
| Virtual mosaic | ✅ | `GdalUtils.BuildVrt` |
| Band stacking | ✅ | `BuildVrtOptions { Separate = true }` |
| On-the-fly transformation | ✅ | `WarpOptions { Format = "VRT" }` (used by `TileGenerator`) |
| No data duplication | ✅ | VRTs reference their sources |

## 7. Cloud and big data

| Feature | Status | API |
|---|---|---|
| `/vsis3/`, `/vsiaz/`, `/vsigs/`, `/vsicurl/`, `/vsizip/`, `/vsitar/` | ✅ | `VirtualFileSystem` path builders and credential helpers, `VsiStream` |
| Amazon S3, Azure Blob, Google Cloud Storage, HTTPS, FTP | ✅ | `ConfigureS3/ConfigureAzure/ConfigureGoogleCloud`, `Gdal.SetPathSpecificOption` |

## 8. Multidimensional data

| Feature | Status | API |
|---|---|---|
| NetCDF, HDF5, climate datasets, data cubes, temporal dimensions | ✅ | `Gdal.OpenMultidimensional`, `MdGroup`, `MdArray`, `MdDimension`, `AsClassicDataset`, `GdalUtils.MultiDimInfo` |

## 9. Metadata

| Feature | Status | API |
|---|---|---|
| EXIF, XMP, GeoTIFF tags, RPC, GCP, domain metadata | ✅ | `GetMetadata("EXIF")`, `GetMetadataList("xml:XMP")`, `GetMetadata("RPC")`, `Gcps`, `SetGcps`, `MetadataDomains` |

## 10. Spatial reference systems

| Feature | Status | API |
|---|---|---|
| EPSG database | ✅ | `SpatialReference.FromEpsg`, `CrsDatabase.GetAll/Search/At` |
| WKT1, WKT2, PROJJSON, ESRI WKT | ✅ | `ToWkt(WktFormat…)`, `ToProjJson`, `ToEsriWkt`, `FromEsri` |
| Custom CRS | ✅ | `FromProj4`, `FromWkt`, `FromUserInput` |

## 11. Command line utilities

| Utility | Status | API |
|---|---|---|
| gdalinfo, gdal_translate, gdalwarp, gdaldem, gdalbuildvrt, gdal_rasterize | ✅ | `GdalUtils.Info/Translate/Warp/DemProcessing/BuildVrt/Rasterize` |
| gdal_contour | ✅ | `RasterAlgorithms.GenerateContours` |
| gdal_merge.py | ✅ | `GdalUtils.Warp(sources…)` or `BuildVrt` + `Translate` |
| ogrinfo, ogr2ogr | ✅ | `GdalUtils.VectorInfo`, `GdalUtils.VectorTranslate` |
| ogrmerge.py | ✅ | `VectorTranslate` with several sources or `VectorTranslateOptions.Append` |
| gdal (3.11+ unified CLI) | ✅ | `GdalCli.Run` |

## 12. Programming APIs

GDAL.Net is a modern C# binding over the GDAL C API (independent of the official SWIG bindings), with Blazor and Avalonia component packages on top.

## 13. Advanced GIS

| Feature | Status | API |
|---|---|---|
| Georeferencing, GCP, RPC transformations | ✅ | `SetGcps`, `WarpOptions` (`PolynomialOrder`, `ThinPlateSpline`, `Rpc`, `RpcDem`) |
| Terrain analysis, DEM processing, hillshade | ✅ | `GdalUtils.DemProcessing`, `RenderOptions.Hillshade` |
| Contour generation | ✅ | `RasterAlgorithms.GenerateContours` |
| Viewshed | ✅ | `RasterAlgorithms.Viewshed` |
| Polygonization / raster vectorization | ✅ | `RasterAlgorithms.Polygonize`, `Sieve` |
| Tile generation | ✅ | `TileGenerator`, `GdalCli.Run("raster", "tile", …)` |

## 14. Enterprise use cases

| Area | GDAL.Net building blocks |
|---|---|
| Data integration (200+ formats) | `Gdal.Open`, `Gdal.Drivers` |
| Spatial ETL | `GdalUtils.VectorTranslate`, `GdalUtils.Translate`, `GdalCli` pipelines |
| Cloud storage | `VirtualFileSystem` |
| Analytics | `BandMath`, `SpectralIndices`, `DemProcessing`, `Kernels` |
| Visualization | `RasterRenderer`, `TileGenerator`, Blazor/Avalonia components |
| AI/ML pre-processing | Zero-copy `Read<T>` into tensors, `ReadResampled`, `Warp` to a common grid |
| Big data | VRT, `/vsicurl/` streaming, overview-aware reads, `ProcessRows` |
| Databases | PostGIS / MSSQL drivers, `ExecuteSql`, transactions |
| CRS | `SpatialReference`, `CoordinateTransformation`, `CrsDatabase` |
| Automation | `GdalUtils`, `GdalCli`, progress and cancellation |

**Next:** [Troubleshooting](troubleshooting.md)
