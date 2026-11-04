# Native OpenSceneGraph runtime

`OpenSceneGraph.Net.Native` wraps the real **OpenSceneGraph 3.6.5** through `osgnet.dll`, a small C ABI shim (`native/osgnet/osgnet.h`). It is optional: everything else in the library works without it.

![Native OSG offscreen render](../images/native/molecule.png)

## What it adds

- **53 OSG reader/writer plugins**: `.osg .osgt .osgb .osgx .ive .3ds .ac .dxf .flt (OpenFlight) .lwo .lws .x .md2 .bvh .shp .ply .stl .obj .osgjs` and more, plus OSG archives (`.osga`) and the pseudo-loaders (`.scale`, `.rot`, `.trans`).
- **osgViewer windows** with OSG's own manipulators and the stats, wireframe and lighting handlers.
- **Offscreen rendering** to an RGBA image (pbuffer, falling back to a hidden window), used for thumbnails, CI images and the documentation screenshots.
- **Optimizer and smoothing**: `osgUtil::Optimizer` (DEFAULT_OPTIMIZATIONS) and `SmoothingVisitor`.
- **Scene conversion** in both directions.

```csharp
OsgNative.Initialize();                         // registers plugin path + Registry fallback
Console.WriteLine(OsgNative.OsgVersion);        // "3.6.5"

using var cow = NativeNode.Read("cow.osgt");    // osg::Node, reference counted (SafeHandle)
cow.Optimize();
cow.Write("cow.osgb");
Group managed = cow.ToManaged();                // triangle meshes + transforms + material + texture

using var native = NativeSceneConverter.ToNative(myManagedScene);
using var viewer = NativeViewer.CreateWindow(1280, 720, "Viewer");
viewer.Scene = native;
viewer.SetManipulator(NativeManipulator.Terrain);
viewer.Run();

var image = NativeViewer.RenderToImage(myManagedScene, 1920, 1080, viewMatrix);
PngReader.Write("shot.png", image);
```

Once `Initialize()` has run, `Registry.ReadNodeFile("model.3ds")` works transparently: formats without a managed plugin go to OSG, and the result is converted to a managed graph.

## Conversion rules

- **Managed → native:** transforms become `MatrixTransform`s; `Switch` keeps only active children and `Lod` only the highest-detail child; geometry, per-vertex colours, texture coordinates, material diffuse colour, lighting on/off and textures are preserved; transparency switches on blending and the transparent bin.
- **Native → managed:** every drawable reachable from the node becomes a `Geometry` (triangles via `osg::TriangleIndexFunctor`) under a `MatrixTransform` carrying its world matrix. The nearest material diffuse colour, texture unit 0 (8-bit RGB/RGBA/luminance images) and lighting mode are taken from the node path. Lines and points are not extracted.

## Image plugins

This build has no libpng or libjpeg, so OSG itself reads only `.bmp .dds .tga .rgb .pnm .hdr .ktx .pic` images. The managed side decodes PNG. If you rebuild OSG with libpng/libjpeg (`native/build-osg.ps1`, CMake finds them automatically), the `png`/`jpeg` plugins are added.

## Building it yourself

```powershell
powershell -File native/build-osg.ps1      # OpenSceneGraph 3.6.5, Release x64 → native/third_party/osg-install
powershell -File native/build-osgnet.ps1   # osgnet.dll + staging into runtimes/win-x64/native
```

The script applies the fixes needed for current toolchains:

- CMake 4 needs `-DCMAKE_POLICY_VERSION_MINIMUM=3.5`.
- The Windows SDK clashes with C++17 `std::byte`, so the build uses C++14 with `/D_HAS_STD_BYTE=0`.
- Recent MSVC STL removed `_FPOSOFF`, which the osga plugin used; the script patches it.

On Linux and macOS, build OSG with its CMake and the shim with `native/osgnet/CMakeLists.txt` (`-DOSG_ROOT=...`), then place `libosgnet` next to your app. The managed code is platform neutral.

## ABI

All functions are `extern "C"`, use opaque handles and UTF-8 strings, and pass matrices as 16 floats in `osg::Matrixf` memory order (identical to `System.Numerics.Matrix4x4`). `osgnet_abi_version()` returns `1`, and `OsgNative.IsAvailable` checks it.
