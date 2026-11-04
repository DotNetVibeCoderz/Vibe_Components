# Visualisasi ilmiah

## Colormap

`ColorMap.Sample(kind, t)` dan `ColorMap.Map(kind, value, min, max)` mendukung `Viridis`, `Inferno`, `CoolWarm`, `Jet`, `Bone`, dan `Grayscale`. `ColorMap.Legend(kind)` mengembalikan texture legenda.

## Volume

```csharp
var ct = ScalarField3D.Phantom(64);                               // kepala CT sintetis
var field = ScalarField3D.FromFunction(80, extent: 10, p => MathF.Sin(p.X) * MathF.Cos(p.Y) + p.Z * 0.1f);
var raw = new ScalarField3D(nx, ny, nz, spacing, origin, values);  // dari DICOM, NetCDF, SEG-Y, output FEM...

root.AddChild(Isosurface.Extract(ct, iso: 600, color: bone));      // marching tetrahedra, di-weld, normal dari gradien
root.AddChild(VolumeSlices.Slice(ct, VolumeSlices.Axis.Z, 32, ColorMap.Kind.Bone));
root.AddChild(VolumeSlices.PointCloud(field, threshold: 0.8f, stride: 2));
```

Marching tetrahedra (6 tetrahedron per sel) menghasilkan permukaan yang kedap (watertight) tanpa kasus ambigu seperti pada marching cubes. Vertex pada edge yang dipakai bersama di-weld, dan normal diperoleh dari gradien field. Isosurface dirender dua sisi. Buat semi-transparan dengan `StateSet.MakeTransparent()` untuk melihat struktur bagian dalam.

## Medan vektor

```csharp
Vector3 U(Vector3 p) => ...;                                       // medan kecepatan / gaya apa pun
root.AddChild(VectorField.Glyphs(U, min, max, nx: 24, ny: 12, nz: 1, scale: 0.5f, ColorMap.Kind.CoolWarm));
root.AddChild(VectorField.Streamlines(U, seeds, steps: 400, stepSize: 0.05f, ColorMap.Kind.Inferno));  // RK4
```

## Permukaan dan plot

`Shapes.Plane(sizeX, sizeY, divX, divY, color, height: (x, y) => f(x, y))` membangun permukaan fungsi. Untuk plot beranimasi, ubah `Positions` dan `Colors` setiap frame di dalam update callback, panggil `ComputeNormals()` (diakselerasi Rust), dan set `Usage = BufferUsage.Dynamic`.

Lihat sampel di gallery: aliran CFD, volume seismik, CT scan, plotter fungsi, dan satelit.
