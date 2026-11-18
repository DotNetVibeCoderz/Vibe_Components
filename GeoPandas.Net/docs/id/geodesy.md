# Proyeksi, geodesi, survei & produk satelit

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [🇬🇧 English](../en/geodesy.md)

Fase 3 (v0.3) melengkapi engine CRS dan menambahkan geodesi serta survei:

- dataset EPSG lengkap;
- proyeksi umum yang belum tersedia;
- pergeseran datum berbasis grid NTv2 dan model geoid;
- hitungan poligon dan perataan jaring kuadrat terkecil;
- pengolahan GNSS dari berkas RINEX.

v0.3 juga menyertakan pembaca produk satelit (`GeoPandas.Net.RemoteSensing`). Semua hasil di halaman ini diverifikasi terhadap PROJ 9.8 / pyproj, dan nilai rujukannya ada di `tests/GeoPandas.Net.Tests/data`.

## Proyeksi

| Metode | Metode EPSG | Nama PROJ | Contoh CRS |
|---|---|---|---|
| Transverse Mercator (deret Krüger orde 6) | 9807 | `tmerc`, `utm` | UTM, TM-3 Indonesia, NZTM |
| Transverse Mercator (South Orientated) | 9808 | – | Lo Afrika Selatan |
| Lambert Conformal Conic 1SP / 2SP | 9801 / 9802 | `lcc` | |
| Albers Equal Area | 9822 | `aea` | |
| Mercator A/B, Web Mercator | 9804 / 9805 / 1024 | `merc` | EPSG:3857 |
| **Lambert Azimuthal Equal-Area** (elipsoid dan bola) | 9820 / 1027 | `laea` | EPSG:3035 (Eropa), 9311 |
| **Polar Stereographic** A/B, UPS | 9810 / 9829 | `stere`, `ups` | EPSG:3031, 3413, 32661 |
| **Oblique Stereographic** | 9809 | `sterea` | EPSG:28992 (RD New) |
| **Hotine Oblique Mercator** A/B (RSO, Swiss) | 9812 / 9815 | `omerc`, `somerc` | EPSG:3375 (RSO Malaysia), 2056 |
| **Cassini-Soldner** | 9806 | `cass` | EPSG:3168, 28191 |
| **Equidistant Cylindrical** | 1028 / 1029 | `eqc` | EPSG:4087 |
| **Cylindrical Equal-Area** | 9835 | `cea` | EPSG:6933 (EASE-Grid 2) |
| **Sinusoidal**, **Mollweide**, **Robinson** | – | `sinu`, `moll`, `robin` | MODIS, World Mollweide / Robinson (ESRI .prj WKT) |
| **Equal Earth** | 1078 | `eqearth` | EPSG:8857 |

Semua proyeksi dapat dibaca dan ditulis sebagai kode EPSG, string PROJ, WKT1/ESRI, WKT2 dan PROJJSON. Hasil maju dan balik cocok dengan PROJ hingga nanometer.

```csharp
var europe = gdf.ToCrs(3035);                                         // LAEA Eropa
var rso    = CoordinateReferenceSystem.FromEpsg(3375);                // Hotine varian A
var world  = cities.ToCrs("+proj=eqearth +datum=WGS84");
Console.WriteLine(rso.ToWkt());
```

## Dataset EPSG

`EpsgDatabase` menyematkan 5 804 CRS geografis dan terproyeksi dari EPSG v12.029 sebagai tabel terkompresi 173 KB. Setiap entri memuat:

- datum beserta transformasi Helmert ke WGS 84 dengan cakupan wilayah terluas;
- parameter proyeksi;
- wilayah penggunaan (area of use).

Dari 5 217 kode terproyeksi, 5 216 sesuai dengan PROJ dengan selisih di bawah 1 mm. Satu-satunya pengecualian adalah Palestine Cassini (EPSG:28191), dengan selisih 9 mm.

```csharp
var crs  = CoordinateReferenceSystem.FromEpsg(23834);                 // DGN95 / Indonesia TM-3 zone 48.2
var hits = CrsRegistry.Search("Indonesia TM-3");                      // cari berdasarkan nama
var here = CrsRegistry.AtLocation(106.8, -6.2);                        // CRS yang berlaku di Jakarta
```

Untuk membuat ulang tabel dari rilis PROJ yang lebih baru, jalankan `python tools/epsg/generate_epsg.py path/ke/proj.db`.

## Pergeseran datum berbasis grid (NTv2)

```csharp
var grid = HorizontalGridShift.LoadNtv2("ntv2_0.gsb");               // sub-grid, bilinear, invers iteratif
GridCatalog.Register("ntv2_0.gsb", grid);                              // atau simpan di GEOPANDAS_NET_GRIDS / PROJ_DATA
var nad27 = CoordinateReferenceSystem.Parse("+proj=longlat +ellps=clrk66 +nadgrids=ntv2_0.gsb");
```

Hasil maju dan balik sesuai dengan PROJ `hgridshift` dengan selisih di bawah 10 µm. Grid NADCON `.las/.los` belum didukung.

## Geoid dan tinggi ortometrik

Receiver GNSS mengukur tinggi elipsoid *h*. Peta dan gambar teknik memakai tinggi ortometrik *H = h − N*, dengan *N* adalah undulasi geoid.

