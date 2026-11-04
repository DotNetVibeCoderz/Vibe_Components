using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Text;
using OpenSceneGraph.Net.Animation;
using OpenSceneGraph.Net.Geo;
using OpenSceneGraph.Net.IO;
using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Particles;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.Simulation;
using OpenSceneGraph.Net.State;
using OpenSceneGraph.Net.Terrain;

namespace OpenSceneGraph.Net.Tests;

/// <summary>Paths no gallery sample reaches: paging, threading, less common formats, networking.</summary>
public class PagingAndThreadingTests
{
    private static bool FrameUntil(Viewer v, Func<bool> done, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            v.Frame();
            if (done()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    [Fact]
    public void PagedLod_loads_high_detail_child_from_file_and_expires_it()
    {
        var dir = Directory.CreateTempSubdirectory("osgnet_paged").FullName;
        Registry.WriteNodeFile(Shapes.Sphere(1, 32, 16), Path.Combine(dir, "hi.osgn"));
        var low = Shapes.Sphere(1, 6, 3);
        low.Name = "low";
        var lod = new PagedLod { CenterMode = LodCenterMode.UserDefinedCenter, Center = Vector3.Zero, Radius = 1, DatabasePath = dir, ExpiryTime = 0 };
        lod.AddChild(low, 50, float.MaxValue);
        lod.SetFileName(1, "hi.osgn");
        lod.SetRange(1, 0, 50);

        using var v = new Viewer(lod);
        v.SetViewport(800, 600);
        var cam = new TrackballManipulator2(new Vector3(0, -10, 0));
        v.CameraManipulator = cam;
        Assert.True(FrameUntil(v, () => lod.ChildCount == 2), $"child not paged in: {v.Pager.LastError}");
        Assert.Equal(1, v.Pager.LoadedCount);

        // the eye moves far away: the file-backed child is no longer used and gets expired
        cam.Eye = new Vector3(0, -500, 0);
        Assert.True(FrameUntil(v, () => lod.ChildCount == 1), "child not expired");
        Assert.Equal("low", lod.GetChild(0).Name);
    }

    [Fact]
    public void Missing_paged_file_is_reported_and_not_retried_forever()
    {
        var lod = new PagedLod { CenterMode = LodCenterMode.UserDefinedCenter, Center = Vector3.Zero, Radius = 1 };
        lod.AddChild(Shapes.Cube(1), 50, float.MaxValue);
        lod.SetFileName(1, "does-not-exist.osgn");
        lod.SetRange(1, 0, 50);
        using var v = new Viewer(lod);
        v.SetViewport(800, 600);
        v.CameraManipulator = new TrackballManipulator2(new Vector3(0, -10, 0));
        Assert.True(FrameUntil(v, () => v.Pager.FailedCount > 0));
        for (int i = 0; i < 20; i++) { v.Frame(); Thread.Sleep(2); }
        Assert.Equal(1, v.Pager.FailedCount);
    }

    [Fact]
    public void ProxyNode_loads_its_files()
    {
        var dir = Directory.CreateTempSubdirectory("osgnet_proxy").FullName;
        Registry.WriteNodeFile(Shapes.Cube(1), Path.Combine(dir, "a.osgn"));
        var proxy = new ProxyNode { DatabasePath = dir };
        proxy.FileNames.Add("a.osgn");
        var root = new Group().AddChild(proxy).AddChild(Shapes.Cube(1));
        using var v = new Viewer(root);
        v.SetViewport(800, 600);
        v.CameraManipulator = new TrackballManipulator2(new Vector3(0, -10, 0));
        Assert.True(FrameUntil(v, () => proxy.ChildCount == 1), $"proxy not loaded: {v.Pager.LastError}");
    }

    [Fact]
    public void DrawThreadPerContext_delivers_frames_with_one_frame_latency()
    {
        var box = Shapes.Cube(1);
        using var v = new Viewer(box) { ThreadingModel = ThreadingModel.DrawThreadPerContext, FixedTimeStep = 1 / 60.0 };
        v.SetViewport(800, 600);
        var first = v.Frame();   // nothing has been culled yet
        Assert.Empty(first.Passes);
        for (int i = 0; i < 50; i++)
        {
            var list = v.Frame();
            Assert.Single(list.MainPass.Opaque);
            Assert.Equal(v.FrameStamp.FrameNumber - 1, list.Stats.FrameNumber);
        }
    }

    [Fact]
    public void Assigning_a_manipulator_prevents_auto_home()
    {
        using var v = new Viewer();
        v.SetViewport(800, 600);
        var tb = new TrackballManipulator { Distance = 123 };
        v.CameraManipulator = tb;
        v.SceneData = Shapes.Cube(1);
        v.Frame();
        Assert.Equal(123, tb.Distance);

        using var v2 = new Viewer(Shapes.Cube(1));
        v2.SetViewport(800, 600);
        v2.Frame();
        Assert.NotEqual(10f, ((TrackballManipulator)v2.CameraManipulator!).Distance); // default manipulator was homed
    }

    /// <summary>Fixed camera for tests.</summary>
    private sealed class TrackballManipulator2(Vector3 eye) : ICameraManipulator
    {
        public Vector3 Eye { get; set; } = eye;
        public string Name => "Fixed";
        public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Eye, Vector3.Zero, Vector3.UnitZ);
        public Vector3 Center => Vector3.Zero;
        public void Home(Math.BoundingSphere bound) { }
        public void Update(double deltaTime) { }
        public bool Handle(in InputEvent e, Viewer viewer) => false;
    }
}

public class CullingPathTests
{
    [Fact]
    public void Cluster_cull_rejects_subgraph_facing_away()
    {
        var node = Shapes.Cube(1);
        node.ClusterCull = new ClusterCull(Vector3.Zero, -Vector3.UnitY * -1, 0f); // normal +Y
        var behind = SceneGraphTests.CullScene(node, eye: new Vector3(0, -10, 0));  // eye on the -Y side
        Assert.Empty(behind.MainPass.Opaque);
        var front = SceneGraphTests.CullScene(node, eye: new Vector3(0, 10, 0));
        Assert.Single(front.MainPass.Opaque);
    }

