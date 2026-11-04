using Stopwatch = System.Diagnostics.Stopwatch;
using System.Numerics;
using OpenSceneGraph.Net.Math;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Rendering;

[Flags]
public enum CullingMode
{
    None = 0,
    ViewFrustum = 1,
    SmallFeature = 2,
    Cluster = 4,
    Occlusion = 8,
    Default = ViewFrustum | SmallFeature | Cluster | Occlusion,
}

/// <summary>Culling configuration (<c>osg::CullSettings</c>).</summary>
public sealed class CullSettings
{
    public CullingMode Mode { get; set; } = CullingMode.Default;

    /// <summary>Objects whose projected diameter is below this many pixels are skipped.</summary>
    public float SmallFeaturePixels { get; set; } = 1.5f;

    /// <summary>Multiplies LOD distances; values &gt; 1 switch to lower detail sooner.</summary>
    public float LodScale { get; set; } = 1f;
}

/// <summary>Receives on-demand loading requests issued during culling (implemented by <see cref="IO.DatabasePager"/>).</summary>
public interface IDatabaseRequestHandler
{
    void RequestPagedChild(PagedLod lod, int childIndex, float priority, double time);
    void RequestProxy(ProxyNode proxy);
}

/// <summary>
/// Traverses the scene from a camera and produces a <see cref="RenderList"/> (<c>osgUtil::CullVisitor</c>):
/// view-frustum, small-feature, cluster and occlusion culling, LOD selection, paging requests, state inheritance,
/// light collection, nested cameras (HUD / render-to-texture) and automatic near/far computation.
/// </summary>
public sealed class CullVisitor : NodeVisitor
{
    private readonly Stack<Matrix4x4> _matrixStack = new();
    private readonly Stack<ResolvedState> _stateStack = new();
    private readonly Dictionary<(ResolvedState, StateSet), (int Version, int UniformVersion, ResolvedState State)> _stateCache = [];
    private readonly List<OcclusionVolume> _occluders = [];

    private Matrix4x4 _model = Matrix4x4.Identity;
    private Matrix4x4 _view;
    private Matrix4x4 _proj;
    private Frustum _frustum;
    private Vector3 _eye;
    private float _pixelScale; // pixels per (world unit / distance)
    private bool _ortho;
    private RenderPass _pass = null!;
    private RenderList _list = null!;
    private ResolvedState _state = ResolvedState.Default;
    private IDatabaseRequestHandler? _pager;
    private double _time;
    private float _minDepth, _maxDepth;
    private Viewport _viewport;

    public CullVisitor() : base(TraversalMode.ActiveChildren) { }

    public CullSettings Settings { get; set; } = new();

