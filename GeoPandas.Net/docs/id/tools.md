# Tools: CLI, galeri, template, benchmark

## CLI `gpd`

```bash
dotnet tool install -g GeoPandas.Net.Cli
gpd info kecamatan.gpkg
gpd convert kecamatan.gpkg kecamatan.geojson --to-crs EPSG:4326
gpd sjoin poi.geojson kecamatan.gpkg hasil.gpkg --predicate within
gpd dissolve kecamatan.gpkg wilayah.shp --by region --agg sum
gpd plot kecamatan.gpkg peta.svg --column population --cmap YlOrRd --scheme jenks
gpd route jalan.shp --from 700000,9310000 --to 705000,9315000 --cost travel_tim --out rute.geojson
gpd raster hillshade dem.tif hillshade.tif
gpd raster contours dem.tif kontur.geojson --interval 50
gpd crs EPSG:23834
gpd sample dem dem.tif
gpd ask kecamatan.gpkg "Berapa total penduduk per wilayah?"
gpd convert kecamatan.gpkg kecamatan.parquet            # GeoParquet, .fgb, .feather juga bisa
gpd scan jalan.parquet --bbox 700000,9308000,704000,9312000 --where "highway=primary" --select name --explain
gpd las info tile.laz --classes
gpd las ndsm tile.las tinggi.tif --cell 1
gpd netcdf info era5.nc
gpd repo init persil.repo --key id_persil
gpd repo commit persil.repo persil.gpkg --m "impor awal"
gpd repo diff persil.repo --out perubahan.geojson
gpd crs search "TM-3 zone 48"                           # cari dataset EPSG
gpd crs at 106.8 -6.2                                   # CRS yang berlaku di Jakarta
gpd geoid 106.8 -6.2 --model InaGeoid2020.isg --h 58.4  # undulasi N dan tinggi ortometrik
gpd survey traverse kaki.csv --start 1000,1000 --backsight 270 --close-on 1000,1000
gpd survey adjust jaring.txt --out elips.geojson        # perataan kuadrat terkecil
gpd gnss spp site0010.24o brdc0010.24n --out lintasan.geojson
gpd copc info awan.copc.laz
gpd s2 ndvi S2B_MSIL2A_….SAFE ndvi.tif
```

Jalankan `gpd help` untuk melihat semua perintah. Catatan: nama kolom pada Shapefile dibatasi 10 karakter (batasan format DBF), misalnya `travel_time_s` menjadi `travel_tim`.

## Aplikasi galeri

- **Blazor**: `dotnet run --project gallery/GeoPandas.Net.Gallery.Blazor`, lalu buka `http://localhost:5280` (contoh tautan: `/sample/route?lang=id`).
- **Avalonia**: `dotnet run --project gallery/GeoPandas.Net.Gallery.Avalonia -- --lang id`. Tambahkan `--screenshot hasil.png` untuk mengekspor tangkapan layar.

Kedua galeri merender dari `GalleryCatalog` yang sama. Untuk menambah contoh, tambahkan `GallerySample` beserta judul/ringkasan dwibahasa, potongan kode dan fungsi `Build`.

## Template

```bash
dotnet new install GeoPandas.Net.Templates
dotnet new gpd-console  -n Analisis
dotnet new gpd-blazor   -n PetaWeb
dotnet new gpd-avalonia -n PetaDesktop
```

## Benchmark

```bash
dotnet run -c Release --project benchmarks/GeoPandas.Net.Benchmarks -- --filter "*"
```

Hasil pengukuran terbaru ada di [Progress.md](../../Progress.md).
