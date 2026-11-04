using System.Diagnostics;
using System.Numerics;
using OpenSceneGraph.Net.IO;
using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Math;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net;

/// <summary>Threading models of <c>osgViewer::ViewerBase</c>, adapted to a host-driven frame loop.</summary>
public enum ThreadingModel
{
    /// <summary>Event, update and cull run on the calling thread; the returned list belongs to this frame.</summary>
    SingleThreaded,

    /// <summary>
    /// Cull of frame N runs on a worker while the host draws frame N-1 (one frame of latency).
    /// Update never overlaps cull, so scene mutation from update callbacks stays safe.
    /// </summary>
    DrawThreadPerContext,
}

/// <summary>
/// Frame loop driver (<c>osgViewer::Viewer</c>): processes events, runs the update traversal, merges paged data
/// and culls the scene into a <see cref="RenderList"/>. Rendering is done by the host with an <see cref="IRenderer"/>:
/// <code>
/// var list = viewer.Frame();
/// renderer.Render(list);
/// </code>
/// </summary>
public sealed class Viewer : IDisposable
{
    private readonly UpdateVisitor _update = new();
    private readonly CullVisitor _cull = new();
    private readonly RenderList[] _lists = [new(), new()];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _current;
    private Task? _pendingCull;
    private long _frameNumber;
    private double _lastTime;
    private double _fpsAccum;
    private int _fpsFrames;
    private double _fps;
    private Node? _scene;
    private bool _homePending;

    public Viewer(Node? scene = null)
    {
        Pager = new DatabasePager();
        SceneData = scene;
    }

    /// <summary>Root of the scene graph. Setting it re-homes the manipulator on the next frame.</summary>
    public Node? SceneData
    {
        get => _scene;
        set { _scene = value; _homePending = HomeOnSceneChange && _manipulatorIsDefault; }
    }

    public Camera Camera { get; } = new();
    public SceneEnvironment Environment { get; } = new();
    public CullSettings CullSettings => _cull.Settings;
    public DatabasePager Pager { get; }
    public EventQueue EventQueue { get; } = new();
    public List<IEventHandler> EventHandlers { get; } = [];
    public ThreadingModel ThreadingModel { get; set; } = ThreadingModel.SingleThreaded;

    /// <summary>When set, frames use this simulation time step instead of wall-clock time (deterministic playback).</summary>
    public double? FixedTimeStep { get; set; }

    public double TimeScale { get; set; } = 1.0;
    public bool Paused { get; set; }

    private ICameraManipulator? _manipulator = new TrackballManipulator();
    private bool _manipulatorIsDefault = true;

    /// <summary>
    /// The camera manipulator. Assigning one marks the camera as user-configured, so setting
    /// <see cref="SceneData"/> afterwards no longer re-homes it (call <see cref="Home"/> explicitly if wanted).
    /// </summary>
    public ICameraManipulator? CameraManipulator
    {
        get => _manipulator;
        set { _manipulator = value; _manipulatorIsDefault = false; }
    }

    /// <summary>When true (default) a new scene is framed automatically, unless a manipulator was assigned explicitly.</summary>
    public bool HomeOnSceneChange { get; set; } = true;

    public FrameStamp FrameStamp { get; private set; }

    /// <summary>The list produced by the most recent completed cull.</summary>
    public RenderList LastRenderList => _lists[_current];

    public double FramesPerSecond => _fps;

    /// <summary>Invoked after the update traversal, before culling (good place for simulation steps).</summary>
    public event Action<Viewer, FrameStamp>? Updated;

    /// <summary>Sets the viewport size; hosts call this on resize.</summary>
    public void SetViewport(int width, int height) => Camera.Viewport = new Viewport(0, 0, System.Math.Max(1, width), System.Math.Max(1, height));

    /// <summary>Frames the whole scene with the current manipulator (space bar / HOME).</summary>
    public void Home()
    {
        if (_scene is not null) CameraManipulator?.Home(_scene.Bound);
    }

    /// <summary>Runs one frame and returns the render list to draw.</summary>
    public RenderList Frame()
    {
        if (ThreadingModel == ThreadingModel.SingleThreaded)
        {
            Advance();
            EventTraversal();
            UpdateTraversal();
            CullInto(_lists[_current]);
            return _lists[_current];
        }

        // pipelined: finish the previous cull (frame N-1), then update frame N and cull it in the background
        _pendingCull?.Wait();
        int ready = _current;
        Advance();
        EventTraversal();
        UpdateTraversal();
        int next = 1 - ready;
        var cam = SnapshotCamera();
        _pendingCull = Task.Run(() => CullInto(_lists[next], cam));
        _current = next;
        return _lists[ready];
    }

