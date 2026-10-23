[English](../en/geodesy-and-coordinates.md)

# Geodesi & koordinat

Semua yang ada di halaman ini adalah C# murni di paket inti `Cesium.Net`. Tidak perlu viewer maupun JavaScript, jadi bisa dipakai di server, dalam pengujian, dan di background job.

## Tipe koordinat

| Tipe | Satuan | Kegunaan |
|---|---|---|
| `GeoPoint(lon, lat, h)` | derajat, meter | Tipe posisi utama (WGS84). **Longitude lebih dulu.** |
| `Cartographic(lon, lat, h)` | radian, meter | Sama dengan `Cartographic` di CesiumJS |
| `Cartesian3(x, y, z)` | meter | Posisi ECEF, offset ENU, vektor |
| `Cartesian2(x, y)` | piksel atau meter | Posisi layar, Web Mercator |
| `Cartesian4(x, y, z, w)` | — | Vektor homogen |
| `GeoRectangle(w, s, e, n)` | derajat | Bentang (extent) |
| `HeadingPitchRoll`, `HeadingPitchRange` | derajat (+ meter) | Orientasi, offset kamera |

```csharp
var p = new GeoPoint(106.827153, -6.175392, 0);
Cartographic c = p.ToCartographic();              // radian
Cartesian3 ecef = p.ToCartesian();                // meter ECEF
GeoPoint back = ecef.ToGeoPoint();
GeoPoint fromRad = GeoPoint.FromRadians(1.8645, -0.1078);
double rad = GeoMath.ToRadians(45), deg = GeoMath.ToDegrees(Math.PI);
double lon = GeoMath.NormalizeLongitude(190);     // -170
double brg = GeoMath.NormalizeBearing(-30);       // 330
```

## Konversi (`CoordinateConverter`)

| Dari → Ke | Metode |
|---|---|
| WGS84 → ECEF | `GeodeticToEcef(p)` |
| ECEF → WGS84 | `EcefToGeodetic(c)` (iteratif, sub-milimeter) |
| ECEF ↔ ENU lokal | `EcefToEnu(ecef, origin)`, `EnuToEcef(enu, origin)` |
| WGS84 ↔ ENU | `GeodeticToEnu(p, origin)`, `EnuToGeodetic(enu, origin)` |
| WGS84 ↔ Web Mercator (EPSG:3857) | `GeodeticToWebMercator(p)`, `WebMercatorToGeodetic(m)` |
| WGS84 → tile XYZ | `GeodeticToTile(p, zoom)` |
| Derajat → teks DMS | `ToDms(degrees, isLatitude)` |

```csharp
var origin = new GeoPoint(106.8456, -6.2088);

// Tata letak objek dalam meter di sekitar titik asal (timur, utara, atas):
GeoPoint northEast = CoordinateConverter.EnuToGeodetic(new Cartesian3(300, 500, 0), origin);
Cartesian3 enu = CoordinateConverter.GeodeticToEnu(new GeoPoint(106.85, -6.20, 40), origin); // (x=T, y=U, z=Atas)

Cartesian2 merc = CoordinateConverter.GeodeticToWebMercator(origin);   // meter
(int x, int y) = CoordinateConverter.GeodeticToTile(origin, zoom: 15);  // slippy tile
string label = $"{CoordinateConverter.ToDms(origin.Latitude, true)} {CoordinateConverter.ToDms(origin.Longitude, false)}";
// 6°12'31.7"S 106°50'44.2"E
```

Metode `GeodeticToEcef`, `EcefToGeodetic`, `EcefToEnu`, dan `EnuToEcef` menerima `Ellipsoid` opsional (default `Ellipsoid.Wgs84`). Web Mercator membatasi latitude ke ±85,0511°.

Tata letak ENU inilah yang dipakai entity showcase di galeri untuk menempatkan bentuk-bentuk dengan jarak tetap dalam meter.

## Perhitungan geodesik (`Geodesy`)

Perhitungan dilakukan pada ellipsoid WGS84, kecuali jika metodenya menyebutkan lain.

