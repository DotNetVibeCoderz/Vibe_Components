[English](../en/offline-and-ion.md)

# Offline & Cesium ion

## Apa dimuat dari mana

| Sumber daya | Sumber default | Alternatif offline |
|---|---|---|
| CesiumJS (`Cesium.js`, `Widgets/`, `Workers/`, `Assets/`, `ThirdParty/`) | `https://cdn.jsdelivr.net/npm/cesium@1.146.0/Build/Cesium/` | Host build sendiri; atur `CesiumBaseUrl` |
| JS bridge (`cesium-net.js`) | Sudah disertakan: static web asset Blazor / embedded resource Avalonia | Sudah lokal |
| Imagery dasar | Tile OpenStreetMap | Tile TMS/XYZ lokal, `SingleTile`, atau Natural Earth II bawaan CesiumJS |
| Terrain | Elipsoid (tanpa jaringan) | Elipsoid, atau server quantized-mesh lokal |
| Pin dengan ikon Maki | `{CesiumBaseUrl}/Assets/Textures/maki/` | Ikut serta dalam build yang di-host sendiri |
| Data Anda (glTF, GeoJSON, 3D Tiles…) | sesuai URL Anda | `wwwroot` di Blazor, `MapFolder` di Avalonia |

## Mendapatkan build CesiumJS

```bash
npm pack cesium@1.146.0          # mengunduh cesium-1.146.0.tgz
tar -xzf cesium-1.146.0.tgz      # → package/Build/Cesium/
```

Gunakan versi yang sama, karena bridge diuji terhadap `CesiumViewerOptions.DefaultCesiumVersion` (`1.146.0`). Untuk memakai versi CDN lain tanpa self-hosting, atur `CesiumBaseUrl = CesiumScripts.DefaultCesiumBaseUrl("1.xxx.0")` (namespace `CesiumNet.Interop`).

## Blazor: `wwwroot` + `CesiumBaseUrl`

1. Salin `package/Build/Cesium/` ke `wwwroot/cesium/` di proyek **host** (untuk WebAssembly, proyek client).
2. Arahkan Cesium.Net ke sana, baik secara global maupun per viewer:

```csharp
builder.Services.AddCesiumNet(o => o.CesiumBaseUrl = "/cesium/");
// atau
var options = new CesiumViewerOptions { CesiumBaseUrl = "/cesium/" };
```

3. Gunakan lapisan dasar yang bisa offline:

```csharp
var options = new CesiumViewerOptions
{
    CesiumBaseUrl = "/cesium/",
    BaseLayer = new ImageryLayerOptions { Provider = ImageryProviderKind.Tms, Url = "/cesium/Assets/Textures/NaturalEarthII" },
    Terrain = TerrainOptions.Ellipsoid(),
};
```

Build ini berukuran sekitar 30–40 MB dalam kira-kira seribu berkas. Jika Anda memakai `MapStaticAssets`, berkas-berkas itu di-fingerprint dan dikompresi saat build, sehingga build pertama lebih lama. Jika itu jadi masalah, layani foldernya dengan `app.UseStaticFiles()` dari path terpisah.

## Avalonia: `MapFolder`

```csharp
var cesiumUrl = CesiumLocalServer.Shared.MapFolder("cesium", Path.Combine(AppContext.BaseDirectory, "cesium"));
var tilesUrl  = CesiumLocalServer.Shared.MapFolder("tiles",  @"D:\maps\jakarta-xyz");

Globe.Options = new CesiumViewerOptions
{
    CesiumBaseUrl = cesiumUrl,
    BaseLayer = ImageryLayerOptions.FromTemplate(tilesUrl + "{z}/{x}/{y}.png", "© Local survey", maximumLevel: 18),
};
```

```xml
<ItemGroup>
  <None Include="cesium\**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Avalonia tidak punya padanan `AddCesiumNet`, jadi atur `CesiumBaseUrl` di `Options` setiap `CesiumView`. Lihat [Avalonia](avalonia.md#cesiumlocalserver-dan-mapfolder) untuk tipe MIME dan batasannya (nama satu segmen, tanpa `Content-Encoding`).

## Cesium ion

[Cesium ion](https://cesium.com/ion) meng-host terrain, imagery, 3D Tiles (termasuk fotogrametri dan konversi BIM), dan glTF. **Token** hanya diperlukan untuk konten ion:

| Fitur | Perlu token ion |
|---|---|
| `TerrainOptions.WorldTerrain()`, `TerrainProviderKind.Ion` | ya |
| `TilesetOptions.OsmBuildings()`, `FromIon(id)` | ya |
| `TilesetOptions.GooglePhotorealistic()` tanpa `key` | ya |
| `ImageryProviderKind.Ion`, Bing lewat ion | ya |
| Widget `BaseLayerPicker`, `Geocoder` (konten default ion/Bing) | ya |
| OSM, Esri, CARTO, WMS/WMTS/TMS/XYZ, terrain ArcGIS, 3D Tiles milik Anda | tidak |

### Memberikan token

```csharp
// Blazor: default untuk seluruh aplikasi (appsettings / user secrets / environment)
builder.Services.AddCesiumNet(o => o.IonAccessToken = builder.Configuration["Cesium:IonToken"]);

