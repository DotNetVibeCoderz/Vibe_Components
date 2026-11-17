# Data vektor, CRS & IO

## Model data

- **`Column<T>`**: buffer bertipe yang immutable (`double`, `long`, `bool`, `string?`, `DateTime`, geometri) dengan bitmap validitas untuk tipe value. Gunakan `Column<long>.FromNullable` bila perlu nilai null.
- **`DataFrame`**: kumpulan kolom berpanjang sama. Setiap operasi menghasilkan frame baru, dan proyeksi kolom (`Select`/`Drop`/`Rename`) berbagi buffer sehingga tidak menyalin data.
- **`GeoSeries`**: kolom geometri yang memiliki `Crs` dan indeks STR-tree (`SIndex`) yang dibangun saat pertama dipakai.
- **`GeoDataFrame`**: `DataFrame` dengan kolom geometri aktif.

```csharp
var teratas = kecamatan
    .Filter<double>("green_ratio", g => g > 0.2)
    .WithColumn<double>("kepadatan", r => r.GetDouble("population") / r.GetDouble("area_km2"))
    .SortBy("kepadatan", ascending: false)
    .Head(10);

var perWilayah = kecamatan.GroupBy("region").Agg(("population", AggFunc.Sum, "penduduk"));
Console.WriteLine(kecamatan.Describe());
```

## Spatial join & overlay

```csharp
var s = sekolah.SJoin(kecamatan, SpatialPredicate.Within, JoinHow.Left);             // kolom index_right
var n = sekolah.SJoinNearest(rumahSakit, maxDistance: 5000, distanceColumn: "jarak_m");
var irisan  = tataGuna.Overlay(zonaBanjir, OverlayHow.Intersection);
var wilayah = kecamatan.Dissolve("region", AggFunc.Sum);
var potong  = poi.Clip(Geo.Box(700000, 9308000, 704000, 9312000));
var bagian  = wilayah.Explode();
```

Semua operasi di atas memakai STR-tree, prepared geometry dan overlay yang robust (OverlayNG). Bila CRS kedua data berbeda, operasi melempar exception, bukan diam-diam menghasilkan angka yang salah.

## Sistem koordinat (CRS)

```csharp
var tm3  = CoordinateReferenceSystem.Parse("EPSG:23834");                        // DGN95 / TM-3 zona 48.2
var utm  = CoordinateReferenceSystem.Parse("+proj=utm +zone=48 +south +datum=WGS84");
var prj  = CoordinateReferenceSystem.Parse(File.ReadAllText("persil.prj"));       // ESRI / OGC WKT / WKT2
var t    = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, tm3);
var (x, y) = t.Transform(106.8456, -6.2088);
```

| Fitur | Detail |
|---|---|
| Proyeksi | Transverse Mercator (UTM, TM-3, BNG; selisih < 1 mm terhadap deret Krüger di dalam zona), Mercator, Web Mercator, Lambert Conformal Conic, Albers |
| Datum | WGS 84, GRS80, Clarke 1866, International 1924, Airy, DGN95; Helmert 7 parameter |
| Registri | 4326, 4269, 4258, 4267, 4230, 4755, 3857, 3395, 2154, 5070, 27700, 23830–23845 (TM-3), 326xx/327xx (UTM), 269xx; tambah sendiri via `CrsRegistry.Register` |

## Format file

| Format | Baca | Tulis | Catatan |
|---|---|---|---|
| GeoJSON | ✔ | ✔ | Orientasi ring RFC 7946 |
| Shapefile | ✔ | ✔ | .shp/.shx/.dbf/.prj/.cpg, nilai Z, hole, UTF-8 |
| GeoPackage | ✔ | ✔ | Banyak layer (`GeoPackage.ListLayers`) |
| CSV | ✔ | ✔ | Kolom WKT atau lon/lat (dideteksi otomatis) |
| DXF | ✔ | ✔ | Melalui `GeoPandas.Net.CAD` |

## Membuat peta

```csharp
var peta = new MapScene { Title = "Cakupan layanan", ShowFrame = true, Padding = 46 }
    .AddLayer(kecamatan, new LayerStyle { Column = "population", ColorMap = ColorMaps.YlOrRd, Scheme = ClassificationScheme.NaturalBreaks })
    .AddLayer(halte, new LayerStyle { Fill = "#b0306a", LabelColumn = "name" });
peta.SaveSvg("cakupan.svg", 1200, 800);
```