```csharp
var jakarta  = new GeoPoint(106.8272, -6.1754);
var surabaya = new GeoPoint(112.7521, -7.2575);

double d   = Geodesy.Distance(jakarta, surabaya);          // Vincenty, akurasi mm → ~665 km
double dh  = Geodesy.HaversineDistance(jakarta, surabaya); // bola, cepat, galat ~0,5 %
double brg = Geodesy.Bearing(jakarta, surabaya);           // bearing awal, derajat dari utara
GeodesicInverse inv = Geodesy.Inverse(jakarta, surabaya);  // jarak + bearing awal + akhir

GeoPoint dest = Geodesy.Destination(jakarta, bearing: 90, distance: 50_000); // 50 km tepat ke timur
GeoPoint mid  = Geodesy.Midpoint(jakarta, surabaya);
GeoPoint[] line = Geodesy.Interpolate(jakarta, surabaya, 50);              // 50 titik termasuk ujung

double slant = Geodesy.SlantDistance(jakarta.WithHeight(0), surabaya.WithHeight(10_000)); // garis lurus 3D
```

| Metode | Catatan |
|---|---|
| `Distance`, `Bearing`, `Inverse` | Vincenty inverse; beralih ke solusi bola untuk titik yang hampir antipodal |
| `Destination` | Vincenty direct; mempertahankan ketinggian titik awal |
| `Interpolate(a, b, count)` | `count` ≥ 2; ketinggian diinterpolasi linier |
| `PathLength(path)` | Jumlah jarak tiap segmen |
| `Perimeter(ring)` | Menutup ring secara otomatis |
| `Area(ring)` | m², ekses sferis pada bola autalik WGS84 (~0,1 % dari luas ellipsoid yang eksak); orientasi tidak berpengaruh |
| `Centroid(points)` | Rata-rata di ECEF, diproyeksikan kembali; ketinggian 0 |
| `Contains(ring, p)` | Uji ray-casting planar dalam lon/lat |
| `Circle(center, radius, segments = 64)` | Ring lingkaran geodesik (tidak tertutup) |
| `FormatDistance`, `FormatArea` | `"850.0 m"`, `"12.40 km"`, `"3.20 ha"`, `"1.25 km²"` |

```csharp
GeoPoint[] lot = [new(106.80, -6.20), new(106.81, -6.20), new(106.81, -6.21), new(106.80, -6.21)];
Console.WriteLine(Geodesy.FormatArea(Geodesy.Area(lot)));          // ~1.23 km²
Console.WriteLine(Geodesy.FormatDistance(Geodesy.Perimeter(lot)));
bool inside = Geodesy.Contains(lot, new GeoPoint(106.805, -6.205)); // true
GeoPoint center = Geodesy.Centroid(lot);
GeoPoint[] buffer = Geodesy.Circle(center, 2_000);
```

### Batasan

- `Area` dan `Contains` memperlakukan longitude apa adanya. Pecah poligon yang melintasi meridian ±180°, dan jangan gunakan ring yang melingkupi kutub.
- `Contains` bersifat planar dalam derajat. Di dekat kutub atau untuk poligon yang sangat besar, tepinya berbeda dari geodesik sebenarnya.
- `Area` tidak eksak pada ellipsoid. Gunakan pustaka geodesi khusus (mis. GeographicLib) jika Anda butuh luas setara survei.

## Kueri spasial

| Kueri | Caranya |
|---|---|
| Titik dalam poligon / geofence | `Geodesy.Contains(ring, p)` |
| Titik dalam bounding box | `GeoRectangle.Contains(p)`; `GeoRectangle.FromPoints(points)` membuatnya |
| Apa yang terlihat di layar? | `(await cesium.Camera.GetStateAsync()).ViewRectangle` |
| Dalam radius tertentu | `Geodesy.Distance(center, p) <= r` |
| Apa yang ada di bawah kursor / di suatu piksel? | `PickResult` dari `MouseMove`/`LeftClick`, atau `Scene.PickAsync(x, y)` (ray cast terhadap 3D Tiles, terrain, ellipsoid) |
| Di mana sebuah posisi pada layar? | `Scene.ToScreenAsync(p)` |

```csharp
var view = (await cesium.Camera.GetStateAsync()).ViewRectangle;
var visible = view is { } r ? sites.Where(s => r.Contains(s.Position)).ToList() : sites;
```

Untuk dataset besar, simpan titik-titiknya dalam indeks spasial (R-tree atau grid geohash) di kode Anda sendiri. Cesium.Net tidak menyertakannya.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