// Host apa pun: per viewer
var options = new CesiumViewerOptions { IonAccessToken = token };
```

Token dikirim ke browser atau WebView karena CesiumJS memanggil ion secara langsung. Token itu tidak rahasia, jadi perlakukan sebagai data publik:

- Buat token khusus yang hanya punya scope **`assets:read`** dan hanya untuk aset yang dibutuhkan.
- Batasi token ke **allowed URLs** di dashboard ion. Di Avalonia origin-nya `http://localhost:{port acak}`.
- Jangan pernah mengirim token dengan scope `assets:list`, `assets:write`, atau scope akun lain ke client.

Semua viewer dalam satu halaman berbagi `Cesium.Ion.defaultAccessToken`. Jika dua viewer Blazor di halaman yang sama memakai token berbeda, token terakhir yang berlaku.

### `CesiumIonClient`

Client REST minimal di sisi server untuk mendaftar dan memeriksa aset Anda, misalnya untuk membuat pemilih aset:

```csharp
using CesiumNet.Ion;

var ion = new CesiumIonClient(httpClient, serverSideToken);        // token dengan assets:list / assets:read
IReadOnlyList<IonAsset> assets = await ion.ListAssetsAsync();
foreach (var a in assets.Where(a => a.Status == "COMPLETE"))
    Console.WriteLine($"{a.Id,8} {a.Type,-8} {a.Name}");

IonAsset? one = await ion.GetAssetAsync(CesiumIonClient.WellKnownAssets.CesiumOsmBuildings);

// Serahkan id ke viewer (yang memakai token read-only miliknya sendiri):
var asset = assets.First(a => a.Type == "3DTILES");
await cesium.Tilesets.AddAsync(TilesetOptions.FromIon(asset.Id, flyTo: true));
```

- `ListAssetsAsync` membutuhkan scope `assets:list`; `GetAssetAsync` membutuhkan `assets:read` dan mengembalikan `null` pada 404. Error HTTP lain melempar `HttpRequestException`. **401** berarti token tidak ada atau tidak valid; **403** berarti scope kurang.
- `Type` aset: `3DTILES`, `GLTF`, `IMAGERY`, `TERRAIN`, `KML`, `CZML`, `GEOJSON`. `Status`: `COMPLETE`, `IN_PROGRESS`, `AWAITING_FILES`, `NOT_STARTED`, `DATA_ERROR`, `ERROR`.
- Upload, tiling, konversi BIM, dan versioning aset dilakukan di aplikasi web atau REST API ion. Fitur-fitur itu tidak dibungkus oleh `CesiumIonClient`.
- `WellKnownAssets`: `CesiumWorldTerrain` (1), `BingAerial` (2), `BingAerialWithLabels` (3), `BingRoad` (4), `CesiumOsmBuildings` (96188), `GooglePhotorealistic3DTiles` (2275207), `Sentinel2` (3954), `EarthAtNight` (3812).

## Atribusi

Sebagian besar lisensi data peta mewajibkan atribusi yang terlihat.

- Biarkan `CesiumViewerOptions.ShowCredits = true` (default). Opsi ini menampilkan bar kredit CesiumJS, termasuk logo Cesium ion saat konten ion dipakai, serta kredit setiap provider.
- Isi `ImageryLayerOptions.Credit` untuk lapisan kustom. Preset bawaan sudah menyertakan kreditnya: Esri, "© OpenStreetMap contributors © CARTO", dan seterusnya.
- **OpenStreetMap**: "© OpenStreetMap contributors" wajib ditampilkan. Server publik `tile.openstreetmap.org` tidak ditujukan untuk trafik produksi yang besar, jadi gunakan server tile komersial atau yang di-host sendiri.
- Basemap **Esri**: pertahankan atribusi Esri dan ikuti ketentuan Esri untuk penggunaan produksi.
- **Google Photorealistic 3D Tiles**: logo Google dan atribusi datanya harus tetap terlihat.
- **Bing Maps**: membutuhkan key Anda sendiri dan atribusi Microsoft.
- Jika bar kredit disembunyikan (`ShowCredits = false`), Anda wajib menampilkan atribusi yang setara di UI Anda sendiri.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
