using System.Numerics;
using OpenSceneGraph.Net.Geo;
using OpenSceneGraph.Net.IO;
using OpenSceneGraph.Net.Math;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.Simulation;
using OpenSceneGraph.Net.State;
using OpenSceneGraph.Net.Terrain;

namespace OpenSceneGraph.Net.Tests;

public class SceneGraphTests
{
    [Fact]
    public void Bound_propagates_through_transforms_and_dirties_upward()
    {
        var box = Shapes.Cube(2);
        var t = MatrixTransform.Translate(10, 0, 0);
        t.AddChild(box);
        var root = new Group().AddChild(t);
        Assert.Equal(10, root.Bound.Center.X, 3);
        t.Matrix = Matrix4x4.CreateTranslation(-5, 0, 0);
        Assert.Equal(-5, root.Bound.Center.X, 3);
    }

    [Fact]
    public void Switch_only_traverses_enabled_children()
    {
        var sw = new Switch();
        sw.AddChild(Shapes.Cube(1), true);
        sw.AddChild(Shapes.Cube(1), false);
        var list = CullScene(sw);
        Assert.Single(list.MainPass.Opaque);
    }

    [Fact]
    public void Lod_selects_child_by_distance()
    {
        var lod = new Lod();
        var near = Shapes.Sphere(1, 32, 16); near.Name = "near";
        var far = Shapes.Sphere(1, 6, 3); far.Name = "far";
        lod.AddChild(near, 0, 20);
        lod.AddChild(far, 20, float.MaxValue);
        Assert.Equal("near", CullScene(lod, eye: new Vector3(0, -10, 0)).MainPass.Opaque.Single().Geometry.Name);
        Assert.Equal("far", CullScene(lod, eye: new Vector3(0, -50, 0)).MainPass.Opaque.Single().Geometry.Name);
    }

    [Fact]
    public void Frustum_culls_objects_behind_camera()
    {
        var root = new Group();
        root.AddChild(MatrixTransform.Translate(0, 10, 0).AddChild(Shapes.Cube(1)));
        root.AddChild(MatrixTransform.Translate(0, -30, 0).AddChild(Shapes.Cube(1)));
        var list = CullScene(root, eye: new Vector3(0, -5, 0), center: new Vector3(0, 10, 0));
        Assert.Single(list.MainPass.Opaque);
        Assert.Equal(1, list.Stats.NodesCulled);
    }

    [Fact]
    public void Occluder_culls_hidden_object()
    {
        var root = new Group();
        var wall = new OccluderNode { Occluder = [new(-10, 0, -10), new(10, 0, -10), new(10, 0, 10), new(-10, 0, 10)] };
        root.AddChild(wall);
        root.AddChild(MatrixTransform.Translate(0, 20, 0).AddChild(Shapes.Cube(1)));
        var list = CullScene(root, eye: new Vector3(0, -10, 0), center: new Vector3(0, 20, 0));
        Assert.Empty(list.MainPass.Opaque);
    }

    [Fact]
    public void StateSet_override_wins_over_child()
    {
        var parent = new Group { StateSet = new StateSet { PolygonMode = PolygonMode.Line, Override = true } };
        var child = Shapes.Cube(1);
        child.GetOrCreateStateSet().PolygonMode = PolygonMode.Fill;
        parent.AddChild(child);
        Assert.Equal(PolygonMode.Line, CullScene(parent).MainPass.Opaque[0].State.PolygonMode);
    }

    [Fact]
    public void Transparent_items_sorted_back_to_front()
    {
        var root = new Group();
        for (int i = 0; i < 3; i++)
        {
            var c = Shapes.Cube(1, new Vector4(1, 1, 1, 0.5f));
            root.AddChild(MatrixTransform.Translate(0, i * 5, 0).AddChild(c));
        }
        var t = CullScene(root, eye: new Vector3(0, -10, 0), center: new Vector3(0, 10, 0)).MainPass.Transparent;
        Assert.Equal(3, t.Count);
        Assert.True(t[0].Depth >= t[1].Depth && t[1].Depth >= t[2].Depth);
    }

    [Fact]
    public void Picking_hits_nearest_triangle()
    {
        var root = new Group().AddChild(Shapes.Cube(2));
        var ri = new RayIntersector(new Vector3(0, -10, 0), Vector3.UnitY);
        root.Accept(ri);
        Assert.NotNull(ri.First);
        Assert.Equal(-1, ri.First!.Value.WorldPoint.Y, 3);
    }

    internal static RenderList CullScene(Node root, Vector3? eye = null, Vector3? center = null)
    {
        var cam = new Camera { Viewport = new Viewport(0, 0, 800, 600) };
        cam.LookAt(eye ?? new Vector3(0, -10, 2), center ?? Vector3.Zero);
        var list = new RenderList();
        new CullVisitor().Run(root, cam, new SceneEnvironment { HeadLight = false }, list, 0);
        return list;
    }
}

public class FormatTests
{
    [Fact]
    public void Osgn_roundtrip_preserves_hierarchy_and_geometry()
    {
        var root = new Group { Name = "root" };
        var pat = new PositionAttitudeTransform { Position = new(1, 2, 3), Name = "pat" };
        var g = Shapes.Sphere(1, 12, 6, Color.Red);
        pat.AddChild(g);
        pat.AddChild(g); // shared
        root.AddChild(pat);
        var lod = new Lod();
        lod.AddChild(Shapes.Cube(1), 0, 50);
        root.AddChild(lod);

        var ms = new MemoryStream();
        new OsgnReaderWriter().WriteNode(root, ms);
        ms.Position = 0;
        var back = (Group)new OsgnReaderWriter().ReadNode(ms, ReadOptions.Default);
        var p2 = (PositionAttitudeTransform)back.GetChild(0);
        Assert.Equal(new Vector3(1, 2, 3), p2.Position);
        Assert.Same(p2.GetChild(0), p2.GetChild(1));
        Assert.Equal(g.Positions, ((Geometry)p2.GetChild(0)).Positions);
        Assert.Equal(50, ((Lod)back.GetChild(1)).Ranges[0].Max);
    }