```csharp
var geoid = GeoidModel.Load("InaGeoid2020.isg");  // .gtx (PROJ), .isg (ISG / InaGeoid / EGM2008), .tif (dengan Geoids.Register())
double n = geoid.Undulation(106.8456, -6.2088);
double H = geoid.OrthometricHeight(106.8456, -6.2088, ellipsoidalHeight: 58.4);

// CRS gabungan: nilai z adalah tinggi ortometrik; transformasi menambah/mengurangi N otomatis
var utmH = CoordinateReferenceSystem.FromEpsg(32748).WithGeoid(geoid);
```

- **Model bawaan:** `GeoidModel.Egm96Coarse` adalah grid EGM96 1° (RMS sekitar 0,44 m terhadap model penuh). Cukup untuk pratinjau; untuk pekerjaan survei, muat berkas EGM2008 atau InaGeoid resolusi penuh dari NGA / BIG.
- **String PROJ:** `+geoidgrids=` dipahami, termasuk alias `egm96_coarse`.
- **Akurasi:** hasil GTX identik dengan PROJ `vgridshift`.

## Survei

### Poligon dan COGO

```csharp
var t = Traverse.Compute("BM1", 1000, 1000, backsightAzimuth: 270, legs,
                         closeOn: (1000, 1000), adjustment: TraverseAdjustment.Bowditch);
Console.WriteLine($"sudut {t.AngularMisclosureSeconds:F1}\"  linier {t.LinearMisclosure:F3} m  1:{t.PrecisionRatio:F0}");
var p  = Cogo.BearingBearing(e1, n1, az1, e2, n2, az2);                // perpotongan arah
var rs = Cogo.Resection(a, b, c, dirA, dirB, dirC);                      // reseksi Tienstra
```

### Perataan jaring kuadrat terkecil

```csharp
var net = new AdjustmentNetwork()
    .AddFixed("BM1", 1000, 1000).AddFixed("BM2", 1600, 1050)
    .AddStation("P1", 1290, 1260)
    .Distance("BM1", "P1", 390.514, sigma: 0.003)
    .Azimuth("BM1", "P1", 50.19444, sigmaArcSec: 2)
    .Distance("BM2", "P1", 360.553, sigma: 0.003);
var r = net.Adjust2D();                                   // σ̂0², uji global χ², deteksi outlier
r.ErrorEllipses(scale: 1000).ToFile("elips.geojson");      // elips kesalahan 95 %
```

Perataan bidang memakai Gauss-Newton dengan Cholesky dan mendukung jarak, arah (dengan unknown orientasi per set), sudut, azimut, koordinat dan baseline GNSS. Laporannya memuat:

- faktor variansi aposteriori dan uji global χ²;
- bilangan redundansi;
- data snooping Baarda (|w| > 3,29);
- elips kesalahan 95 %.

`AdjustLeveling()` meratakan jaring sipat datar, dan `AdjustmentNetwork.LevelingSigma(mmPerAkarKm, km)` memberi simpangan baku beda tinggi hasil sipat datar.

### GNSS: RINEX dan penentuan posisi absolut

```csharp
var obs = RinexObservation.Read("site0010.24o");                         // RINEX 2.x / 3.x
var nav = RinexNavigation.Read("brdc0010.24n");                          // efemeris siaran GPS, Klobuchar
var track = Gnss.SinglePointPositioning(obs, nav, new SppOptions { ElevationMask = 15 });
```

Penentuan posisi absolut mencakup:

- model orbit dan jam IS-GPS-200;
- koreksi ionosfer Klobuchar dan troposfer Saastamoinen;
- koreksi Sagnac;
- kuadrat terkecil berbobot elevasi;
- deteksi dan pengecualian satelit bermasalah.

Pada berkas RINEX nyata yang disertakan, posisi hasil hitungan berada dalam 2–5 m dari posisi receiver.

## Produk satelit (`GeoPandas.Net.RemoteSensing`)

```csharp
var scene = LandsatScene.Open("LC09_L2SP_…");          // Landsat Collection 2 (MTL .txt/.json)
var ndvi  = scene.Ndvi();                               // reflektansi terskala, awan/bayangan QA_PIXEL di-mask
var s2    = Sentinel2Product.Open("S2B_MSIL2A_….SAFE"); // Sentinel-2 L1C/L2A, JPEG 2000 via CoreJ2K
var red   = s2.Reflectance("B04", 10);                  // offset baseline ≥ 04.00 dan kuantifikasi diterapkan
var item  = StacItem.Load("item.json");                 // item STAC dan asetnya
var (h, v) = ModisGrid.TileFor(106.8, -6.2);            // tile sinusoidal MODIS/VIIRS (28, 9)
var refl  = ModisGrid.ReadHdfEos5("VNP09GA….h5", "SurfReflect_I1_1");
```

## Tambahan format v0.3

- **LAZ 1.4 dan COPC:** LAZ untuk format titik LAS 1.4 6–10 memakai codec native `laz`. Berkas COPC dapat dikueri per kedalaman dan bbox lewat `Copc.Read(path, bbox, maxDepth)`.
- **GeoArrow native di GeoParquet:** tulis dengan `GeoParquetEncoding.Native`; berkas native dari geopandas terbaca otomatis.
- **NetCDF:**
  - `ReadWindow` membaca hyperslab dan hanya mendekode chunk yang tersentuh.
  - `CurvilinearCoordinates`, `ToPoints` dan `Regrid` menangani grid kurvilinier 2-D.
- **CI basis data langsung:** PostGIS dan Oracle Free berjalan sebagai container layanan di job CI `sql-services`.
