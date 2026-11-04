# Scientific visualisation

## Colormaps

`ColorMap.Sample(kind, t)` and `ColorMap.Map(kind, value, min, max)` support `Viridis`, `Inferno`, `CoolWarm`, `Jet`, `Bone` and `Grayscale`. `ColorMap.Legend(kind)` returns a legend texture.

## Volumes

```csharp
var ct = ScalarField3D.Phantom(64);                               // synthetic CT head
var field = ScalarField3D.FromFunction(80, extent: 10, p => MathF.Sin(p.X) * MathF.Cos(p.Y) + p.Z * 0.1f);
var raw = new ScalarField3D(nx, ny, nz, spacing, origin, values);  // from DICOM, NetCDF, SEG-Y, FEM output...

root.AddChild(Isosurface.Extract(ct, iso: 600, color: bone));      // marching tetrahedra, welded, gradient normals
root.AddChild(VolumeSlices.Slice(ct, VolumeSlices.Axis.Z, 32, ColorMap.Kind.Bone));
root.AddChild(VolumeSlices.PointCloud(field, threshold: 0.8f, stride: 2));
```

Marching tetrahedra (6 tetrahedra per cell) gives watertight surfaces without the ambiguous cases of marching cubes. Shared edge vertices are welded, and normals come from the field gradient. Isosurfaces render two-sided. Make them translucent with `StateSet.MakeTransparent()` to see inner structures.

## Vector fields

```csharp
Vector3 U(Vector3 p) => ...;                                       // any velocity / force field
root.AddChild(VectorField.Glyphs(U, min, max, nx: 24, ny: 12, nz: 1, scale: 0.5f, ColorMap.Kind.CoolWarm));
root.AddChild(VectorField.Streamlines(U, seeds, steps: 400, stepSize: 0.05f, ColorMap.Kind.Inferno));  // RK4
```

## Surfaces and plots

`Shapes.Plane(sizeX, sizeY, divX, divY, color, height: (x, y) => f(x, y))` builds a function surface. For animated plots, mutate `Positions` and `Colors` each frame in an update callback, call `ComputeNormals()` (Rust accelerated), and set `Usage = BufferUsage.Dynamic`.

See the gallery samples: CFD flow, seismic volume, CT scan, function plotter and satellites.
