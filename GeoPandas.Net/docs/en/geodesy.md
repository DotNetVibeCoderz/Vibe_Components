# Projections, geodesy, surveying & satellite products

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · Made by Gravicode Studios, led by Kang Fadhil · [🇮🇩 Bahasa Indonesia](../id/geodesy.md)

Phase 3 (v0.3) completes the CRS engine and adds geodesy and surveying:

- the full EPSG dataset;
- the remaining common projections;
- NTv2 grid datum shifts and geoid models;
- traverse and least-squares network adjustment;
- GNSS processing from RINEX files.

v0.3 also ships the satellite product readers (`GeoPandas.Net.RemoteSensing`). Every result on this page was checked against PROJ 9.8 / pyproj, and those reference values are in `tests/GeoPandas.Net.Tests/data`.

## Projections

| Method | EPSG method | PROJ name | Typical CRS |
|---|---|---|---|
| Transverse Mercator (Krüger 6th-order series) | 9807 | `tmerc`, `utm` | UTM, TM-3 Indonesia, NZTM |
| Transverse Mercator (South Orientated) | 9808 | – | South Africa Lo |
| Lambert Conformal Conic 1SP / 2SP | 9801 / 9802 | `lcc` | |
| Albers Equal Area | 9822 | `aea` | |
| Mercator A/B, Web Mercator | 9804 / 9805 / 1024 | `merc` | EPSG:3857 |
| **Lambert Azimuthal Equal-Area** (ellipsoidal and spherical) | 9820 / 1027 | `laea` | EPSG:3035 (Europe), 9311 |
| **Polar Stereographic** A/B, UPS | 9810 / 9829 | `stere`, `ups` | EPSG:3031, 3413, 32661 |
| **Oblique Stereographic** | 9809 | `sterea` | EPSG:28992 (RD New) |
| **Hotine Oblique Mercator** A/B (RSO, Swiss) | 9812 / 9815 | `omerc`, `somerc` | EPSG:3375 (RSO Malaysia), 2056 |
| **Cassini-Soldner** | 9806 | `cass` | EPSG:3168, 28191 |
| **Equidistant Cylindrical** | 1028 / 1029 | `eqc` | EPSG:4087 |
| **Cylindrical Equal-Area** | 9835 | `cea` | EPSG:6933 (EASE-Grid 2) |
| **Sinusoidal**, **Mollweide**, **Robinson** | – | `sinu`, `moll`, `robin` | MODIS, World Mollweide / Robinson (ESRI .prj WKT) |
| **Equal Earth** | 1078 | `eqearth` | EPSG:8857 |

All of them can be read and written as EPSG codes, PROJ strings, WKT1/ESRI, WKT2 and PROJJSON. Forward and inverse results match PROJ to the nanometre.

```csharp
var europe = gdf.ToCrs(3035);                                         // LAEA Europe
var rso    = CoordinateReferenceSystem.FromEpsg(3375);                // Hotine variant A
var world  = cities.ToCrs("+proj=eqearth +datum=WGS84");
Console.WriteLine(rso.ToWkt());                                        // PROJECTION["Hotine_Oblique_Mercator"] …
```

## The EPSG dataset

`EpsgDatabase` embeds 5 804 geographic and projected CRSs from EPSG v12.029 as a 173 KB compressed table. Each entry carries:

- its datum and the most widely applicable Helmert transformation to WGS 84;
- its projection parameters;
- its area of use.

Across 5 216 of the 5 217 projected codes, results agree with PROJ to within 1 mm. The one exception is Palestine Cassini (EPSG:28191), which differs by 9 mm.

```csharp
var crs  = CoordinateReferenceSystem.FromEpsg(23834);                 // DGN95 / Indonesia TM-3 zone 48.2
var hits = CrsRegistry.Search("Indonesia TM-3");                      // name search
var here = CrsRegistry.AtLocation(106.8, -6.2);                        // CRSs whose area of use contains Jakarta
Console.WriteLine($"{crs.AreaOfUse} · {EpsgDatabase.Version}");
```

