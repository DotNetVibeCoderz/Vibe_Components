using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Gallery.Web.Services;

/// <summary>A running sample: its viewer, context (telemetry, controls) and scene root.</summary>
public sealed class SampleSession : IDisposable
{
    private SampleSession(GallerySample sample, Viewer viewer, SampleContext ctx, Node scene)
    {
        Sample = sample;
        Viewer = viewer;
        Context = ctx;
        Scene = scene;
    }

    public GallerySample Sample { get; }
    public Viewer Viewer { get; }
    public SampleContext Context { get; }
    public Node Scene { get; }
    public LiveStats Stats { get; } = new();

    public bool UsesOrbitCamera => Viewer.CameraManipulator is TrackballManipulator;

    public static SampleSession Start(GallerySample sample)
    {
        var viewer = new Viewer();
        var ctx = new SampleContext(viewer);
        var scene = sample.Build(ctx);
        viewer.SceneData = scene;
        return new SampleSession(sample, viewer, ctx, scene);
    }

    public void Dispose() => Viewer.Dispose();
}

/// <summary>Latest frame statistics, written every frame by the canvas and read by the UI a few times per second.</summary>
public sealed class LiveStats
{
    public double Fps;
    public int DrawItems;
    public long Triangles;
    public int Culled;
    public int Lights;
    public double CullMs;
    public long Frame;

    public void Update(RenderStats s)
    {
        Fps = s.FramesPerSecond;
        DrawItems = s.DrawItems;
        Triangles = s.Triangles;
        Culled = s.NodesCulled;
        Lights = s.ActiveLights;
        CullMs = s.CullMs;
        Frame = s.FrameNumber;
    }
}
