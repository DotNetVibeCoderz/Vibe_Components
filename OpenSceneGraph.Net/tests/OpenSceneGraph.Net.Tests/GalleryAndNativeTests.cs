using OpenSceneGraph.Net.Gallery;
using OpenSceneGraph.Net.Native;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Tests;

public class GalleryTests
{
    public static TheoryData<string> SampleIds() => new(SampleCatalog.All.Select(s => s.Id));

    [Theory]
    [MemberData(nameof(SampleIds))]
    public void Every_sample_builds_updates_and_culls(string id)
    {
        var sample = SampleCatalog.Find(id)!;
        using var viewer = new Viewer { FixedTimeStep = 1 / 30.0 };
        viewer.SetViewport(800, 600);
        var ctx = new SampleContext(viewer);
        viewer.SceneData = sample.Build(ctx);
        RenderList? list = null;
        for (int i = 0; i < 20; i++) list = viewer.Frame();
        Assert.NotNull(list);
        int draws = list!.Passes.Sum(p => p.Opaque.Count + p.Transparent.Count);
        Assert.True(draws > 0, $"{id} produced no draw items");
        Assert.False(string.IsNullOrWhiteSpace(sample.Title.Id));
        Assert.False(string.IsNullOrWhiteSpace(sample.Description.En));
    }
}

public class NativeTests
{
    [Fact]
    public void Native_osg_loads_and_roundtrips_osgt()
    {
        if (!OsgNative.Initialize()) return; // native runtime not present on this machine
        Assert.StartsWith("3.6", OsgNative.OsgVersion);
        var scene = new Group();
        scene.AddChild(Meshes.Shapes.Sphere(1, 16, 8, State.Color.Red));
        using var native = NativeSceneConverter.ToNative(scene);
        var path = Path.Combine(Path.GetTempPath(), "osgnet_native.osgt");
        native.Write(path);
        using var back = NativeNode.Read(path);
        Assert.True(back.Statistics.Triangles > 0);
        var managed = back.ToManaged();
        var stats = new StatsVisitor();
        managed.Accept(stats);
        Assert.Equal(16 * 8 * 2, stats.Triangles);

        // the registry now falls back to OSG plugins for .osgt
        var viaRegistry = IO.Registry.ReadNodeFile(path);
        Assert.NotNull(viaRegistry);
    }

    [Theory]
    [InlineData("osgb")]
    [InlineData("3ds")]
    [InlineData("stl")]
    public void Managed_scene_survives_native_writers(string ext)
    {
        if (!OsgNative.Initialize()) return;
        var scene = new Group().AddChild(Meshes.Shapes.Cube(2, State.Color.Orange));
        var path = Path.Combine(Directory.CreateTempSubdirectory("osgnet_write").FullName, "m." + ext);
        using (var native = NativeSceneConverter.ToNative(scene)) native.Write(path);
        using var back = NativeNode.Read(path);
        var stats = new StatsVisitor();
        back.ToManaged().Accept(stats);
        Assert.Equal(12, stats.Triangles);
    }

    [Fact]
    public void Native_offscreen_render_produces_pixels()
    {
        if (!OsgNative.Initialize()) return;
        var scene = new Group().AddChild(Meshes.Shapes.Cube(2, State.Color.Orange));
        State.Image img;
        try { img = NativeViewer.RenderToImage(scene, 160, 120); }
        catch (InvalidOperationException) { return; } // no GL context (headless CI)
        Assert.Contains(img.Pixels.Chunk(4), p => p[0] > 200 && p[1] > 100 && p[2] < 140); // lit orange cube
    }
}