    [Fact]
    public void Small_feature_culling_drops_tiny_objects()
    {
        var tiny = MatrixTransform.Translate(0, 5000, 0).AddChild(Shapes.Cube(0.01f));
        Assert.Empty(SceneGraphTests.CullScene(tiny, eye: new Vector3(0, -10, 0), center: new Vector3(0, 100, 0)).MainPass.Opaque);
    }

    [Fact]
    public void Hud_and_rtt_cameras_create_ordered_passes()
    {
        var target = new Texture2D(64, 64);
        var root = new Group();
        root.AddChild(Shapes.Cube(1));
        root.AddChild(new CameraNode { RenderOrder = CameraRenderOrder.PostRender }.AddChild(MatrixTransform.Translate(10, 10, 0).AddChild(Shapes.Cube(5))));
        var view = Matrix4x4.CreateLookAt(new Vector3(0, -5, 0), Vector3.Zero, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 0.1f, 100);
        root.AddChild(new CameraNode { RenderOrder = CameraRenderOrder.PreRender, RenderTarget = target, ViewMatrix = view, ProjectionMatrix = proj }.AddChild(Shapes.Cube(1)));
        var list = SceneGraphTests.CullScene(root);
        Assert.Equal([PassKind.PreRender, PassKind.Main, PassKind.PostRender], list.Passes.Select(p => p.Kind));
        Assert.Same(target, list.Passes[0].Target);
        Assert.All(list.Passes, p => Assert.Single(p.Opaque));
    }
}

public class FormatPathTests
{
    [Fact]
    public void Ply_ascii_and_binary_read()
    {
        const string ascii = "ply\nformat ascii 1.0\nelement vertex 4\nproperty float x\nproperty float y\nproperty float z\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n0 0 0 255 0 0\n1 0 0 0 255 0\n1 1 0 0 0 255\n0 1 0 255 255 255\n4 0 1 2 3\n";
        var g = (Geometry)new PlyReader().ReadNode(new MemoryStream(Encoding.ASCII.GetBytes(ascii)), ReadOptions.Default);
        Assert.Equal(4, g.VertexCount);
        Assert.Equal(2, g.TriangleCount);
        Assert.Equal(1f, g.Colors![0].X, 3);

        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("ply\nformat binary_little_endian 1.0\nelement vertex 3\nproperty float x\nproperty float y\nproperty float z\nelement face 1\nproperty list uchar uint vertex_indices\nend_header\n"));
        var w = new BinaryWriter(ms);
        foreach (var f in new float[] { 0, 0, 0, 2, 0, 0, 0, 2, 0 }) w.Write(f);
        w.Write((byte)3); w.Write(0u); w.Write(1u); w.Write(2u);
        ms.Position = 0;
        var b = (Geometry)new PlyReader().ReadNode(ms, ReadOptions.Default);
        Assert.Equal(1, b.TriangleCount);
        Assert.Equal(2f, b.Positions[1].X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeoTiff_float32_is_read_south_up_with_georeference(bool tiled)
    {
        var hf = HeightFieldReaderWriter.ReadHeightField(new MemoryStream(BuildTiff(tiled)));
        Assert.Equal(4, hf.Columns);
        Assert.Equal(4, hf.Rows);
        Assert.Equal(30f, hf.SpacingX);
        // raster row 0 is north, so the first stored value ends up on the last HeightField row
        Assert.Equal(0f, hf[0, 3]);
        Assert.Equal(15f, hf[3, 0]);
        Assert.Equal(500000, hf.GeoOrigin!.Value.X);
        Assert.Equal(9000000 - 3 * 30, hf.GeoOrigin!.Value.Y);
    }

    private static byte[] BuildTiff(bool tiled)
    {
        const int n = 4;
        var tags = new List<(ushort Tag, ushort Type, uint Count, uint Value)>();
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write(0u);

        uint dataOffset = (uint)ms.Position;
        for (int i = 0; i < n * n; i++) w.Write((float)i);
        uint scaleOffset = (uint)ms.Position;
        foreach (var d in new double[] { 30, 30, 0 }) w.Write(d);
        uint tieOffset = (uint)ms.Position;
        foreach (var d in new double[] { 0, 0, 0, 500000, 9000000, 0 }) w.Write(d);

        tags.Add((256, 4, 1, n)); tags.Add((257, 4, 1, n));
        tags.Add((258, 3, 1, 32)); tags.Add((259, 3, 1, 1));
        tags.Add((277, 3, 1, 1)); tags.Add((339, 3, 1, 3));
        if (tiled) { tags.Add((322, 4, 1, n)); tags.Add((323, 4, 1, n)); tags.Add((324, 4, 1, dataOffset)); }
        else { tags.Add((273, 4, 1, dataOffset)); tags.Add((278, 4, 1, n)); }
        tags.Add((33550, 12, 3, scaleOffset)); tags.Add((33922, 12, 6, tieOffset));
        tags.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        uint ifd = (uint)ms.Position;
        w.Write((ushort)tags.Count);
        foreach (var (tag, type, count, value) in tags)
        {
            w.Write(tag); w.Write(type); w.Write(count); w.Write(value);
        }
        w.Write(0u);
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ifd);
        return bytes;
    }

