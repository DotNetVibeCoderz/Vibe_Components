# Skala besar, 3D, kembaran digital & cloud

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [🇬🇧 English](../en/scale-3d-twins.md)

Fase 5 (v0.5) menambahkan:

- **Grid global:** indeks sel H3 dan S2 (`GeoPandas.Net.Geometry.Index`), dengan helper GeoDataFrame di Core.
- **Pemrosesan terpartisi:** `PartitionedGeoDataFrame` dan dataset GeoParquet terpartisi.
- **3D:** glTF dan OGC 3D Tiles (`GeoPandas.Net.Scene`), integrasi Cesium.Net (`GeoPandas.Net.Cesium`) serta peta interaktif MapLibre (`Explore()`, `GeoPandas.Net.Blazor`).
- **Kembaran digital:** live layer IoT di atas quadtree dinamis (`GeoPandas.Net.Twin`).
- **Cloud:** klien Azure Maps, AWS Location Service dan Azure Digital Twins (`GeoPandas.Net.Cloud`).
- **Machine learning dan GPU:** segmentasi ONNX dan tutupan lahan ML.NET (`GeoPandas.Net.ML`), kernel raster ILGPU (`GeoPandas.Net.Gpu`).

`Scene`, `Twin` dan `Cloud` termasuk dalam meta-package `GeoPandas.Net`. `Cesium`, `Blazor`, `ML` dan `Gpu` adalah paket terpisah karena membawa dependensi besar (Blazor, ML.NET, ONNX Runtime, ILGPU).

Setiap hasil diverifikasi terhadap implementasi independen. Berkas rujukannya ada di `tests/GeoPandas.Net.Tests/data`:

| Fitur | Rujukan | Kesesuaian |
|---|---|---|
| Sel H3, pusat, batas, disk, parent/child, pengisian poligon, kompaksi | h3-py 4.5 (Uber H3) | id sel dan himpunan identik; koordinat dalam 1e-10° |
| Luas sel H3 | h3-py 4.5 | relatif 1e-12 |
| Id sel S2, token, tetangga, child, pusat dan verteks | s2sphere 0.2.5 | id dan token identik; koordinat dalam 1e-10° |
| Luas eksak S2 | s2sphere 0.2.5 | relatif 1e-6 (s2sphere mendokumentasikan akurasi 6 digit) |
| Keluaran glTF 2.0 / 3D Tiles 1.1 | Khronos glTF-Validator, CesiumGS 3d-tiles-validator | 0 error, 0 peringatan |
| Georeferensi 3D Tiles (CRS → ECEF) | pyproj | 0,06 mm |
| Tanda tangan AWS SigV4 | botocore 1.44.5 | header `Authorization` identik |
| Segmentasi ONNX (satu tile dan tile bertumpang) | onnxruntime 1.32 (Python) | 1e-6, kelas arg-max sama di semua piksel |
| Slope, aspect, hillshade, NDVI, konvolusi, IDW di GPU | implementasi managed | pembulatan float |
| Spatial join terpartisi | `GeoDataFrame.SJoin` | baris dan urutan identik |

## H3 dan S2

Kode H3 adalah port dari pustaka C Uber H3 v4; tabel lookup-nya dibangkitkan dari sumber C oleh `tools/h3/h3tables.py` (Apache 2.0). Kode S2 mengikuti pustaka geometri S2 rujukan (id sel, kurva Hilbert, proyeksi kuadratik).

```csharp
using GeoPandas.Net.Geometry.Index;

ulong cell = H3.LatLngToCell(-6.2, 106.8, res: 9);         // "898c106a48fffff"
var ring   = H3.GridDisk(cell, k: 1);                       // sel itu dan 6 tetangganya
Polygon hex = H3.CellToPolygon(cell);
double km2  = H3.CellAreaKm2(cell);
var cover   = H3.PolygonToCells(batasKotaWgs84, 8);         // sel yang pusatnya ada di dalam
var lebihSedikit = H3.Compact(cover);

var hexbins = H3Frame.Bin(pois, res: 8, valueColumn: "capacity");   // count, capacity_sum, capacity_mean per heksagon
var tagged  = H3Frame.IndexPoints(pois, 9);                         // menambah kolom "h3"

ulong s2 = S2.FromLatLng(-6.2, 106.8, level: 15);
string token = S2.ToToken(s2);                              // "2e69f6be4"
var covering = S2.Cover(poligonWgs84, level: 12);
```

H3 dan S2 selalu bekerja di WGS 84. Helper frame memproyeksi ulang masukan terproyeksi dengan sendirinya.

