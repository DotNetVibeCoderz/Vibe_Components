# Arsitektur

```
        ┌──────────── your app ────────────┐
        │  scene graph (OpenSceneGraph.Net) │   Group / Transform / Lod / Geometry / StateSet ...
        └──────────────┬───────────────────┘
                       │ Viewer.Frame()
     events → update traversal → pager merge → manipulator → CullVisitor
                       │
                       ▼
                  RenderList            passes (RTT, main, HUD) · sorted draw items · lights · environment
        ┌──────────────┼───────────────────────┬──────────────────────┐
        ▼              ▼                       ▼                      ▼
  GlRenderer      Blazor WebGL2          NativeSceneConverter     your renderer
  (Avalonia)      (OsgCanvas)            → osgViewer (OSG 3.6.5)   (IRenderer)
```

## Proyek

| Proyek | Peran |
|---|---|
| `src/OpenSceneGraph.Net` | Inti: Math, Scene, State, Meshes, Rendering (cull/render list/shader), Manipulators, Animation, Particles, IO, Terrain, Geo, Simulation, Science, Interop (fast path Rust) |
| `src/OpenSceneGraph.Net.Rendering.OpenGL` | `GlRenderer` yang dibangun di atas Silk.NET.OpenGL |
| `src/OpenSceneGraph.Net.Avalonia` | `OsgView : OpenGlControlBase` |
| `src/OpenSceneGraph.Net.Blazor` | Komponen `OsgCanvas` dan modul JS WebGL2 |
| `src/OpenSceneGraph.Net.Native` | P/Invoke ke `osgnet.dll`, sebuah shim C ABI di atas OSG |
| `native/osgnet` | Shim C++ (CMake) |
| `native/osgnet-fast` | `cdylib` Rust: normal, mesh heightfield, batch sphere culling, integrasi partikel |
| `src/OpenSceneGraph.Net.Gallery.Core` | 22 sampel yang dipakai bersama oleh kedua aplikasi gallery |
| `src/OpenSceneGraph.Net.Cli`, `src/OpenSceneGraph.Net.Templates` | Tooling |

## Frame

`Viewer.Frame()` menjalankan langkah-langkah berikut secara berurutan:

1. **Memajukan** `FrameStamp` (jam dinding atau `FixedTimeStep`, `TimeScale`, `Paused`).
2. **Event traversal.** Mengosongkan `EventQueue`; `EventHandlers` yang terdaftar mendapat kesempatan pertama, lalu camera manipulator.
3. **Update traversal.** Menggabungkan hasil muat `DatabasePager` yang sudah selesai, menjalankan setiap `IUpdateCallback`, memicu `Viewer.Updated`, memperbarui manipulator, dan membuang tile paged yang tidak terpakai.
4. **Cull traversal** (`CullVisitor`). Menelusuri graph dengan matriks akumulasi dan `ResolvedState` yang diwariskan, menolak subgraph yang tidak terlihat, memilih child LOD, mengirim permintaan paging, mengumpulkan lampu, membuka pass tambahan untuk `CameraNode`, dan menghitung bidang near/far.

Setelah itu host memanggil `renderer.Render(list)`. Renderer tidak pernah menyentuh scene graph. Renderer hanya membaca `RenderList` beserta objek `Geometry`/`Texture2D` yang dirujuknya, yang di-cache di GPU berdasarkan `Id`/`Version`.

## Kontrak RenderList

- `Passes` diurutkan: pre-render (render-to-texture), main, lalu post-render (HUD).
- Setiap pass memiliki item `Opaque` yang diurutkan berdasarkan render bin, lalu state key, lalu dari depan ke belakang, serta item `Transparent` yang diurutkan dari belakang ke depan.
- `DrawItem` menyimpan `Geometry`, `World` (matriks row-vector), `ResolvedState` bersama yang immutable, dan `Depth` terhadap view.
- `Lights` sudah berada di world space. Headlight ditambahkan secara otomatis jika `Environment.HeadLight` aktif.
- Matriks menggunakan `System.Numerics` (row-vector, `v' = v * M`). Mengunggahnya ke GLSL tanpa ditranspose menghasilkan padanan column-vector.

## Threading

`Viewer.ThreadingModel`:

- `SingleThreaded`: semuanya berjalan di thread pemanggil.
- `DrawThreadPerContext`: cull untuk frame N berjalan di worker sementara host menggambar frame N−1 (latensi satu frame). Update tidak pernah tumpang tindih dengan cull, sehingga mengubah scene dari update callback tetap aman.

Database pager selalu memuat data di background thread. Hasilnya digabungkan saat update traversal, sehingga graph tidak pernah dimodifikasi secara bersamaan.

## Catatan performa

- Resolved state di-cache per (state parent, StateSet, versi), sehingga culling dalam kondisi stabil hampir tidak melakukan alokasi.
- Render pass dan draw list di-pool dan dipakai ulang dari frame ke frame.
- Buffer geometry hanya diunggah ketika `Geometry.Version` berubah. Tandai mesh dinamis dengan `BufferUsage.Dynamic`/`Stream`.
- Geometry dan state set yang dipakai bersama berfungsi sebagai instancing: gambar `Geometry` yang sama di bawah banyak transform.
- Fast path Rust aktif di atas ambang ukuran tertentu, ketika overhead P/Invoke sudah tertutupi. Set `OSGNET_DISABLE_FAST=1` untuk melakukan benchmark jalur managed.