    [Fact]
    public void Obj_mtl_texture_and_negative_indices()
    {
        var dir = Directory.CreateTempSubdirectory("osgnet_obj").FullName;
        PngReader.Write(Path.Combine(dir, "t.png"), Image.Solid(Color.Red));
        File.WriteAllText(Path.Combine(dir, "m.mtl"), "newmtl red\nKd 1 0 0\nd 0.5\nmap_Kd t.png\n");
        File.WriteAllText(Path.Combine(dir, "a.obj"), "mtllib m.mtl\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nvt 0 0\nusemtl red\nf -4/1 -3/1 -2/1 -1/1\n");
        var g = (Geometry)Registry.ReadNodeFile(Path.Combine(dir, "a.obj"));
        Assert.Equal(2, g.TriangleCount);
        Assert.Equal(0.5f, g.StateSet!.Material!.Diffuse.W);
        Assert.NotNull(g.StateSet.Texture);
        Assert.NotNull(g.Normals);
    }
}

public class NetworkAndAnimationTests
{
    [Fact]
    public async Task Dis_pdu_travels_over_loopback_udp()
    {
        int port = 30000 + Random.Shared.Next(20000);
        using var rx = new DisNetwork(port);
        var got = new TaskCompletionSource<EntityStatePdu>(TaskCreationOptions.RunContinuationsAsynchronously);
        rx.EntityUpdated += (p, _) => got.TrySetResult(p);
        using var tx = new DisNetwork(port, IPAddress.Loopback, listen: false);
        var pdu = new Aircraft { Position = new Vector3(10, 20, 3000) }.ToPdu(new EntityId(1, 2, 3), EntityType.FighterAircraft(), new GeoPoint(-6.9, 107.6), "VIPER-1");
        await tx.SendAsync(pdu);
        var back = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("VIPER-1", back.Marking);
        Assert.Equal(new EntityId(1, 2, 3), back.Id);
        Assert.Equal(3000, Ellipsoid.Wgs84.FromEcef(back.Location).Height, 0);
    }