## Pemrosesan terpartisi

`PartitionedGeoDataFrame` memecah frame menjadi partisi spasial agar pekerjaan dapat berjalan paralel dan kueri dapat melewati partisi:

| Skema | Arti `size` | Gunakan untuk |
|---|---|---|
| `KdTree` | jumlah baris maksimum per partisi (pembagian median seimbang) | unit kerja berukuran rata |
| `Grid` | jumlah sel per sisi (n × n) | tile sederhana dan mudah ditebak |
| `H3` | resolusi | penggabungan dengan data H3 lain |
| `Column` | — (memakai `column`) | partisi atribut, mis. per provinsi |

```csharp
var parts  = PartitionedGeoDataFrame.Create(points, PartitionScheme.KdTree, size: 50_000);
var joined = parts.SJoin(districts, SpatialPredicate.Within).Collect();  // == points.SJoin(districts, Within)
var window = parts.Query(bbox);                                         // hanya partisi yang batasnya berpotongan
double total = parts.MapReduce(f => f["capacity"].Sum(), (a, b) => a + b);
var buffered = parts.Map(f => f.Buffer(10));

GeoParquetDataset.Write(parts, "points_dataset/");     // partition=<key>/part-0.parquet + _dataset.json
var subset = GeoParquetDataset.ReadPartitioned("points_dataset/", bbox);   // hanya membuka berkas yang bertumpang
```

Cara kerjanya:

- Setiap partisi menyimpan id baris asli, sehingga `Collect()` mengembalikan urutan sumber.
- Spatial join sadar-partisi memasangkan setiap partisi hanya dengan baris kanan yang envelope-nya menyentuh partisi itu. Hasil yang dikumpulkan identik dengan `SJoin`, baris demi baris.
- Setiap partisi adalah GeoDataFrame biasa, sehingga partisi yang sama dapat dikirim ke grain Orleans, aktor Akka.NET atau executor Spark. Adapter siap pakai untuk framework tersebut belum disertakan.

Dataset terpartisi berisi satu berkas GeoParquet per partisi ditambah manifest `_dataset.json` yang mencatat jumlah baris dan batas setiap partisi. Pembacaan dengan bounding box berlangsung dalam dua langkah:

1. Hanya berkas yang batasnya berpotongan dengan kotak yang dibuka.
2. Di dalam setiap berkas, row group dilewati memakai kolom bbox covering GeoParquet.

## 3D: glTF, 3D Tiles dan Cesium

`LocalFrame` menempatkan model dalam frame lokal timur-utara-atas (ENU). Setiap verteks dikonversi secara eksak: CRS → WGS 84 → ECEF → ENU.

`MeshBuilders` menghasilkan empat jenis mesh:

- **Terrain:** permukaan DEM, dengan warna opsional.
- **Extrude:** solid tertutup dengan orientasi keluar dari poligon; lubang ditriangulasi dengan Delaunay terkendala.
- **Points:** awan titik.
- **Lines:** polyline.

`Gltf` menulis berkas GLB. Atribut fitur disimpan dengan `EXT_mesh_features` dan `EXT_structural_metadata`.

```csharp
using GeoPandas.Net.Scene;

var frame = LocalFrame.AtCentre(buildings.TotalBounds, buildings.Crs);
Gltf.WriteGlb("kota.glb", MeshBuilders.Extrude(buildings, frame, "height"), MeshBuilders.Terrain(dem, frame, step: 2));

var tileset = Tiles3D.WriteBuildings(buildings, "tiles/", heightColumn: "height",
    options: new TilesetOptions { MaxFeaturesPerTile = 500 }, heightColors: ColorMaps.Viridis);
var terrain = Tiles3D.WriteTerrain(dem, "terrain/", chunk: 128);
```

Tileset berformat 3D Tiles 1.1. Transformasi root-nya ENU → ECEF dan volume pembatasnya berupa kotak berorientasi. Ada dua tata letak:

- **Bangunan:** quadtree dengan penghalusan aditif.
- **Terrain:** satu tile kasar yang dihaluskan menjadi potongan dengan penghalusan pengganti.

Dengan Cesium.Net (`GeoPandas.Net.Cesium`, namespace `CesiumNet`):

```csharp
await viewer.AddGeoDataFrameAsync(districts, fill: "#2b8cbe55");
await viewer.AddPointsAsync(sensors, heightColumn: "z", colorColumn: "pm25");
await viewer.AddTilesetAsync("tiles/tileset.json",
    style: CesiumExtensions.ColorByProperty("height", [(20, "color('#fde725')"), (60, "color('#21918c')")]));
await viewer.FlyToAsync(districts);
```

