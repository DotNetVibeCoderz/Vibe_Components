[Bahasa Indonesia](../id/geodesy-and-coordinates.md)

# Geodesy & coordinates

All of this is pure C# in the core `Cesium.Net` package. It needs no viewer and no JavaScript, so you can use it on servers, in tests and in background jobs.

## Coordinate types

| Type | Units | Use |
|---|---|---|
| `GeoPoint(lon, lat, h)` | degrees, meters | Primary position type (WGS84). **Longitude first.** |
| `Cartographic(lon, lat, h)` | radians, meters | Same as CesiumJS `Cartographic` |
| `Cartesian3(x, y, z)` | meters | ECEF positions, ENU offsets, vectors |
| `Cartesian2(x, y)` | pixels or meters | Screen positions, Web Mercator |
| `Cartesian4(x, y, z, w)` | — | Homogeneous vectors |
| `GeoRectangle(w, s, e, n)` | degrees | Extents |
| `HeadingPitchRoll`, `HeadingPitchRange` | degrees (+ meters) | Orientation, camera offsets |

```csharp
var p = new GeoPoint(106.827153, -6.175392, 0);
Cartographic c = p.ToCartographic();              // radians
Cartesian3 ecef = p.ToCartesian();                // ECEF meters
GeoPoint back = ecef.ToGeoPoint();
GeoPoint fromRad = GeoPoint.FromRadians(1.8645, -0.1078);
double rad = GeoMath.ToRadians(45), deg = GeoMath.ToDegrees(Math.PI);
double lon = GeoMath.NormalizeLongitude(190);     // -170
double brg = GeoMath.NormalizeBearing(-30);       // 330
```

## Conversions (`CoordinateConverter`)

| From → To | Method |
|---|---|
| WGS84 → ECEF | `GeodeticToEcef(p)` |
| ECEF → WGS84 | `EcefToGeodetic(c)` (iterative, sub-millimeter) |
| ECEF ↔ local ENU | `EcefToEnu(ecef, origin)`, `EnuToEcef(enu, origin)` |
| WGS84 ↔ ENU | `GeodeticToEnu(p, origin)`, `EnuToGeodetic(enu, origin)` |
| WGS84 ↔ Web Mercator (EPSG:3857) | `GeodeticToWebMercator(p)`, `WebMercatorToGeodetic(m)` |
| WGS84 → XYZ tile | `GeodeticToTile(p, zoom)` |
| Degrees → DMS text | `ToDms(degrees, isLatitude)` |

```csharp
var origin = new GeoPoint(106.8456, -6.2088);

// Lay out objects in meters around an origin (east, north, up):
GeoPoint northEast = CoordinateConverter.EnuToGeodetic(new Cartesian3(300, 500, 0), origin);
Cartesian3 enu = CoordinateConverter.GeodeticToEnu(new GeoPoint(106.85, -6.20, 40), origin); // (x=E, y=N, z=U)

Cartesian2 merc = CoordinateConverter.GeodeticToWebMercator(origin);   // meters
(int x, int y) = CoordinateConverter.GeodeticToTile(origin, zoom: 15);  // slippy tile
string label = $"{CoordinateConverter.ToDms(origin.Latitude, true)} {CoordinateConverter.ToDms(origin.Longitude, false)}";
// 6°12'31.7"S 106°50'44.2"E
```

The `GeodeticToEcef`, `EcefToGeodetic`, `EcefToEnu` and `EnuToEcef` methods take an optional `Ellipsoid` (default `Ellipsoid.Wgs84`). Web Mercator clamps latitude to ±85.0511°.

ENU layouts are what the gallery's entity showcase uses to place shapes a fixed number of meters apart.

## Geodesic calculations (`Geodesy`)

Calculations run on the WGS84 ellipsoid unless the method says otherwise.