    [Fact]
    public void Dead_reckoning_extrapolates_position()
    {
        var p = new EntityStatePdu { Location = new Vector3d(100, 0, 0), Velocity = new Vector3d(10, 0, 0), DeadReckoning = DeadReckoningAlgorithm.Fpw };
        Assert.Equal(120, p.Extrapolate(2).X, 6);
        p.DeadReckoning = DeadReckoningAlgorithm.Static;
        Assert.Equal(100, p.Extrapolate(2).X, 6);
    }

    [Fact]
    public void Skinning_moves_vertices_with_the_bone_and_leaves_bind_pose_at_rest()
    {
        var skeleton = RigFactory.Tentacle(segments: 4, length: 4);
        var rig = skeleton.Children.OfType<RigGeometry>().Single();
        var rest = (Vector3[])rig.Positions.Clone();
        rig.Skin(); // bind pose: nothing has moved yet
        for (int i = 0; i < rest.Length; i++) Assert.True(Vector3.Distance(rest[i], rig.Positions[i]) < 1e-3f);

        var root = skeleton.Bones[0];
        root.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        rig.Skin();
        var tip = rig.Positions[^1];
        Assert.True(MathF.Abs(tip.Z) < 1f, $"tip should have swung down from z=4, got {tip}");
    }

    [Fact]
    public void AnimationPath_interpolates_and_loops()
    {
        var path = new AnimationPath { LoopMode = LoopMode.Loop };
        path.Insert(0, new ControlPoint(Vector3.Zero)).Insert(2, new ControlPoint(new Vector3(10, 0, 0)));
        Assert.Equal(5, path.GetControlPoint(1).Position.X, 3);
        Assert.Equal(5, path.GetControlPoint(3).Position.X, 3);
        path.LoopMode = LoopMode.Swing;
        Assert.Equal(5, path.GetControlPoint(3).Position.X, 3);
        Assert.Equal(0, path.GetControlPoint(4).Position.X, 3);
        path.LoopMode = LoopMode.NoLoop;
        Assert.Equal(10, path.GetControlPoint(99).Position.X, 3);
    }

    [Fact]
    public void Particle_burst_respects_capacity_and_dies_out()
    {
        var ps = new ParticleSystem(100) { Gravity = Vector3.Zero };
        ps.Emitter.Rate = 0;
        ps.Emitter.LifeMin = ps.Emitter.LifeMax = 0.5f;
        ps.Burst(500);
        Assert.Equal(100, ps.AliveCount);
        ps.Update(ps, new FrameStamp(1, 0, 0, 0.05));
        Assert.Equal(100, ps.DrawCount);
        for (int i = 0; i < 20; i++) ps.Update(ps, new FrameStamp(i + 2, 0, 0, 0.05));
        Assert.Equal(0, ps.AliveCount);
        Assert.Equal(0, ps.DrawCount);
    }

    [Fact]
    public void Tile_urls_are_well_formed()
    {
        var k = new TileKey(10, 815, 529);
        Assert.Equal("https://tile.openstreetmap.org/10/815/529.png", TileSource.OpenStreetMap().GetUrl(k));
        Assert.EndsWith("/tile/10/529/815", TileSource.ArcGis("https://x/MapServer/").GetUrl(k));
        var wms = TileSource.Wms("https://x/wms", "roads").GetUrl(k);
        Assert.Contains("REQUEST=GetMap", wms);
        Assert.Contains("CRS=EPSG:3857", wms);
        var wmts = TileSource.Wmts("https://x/wmts", "roads").GetUrl(k);
        Assert.Contains("TILEMATRIX=10&TILEROW=529&TILECOL=815", wmts);
        Assert.Equal("3", TileMath.QuadKey(new TileKey(1, 1, 1)));
    }

    [Fact]
    public void Terrain_builder_keeps_shared_tile_edges_at_full_resolution()
    {
        var hf = TerrainGenerator.Fractal(33, 2, 20, seed: 3);
        var t = TerrainBuilder.Build(hf, tilesPerSide: 2, levels: 2);
        Assert.Equal(4, t.ChildCount);
        var a = (Geometry)((Lod)t.GetChild(0)).GetChild(0);
        var b = (Geometry)((Lod)t.GetChild(1)).GetChild(0);
        float aRight = a.Positions.Max(p => p.X), bLeft = b.Positions.Min(p => p.X);
        Assert.Equal(aRight, bLeft, 3); // neighbouring tiles share an edge column
        Assert.Equal(hf.Sample(0, 0)!.Value, hf[16, 16] + hf.Origin.Z, 3);
    }
}
