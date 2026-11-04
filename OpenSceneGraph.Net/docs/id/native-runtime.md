# Runtime OpenSceneGraph native

`OpenSceneGraph.Net.Native` membungkus **OpenSceneGraph 3.6.5** yang asli melalui `osgnet.dll`, sebuah shim C ABI kecil (`native/osgnet/osgnet.h`). Paket ini opsional: seluruh bagian lain dari library tetap berfungsi tanpanya.

![Render offscreen OSG native](../images/native/molecule.png)

## Yang ditambahkan

- **53 plugin reader/writer OSG**: `.osg .osgt .osgb .osgx .ive .3ds .ac .dxf .flt (OpenFlight) .lwo .lws .x .md2 .bvh .shp .ply .stl .obj .osgjs` dan lainnya, ditambah arsip OSG (`.osga`) serta pseudo-loader (`.scale`, `.rot`, `.trans`).
- **Jendela osgViewer** dengan manipulator bawaan OSG serta handler stats, wireframe, dan lighting.
- **Rendering offscreen** ke gambar RGBA (pbuffer, dengan fallback ke jendela tersembunyi), yang dipakai untuk thumbnail, gambar CI, dan screenshot dokumentasi.
- **Optimizer dan smoothing**: `osgUtil::Optimizer` (DEFAULT_OPTIMIZATIONS) dan `SmoothingVisitor`.
- **Konversi scene** dua arah.

```csharp
OsgNative.Initialize();                         // mendaftarkan path plugin + fallback Registry
Console.WriteLine(OsgNative.OsgVersion);        // "3.6.5"

using var cow = NativeNode.Read("cow.osgt");    // osg::Node, reference counted (SafeHandle)
cow.Optimize();
cow.Write("cow.osgb");
Group managed = cow.ToManaged();                // mesh segitiga + transform + material + texture

using var native = NativeSceneConverter.ToNative(myManagedScene);
using var viewer = NativeViewer.CreateWindow(1280, 720, "Viewer");
viewer.Scene = native;
viewer.SetManipulator(NativeManipulator.Terrain);
viewer.Run();

var image = NativeViewer.RenderToImage(myManagedScene, 1920, 1080, viewMatrix);
PngReader.Write("shot.png", image);
```

Setelah `Initialize()` dijalankan, `Registry.ReadNodeFile("model.3ds")` bekerja secara transparan: format yang tidak memiliki plugin managed diteruskan ke OSG, lalu hasilnya dikonversi menjadi graph managed.

## Aturan konversi

- **Managed → native:** transform menjadi `MatrixTransform`; `Switch` hanya mempertahankan child yang aktif dan `Lod` hanya child dengan detail tertinggi; geometry, warna per vertex, koordinat texture, warna diffuse material, status lighting on/off, dan texture dipertahankan; transparansi mengaktifkan blending dan bin transparan.
- **Native → managed:** setiap drawable yang dapat dijangkau dari node menjadi `Geometry` (segitiga melalui `osg::TriangleIndexFunctor`) di bawah `MatrixTransform` yang membawa world matrix-nya. Warna diffuse material terdekat, texture unit 0 (gambar RGB/RGBA/luminance 8-bit), dan mode lighting diambil dari node path. Garis dan titik tidak diekstrak.

## Plugin gambar

Build ini tidak menyertakan libpng maupun libjpeg, sehingga OSG sendiri hanya dapat membaca gambar `.bmp .dds .tga .rgb .pnm .hdr .ktx .pic`. Sisi managed dapat mendekode PNG. Jika Anda mem-build ulang OSG dengan libpng/libjpeg (`native/build-osg.ps1`, CMake akan menemukannya secara otomatis), plugin `png`/`jpeg` akan ditambahkan.

## Mem-build sendiri

```powershell
powershell -File native/build-osg.ps1      # OpenSceneGraph 3.6.5, Release x64 → native/third_party/osg-install
powershell -File native/build-osgnet.ps1   # osgnet.dll + penyalinan ke runtimes/win-x64/native
```

Script tersebut menerapkan perbaikan yang diperlukan untuk toolchain terkini:

- CMake 4 memerlukan `-DCMAKE_POLICY_VERSION_MINIMUM=3.5`.
- Windows SDK berbenturan dengan `std::byte` C++17, sehingga build menggunakan C++14 dengan `/D_HAS_STD_BYTE=0`.
- STL MSVC versi terbaru menghapus `_FPOSOFF` yang dipakai oleh plugin osga; script akan mem-patch-nya.

Di Linux dan macOS, build OSG dengan CMake bawaannya dan build shim dengan `native/osgnet/CMakeLists.txt` (`-DOSG_ROOT=...`), lalu letakkan `libosgnet` di samping aplikasi Anda. Kode managed bersifat platform-neutral.

## ABI

Semua fungsi bersifat `extern "C"`, menggunakan opaque handle dan string UTF-8, serta mengirim matriks sebagai 16 float dalam urutan memori `osg::Matrixf` (identik dengan `System.Numerics.Matrix4x4`). `osgnet_abi_version()` mengembalikan `1`, dan `OsgNative.IsAvailable` memeriksanya.
