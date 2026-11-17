# Dokumentasi GeoPandas.Net

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [🇬🇧 English](../en/index.md)

| Panduan | Isi |
|---|---|
| [Memulai](getting-started.md) | Instalasi, GeoDataFrame pertama, baca/tulis file, peta pertama |
| [Data vektor, CRS & IO](vector.md) | `DataFrame`/`GeoSeries`/`GeoDataFrame`, spatial join, overlay, dissolve, engine CRS, format file, peta |
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