To regenerate the table from a newer PROJ release, run `python tools/epsg/generate_epsg.py path/to/proj.db`.

## Grid datum shifts (NTv2)

```csharp
var grid = HorizontalGridShift.LoadNtv2("ntv2_0.gsb");               // sub-grids, bilinear, iterative inverse
GridCatalog.Register("ntv2_0.gsb", grid);                              // or put files in GEOPANDAS_NET_GRIDS / PROJ_DATA
var nad27 = CoordinateReferenceSystem.Parse("+proj=longlat +ellps=clrk66 +nadgrids=ntv2_0.gsb");
var t = CoordinateTransformer.Create(nad27, CoordinateReferenceSystem.FromEpsg(4326));
```

Forward and inverse shifts agree with PROJ `hgridshift` to within 10 µm. NADCON `.las/.los` grids are not supported yet.

## Geoids and orthometric heights

A GNSS receiver measures the ellipsoidal height *h*. Maps and engineering drawings use the orthometric height *H = h − N*, where *N* is the geoid undulation.

```csharp
var geoid = GeoidModel.Load("egm2008-1.gtx");   // .gtx (PROJ), .isg (ISG / InaGeoid / EGM2008), .tif/.tiff (with Geoids.Register())
double n = geoid.Undulation(106.8456, -6.2088);
double H = geoid.OrthometricHeight(106.8456, -6.2088, ellipsoidalHeight: 58.4);

// compound CRS: z values are orthometric heights; transformations add/remove N automatically
var utmH = CoordinateReferenceSystem.FromEpsg(32748).WithGeoid(geoid);
var t = CoordinateTransformer.Create(utmH, CoordinateReferenceSystem.Wgs84);
double x = 700_000, y = 9_300_000, z = 100;
t.Transform(ref x, ref y, ref z);                 // z is now the ellipsoidal height
```

- **Embedded model:** `GeoidModel.Egm96Coarse` is a 1° EGM96 grid, about 0.44 m RMS against the full model. It is fine for previews; survey work should load the full-resolution EGM2008 or InaGeoid files from NGA / BIG.
- **PROJ strings:** `+geoidgrids=` is understood, including the alias `egm96_coarse`.
- **Accuracy:** GTX results match PROJ `vgridshift` exactly.

## Surveying

### Traverse and COGO

```csharp
var legs = new List<TraverseLeg>
{
    new("P1", 125.432, AngleRight: Angles.ParseDms("91 12 30")),
    new("P2",  98.110, AngleRight: Angles.ParseDms("268 47 10")),
    new("BM1", 140.871, AngleRight: Angles.ParseDms("90 01 05")),
};
var t = Traverse.Compute("BM1", 1000, 1000, backsightAzimuth: 270, legs,
                         closeOn: (1000, 1000), adjustment: TraverseAdjustment.Bowditch);
Console.WriteLine($"angular {t.AngularMisclosureSeconds:F1}\"  linear {t.LinearMisclosure:F3} m  1:{t.PrecisionRatio:F0}");
t.LegLines().ToFile("traverse.geojson");

var p  = Cogo.BearingBearing(e1, n1, az1, e2, n2, az2);                // intersections
var rs = Cogo.Resection(a, b, c, dirA, dirB, dirC);                      // Tienstra
```

### Least-squares network adjustment

```csharp
var net = new AdjustmentNetwork()
    .AddFixed("BM1", 1000, 1000).AddFixed("BM2", 1600, 1050)
    .AddStation("P1", 1290, 1260)                                       // approximate coordinates
    .Distance("BM1", "P1", 390.514, sigma: 0.003)
    .Azimuth("BM1", "P1", 50.19444, sigmaArcSec: 2)
    .Distance("BM2", "P1", 360.553, sigma: 0.003);
var r = net.Adjust2D();
Console.WriteLine(r);                                   // σ̂0², χ² global test, flagged outliers
r.ErrorEllipses(scale: 1000).ToFile("ellipses.geojson"); // 95 % confidence ellipses
```

