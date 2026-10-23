[English](../en/3d-tiles-and-data.md)

# 3D Tiles & data

![Contoh 3D Tiles](../images/gallery-3dtiles.png)

## Memuat tileset

```csharp
// tileset.json apa pun (fotogrametri, BIM, point cloud, model kota)
string mesh = await cesium.Tilesets.AddAsync(TilesetOptions.FromUrl("https://example.com/site/tileset.json", flyTo: true));

// Aset Cesium ion (butuh token)
string ionTs = await cesium.Tilesets.AddAsync(TilesetOptions.FromIon(2275207));

// Cesium OSM Buildings (butuh token)
string osm = await cesium.Tilesets.AddAsync(TilesetOptions.OsmBuildings());

// Google Photorealistic 3D Tiles: lewat ion (token) atau API key Google Maps
string google = await cesium.Tilesets.AddAsync(TilesetOptions.GooglePhotorealistic(key: null));
```

`TilesetOptions` yang berguna:

| Properti | Efek |
|---|---|
| `MaximumScreenSpaceError` | Keseimbangan detail dan kecepatan. Default 16; nilai lebih kecil lebih tajam, nilai lebih besar lebih cepat. |
| `SkipLevelOfDetail` | Melewati LOD perantara sehingga dataset masif lebih cepat dimuat (hanya saat dibuat). |
| `DynamicScreenSpaceError` | Menurunkan detail tile yang jauh pada tampilan ke arah horizon (hanya saat dibuat). |
| `HeightOffset` | Menggeser tileset naik/turun dalam meter, misalnya agar fotogrametri pas di atas terrain. |
| `Shadows` | Melempar/menerima bayangan (hanya saat dibuat). |
| `FlyTo` | Zoom ke tileset setelah selesai dimuat. |
| `DebugShowBoundingVolume` | Menampilkan batas tile. |

`Tilesets.UpdateAsync` menerapkan ulang `Show`, `MaximumScreenSpaceError`, `DebugShowBoundingVolume`, `Style`, `PointCloudShading`, `HeightOffset`, `ClippingPolygons`/`ClipInverse`, dan `CustomShader`. Opsi yang hanya berlaku saat pembuatan diabaikan ketika update.

