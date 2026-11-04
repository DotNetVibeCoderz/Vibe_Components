# GIS, terrain, dan simulasi

## Terrain

```csharp
var hf = TerrainGenerator.Fractal(size: 513, spacing: 10, amplitude: 900, seed: 42);    // prosedural
var dem = HeightFieldReaderWriter.ReadHeightField("srtm_n06_e107.tif");                // GeoTIFF (tanpa kompresi)
var asc = HeightFieldReaderWriter.ReadHeightField("dem.asc");                          // grid ASCII ESRI

var terrain = TerrainBuilder.Build(dem, tilesPerSide: 8, levels: 4, ColorRamp.Natural());
float? z = dem.Sample(x, y);                                                           // tinggi bilinear
viewer.CameraManipulator = new TerrainManipulator { HeightAt = dem.Sample };
```

`TerrainBuilder` membagi field menjadi beberapa tile. Setiap tile adalah `Lod` dengan level yang didesimasi secara pangkat dua dan color ramp yang memperhitungkan kemiringan (`Natural`, `Desert`, `Tactical`, atau `ColorRamp` buatan Anda sendiri). Pembuatan mesh menggunakan fast path Rust jika tersedia.
Dukungan GeoTIFF mencakup strip atau tile single-band tanpa kompresi (int16/uint16/int32/float32/float64), georeferensi `ModelPixelScale`/`ModelTiepoint`, dan `GDAL_NODATA`. Konversikan file terkompresi dengan `gdal_translate -co COMPRESS=NONE`.

## Geodesi

```csharp
var e = Ellipsoid.Wgs84;
var jakarta = new GeoPoint(-6.2088, 106.8456, 10);
Vector3d ecef = e.ToEcef(jakarta);                    // presisi ganda
GeoPoint back = e.FromEcef(ecef);                     // konversi bolak-balik dengan galat di bawah milimeter
Vector3 enu = e.ToEnu(target, origin: jakarta);       // meter lokal untuk rendering
Matrix4x4 frame = e.LocalToWorld(jakarta, renderOrigin);
UtmCoordinate utm = Utm.FromGeo(jakarta);             // 48M 704207 E 9313358 N
double km = e.Distance(jakarta, tokyo) / 1000;
double brg = Ellipsoid.Bearing(jakarta, tokyo);
```

Untuk scene berskala planet, lakukan render relatif terhadap sebuah origin (`Vector3d.ToVector3(origin)`) untuk menghindari jitter akibat presisi tunggal, dan gunakan `Node.ClusterCull` untuk melewati tile di sisi globe yang membelakangi kamera. `Globe.Build()` membuat ellipsoid WGS84 bertekstur.

## Tile peta (image layer ala osgEarth)

```csharp
var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("MyApp/1.0 (contact@example.com)");   // diwajibkan oleh kebijakan tile OSM
var loader = new MapTileLoader(TileSource.OpenStreetMap(), http, diskCache: "tilecache");
root.AddChild(await loader.BuildAreaAsync(jakarta, zoom: 14, radiusTiles: 2));
// juga: TileSource.Wms(url, layers), TileSource.Wmts(url, layer), TileSource.ArcGis(mapServerUrl)
```

`TileMath` menyediakan key slippy-map, batas (bounds), dan quadkey Bing. Tile PNG didekode dengan reader bawaan; daftarkan `IImageReader` untuk sumber JPEG.

## Kendaraan

Semua model kendaraan bekerja dalam meter ENU lokal (X timur, Y utara, Z atas), menerima `VehicleControls` (throttle, pitch, roll, yaw, rem) dan fungsi `GroundHeight`, lalu menuliskan posenya dengan `ApplyTo(pat)`.

| Model | Perilaku |
|---|---|
| `Aircraft` | Gaya dorong/hambat/gravitasi sepanjang jalur terbang, bank-to-turn terkoordinasi, stall, mendarat/jatuh |
| `Quadcopter` | Kontrol kecepatan, altitude hold, kemiringan visual |
| `GroundVehicle` | Mengikuti terrain, pitch/roll menyesuaikan kemiringan, arah turret |
| `Ship` | Inersia, efektivitas kemudi yang bergantung pada kecepatan, heave/pitch/roll sesuai kondisi laut |

## DIS (IEEE 1278.1)

```csharp
using var net = new DisNetwork(port: 3000);                   // broadcast; berikan alamat multicast untuk bergabung ke grup
net.EntityUpdated += (pdu, from) => Console.WriteLine($"{pdu.Id} {pdu.Marking} {Ellipsoid.Wgs84.FromEcef(pdu.Location)}");
var pdu = aircraft.ToPdu(new EntityId(1, 3001, 1), EntityType.FighterAircraft(), origin, "VIPER-1");
await net.SendAsync(pdu);
var predicted = pdu.Extrapolate(seconds: 0.5);                // dead reckoning (FPW/RVW/...)
```

Entity State PDU (type 1, protocol 7, 144 byte tanpa articulation parameter) di-encode secara big-endian sesuai standar. Orientasi menggunakan psi/theta/phi DIS relatif terhadap ECEF (`DisOrientation.FromLocal`). Coba `osgnet dis send` dan `osgnet dis listen`.

## Orbit

`KeplerOrbit` mengimplementasikan propagasi dua benda (solusi Newton untuk persamaan Kepler) dengan `PositionAt(t)`, `PeriodSeconds`, dan `SampleTrack()`, yang dipakai oleh sampel konstelasi satelit.

Bridge HLA (IEEE 1516), DDS / ROS 2, dan MQTT ada di [roadmap](../../PLAN.md).