The planar adjustment uses Gauss-Newton with Cholesky and supports distances, directions (with an orientation unknown per set), angles, azimuths, coordinate observations and GNSS baselines. Its report includes:

- a posteriori variance factor and χ² global test;
- redundancy numbers;
- Baarda data snooping (|w| > 3.29);
- 95 % error ellipses.

`AdjustLeveling()` adjusts height networks, and `AdjustmentNetwork.LevelingSigma(mmPerSqrtKm, km)` gives the standard deviation of a levelled height difference.

### GNSS: RINEX and single point positioning

```csharp
var obs = RinexObservation.Read("site0010.24o");                         // RINEX 2.x / 3.x
var nav = RinexNavigation.Read("brdc0010.24n");                          // GPS broadcast ephemerides, Klobuchar
var track = Gnss.SinglePointPositioning(obs, nav, new SppOptions { ElevationMask = 15 });
track.ToFile("track.geojson");                                            // lon/lat/h, ECEF, clock, DOPs, satellites
```

Single point positioning implements:

- the IS-GPS-200 orbit and clock model;
- Klobuchar ionosphere and Saastamoinen troposphere delays;
- the Sagnac correction;
- weighted least squares with elevation-dependent weights;
- fault detection and exclusion of a bad satellite.

On the bundled real RINEX file, positions land within 2–5 m of the receiver's surveyed position.

## Satellite products (`GeoPandas.Net.RemoteSensing`)

```csharp
// Landsat Collection 2 (Level-1 TOA or Level-2 surface reflectance)
var scene = LandsatScene.Open("LC09_L2SP_122064_20240110_20240111_02_T1");   // folder or *_MTL.txt / *_MTL.json
var ndvi  = scene.Ndvi();                    // scaled reflectance, QA_PIXEL clouds/shadows masked
var lst   = scene.SurfaceTemperature();      // kelvin

// Sentinel-2 L1C / L2A SAFE (JPEG 2000 decoded with CoreJ2K)
var s2   = Sentinel2Product.Open("S2B_MSIL2A_….SAFE");
var red  = s2.Reflectance("B04", 10);        // baseline ≥ 04.00 offset (−1000) and quantification applied
var mask = s2.ClearMask();                    // SCL: no data, saturated, shadow, cloud, cirrus

// STAC items (Earth Search, Planetary Computer, local catalogues)
var item = await StacItem.LoadAsync(new Uri("https://earth-search.aws.element84.com/v1/collections/sentinel-2-l2a/items/…"));
var nir  = await item.ReadAssetAsync("nir");

// MODIS / VIIRS sinusoidal grid and HDF-EOS5
var (h, v) = ModisGrid.TileFor(106.8, -6.2);                              // (28, 9)
var refl = ModisGrid.ReadHdfEos5("VNP09GA.A2024010.h28v09.002.h5", "SurfReflect_I1_1");
```

## Related v0.3 format additions

- **LAZ 1.4 and COPC:** LAZ for LAS 1.4 point formats 6–10 uses the native `laz` codec. COPC files support depth and bbox queries through `Copc.Read(path, bbox, maxDepth)`.
- **Native GeoArrow in GeoParquet:** write with `new GeoParquetWriteOptions { Encoding = GeoParquetEncoding.Native }`. Native-encoded files from geopandas are read automatically.
- **NetCDF:**
  - `ReadWindow(name, start, count)` reads a hyperslab, decoding only the chunks it touches.
  - `CurvilinearCoordinates`, `ToPoints` and `Regrid` handle 2-D `lat(y,x)`/`lon(y,x)` grids.
- **Live database CI:** PostGIS and Oracle Free run as service containers in the `sql-services` CI job.