Tile Google Photorealistic datang dengan atribusi dan ketentuan dari Google. Biarkan `ShowCredits = true`; lihat [Offline & Cesium ion](offline-and-ion.md#atribusi).

## Styling

```csharp
await cesium.Tilesets.SetStyleAsync(osm, new TilesetStyle
{
    ColorConditions =
    [
        ["${feature['building']} === 'hospital'", "color('#FF3B30')"],
        ["Number(${feature['cesium#estimatedHeight']}) > 100", "color('#1F4FE0', 0.9)"],
        ["true", "color('white', 0.8)"],
    ],
    Show = "${feature['building']} !== 'construction'",
});
await cesium.Tilesets.SetStyleAsync(osm, null); // reset
```

Ekspresi memakai [bahasa styling 3D Tiles](https://github.com/CesiumGS/3d-tiles/tree/main/specification/Styling), dan kondisi pertama yang cocok yang dipakai. `Defines` mendeklarasikan ekspresi bernama yang bisa dipakai ulang.

## Picking fitur (BIM, model kota)

```csharp
cesium.LeftClick += (_, pick) =>
{
    if (pick.Kind != PickKind.TileFeature || pick.Properties is null) return;
    // pick.OwnerId adalah id tileset; pick.Properties berisi properti fitur (mis. atribut IFC).
    foreach (var (k, v) in pick.Properties) Console.WriteLine($"{k} = {v}");
};
```

Untuk menyembunyikan atau mewarnai fitur tertentu, buat style yang mencocokkan properti fitur, misalnya id elemen.

## Point cloud (LiDAR)

```csharp
string cloud = await cesium.Tilesets.AddAsync(new TilesetOptions
{
    Kind = TilesetKind.Url, Url = "https://example.com/lidar/tileset.json", FlyTo = true,
    PointCloudShading = new PointCloudShading { Attenuation = true, MaximumAttenuation = 4, EyeDomeLighting = true, EyeDomeLightingStrength = 1.0, EyeDomeLightingRadius = 1.0 },
    Style = new TilesetStyle { PointSize = "3", Color = "color('#00C7FF')" },
});
```

## Clipping dan custom shader (digital twin)

```csharp
// Lubangi mesh fotogrametri di lokasi rencana desain baru:
await cesium.Tilesets.UpdateAsync(mesh, new TilesetOptions
{
    ClippingPolygons = [Geodesy.Circle(new GeoPoint(106.8272, -6.1754), 80, 32)],
});

// Shader garis pindai beranimasi; u_time tersedia bila AnimateTime = true.
string twin = await cesium.Tilesets.AddAsync(new TilesetOptions
{
    Kind = TilesetKind.Url, Url = "https://example.com/plant/tileset.json",
    CustomShader = new CustomShaderOptions
    {
        LightingModel = "PBR",
        AnimateTime = true,
        Uniforms = new() { ["u_intensity"] = 0.6 },
        FragmentShaderText = """
            void fragmentMain(FragmentInput fsInput, inout czm_modelMaterial material) {
                float h = fsInput.attributes.positionMC.z;
                float band = smoothstep(0.0, 0.5, abs(fract(h * 0.05 - u_time * 0.2) - 0.5));
                material.diffuse = mix(vec3(0.0, 0.78, 1.0), material.diffuse, mix(1.0, band, u_intensity));
            }
            """,
    },
});
await cesium.Tilesets.SetShaderUniformAsync(twin, "u_intensity", 0.9);
```

`Uniforms` hanya menerima uniform bertipe float. Custom shader tersedia untuk 3D Tiles; untuk model dan entitas, gunakan `ExecuteScriptAsync`.

## Data vektor: GeoJSON, TopoJSON, KML/KMZ, CZML, GPX

```csharp
// GeoJSON dari URL, diekstrusi berdasarkan properti numerik (bangunan bergaya choropleth)
string districts = await cesium.DataSources.LoadAsync(new DataSourceOptions
{
    Kind = DataSourceKind.GeoJson, Url = "https://example.com/districts.geojson", Name = "Districts", FlyTo = true,
    Stroke = CesiumColor.White, StrokeWidth = 2, Fill = CesiumColor.Blue.WithAlpha(0.4f),
    ExtrudeByProperty = "population", ExtrudeScale = 0.01, RandomFill = true,
});

// GeoJSON inline
string pts = await cesium.DataSources.LoadAsync(DataSourceOptions.GeoJsonData("""
    { "type": "FeatureCollection", "features": [
      { "type": "Feature", "properties": { "name": "Monas" }, "geometry": { "type": "Point", "coordinates": [106.8272, -6.1754] } } ] }
    """));

await cesium.DataSources.LoadAsync(DataSourceOptions.TopoJson("https://example.com/provinces.topojson"));
await cesium.DataSources.LoadAsync(DataSourceOptions.Kml("https://example.com/route.kmz"));      // menempel ke tanah
await cesium.DataSources.LoadAsync(DataSourceOptions.Gpx("https://example.com/ride.gpx"));
string czml = await cesium.DataSources.LoadAsync(DataSourceOptions.Czml("https://example.com/mission.czml"));

int count = await cesium.DataSources.CountAsync(districts);
await cesium.DataSources.SetVisibleAsync(districts, false);
await cesium.Camera.FlyToObjectAsync(czml);
await cesium.DataSources.RemoveAsync(pts);
```

- `Data` menerima teks inline: JSON untuk GeoJSON, TopoJSON, dan CZML, serta XML untuk KML dan GPX. KMZ harus dimuat dari URL.
- Dokumen CZML biasanya membawa clock sendiri. Panggil `Clock.ConfigureAsync` setelahnya bila ingin rentang waktu lain.
- Entitas dari data source tetap memakai id dari file. Entitas ini bisa diakses lewat `Entities.UpdateAsync`, `SetVisibleAsync`, `SelectAsync`, `GetPositionAsync`, dan `RemoveAsync`.
- Di Avalonia, sajikan file lokal dengan [`MapFolder`](avalonia.md#cesiumlocalserver-dan-mapfolder).

## 100rb titik dengan primitive

Entitas memang fleksibel, tetapi setiap objeknya memakan CPU. Untuk himpunan titik statis berukuran besar (sensor, alamat, sampel LiDAR), gunakan `PointPrimitiveCollection` berbasis GPU:

```csharp
var rnd = new Random(42);
var positions = new GeoPoint[150_000];
var colors = new CesiumColor[positions.Length];
for (var i = 0; i < positions.Length; i++)
{
    positions[i] = new GeoPoint(95 + rnd.NextDouble() * 46, -11 + rnd.NextDouble() * 17);
    colors[i] = CesiumColor.Lerp(CesiumColor.Cyan, CesiumColor.Magenta, (float)rnd.NextDouble());
}

string cloudId = await cesium.Primitives.AddPointsAsync(new PointCollectionOptions
{
    Positions = positions, Colors = colors, PixelSize = 3,
    ScaleByDistance = new NearFarScalar(1e4, 2.0, 5e6, 0.6),
});

cesium.LeftClick += (_, p) =>
{
    if (p.Kind == PickKind.Primitive && p.OwnerId == cloudId)
        Console.WriteLine($"Point #{p.Properties?["index"]}");
};
await cesium.Camera.FlyToObjectAsync(cloudId);
```

Koleksi titik bersifat statis. Untuk mengubahnya, hapus lalu tambahkan lagi. Koleksi ini mendukung `SetVisibleAsync` dan `RemoveAsync`. Warna per titik di-dedup di sisi JavaScript, jadi palet kecil tetap ringan.

---
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