## Peta interaktif: `Explore()`

```csharp
districts.Explore("population").Open();                 // halaman HTML dengan MapLibre GL JS 4.7.1, dibuka di browser
new ExploreMap { Basemap = "none", Title = "Aset" }
    .Add(pipes, new ExploreStyle { Column = "diameter" })
    .Add(valves, new ExploreStyle { Fill = "#b0306a" })
    .Save("aset.html");
```

Isi halamannya:

- Data berupa GeoJSON yang diproyeksi ulang ke WGS 84.
- Gaya koroplet dan kategorikal memakai colormap dan klasifikasi yang sama dengan `Plot()`.
- Tooltip muncul saat kursor diarahkan, dan legenda sudah tersedia.
- Basemap: OpenStreetMap, tanpa basemap, atau URL XYZ apa pun.

Di Blazor, gunakan `<ExploreView Map="@map" Height="520px" />`. Komponen ini dirender di dalam iframe ber-sandbox, sehingga skripnya tidak pernah menyentuh halaman. `<MapSceneView Scene="@scene" />` menampilkan renderer SVG statis.

## Kembaran digital

```csharp
using GeoPandas.Net.Twin;

var layer = new LiveLayer("armada", districts.TotalBounds, districts.Crs, historyWindow: TimeSpan.FromHours(2))
    .AddZones(districts, "name")
    .AddRule(new ThresholdRule("ngebut", "speed_kmh", Above: 60, Hysteresis: 5));
layer.EventRaised += e => Console.WriteLine(e);      // ZoneEnter / ZoneExit / ThresholdExceeded / ThresholdCleared / Stale

await layer.IngestAsync(readings);                   // IAsyncEnumerable<SensorReading> atau ChannelReader<SensorReading>
var reading = TwinJson.Parse(payloadMqtt, layer.Crs);   // {"id","time","lon","lat",...nilai}

var sekarang = layer.Snapshot();                             // satu baris per entitas
var dekat    = layer.Near(x, y, radius: 500);
var jejak    = layer.Trajectory("truk-7");
var statistik = layer.ZoneStatistics("pm25", TimeSpan.FromMinutes(15), DateTimeOffset.UtcNow);
var basi     = layer.CheckStale(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
```

Perilaku layer:

- Posisi disimpan dalam quadtree dinamis.
- Keanggotaan zona memakai *covers*, sehingga titik di batas zona dihitung berada di dalam.
- Aturan ambang memakai histeresis, sehingga nilai yang berkisar di sekitar batas tidak membanjiri aliran event.
- Event dikirim ke callback `EventRaised` dan ke `Channel` (`layer.Events`).

`DigitalTwin` menggabungkan layer statis (bangunan, pipa) dengan live layer. Fungsi `RollUp`-nya mengagregasi pembacaan langsung per aset.

## Layanan lokasi cloud

Klien `GeoPandas.Net.Cloud` memakai `HttpClient` biasa tanpa SDK cloud. Hasilnya berupa GeoDataFrame WGS 84.

```csharp
using GeoPandas.Net.Cloud;

var maps = new AzureMapsClient(http, Environment.GetEnvironmentVariable("AZURE_MAPS_KEY")!);
// atau: new AzureMapsClient(http, ct => AmbilTokenEntraAsync(ct), mapsClientId)
var hasil = await maps.GeocodeAsync("Jl. Sudirman, Jakarta", top: 5, countryRegion: "ID");
var rute  = await maps.RouteAsync([(106.82, -6.17), (106.84, -6.22)]);
var iso   = await maps.IsochronesAsync(106.8, -6.2, [600, 1200, 1800]);

var aws = new AwsLocationClient(http, "ap-southeast-1", AwsCredentials.FromEnvironment()!);   // SigV4
var tempat = await aws.GeocodeAsync("my-place-index", "Monas, Jakarta");
var leg    = await aws.CalculateRouteAsync("my-route-calculator", (106.82, -6.17), (106.84, -6.22));
await aws.PutGeofencesAsync("zona", districts, "name");      // ring diorientasi ulang sesuai syarat AWS
await aws.SyncTrackerAsync("pelacak-armada", layer);         // live layer → tracker (batch 10)

var adt = new AzureDigitalTwinsClient(http, new Uri("https://my-twins.api.weu.digitaltwins.azure.net"), ct => AmbilTokenAdtAsync(ct));
var pompa = await adt.QueryGeoAsync("SELECT * FROM digitaltwins WHERE IS_OF_MODEL('dtmi:demo:Pump;1')");
await adt.UpdatePropertiesAsync("pump-1", new Dictionary<string, object?> { ["Pressure"] = 3.1 });
await adt.SyncAsync(layer);                                  // Longitude/Latitude, nilai sensor, zona
```