    private Camera SnapshotCamera() => new()
    {
        ViewMatrix = Camera.ViewMatrix, Viewport = Camera.Viewport, ProjectionMode = Camera.ProjectionMode,
        FieldOfViewY = Camera.FieldOfViewY, NearPlane = Camera.NearPlane, FarPlane = Camera.FarPlane,
        OrthoHalfHeight = Camera.OrthoHalfHeight, AutoNearFar = Camera.AutoNearFar, NearFarRatio = Camera.NearFarRatio,
        Stereo = Camera.Stereo, EyeSeparation = Camera.EyeSeparation, ClearColor = Camera.ClearColor,
    };

    private void Advance()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double wallDt = _frameNumber == 0 ? 0 : now - _lastTime;
        _lastTime = now;
        double dt = Paused ? 0 : (FixedTimeStep ?? wallDt) * TimeScale;
        double sim = FrameStamp.SimulationTime + dt;
        FrameStamp = new FrameStamp(++_frameNumber, now, sim, dt);

        _fpsAccum += wallDt;
        _fpsFrames++;
        if (_fpsAccum >= 0.5)
        {
            _fps = _fpsFrames / _fpsAccum;
            _fpsAccum = 0;
            _fpsFrames = 0;
        }
    }

    private void EventTraversal()
    {
        foreach (var e in EventQueue.Drain())
        {
            if (e.Type == InputEventType.Resize) SetViewport(e.Width, e.Height);
            bool handled = false;
            foreach (var h in EventHandlers)
                if (h.Handle(e, this)) { handled = true; break; }
            if (!handled) CameraManipulator?.Handle(e, this);
        }
    }

    private void UpdateTraversal()
    {
        var sw = Stopwatch.StartNew();
        Pager.MergeCompletedRequests(FrameStamp.ReferenceTime);
        if (_homePending && _scene is not null)
        {
            _homePending = false;
            Home();
        }
        if (_scene is not null)
        {
            _update.Frame = FrameStamp;
            _scene.Accept(_update);
        }
        Updated?.Invoke(this, FrameStamp);

        if (CameraManipulator is { } m)
        {
            m.Update(FrameStamp.DeltaTime);
            Camera.ViewMatrix = m.ViewMatrix;
        }
        Pager.ExpireUnused(FrameStamp.ReferenceTime);
        foreach (var l in _lists) l.Stats.UpdateMs = sw.Elapsed.TotalMilliseconds;
    }

    private void CullInto(RenderList list, Camera? cam = null)
    {
        _cull.Run(_scene, cam ?? Camera, Environment, list, FrameStamp.ReferenceTime, Pager, CameraManipulator?.Center);
        list.Stats.FrameNumber = FrameStamp.FrameNumber;
        list.Stats.FramesPerSecond = _fps;
    }

    /// <summary>Projects a world point to window pixels (origin top-left). Returns null when behind the camera.</summary>
    public Vector2? ProjectToScreen(Vector3 world)
    {
        var vp = Camera.Viewport;
        var clip = Vector4.Transform(new Vector4(world, 1), Camera.ViewMatrix * Camera.ProjectionMatrix);
        if (clip.W <= 1e-6f) return null;
        var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        return new Vector2((ndc.X * 0.5f + 0.5f) * vp.Width + vp.X, (1 - (ndc.Y * 0.5f + 0.5f)) * vp.Height + vp.Y);
    }

    /// <summary>Builds a world-space ray through a window pixel.</summary>
    public (Vector3 Origin, Vector3 Direction) ScreenRay(float x, float y)
    {
        var vp = Camera.Viewport;
        float nx = (x - vp.X) / vp.Width * 2 - 1;
        float ny = 1 - (y - vp.Y) / vp.Height * 2;
        Matrix4x4.Invert(Camera.ViewMatrix * Camera.ProjectionMatrix, out var inv);
        var near = Vector4.Transform(new Vector4(nx, ny, 0, 1), inv);
        var far = Vector4.Transform(new Vector4(nx, ny, 1, 1), inv);
        var a = new Vector3(near.X, near.Y, near.Z) / near.W;
        var b = new Vector3(far.X, far.Y, far.Z) / far.W;
        return (a, Vector3.Normalize(b - a));
    }

    /// <summary>Picks the nearest drawable under a window pixel (<c>osgViewer::View::computeIntersections</c>).</summary>
    public Intersection? Pick(float x, float y)
    {
        if (_scene is null) return null;
        var (o, d) = ScreenRay(x, y);
        var ri = new RayIntersector(o, d);
        _scene.Accept(ri);
        return ri.First;
    }

    public void Dispose()
    {
        _pendingCull?.Wait();
        Pager.Dispose();
    }
}