    /// <summary>Culls <paramref name="root"/> for <paramref name="camera"/> into <paramref name="list"/>.</summary>
    public void Run(Node? root, Camera camera, SceneEnvironment env, RenderList list, double time, IDatabaseRequestHandler? pager = null, Vector3? focus = null)
    {
        var sw = Stopwatch.StartNew();
        list.Reset();
        list.Environment = env;
        list.Camera = camera;
        _list = list;
        _pager = pager;
        _time = time;
        _viewport = camera.Viewport;
        _matrixStack.Clear();
        _stateStack.Clear();
        _model = Matrix4x4.Identity;
        _state = ResolvedState.Default;
        if (_stateCache.Count > 8192) _stateCache.Clear();

        var stats = list.Stats;
        stats.NodesVisited = 0;
        stats.DrawablesCulled = 0;
        stats.NodesCulled = 0;
        stats.Triangles = 0;
        stats.PagedLodRequests = 0;

        _pass = list.RentPass(PassKind.Main);
        _pass.ClearColor = camera.ClearColor;
        SetupView(camera.ViewMatrix, camera.ProjectionMatrix, camera.ProjectionMode == ProjectionMode.Orthographic);
        list.Focus = focus ?? root?.Bound.Center ?? Vector3.Zero;

        if (env.HeadLight)
        {
            var fwd = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, Inverse(_view)));
            list.Lights.Add(new LightInstance(
                new Light { Type = LightType.Directional, Intensity = env.HeadLightIntensity, Direction = fwd }, _eye, fwd));
        }

        _occluders.Clear();
        if (root is not null && (Settings.Mode & CullingMode.Occlusion) != 0) CollectOccluders(root);

        _minDepth = float.MaxValue;
        _maxDepth = 0;
        root?.Accept(this);

        // Automatic near/far (COMPUTE_NEAR_FAR_USING_BOUNDING_VOLUMES)
        if (camera.AutoNearFar && !_ortho && _maxDepth > 0 && _minDepth < float.MaxValue)
        {
            float far = MathF.Min(camera.FarPlane, _maxDepth * 1.02f);
            float near = MathF.Max(MathF.Max(camera.NearPlane, far * camera.NearFarRatio), _minDepth * 0.98f);
            if (near >= far) near = far * camera.NearFarRatio;
            _pass.Projection = camera.BuildProjection(near, far);
        }

        list.Finish();
        int draws = 0;
        foreach (var p in list.Passes) draws += p.Opaque.Count + p.Transparent.Count;
        stats.DrawItems = draws;
        stats.ActiveLights = list.Lights.Count;
        stats.CullMs = sw.Elapsed.TotalMilliseconds;
    }

    private static Matrix4x4 Inverse(in Matrix4x4 m) => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;

    private void SetupView(in Matrix4x4 view, in Matrix4x4 proj, bool ortho)
    {
        _view = view;
        _proj = proj;
        _ortho = ortho;
        _frustum = Frustum.FromViewProjection(view * proj);
        _eye = Inverse(view).Translation;
        _pixelScale = proj.M22 * _viewport.Height * 0.5f; // pixels per radian-ish (perspective) or per world unit (ortho)
        _pass.View = view;
        _pass.Projection = proj;
        _pass.Eye = _eye;
    }

    private float PixelSize(in BoundingSphere world)
    {
        if (_ortho) return world.Radius * 2 * _pixelScale;
        float d = Vector3.Distance(world.Center, _eye);
        return d <= world.Radius ? float.MaxValue : world.Radius * 2 * _pixelScale / d;
    }

    /// <summary>Common culling tests; returns false when the node (and its subgraph) should be skipped.</summary>
    private bool IsVisible(Node node, out BoundingSphere world)
    {
        world = default;
        if (!ValidNodeMask(node)) return false;
        _list.Stats.NodesVisited++;
        if (!node.CullingActive) return true;

        var b = node.Bound;
        if (!b.IsValid) return false; // empty subgraph
        world = b.Transform(_model);

        var mode = Settings.Mode;
        if ((mode & CullingMode.ViewFrustum) != 0 && !_frustum.Intersects(world)) return Culled(node);
        if ((mode & CullingMode.SmallFeature) != 0 && Settings.SmallFeaturePixels > 0 && PixelSize(world) < Settings.SmallFeaturePixels) return Culled(node);

        if ((mode & CullingMode.Cluster) != 0 && node.ClusterCull is { } cc)
        {
            var cp = Vector3.Transform(cc.ControlPoint, _model);
            var n = Vector3.Normalize(Vector3.TransformNormal(cc.Normal, _model));
            var toEye = _eye - cp;
            float len = toEye.Length();
            bool inRadius = cc.Radius >= 0 && len < cc.Radius;
            if (!inRadius && len > 1e-6f && Vector3.Dot(toEye / len, n) < cc.Deviation) return Culled(node);
        }

        if (_occluders.Count > 0)
            foreach (var o in _occluders)
                if (o.Occludes(world)) return Culled(node);

        return true;
    }

    private bool Culled(Node node)
    {
        _list.Stats.NodesCulled++;
        if (node is Drawable) _list.Stats.DrawablesCulled++;
        return false;
    }

    private void PushState(Node node)
    {
        _stateStack.Push(_state);
        if (node.StateSet is not { } ss) return;
        var key = (_state, ss);
        if (_stateCache.TryGetValue(key, out var e) && e.Version == ss.Version && e.UniformVersion == ss.Uniforms.Version)
        {
            _state = e.State;
            return;
        }
        var merged = _state.Merge(ss);
        _stateCache[key] = (ss.Version, ss.Uniforms.Version, merged);
        _state = merged;
    }

    private void PopState() => _state = _stateStack.Pop();

    private void TraverseWithState(Node node)
    {
        PushState(node);
        Traverse(node);
        PopState();
    }

    public override void Apply(Node node)
    {
        if (!IsVisible(node, out _)) return;
        TraverseWithState(node);
    }

    public override void Apply(Group group)
    {
        if (!IsVisible(group, out _)) return;
        TraverseWithState(group);
    }

    public override void Apply(Geode geode) => Apply((Group)geode);
    public override void Apply(Switch sw) => Apply((Group)sw);
    public override void Apply(OccluderNode occluder) => Apply((Group)occluder);

    public override void Apply(Transform transform)
    {
        if (transform is AutoTransform at)
        {
            if (!ValidNodeMask(at)) return;
            var mv = _model * _view;
            var eyeLocal = Vector3.Transform(_eye, Inverse(_model));
            at.UpdateForView(mv, eyeLocal);
        }
        else if (!IsVisible(transform, out _)) return;

        _matrixStack.Push(_model);
        _model = transform.ReferenceFrame == ReferenceFrame.Absolute ? transform.GetMatrix() : transform.GetMatrix() * _model;
        TraverseWithState(transform);
        _model = _matrixStack.Pop();
    }

    public override void Apply(Drawable drawable)
    {
        if (!IsVisible(drawable, out var world)) return;
        if (drawable is not Geometry g || g.VertexCount == 0) return;

        PushState(drawable);
        var viewPos = Vector3.Transform(world.Center, _view);
        float depth = -viewPos.Z;
        if (_pass.Kind == PassKind.Main)
        {
            _minDepth = MathF.Min(_minDepth, depth - world.Radius);
            _maxDepth = MathF.Max(_maxDepth, depth + world.Radius);
        }

        var item = new DrawItem { Geometry = g, World = _model, State = _state, Depth = depth };
        if (_state.IsTransparent) _pass.Transparent.Add(item); else _pass.Opaque.Add(item);
        _list.Stats.Triangles += g.TriangleCount;
        PopState();
    }

    public override void Apply(Lod lod)
    {
        if (!IsVisible(lod, out var world)) return;
        PushState(lod);
        lod.TraverseInRange(this, LodValue(lod, world));
        PopState();
    }

    private float LodValue(Lod lod, in BoundingSphere world)
    {
        if (lod.RangeMode == LodRangeMode.PixelSizeOnScreen) return PixelSize(world);
        var c = Vector3.Transform(lod.GetCenter(), _model);
        return Vector3.Distance(c, _eye) * Settings.LodScale;
    }

    public override void Apply(PagedLod lod)
    {
        if (!IsVisible(lod, out var world)) return;
        PushState(lod);
        float value = LodValue(lod, world);
        var ranges = lod.Ranges;
        int lastTraversed = -1;
        bool needLoad = false;
        for (int i = 0; i < ranges.Count; i++)
        {
            var (min, max) = ranges[i];
            if (value < min || value >= max) continue;
            if (i < lod.ChildCount)
            {
                lod.GetChild(i).Accept(this);
                if (i < lod.Files.Count) lod.Files[i].LastUsedTime = _time;
                lastTraversed = i;
            }
            else needLoad = true;
        }

        if (needLoad)
        {
            int n = lod.ChildCount;
            if (n > 0 && lastTraversed != n - 1) lod.GetChild(n - 1).Accept(this); // show coarser data meanwhile
            if (n < lod.Files.Count && !string.IsNullOrEmpty(lod.Files[n].FileName) && _pager is not null)
            {
                var f = lod.Files[n];
                float priority = lod.RangeMode == LodRangeMode.PixelSizeOnScreen
                    ? value / MathF.Max(1, ranges[n].Min)
                    : ranges[n].Max / MathF.Max(1e-3f, value);
                _pager.RequestPagedChild(lod, n, f.PriorityOffset + f.PriorityScale * priority, _time);
                _list.Stats.PagedLodRequests++;
            }
        }
        PopState();
    }

    public override void Apply(ProxyNode proxy)
    {
        if (!ValidNodeMask(proxy)) return;
        if (proxy.ChildCount < proxy.FileNames.Count && !proxy.Requested && _pager is not null)
        {
            proxy.Requested = true;
            _pager.RequestProxy(proxy);
        }
        if (proxy.ChildCount > 0) Apply((Group)proxy);
    }

    public override void Apply(LightSource light)
    {
        if (!ValidNodeMask(light)) return;
        var l = light.Light;
        var pos = Vector3.Transform(l.Position, _model);
        var dir = Vector3.TransformNormal(l.Direction, _model);
        dir = dir.LengthSquared() > 0 ? Vector3.Normalize(dir) : -Vector3.UnitZ;
        if (_list.Lights.Count < 8) _list.Lights.Add(new LightInstance(l, pos, dir));
        TraverseWithState(light);
    }

    public override void Apply(CameraNode cam)
    {
        if (!ValidNodeMask(cam)) return;

        // save main-pass context
        var savedPass = _pass; var savedView = _view; var savedProj = _proj; var savedFrustum = _frustum;
        var savedEye = _eye; var savedPixel = _pixelScale; var savedOrtho = _ortho; var savedModel = _model;

        var kind = cam.RenderOrder switch
        {
            CameraRenderOrder.PreRender => PassKind.PreRender,
            CameraRenderOrder.PostRender => PassKind.PostRender,
            _ => PassKind.PostRender,
        };
        _pass = _list.RentPass(kind);
        _pass.CameraNode = cam;
        _pass.Order = cam.RenderOrderNumber;
        _pass.ClearColor = cam.ClearColor;
        _pass.ClearDepth = cam.ClearDepth;
        _pass.Target = cam.RenderTarget;

        var view = cam.ReferenceFrame == ReferenceFrame.Absolute ? cam.ViewMatrix : cam.ViewMatrix * _view;
        bool hud = cam.ProjectionMatrix is null;
        var proj = cam.ProjectionMatrix ?? Matrix4x4.CreateOrthographicOffCenter(0, _viewport.Width, 0, _viewport.Height, -1000, 1000);
        SetupView(view, proj, hud || proj.M44 == 1f);
        if (cam.ReferenceFrame == ReferenceFrame.Absolute) _model = Matrix4x4.Identity;

        var savedCull = Settings.Mode;
        if (hud) Settings.Mode &= ~(CullingMode.SmallFeature | CullingMode.Occlusion);
        TraverseWithState(cam);
        Settings.Mode = savedCull;

        _pass = savedPass; _view = savedView; _proj = savedProj; _frustum = savedFrustum;
        _eye = savedEye; _pixelScale = savedPixel; _ortho = savedOrtho; _model = savedModel;
    }

    private void CollectOccluders(Node root)
    {
        var finder = new FindNodesVisitor(n => n is OccluderNode);
        root.Accept(finder);
        Span<Vector3> buf = stackalloc Vector3[16];
        foreach (OccluderNode o in finder.Found)
        {
            if (o.Occluder.Length < 3 || o.Occluder.Length > 16) continue;
            var world = o.ComputeWorldMatrix();
            for (int i = 0; i < o.Occluder.Length; i++) buf[i] = Vector3.Transform(o.Occluder[i], world);
            var poly = buf[..o.Occluder.Length];
            var bs = BoundingSphere.FromBox(BoundingBox.FromPoints(poly));
            if (!_frustum.Intersects(bs)) continue;
            if (OcclusionVolume.TryCreate(poly, _eye, out var vol)) _occluders.Add(vol);
        }
    }
}
