# Scene graph

## Node

| Class | Padanan OSG | Catatan |
|---|---|---|
| `Node` | `osg::Node` | `Name`, `NodeMask`, `StateSet`, `UpdateCallbacks`/`OnUpdate`, `CullingActive`, `ClusterCull`, `UserData`, `Bound` yang di-cache |
| `Group`, `Geode` | `osg::Group`, `osg::Geode` | Child boleh dipakai bersama (instancing); `FindByName` |
| `MatrixTransform` | `osg::MatrixTransform` | `Matrix` (row-vector) |
| `PositionAttitudeTransform` | `osg::PositionAttitudeTransform` | `Position`, `Attitude` (quaternion), `Scale`, `PivotPoint` |
| `AutoTransform` | `osg::AutoTransform` | Billboard dan ukuran layar konstan |
| `Switch` | `osg::Switch` | `SetValue`, `SetSingleChildOn`, `SetAllChildrenOff` |
| `Lod` | `osg::LOD` | Rentang berdasarkan jarak atau ukuran piksel, titik pusat yang ditentukan pengguna |
| `PagedLod` | `osg::PagedLOD` | Child berbasis file yang dimuat oleh `DatabasePager`, dengan kedaluwarsa |
| `ProxyNode` | `osg::ProxyNode` | Memuat file sekali saja, pada traversal pertama |
| `LightSource` | `osg::LightSource` | Memosisikan `Light` dengan transform akumulasi |
| `CameraNode` | `osg::Camera` (di dalam graph) | HUD (post-render, ortho piksel) atau render-to-texture (`RenderTarget`) |
| `OccluderNode` | `osg::OccluderNode` | Occluder planar konveks untuk occlusion culling di CPU |
| `Geometry` | `osg::Geometry` | Array ditambah satu primitive set; panggil `MarkDirty()` setelah mengubah array |

```csharp
var lod = new Lod();
lod.AddChild(Shapes.Sphere(1, 64, 32), 0, 30);       // dekat
lod.AddChild(Shapes.Sphere(1, 8, 4), 30, 1000);      // jauh

var tiles = new PagedLod { CenterMode = LodCenterMode.UserDefinedCenter, Center = c, Radius = 500, DatabasePath = "tiles" };
tiles.AddChild(lowRes, 0, float.MaxValue);           // level kasar yang tertanam
tiles.SetFileName(1, "tile_0_0_hi.osgn");
tiles.SetRange(1, 0, 800);                           // dimuat jika lebih dekat dari 800 m
```

## State set

`StateSet` menyimpan `Material`, `Texture`, `Program`, `Lighting`, `CullFace`, `Blend`, `PolygonMode`, `DepthTest`, `DepthWrite`, `RenderBin`, `LineWidth`, `PointSize`, `CastShadows`, `ReceiveShadows`, `Fog`, dan `Uniforms`.
Nilai yang tidak diset (`null`) diwariskan dari parent. `Override = true` membuat atribut yang diset di sini mengalahkan atribut milik turunannya, seperti `osg::StateAttribute::OVERRIDE`.

```csharp
var xray = new Group { StateSet = new StateSet { PolygonMode = PolygonMode.Line, Override = true } };
glass.GetOrCreateStateSet().MakeTransparent();       // AlphaBlend, bin transparan, tanpa depth write
```

Material dengan alpha di bawah 1 otomatis menjadi blended dan diurutkan berdasarkan kedalaman.

## Visitor

Turunkan dari `NodeVisitor` (atau `MatrixTrackingVisitor` untuk mendapatkan `CurrentMatrix`) lalu override overload `Apply`. Visitor bawaan:

- `UpdateVisitor`
- `CullVisitor`
- `RayIntersector` (picking)
- `FindNodesVisitor` (`FindNodesVisitor.FindAll<Geometry>(root)`)
- `StatsVisitor`

```csharp
var hit = viewer.Pick(mouseX, mouseY);              // segitiga terdekat di bawah kursor
if (hit is { } h) Console.WriteLine($"{h.Drawable.Name} at {h.WorldPoint}");
```

## Culling

`viewer.CullSettings.Mode` merupakan kombinasi dari flag di bawah ini. `SmallFeaturePixels` (default 1.5 px) dan `LodScale` digunakan untuk menyetel culling.

| Flag | Yang ditolak |
|---|---|
| `ViewFrustum` | Bounding sphere yang berada di luar enam bidang frustum |
| `SmallFeature` | Objek yang ukuran proyeksinya di bawah `SmallFeaturePixels` |
| `Cluster` | Subgraph yang membelakangi mata pengamat, melalui `Node.ClusterCull` (titik kontrol, normal, deviasi); dipakai untuk terrain berskala planet |
| `Occlusion` | Sphere yang sepenuhnya tersembunyi di balik poligon `OccluderNode` (shadow volume yang dibangun dari posisi mata) |

`RenderStats` melaporkan `NodesVisited`, `NodesCulled`, `DrawablesCulled`, `DrawItems`, `Triangles`, dan `CullMs`.

## Format file

`Registry.ReadNodeFile` / `WriteNodeFile` memilih plugin berdasarkan ekstensi.

| Format | Baca | Tulis | Catatan |
|---|---|---|---|
| `.osgn` | ✅ | ✅ | Format native managed: JSON dengan array base64; node dan state set yang dipakai bersama tetap dipertahankan |
| `.obj` + `.mtl` | ✅ | ✅ | Material, texture `map_Kd`, warna per vertex, indeks negatif |
| `.stl` | ✅ ASCII/binary | ✅ binary | |
| `.ply` | ✅ ASCII / binary LE | — | Posisi, normal, warna, face atau point cloud |
| `.asc`, `.tif` | ✅ | — | DEM menjadi terrain bertile (lihat GIS) |
| gambar | PNG, TGA, BMP, PPM/PGM | PNG | Daftarkan `IImageReader` untuk JPEG |
| format lainnya | melalui `OpenSceneGraph.Net.Native` | melalui native | 53 plugin OSG |