    [Fact]
    public void Obj_and_stl_roundtrip()
    {
        var cube = Shapes.Cube(2);
        foreach (var ext in new[] { "obj", "stl" })
        {
            var path = Path.Combine(Path.GetTempPath(), $"osgnet_test.{ext}");
            Registry.WriteNodeFile(cube, path);
            var n = Registry.ReadNodeFile(path);
            var stats = new StatsVisitor();
            n.Accept(stats);
            Assert.Equal(12, stats.Triangles);
        }
    }

    [Fact]
    public void Png_encode_decode_roundtrip()
    {
        var img = Image.Checkerboard(16, 4, Color.Red, Color.Blue);
        var ms = new MemoryStream();
        PngReader.Write(ms, img.Width, img.Height, img.Pixels);
        ms.Position = 0;
        var back = new PngReader().ReadImage(ms);
        Assert.Equal(img.Pixels, back.Pixels);
    }

    [Fact]
    public void Ascii_grid_reads_heights_south_up()
    {
        const string asc = "ncols 3\nnrows 2\nxllcorner 0\nyllcorner 0\ncellsize 10\nNODATA_value -9999\n1 2 3\n4 5 6\n";
        var hf = HeightFieldReaderWriter.ReadAsciiGrid(asc);
        Assert.Equal(4, hf[0, 0]); // southern row is the last line in the file
        Assert.Equal(3, hf[2, 1]);
    }
}

public class GeoTests
{
    [Fact]
    public void Ecef_roundtrip_is_submillimetre()
    {
        var p = new GeoPoint(-6.2088, 106.8456, 123.4);
        var back = Ellipsoid.Wgs84.FromEcef(Ellipsoid.Wgs84.ToEcef(p));
        Assert.Equal(p.Latitude, back.Latitude, 8);
        Assert.Equal(p.Longitude, back.Longitude, 8);
        Assert.Equal(p.Height, back.Height, 3);
    }

    [Fact]
    public void Utm_matches_reference_and_roundtrips()
    {
        // Jakarta (Monas) -> zone 48 south
        var p = new GeoPoint(-6.175392, 106.827153);
        var u = Utm.FromGeo(p);
        Assert.Equal(48, u.Zone);
        Assert.False(u.North);
        Assert.InRange(u.Easting, 701_000, 703_000);
        Assert.InRange(u.Northing, 9_316_000, 9_318_000);
        var back = Utm.ToGeo(u);
        Assert.Equal(p.Latitude, back.Latitude, 6);
        Assert.Equal(p.Longitude, back.Longitude, 6);
    }

    [Fact]
    public void Enu_roundtrip()
    {
        var origin = new GeoPoint(-7.25, 112.75, 10);
        var enu = new Vector3(1000, -2500, 300);
        var back = Ellipsoid.Wgs84.ToEnu(Ellipsoid.Wgs84.FromEnu(enu, origin), origin);
        Assert.Equal(enu.X, back.X, 1);
        Assert.Equal(enu.Y, back.Y, 1);
        Assert.Equal(enu.Z, back.Z, 1);
    }

    [Fact]
    public void Tile_math_matches_known_tile()
    {
        var k = TileMath.FromGeo(51.5074, -0.1278, 10); // London
        Assert.Equal(new TileKey(10, 511, 340), k);
    }

    [Fact]
    public void Dis_entity_state_roundtrip()
    {
        var pdu = new EntityStatePdu
        {
            Id = new EntityId(1, 2, 3), Type = EntityType.Tank(), Location = new Vector3d(-1, 6378137, 2),
            Velocity = new Vector3d(1, 2, 3), Orientation = (0.1f, 0.2f, 0.3f), Marking = "ALPHA-1",
        };
        var bytes = pdu.Encode();
        Assert.Equal(EntityStatePdu.Size, bytes.Length);
        var back = EntityStatePdu.Decode(bytes);
        Assert.Equal(pdu.Id, back.Id);
        Assert.Equal(pdu.Type, back.Type);
        Assert.Equal(pdu.Location, back.Location);
        Assert.Equal("ALPHA-1", back.Marking);
    }
}

public class TerrainAndScienceTests
{
    [Fact]
    public void Heightfield_sample_is_bilinear()
    {
        var hf = new HeightField(2, 2, 10, 10, [0, 10, 0, 10]);
        Assert.Equal(5, hf.Sample(5, 5)!.Value, 3);
        Assert.Null(hf.Sample(50, 5));
    }

    [Fact]
    public void Isosurface_of_sphere_has_expected_radius()
    {
        var f = Science.ScalarField3D.FromFunction(40, 4, p => p.Length());
        var g = Science.Isosurface.Extract(f, 1.5f);
        Assert.True(g.TriangleCount > 500);
        foreach (var p in g.Positions) Assert.InRange(p.Length(), 1.4f, 1.6f);
    }

    [Fact]
    public void Fast_and_managed_normals_agree()
    {
        var plane = Shapes.Plane(10, 10, 80, 80, height: (x, y) => MathF.Sin(x) * MathF.Cos(y));
        var managed = new Vector3[plane.VertexCount];
        MeshUtil.ComputeNormalsManaged(plane.Positions, plane.Indices, managed);
        var any = MeshUtil.ComputeSmoothNormals(plane.Positions, plane.Indices);
        for (int i = 0; i < managed.Length; i++) Assert.True(Vector3.Distance(managed[i], any[i]) < 1e-4f);
    }
}
