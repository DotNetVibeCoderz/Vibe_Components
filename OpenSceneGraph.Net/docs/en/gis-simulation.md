# GIS, terrain and simulation

## Terrain

```csharp
var hf = TerrainGenerator.Fractal(size: 513, spacing: 10, amplitude: 900, seed: 42);    // procedural
var dem = HeightFieldReaderWriter.ReadHeightField("srtm_n06_e107.tif");                // GeoTIFF (uncompressed)
var asc = HeightFieldReaderWriter.ReadHeightField("dem.asc");                          // ESRI ASCII grid

var terrain = TerrainBuilder.Build(dem, tilesPerSide: 8, levels: 4, ColorRamp.Natural());
float? z = dem.Sample(x, y);                                                           // bilinear height
viewer.CameraManipulator = new TerrainManipulator { HeightAt = dem.Sample };
```

`TerrainBuilder` splits the field into tiles. Each tile is a `Lod` with power-of-two decimated levels and slope-aware colour ramps (`Natural`, `Desert`, `Tactical`, or your own `ColorRamp`). Mesh generation uses the Rust fast path when it is available.
GeoTIFF support covers single-band, uncompressed strips or tiles (int16/uint16/int32/float32/float64), `ModelPixelScale`/`ModelTiepoint` georeferencing and `GDAL_NODATA`. Convert compressed files with `gdal_translate -co COMPRESS=NONE`.

## Geodesy

```csharp
var e = Ellipsoid.Wgs84;
var jakarta = new GeoPoint(-6.2088, 106.8456, 10);
Vector3d ecef = e.ToEcef(jakarta);                    // double precision
GeoPoint back = e.FromEcef(ecef);                     // sub-millimetre round trip
Vector3 enu = e.ToEnu(target, origin: jakarta);       // local metres for rendering
Matrix4x4 frame = e.LocalToWorld(jakarta, renderOrigin);
UtmCoordinate utm = Utm.FromGeo(jakarta);             // 48M 704207 E 9313358 N
double km = e.Distance(jakarta, tokyo) / 1000;
double brg = Ellipsoid.Bearing(jakarta, tokyo);
```

For planet-scale scenes, render relative to an origin (`Vector3d.ToVector3(origin)`) to avoid single-precision jitter, and use `Node.ClusterCull` to skip tiles on the far side of the globe. `Globe.Build()` creates a textured WGS84 ellipsoid.

## Map tiles (osgEarth-style image layers)

```csharp
var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("MyApp/1.0 (contact@example.com)");   // required by the OSM tile policy
var loader = new MapTileLoader(TileSource.OpenStreetMap(), http, diskCache: "tilecache");
root.AddChild(await loader.BuildAreaAsync(jakarta, zoom: 14, radiusTiles: 2));
// also: TileSource.Wms(url, layers), TileSource.Wmts(url, layer), TileSource.ArcGis(mapServerUrl)
```

`TileMath` provides slippy-map keys, bounds and Bing quadkeys. PNG tiles decode with the built-in reader; register an `IImageReader` for JPEG sources.

## Vehicles

All vehicle models work in local ENU metres (X east, Y north, Z up), take `VehicleControls` (throttle, pitch, roll, yaw, brake) and a `GroundHeight` function, and write their pose with `ApplyTo(pat)`.

| Model | Behaviour |
|---|---|
| `Aircraft` | Thrust/drag/gravity along the flight path, coordinated bank-to-turn, stall, touchdown/crash |
| `Quadcopter` | Velocity control, altitude hold, visual tilt |
| `GroundVehicle` | Terrain following, pitch/roll aligned to the slope, turret heading |
| `Ship` | Inertia, rudder effectiveness with speed, sea-state heave/pitch/roll |

## DIS (IEEE 1278.1)

```csharp
using var net = new DisNetwork(port: 3000);                   // broadcast; pass a multicast address to join a group
net.EntityUpdated += (pdu, from) => Console.WriteLine($"{pdu.Id} {pdu.Marking} {Ellipsoid.Wgs84.FromEcef(pdu.Location)}");
var pdu = aircraft.ToPdu(new EntityId(1, 3001, 1), EntityType.FighterAircraft(), origin, "VIPER-1");
await net.SendAsync(pdu);
var predicted = pdu.Extrapolate(seconds: 0.5);                // dead reckoning (FPW/RVW/...)
```

The Entity State PDU (type 1, protocol 7, 144 bytes with no articulation parameters) is encoded big-endian per the standard. Orientation uses DIS psi/theta/phi relative to ECEF (`DisOrientation.FromLocal`). Try `osgnet dis send` and `osgnet dis listen`.

## Orbits

`KeplerOrbit` implements two-body propagation (Newton solution of Kepler's equation) with `PositionAt(t)`, `PeriodSeconds` and `SampleTrack()`, used by the satellite constellation sample.

HLA (IEEE 1516), DDS / ROS 2 and MQTT bridges are on the [roadmap](../../PLAN.md).
