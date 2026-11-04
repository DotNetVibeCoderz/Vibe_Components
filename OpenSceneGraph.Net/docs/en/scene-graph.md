# Scene graph

## Nodes

| Class | OSG equivalent | Notes |
|---|---|---|
| `Node` | `osg::Node` | `Name`, `NodeMask`, `StateSet`, `UpdateCallbacks`/`OnUpdate`, `CullingActive`, `ClusterCull`, `UserData`, cached `Bound` |
| `Group`, `Geode` | `osg::Group`, `osg::Geode` | Children may be shared (instancing); `FindByName` |
| `MatrixTransform` | `osg::MatrixTransform` | `Matrix` (row-vector) |
| `PositionAttitudeTransform` | `osg::PositionAttitudeTransform` | `Position`, `Attitude` (quaternion), `Scale`, `PivotPoint` |
| `AutoTransform` | `osg::AutoTransform` | Billboards and constant screen size |
| `Switch` | `osg::Switch` | `SetValue`, `SetSingleChildOn`, `SetAllChildrenOff` |
| `Lod` | `osg::LOD` | Distance or pixel-size ranges, user centre |
| `PagedLod` | `osg::PagedLOD` | File-backed children loaded by the `DatabasePager`, expiry |
| `ProxyNode` | `osg::ProxyNode` | Loads files once, on first traversal |
| `LightSource` | `osg::LightSource` | Positions a `Light` with the accumulated transform |
| `CameraNode` | `osg::Camera` (in-graph) | HUD (post-render, pixel ortho) or render-to-texture (`RenderTarget`) |
| `OccluderNode` | `osg::OccluderNode` | Convex planar occluder for CPU occlusion culling |
| `Geometry` | `osg::Geometry` | Arrays plus one primitive set; `MarkDirty()` after editing arrays |

```csharp
var lod = new Lod();
lod.AddChild(Shapes.Sphere(1, 64, 32), 0, 30);       // near
lod.AddChild(Shapes.Sphere(1, 8, 4), 30, 1000);      // far

var tiles = new PagedLod { CenterMode = LodCenterMode.UserDefinedCenter, Center = c, Radius = 500, DatabasePath = "tiles" };
tiles.AddChild(lowRes, 0, float.MaxValue);           // embedded coarse level
tiles.SetFileName(1, "tile_0_0_hi.osgn");
tiles.SetRange(1, 0, 800);                           // loaded when closer than 800 m
```

## State sets

`StateSet` holds `Material`, `Texture`, `Program`, `Lighting`, `CullFace`, `Blend`, `PolygonMode`, `DepthTest`, `DepthWrite`, `RenderBin`, `LineWidth`, `PointSize`, `CastShadows`, `ReceiveShadows`, `Fog` and `Uniforms`.
Unset (`null`) values inherit from the parent. `Override = true` makes the attributes set here win over descendants, like `osg::StateAttribute::OVERRIDE`.

```csharp
var xray = new Group { StateSet = new StateSet { PolygonMode = PolygonMode.Line, Override = true } };
glass.GetOrCreateStateSet().MakeTransparent();       // AlphaBlend, transparent bin, no depth write
```

A material whose alpha is below 1 automatically becomes blended and depth-sorted.

## Visitors

Derive from `NodeVisitor` (or `MatrixTrackingVisitor` to get `CurrentMatrix`) and override `Apply` overloads. Built in:

- `UpdateVisitor`
- `CullVisitor`
- `RayIntersector` (picking)
- `FindNodesVisitor` (`FindNodesVisitor.FindAll<Geometry>(root)`)
- `StatsVisitor`

```csharp
var hit = viewer.Pick(mouseX, mouseY);              // nearest triangle under the cursor
if (hit is { } h) Console.WriteLine($"{h.Drawable.Name} at {h.WorldPoint}");
```

## Culling

`viewer.CullSettings.Mode` is a combination of the flags below. `SmallFeaturePixels` (default 1.5 px) and `LodScale` tune the culling.

| Flag | What it rejects |
|---|---|
| `ViewFrustum` | Bounding spheres outside the six frustum planes |
| `SmallFeature` | Objects whose projected size is below `SmallFeaturePixels` |
| `Cluster` | Subgraphs facing away from the eye, via `Node.ClusterCull` (control point, normal, deviation); used for planet-scale terrain |
| `Occlusion` | Spheres fully hidden behind `OccluderNode` polygons (shadow volumes built from the eye) |

`RenderStats` reports `NodesVisited`, `NodesCulled`, `DrawablesCulled`, `DrawItems`, `Triangles` and `CullMs`.

## File formats

`Registry.ReadNodeFile` / `WriteNodeFile` pick a plugin by extension.

| Format | Read | Write | Notes |
|---|---|---|---|
| `.osgn` | ✅ | ✅ | The managed native format: JSON with base64 arrays; shared nodes and state sets are preserved |
| `.obj` + `.mtl` | ✅ | ✅ | Materials, `map_Kd` textures, vertex colours, negative indices |
| `.stl` | ✅ ASCII/binary | ✅ binary | |
| `.ply` | ✅ ASCII / binary LE | — | Positions, normals, colours, faces or point clouds |
| `.asc`, `.tif` | ✅ | — | DEM to tiled terrain (see GIS) |
| images | PNG, TGA, BMP, PPM/PGM | PNG | Register an `IImageReader` for JPEG |
| everything else | via `OpenSceneGraph.Net.Native` | via native | 53 OSG plugins |
