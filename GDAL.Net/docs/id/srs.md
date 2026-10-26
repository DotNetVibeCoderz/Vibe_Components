# Sistem koordinat

Halaman ini membahas pembuatan dan ekspor sistem referensi koordinat (CRS), transformasi koordinat termasuk transformasi datum, serta pencarian di database EPSG (namespace `GDAL.Net.Srs`, ditenagai PROJ 9.8).

## Membuat `SpatialReference`

```csharp
using GDAL.Net;
using GDAL.Net.Srs;

using var wgs84 = SpatialReference.Wgs84;                    // EPSG:4326, lon/lat order
using var merc = SpatialReference.WebMercator;               // EPSG:3857
using var utm = SpatialReference.FromEpsg(32748);            // WGS 84 / UTM zone 48S
using var utm2 = SpatialReference.Utm(48, north: false);
using var best = SpatialReference.UtmFor(longitude: 107.6, latitude: -6.9);
using var any = SpatialReference.FromUserInput("urn:ogc:def:crs:EPSG::23830");   // also "WGS84", "EPSG:4326", WKT, PROJJSON
using var fromWkt = SpatialReference.FromWkt(utm.ToWkt(WktFormat.Wkt2_2019));
using var fromJson = SpatialReference.FromProjJson(utm.ToProjJson());
using var fromProj = SpatialReference.FromProj4("+proj=utm +zone=48 +south +datum=WGS84 +units=m +no_defs");
using var fromEsri = SpatialReference.FromEsri(File.ReadAllText("roads.prj"));
```

## Memeriksa dan mengekspor

| Member | Hasil |
|---|---|
| `Name`, `AuthorityName`, `AuthorityCode`, `Epsg` | "WGS 84 / UTM zone 48S", "EPSG", "32748", 32748 (diidentifikasi otomatis bila tidak eksplisit) |
| `IsGeographic`, `IsProjected`, `IsGeocentric`, `IsCompound` | Jenis CRS |
| `LinearUnits`, `AngularUnits` | ("metre", 1.0), ("degree", 0.0174…) |
| `UtmZone` | -48 (negatif = selatan) |
| `AreaOfUse` | Batas WGS84 + nama wilayah |
| `ToWkt(WktFormat.Wkt1 / Wkt2_2015 / Wkt2_2019 / Wkt1Esri, multiline)` | String WKT |
| `ToPrettyWkt()`, `ToProjJson()`, `ToProj4()`, `ToEsriWkt()` | Encoding lain |
| `IsSame(other)`, `Clone()`, `Validate()` | Perbandingan dan utilitas |

```csharp
Console.WriteLine(utm.ToWkt(WktFormat.Wkt2_2019, multiline: true));
Console.WriteLine(utm.ToProjJson());
File.WriteAllText("out.prj", utm.ToEsriWkt());
```

## Transformasi koordinat

```csharp
using var ct = CoordinateTransformation.Create(4326, 32748);     // or (SpatialReference, SpatialReference)
var (x, y) = ct.Transform(107.6191, -6.9175);                   // ≈ (789 431, 9 235 1xx)
var (x3, y3, z3) = ct.Transform(107.6191, -6.9175, 700);

// Bulk, in place, no allocations
double[] xs = [106.8456, 107.6191, 112.7521];
double[] ys = [-6.2088, -6.9175, -7.2575];
int transformed = ct.TransformInPlace(xs, ys);

// Bounding boxes (edges densified)
Envelope utmBox = ct.TransformBounds(new Envelope(107, -7.5, 108, -6.5));
```

## Transformasi datum dan pipeline

PROJ otomatis memilih operasi paling akurat yang tersedia. `TransformationOptions` memungkinkan Anda mengarahkannya:

```csharp
// WGS 84 → DGN95 (Indonesian geodetic datum), with an area of interest to choose the right operation
using var dgn95 = SpatialReference.FromEpsg(4755);
using var toDgn = CoordinateTransformation.Create(wgs84, dgn95, new TransformationOptions
{
    AreaOfInterest = new Envelope(95, -11, 141, 6),
    BallparkAllowed = false,
});

// An explicit PROJ pipeline
using var custom = CoordinateTransformation.Create(wgs84, utm, new TransformationOptions
{
    Operation = "+proj=pipeline +step +proj=axisswap +order=2,1 +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=utm +zone=48 +south +ellps=WGS84",
});
```

Dengan `OnlyBest = true`, transformasi gagal (tanpa fallback) jika grid untuk transformasi terbaik tidak tersedia.

## Mencari di database EPSG

```csharp
IReadOnlyList<CrsInfo> all = CrsDatabase.GetAll("EPSG");               // cached after the first call
foreach (var crs in CrsDatabase.Search("Indonesia TM-3").Take(10))
    Console.WriteLine($"{crs.Id}  {crs.Name}  ({crs.AreaName})");
var projectedHere = CrsDatabase.At(longitude: 107.6, latitude: -6.9);  // projected CRSs valid at a point
var geographic = CrsDatabase.Search("DGN95", type: CrsType.Geographic2D);
```

Otoritas lain ("ESRI", "IAU_2015", "OGC", …) tersedia lewat parameter `authority`.

**Berikutnya:** [Utilitas](utilities.md) · [Raster](raster.md) · [Vektor](vector.md)
