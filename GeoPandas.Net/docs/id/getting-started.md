# Memulai

## Instalasi

```bash
dotnet new console -n AplikasiGis && cd AplikasiGis
dotnet add package GeoPandas.Net
```

Atau gunakan template:

```bash
dotnet new install GeoPandas.Net.Templates
dotnet new gpd-console  -n Analisis       # skrip analisis
dotnet new gpd-blazor   -n PetaWeb        # aplikasi peta Blazor Server
dotnet new gpd-avalonia -n PetaDesktop    # aplikasi peta desktop Avalonia
```

## GeoDataFrame pertama

```csharp
using GeoPandas.Net;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;

var kota = GeoDataFrame.FromGeometries(
    [Geo.Point(106.8456, -6.2088), Geo.Point(107.6191, -6.9175), Geo.Point(112.7521, -7.2575)],
    CoordinateReferenceSystem.Wgs84,
    Column.Create("nama", new[] { "Jakarta", "Bandung", "Surabaya" }),
    Column.Create("penduduk_juta", new[] { 34.5, 8.6, 10.0 }));

Console.WriteLine(kota);          // tabel + CRS
Console.WriteLine(kota.Info());   // skema kolom
```

Dari tabel CSV yang berisi koordinat:

```csharp
var df  = GeoPandas.Net.Data.Csv.ReadDataFrame("stasiun.csv");   // lon, lat, nama, ...
var gdf = GeoDataFrame.FromXY(df, "lon", "lat", CoordinateReferenceSystem.Wgs84);
```

## Baca, transformasi, tulis

```csharp
var jalan = Gpd.ReadFile("jalan.shp");                 // CRS otomatis dari .prj
var utm   = jalan.ToCrs(jalan.EstimateUtmCrs());       // mis. WGS 84 / UTM 48S untuk Jakarta
var utama = utm.Filter<string?>("highway", h => h is "primary" or "secondary");
Console.WriteLine($"Panjang jalan utama: {utama.GeomLength().Sum() / 1000:F1} km");
utama.ToFile("jalan_utama.gpkg");
utama.ToCrs(4326).ToFile("jalan_utama.geojson");
```

Untuk data resmi Indonesia (BIG), CRS DGN95 / TM-3 tersedia langsung, misalnya `CoordinateReferenceSystem.FromEpsg(23834)` untuk zona 48.2.

## Peta pertama

```csharp
using GeoPandas.Net.Plotting;

var peta = utm.Plot(new LayerStyle { Column = "highway", Categorical = true, LegendTitle = "Kelas jalan" }, "Jaringan jalan");
peta.ShowFrame = true;               // bingkai lembar peta dengan tick koordinat
peta.SaveSvg("jalan.svg", 1000, 700);
```

## Data contoh

`GeoPandas.Net.Datasets.SampleData` menyediakan data sintetis yang reproducible untuk kota fiktif di sekitar Jakarta (EPSG:32748): `Districts()`, `Roads()`, `PointsOfInterest()`, `WaterNetwork()`, `Dem()`, `Multispectral()`, `SitePlanDxf()`, serta `Cities()` (kota dunia, WGS 84).

Lanjut ke: [Data vektor, CRS & IO](vector.md) · [Analisis](analysis.md)