```csharp
var jakarta  = new GeoPoint(106.8272, -6.1754);
var surabaya = new GeoPoint(112.7521, -7.2575);

double d   = Geodesy.Distance(jakarta, surabaya);          // Vincenty, mm accuracy → ~665 km
double dh  = Geodesy.HaversineDistance(jakarta, surabaya); // sphere, fast, ~0.5 % error
double brg = Geodesy.Bearing(jakarta, surabaya);           // initial bearing, deg from north
GeodesicInverse inv = Geodesy.Inverse(jakarta, surabaya);  // distance + initial + final bearing

GeoPoint dest = Geodesy.Destination(jakarta, bearing: 90, distance: 50_000); // 50 km due east
GeoPoint mid  = Geodesy.Midpoint(jakarta, surabaya);
GeoPoint[] line = Geodesy.Interpolate(jakarta, surabaya, 50);              // 50 points incl. ends

double slant = Geodesy.SlantDistance(jakarta.WithHeight(0), surabaya.WithHeight(10_000)); // straight 3D line
```

| Method | Notes |
|---|---|
| `Distance`, `Bearing`, `Inverse` | Vincenty inverse; falls back to a spherical solution for nearly antipodal points |
| `Destination` | Vincenty direct; keeps the start height |
| `Interpolate(a, b, count)` | `count` ≥ 2; heights interpolated linearly |
| `PathLength(path)` | Sum of segment distances |
| `Perimeter(ring)` | Closes the ring automatically |
| `Area(ring)` | m², spherical excess on the WGS84 authalic sphere (~0.1 % of the exact ellipsoidal area); orientation doesn't matter |
| `Centroid(points)` | Average in ECEF, projected back; height 0 |
| `Contains(ring, p)` | Planar ray-casting test in lon/lat |
| `Circle(center, radius, segments = 64)` | Geodesic circle ring (not closed) |
| `FormatDistance`, `FormatArea` | `"850.0 m"`, `"12.40 km"`, `"3.20 ha"`, `"1.25 km²"` |

```csharp
GeoPoint[] lot = [new(106.80, -6.20), new(106.81, -6.20), new(106.81, -6.21), new(106.80, -6.21)];
Console.WriteLine(Geodesy.FormatArea(Geodesy.Area(lot)));          // ~1.23 km²
Console.WriteLine(Geodesy.FormatDistance(Geodesy.Perimeter(lot)));
bool inside = Geodesy.Contains(lot, new GeoPoint(106.805, -6.205)); // true
GeoPoint center = Geodesy.Centroid(lot);
GeoPoint[] buffer = Geodesy.Circle(center, 2_000);
```

### Limits

- `Area` and `Contains` treat longitudes literally. Split polygons that cross the ±180° meridian, and don't use rings that enclose a pole.
- `Contains` is planar in degrees. Near the poles or for very large polygons, edges differ from true geodesics.
- `Area` is not exact on the ellipsoid. Use a dedicated geodesy library (e.g. GeographicLib) if you need survey-grade area.

## Spatial queries

| Query | How |
|---|---|
| Point in polygon / geofence | `Geodesy.Contains(ring, p)` |
| Point in bounding box | `GeoRectangle.Contains(p)`; `GeoRectangle.FromPoints(points)` builds one |
| What's in view? | `(await cesium.Camera.GetStateAsync()).ViewRectangle` |
| Within radius | `Geodesy.Distance(center, p) <= r` |
| What's under the cursor / at a pixel? | `MouseMove`/`LeftClick` `PickResult`, or `Scene.PickAsync(x, y)` (ray cast against 3D Tiles, terrain, ellipsoid) |
| Where is a position on screen? | `Scene.ToScreenAsync(p)` |

```csharp
var view = (await cesium.Camera.GetStateAsync()).ViewRectangle;
var visible = view is { } r ? sites.Where(s => r.Contains(s.Position)).ToList() : sites;
```

For large datasets, put the points in a spatial index (an R-tree or a geohash grid) in your own code. Cesium.Net does not include one.

---
Made by Gravicode Studios, led by Kang Fadhil.
