# Tools

## CLI osgnet

```bash
dotnet tool install -g OpenSceneGraph.Net.Cli      # atau: dotnet run --project src/OpenSceneGraph.Net.Cli -- <command>
```

| Perintah | Contoh |
|---|---|
| `info` | Menampilkan versi, OSG native, fast path Rust, serta plugin managed dan native |
| `inspect <file> [--tree] [--native]` | `osgnet inspect cow.osgt --tree` |
| `convert <in> <out> [--native]` | `osgnet convert model.3ds model.osgb` · `osgnet convert part.stl part.obj` |
| `optimize <in> <out>` | `osgnet optimize city.osgt city_opt.osgb` |
| `view <file\|sample:id>` | `osgnet view sample:night-city` (jendela osgViewer native) |
| `render <file\|sample:id> <out.png>` | `osgnet render sample:molecule molecule.png --width 1920 --height 1080` |
| `terrain <out> [--dem f.tif] [--size --spacing --height --seed --tiles --levels]` | `osgnet terrain java.osgn --dem java.tif --tiles 8 --levels 4` |
| `samples` / `sample <id> <out>` | `osgnet sample ct-scan ct.osgt` |
| `dis listen` / `dis send` | `osgnet dis send --target 239.1.2.3 --count 100` |
| `geo <lat> <lon> [h]` | `osgnet geo -6.2088 106.8456` |

## Template proyek

```bash
dotnet new install OpenSceneGraph.Net.Templates
dotnet new osgnet-console  -o Hello3D        # jendela osgViewer native, atau `dotnet run -- --render`
dotnet new osgnet-avalonia -o Desktop3D      # jendela Avalonia + OsgView
dotnet new osgnet-blazor   -o Web3D          # Blazor WebAssembly + OsgCanvas
```

## Gallery

22 sampel dalam 9 kategori. Kode scene berada di `src/OpenSceneGraph.Net.Gallery.Core/SampleCatalog*.cs` dan dipakai bersama oleh:

- **Web:** `dotnet run --project src/OpenSceneGraph.Net.Gallery.Blazor`, lalu buka URL yang ditampilkan. Deep link berbentuk `/sample/<id>`.
- **Desktop:** `dotnet run --project src/OpenSceneGraph.Net.Gallery.Avalonia`. Opsi `--screenshot all docs/images/gallery` menulis file PNG untuk setiap sampel.

Setiap sampel menampilkan deskripsi, fitur OSG yang digunakan, telemetri langsung, key binding, dan cuplikan kode yang dapat disalin. Bahasa dapat dialihkan antara English dan Bahasa Indonesia.

## Simulator

`dotnet run --project src/OpenSceneGraph.Net.Simulator`: pesawat, UAV, kendaraan darat, atau kapal di atas terrain yang dihasilkan secara prosedural, dengan cuaca (kabut, hujan, salju), waktu dalam sehari, HUD, minimap, waypoint misi, kamera chase dan kokpit, serta broadcasting DIS opsional.

## Pipeline aset 3D (Rodin + Blender)

Model tank dan jet yang detail di gallery dibuat dengan **Rodin** (Hyper3D) dari prompt teks, lalu diproses oleh **Blender** yang berjalan secara headless:

```bash
blender -b --python tools/blender/glb_to_obj.py -- in.glb out.obj --size 7 --faces 6000 --yaw 180
```

Script tersebut menggabungkan mesh, mengorientasikan model dengan Z ke atas dan bagian hidung mengarah ke +Y, menskalakannya ke ukuran dunia nyata, meletakkannya di z = 0, mendesimasinya sesuai anggaran segitiga, lalu mem-bake texture base-colour ke PNG untuk reader OBJ/MTL managed milik OpenSceneGraph.Net. File GLB sumber (20 MB) tidak di-commit; hasil konversi OBJ + PNG ada di `src/OpenSceneGraph.Net.Gallery.Core/Assets/`. Ikon NuGet (`assets/icon.png`) juga dibuat dengan Rodin.