Autentikasi:

| Layanan | Pilihan |
|---|---|
| Azure Maps | Subscription key, atau token bearer Microsoft Entra ID ditambah `x-ms-client-id` |
| AWS Location | SigV4 dengan `AwsCredentials`, atau API key (hanya places dan routes) |
| Azure Digital Twins | Token bearer dari `AccessTokenProvider` Anda |

Sumber token apa pun dapat dipasang sebagai `AccessTokenProvider`, misalnya `DefaultAzureCredential` dari Azure.Identity.

Kesalahan dilempar sebagai `CloudServiceException`, yang membawa status HTTP dan isi respons.

## Machine learning dan GPU

```csharp
using GeoPandas.Net.ML;

using var seg = new OnnxSegmenter("unet.onnx") { Bands = [2, 1, 0, 3], Mean = [...], Std = [...], TileSize = 512, Overlap = 32, Softmax = true,
                                                 ClassNames = ["air", "terbangun", "vegetasi"] };
var (kelas, probabilitas) = seg.Predict(citra);            // tile bertumpang, bagian tengah yang dipakai

var clf = LandCoverClassifier.Train(citra, poligonLatih, "class", algorithm: LandCoverAlgorithm.RandomForest);
var peta = clf.Predict(citra);
var laporan = clf.Evaluate(LandCoverClassifier.SamplePixels(citra, titikUji, "class", [0, 1, 2, 3]));
Console.WriteLine(laporan);                                // akurasi keseluruhan, kappa; laporan.ToFrame() = matriks konfusi
```

`OnnxSegmenter` menerima model dengan ukuran masukan tetap maupun tinggi dan lebar dinamis. Pada ukuran tetap, tile di tepi diberi padding nol. `LandCoverClassifier` menyediakan tiga algoritme:

- `RandomForest`: FastForest one-versus-all.
- `GradientBoosting`: FastTree one-versus-all.
- `MaximumEntropy`: SDCA.

```csharp
using GeoPandas.Net.Gpu;

using var gpu = GpuContext.Create();                        // CUDA → OpenCL → akselerator CPU
var shade = GpuRaster.Hillshade(gpu, dem, 315, 45);
var ndvi  = GpuRaster.NormalizedDifference(gpu, citra, bandA: 3, bandB: 2);
var idw   = GpuRaster.Idw(gpu, xs, ys, zs, dem.Transform, dem.Width, dem.Height, power: 2);
```

## Operasi GeoLLM

`GeoAssistant` memiliki empat operasi rencana baru yang masuk whitelist:

- `h3_bin`: binning heksagon.
- `within_distance`: baris dalam jarak tertentu dari sebuah titik, diurutkan menurut jarak.
- `simplify`: penyederhanaan geometri.
- `unique`: nilai unik sebuah kolom beserta jumlahnya.

Seperti sebelumnya, LLM hanya mengusulkan rencana JSON. GeoPandas.Net memvalidasi setiap langkah terhadap whitelist sebelum menjalankannya.

## Baris perintah

```text
gpd h3 bin pois.gpkg 8 --value capacity --out hex.gpkg
gpd h3 cell -6.2 106.8 9            gpd h3 disk 898c106a48fffff 2
gpd s2cell -6.2 106.8 --level 20
gpd partition points.parquet points_ds --scheme kd --size 100000
gpd dataset read points_ds --bbox 700000,9310000,705000,9315000 --out jendela.gpkg
gpd tiles3d buildings bangunan.gpkg tiles --height height
gpd tiles3d terrain dem.tif terrain
gpd explore districts.gpkg --column population --out peta.html
gpd geocode "Monas, Jakarta" --provider azure         # AZURE_MAPS_KEY
```

## Belum ada di rilis ini

Butir rencana berikut ditunda:

- **Adapter Orleans, Akka.NET dan Spark.** Model partisi di atas adalah titik integrasinya.
- **Pelatihan TorchSharp.** Model yang dilatih di tempat lain dijalankan lewat ONNX.
- **Embedding HF.Net dan IoTComNet.** Paket-paket ini belum ada di NuGet.
- **Penampil awan titik native OpenSceneGraph/ThreeNet.** Sebagai gantinya, awan titik diekspor sebagai glTF untuk Cesium atau penampil glTF apa pun.
