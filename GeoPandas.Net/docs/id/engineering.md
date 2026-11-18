# Teknik, BIM & ekonometrika spasial

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [🇬🇧 English](../en/engineering.md)

Fase 4 (v0.4) menambahkan empat kemampuan rekayasa:

- **Jaringan utilitas:** hidraulika kompatibel EPANET, aturan topologi dan isolasi katup (di `GeoPandas.Net.Network`).
- **Desain sipil:** alinyemen, koridor, galian-timbunan dan pemecahan persil (`GeoPandas.Net.Civil`).
- **BIM:** IFC ⇄ GIS (`GeoPandas.Net.BIM`).
- **CAD:** DWG dan DGN (`GeoPandas.Net.CAD`).

Fase ini juga menambahkan regresi spasial dan GWR ke `GeoPandas.Net.Analytics`.

Setiap hasil di halaman ini diverifikasi terhadap implementasi independen, dan nilai rujukannya ada di `tests/GeoPandas.Net.Tests/data`:

| Fitur | Rujukan | Kesesuaian |
|---|---|---|
| OLS, diagnostik LM, ML lag / error | PySAL spreg 1.9 | 1e-9 (OLS), 1e-6 (ML) |
| GWR (bandwidth, estimasi lokal, AICc) | PySAL mgwr 2.2 | bandwidth sama; estimasi hingga 1e-15 |
| Kriging universal | PyKrige 1.7 | 1e-12 |
| Hidraulika (Net1, Net3, SI Darcy-Weisbach) | EPANET 2.2 (lewat WNTR) | head dalam 0,05 mm; status link identik |
| Geometri IFC, georeferensi, properti | IfcOpenShell 0.9 (OpenCascade) | 1e-15 m |
| DWG | ezdxf pada gambar yang sama dalam DXF | jumlah entitas dan teks identik; koordinat 2e-13 |
| DGN v7 | GDAL 3.12, dua arah | persis (koordinat integer) |

## Regresi spasial

```csharp
var w   = SpatialWeights.Queen(kecamatan).RowStandardize();
var ols = SpatialRegression.Ols(kecamatan, "pendapatan", ["rasio_hijau", "kepadatan"], w);   // + LM-lag, LM-error, robust, SARMA
var lag = SpatialRegression.SpatialLag(kecamatan, "pendapatan", ["rasio_hijau", "kepadatan"], w);
var err = SpatialRegression.SpatialError(kecamatan, "pendapatan", ["rasio_hijau", "kepadatan"], w);
```

- **Estimasi:** model ML memakai likelihood terkonsentrasi; log|I − ρW| dihitung dari nilai eigen W (bobot simetris atau simetris yang distandardisasi baris), dan W lainnya memakai dekomposisi LU. Standard error berasal dari matriks informasi penuh, seperti `method="full"` di spreg.
- **Batas ukuran:** model ML membangun matriks padat n × n, jadi ditujukan untuk hingga beberapa ribu observasi.
- **Konvensi AIC:** seperti GeoDa dan spreg, AIC model error hanya menghitung β (tanpa λ).

## Regresi terboboti geografis (GWR)

```csharp
var gwr = Gwr.Fit(titik, "y", ["x1", "x2"]);   // bisquare adaptif, bandwidth golden-section dengan AICc
gwr.AddTo(titik).ToFile("gwr.gpkg");          // b_x1, se_x1, t_x1, local_r2 per lokasi
```

## Kriging universal

```csharp
var (z, varians) = Interpolation.UniversalKriging(sx, sy, sz, qx, qy, variogram, KrigingDrift.Linear);
```

`KrigingDrift.Quadratic` menambahkan suku x², xy dan y². Tanpa variogram, variogram dicocokkan pada residu permukaan tren OLS.

## Jaringan utilitas

### Hidraulika (kompatibel EPANET)

```csharp
var net = WaterNetwork.ReadInp("kota.inp");          // atau WaterNetwork.FromGeoDataFrames(pipa, junction, reservoir)
var hasil = Hydraulics.Solve(net);                   // kondisi tunak, algoritma gradien global (seperti EPANET 2)
hasil.NodesFrame().ToFile("tekanan.gpkg");
var gangguan = Hydraulics.Solve(net, new HydraulicOptions { ClosedLinks = ["P-17", "P-18"] });
```

**Didukung:**

- Rumus Hazen-Williams, Darcy-Weisbach dan Chezy-Manning, dengan kehilangan minor.
- Katup searah.
- Pompa: kurva 1, 3 dan banyak titik, daya konstan, dan kecepatan.
- Katup buka, tutup dan TCV.
- Pola pada langkah tertentu.
- Kontrol sederhana pada waktu 0.

**Belum didukung:** PRV/PSV/FCV/PBV dan simulasi periode panjang.

### Aturan topologi & isolasi katup

```csharp
var masalah = NetworkTopology.Validate(pipa, tolerance: 0.001, snapDistance: 1.0, sources: sumber);
var iso = new ValveIsolation(net, ValveIsolation.SnapValves(net, titikKatup));
var r = iso.Isolate("P-104");   // katup yang ditutup, pipa terisolasi, isolasi tak sengaja, kebutuhan air terdampak
```

## Teknik sipil

```csharp
var h = new HorizontalAlignment(pi, radii: [380, 320], spiralLengths: [70, 60]);   // tangen, lingkaran, klotoid
var v = new VerticalAlignment([new Pvi(0, 100), new Pvi(1000, 112, CurveLength: 200), new Pvi(2000, 105)]);
var koridor = new Corridor(h, v, CorridorTemplate.TwoLaneRoad(), dem);
koridor.Volumes(20, cutFactor: 0.9);      // luas & volume galian/timbunan (average end area), ordinat mass haul
Earthwork.Volume(dem, koridor.DesignSurface(20));
Subdivision.Split(persil, parts: 6);      // kavling luas sama, tegak lurus muka jalan
```

## BIM: IFC ⇄ GIS

```csharp
var model = IfcModel.Read("gedung.ifc");       // IFC2x3, IFC4, IFC4x3
model.ElementsFrame().ToFile("elemen.gpkg");   // footprint dalam koordinat peta, rentang z, volume, semua Pset/Qto
model.BuildingFootprint();                     // fitur LOD1 untuk model kota
IfcWriter.WriteBuildings(bangunan, "kota.ifc", heightColumn: "tinggi");   // footprint GIS → IFC4 tergeoreferensi
```

Georeferensi berasal dari `IfcMapConversion`, `ePSet_MapConversion`, atau lintang/bujur IfcSite yang diproyeksikan ke UTM.

## CAD: DWG dan DGN

```csharp
var dwg = Dwg.Read("tapak.dwg");      // R14 … 2018 lewat ACadSharp (MIT); blok dan hatch dipecah
var dgn = Dgn.Read("survei.dgn");     // MicroStation v7; v8 (kontainer OLE) belum didukung
Gpd.ReadFile("rencana.dwg"); gdf.ToFile("rencana.dgn");
```
