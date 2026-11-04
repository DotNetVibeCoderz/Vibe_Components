using System.Numerics;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Rendering;

/// <summary>
/// The effective, fully inherited render state of a drawable (<c>osg::State</c> after applying the StateSet stack).
/// Instances are immutable and shared between draw items; renderers may use <see cref="Key"/> for state sorting.
/// </summary>
public sealed class ResolvedState
{
    private static int s_nextKey;

    internal ResolvedState() => Key = Interlocked.Increment(ref s_nextKey);

    public int Key { get; }
    public Material Material { get; internal set; } = DefaultMaterial;
    public Texture2D? Texture { get; internal set; }
    public ShaderProgram? Program { get; internal set; }
    public bool Lighting { get; internal set; } = true;
    public CullFaceMode CullFace { get; internal set; } = CullFaceMode.Back;
    public BlendMode Blend { get; internal set; } = BlendMode.Opaque;
    public PolygonMode PolygonMode { get; internal set; } = PolygonMode.Fill;
    public bool DepthTest { get; internal set; } = true;
    public bool DepthWrite { get; internal set; } = true;
    public int RenderBin { get; internal set; } = RenderBins.Opaque;
    public float LineWidth { get; internal set; } = 1f;
    public float PointSize { get; internal set; } = 1f;
    public bool CastShadows { get; internal set; } = true;
    public bool ReceiveShadows { get; internal set; } = true;
    public bool Fog { get; internal set; } = true;
    public UniformCollection? Uniforms { get; internal set; }

    internal uint OverrideMask;

    public bool IsTransparent => Blend != BlendMode.Opaque || RenderBin >= RenderBins.Transparent && RenderBin < RenderBins.Overlay;

    public static readonly Material DefaultMaterial = new();
    public static readonly ResolvedState Default = new();

    internal ResolvedState CloneState() => new()
    {
        Material = Material, Texture = Texture, Program = Program, Lighting = Lighting, CullFace = CullFace, Blend = Blend,
        PolygonMode = PolygonMode, DepthTest = DepthTest, DepthWrite = DepthWrite, RenderBin = RenderBin, LineWidth = LineWidth,
        PointSize = PointSize, CastShadows = CastShadows, ReceiveShadows = ReceiveShadows, Fog = Fog, Uniforms = Uniforms,
        OverrideMask = OverrideMask,
    };

    /// <summary>Applies a child state set over this (parent) state, honouring parent overrides.</summary>
    internal ResolvedState Merge(StateSet ss)
    {
        var r = CloneState();
        uint locked = OverrideMask;
        uint set = 0;
        bool Can(int bit) => (locked & (1u << bit)) == 0;

        if (ss.Material is { } m && Can(0)) { r.Material = m; set |= 1u << 0; }
        if (ss.Texture is { } t && Can(1)) { r.Texture = t; set |= 1u << 1; }
        if (ss.Program is { } p && Can(2)) { r.Program = p; set |= 1u << 2; }
        if (ss.Lighting is { } l && Can(3)) { r.Lighting = l; set |= 1u << 3; }
        if (ss.CullFace is { } cf && Can(4)) { r.CullFace = cf; set |= 1u << 4; }
        if (ss.Blend is { } b && Can(5)) { r.Blend = b; set |= 1u << 5; }
        if (ss.PolygonMode is { } pm && Can(6)) { r.PolygonMode = pm; set |= 1u << 6; }
        if (ss.DepthTest is { } dt && Can(7)) { r.DepthTest = dt; set |= 1u << 7; }
        if (ss.DepthWrite is { } dw && Can(8)) { r.DepthWrite = dw; set |= 1u << 8; }
        if (ss.RenderBin is { } rb && Can(9)) { r.RenderBin = rb; set |= 1u << 9; }
        if (ss.LineWidth is { } lw && Can(10)) { r.LineWidth = lw; set |= 1u << 10; }
        if (ss.PointSize is { } ps && Can(11)) { r.PointSize = ps; set |= 1u << 11; }
        if (ss.CastShadows is { } cs && Can(12)) { r.CastShadows = cs; set |= 1u << 12; }
        if (ss.ReceiveShadows is { } rs && Can(13)) { r.ReceiveShadows = rs; set |= 1u << 13; }
        if (ss.Fog is { } fg && Can(14)) { r.Fog = fg; set |= 1u << 14; }
        if (ss.Uniforms.Count > 0 && Can(15)) { r.Uniforms = ss.Uniforms; set |= 1u << 15; }

        // transparent material implies blending unless explicitly configured
        if (ss.Material is { IsTransparent: true } && ss.Blend is null && r.Blend == BlendMode.Opaque && Can(5))
        {
            r.Blend = BlendMode.AlphaBlend;
            if (ss.RenderBin is null && Can(9)) r.RenderBin = RenderBins.Transparent;
            if (ss.DepthWrite is null && Can(8)) r.DepthWrite = false;
        }

        if (ss.Override) r.OverrideMask |= set;
        return r;
    }
}

/// <summary>One drawable instance to render.</summary>
public struct DrawItem
{
    public Geometry Geometry;
    public Matrix4x4 World;
    public ResolvedState State;

    /// <summary>View-space distance used for sorting (larger = farther).</summary>
    public float Depth;
}

/// <summary>A light after transformation to world space.</summary>
public readonly record struct LightInstance(Light Light, Vector3 WorldPosition, Vector3 WorldDirection);

public enum PassKind { PreRender, Main, PostRender }

/// <summary>All draw items for one camera.</summary>
public sealed class RenderPass
{
    public PassKind Kind { get; internal set; }
    public CameraNode? CameraNode { get; internal set; }
    public Matrix4x4 View { get; internal set; }
    public Matrix4x4 Projection { get; internal set; }
    public Vector3 Eye { get; internal set; }
    public Vector4? ClearColor { get; internal set; }
    public bool ClearDepth { get; internal set; } = true;
    public Texture2D? Target { get; internal set; }
    public int Order { get; internal set; }

    /// <summary>Opaque items sorted by render bin then state key (minimises state changes), front to back within a state.</summary>
    public List<DrawItem> Opaque { get; } = new(256);

    /// <summary>Transparent items sorted back to front.</summary>
    public List<DrawItem> Transparent { get; } = new(64);

    internal void Clear()
    {
        Opaque.Clear();
        Transparent.Clear();
        Target = null;
        CameraNode = null;
        ClearColor = null;
    }

    internal void Sort()
    {
        Opaque.Sort(static (a, b) =>
        {
            int c = a.State.RenderBin.CompareTo(b.State.RenderBin);
            if (c != 0) return c;
            c = a.State.Key.CompareTo(b.State.Key);
            return c != 0 ? c : a.Depth.CompareTo(b.Depth);
        });
        Transparent.Sort(static (a, b) =>
        {
            int c = a.State.RenderBin.CompareTo(b.State.RenderBin);
            return c != 0 ? c : b.Depth.CompareTo(a.Depth);
        });
    }
}

/// <summary>Per-frame statistics (<c>osgViewer::StatsHandler</c> data).</summary>
public sealed class RenderStats
{
    public long FrameNumber { get; internal set; }
    public double UpdateMs { get; internal set; }
    public double CullMs { get; internal set; }
    public int NodesVisited { get; internal set; }
    public int DrawablesCulled { get; internal set; }

    /// <summary>Nodes (any type) rejected by culling; a culled group counts once for its whole subgraph.</summary>
    public int NodesCulled { get; internal set; }
    public int DrawItems { get; internal set; }
    public long Triangles { get; internal set; }
    public int PagedLodRequests { get; internal set; }
    public int ActiveLights { get; internal set; }
    public double FramesPerSecond { get; internal set; }

    public override string ToString() =>
        $"frame {FrameNumber} | {FramesPerSecond:F0} fps | update {UpdateMs:F2} ms | cull {CullMs:F2} ms | draws {DrawItems} | tris {Triangles:N0} | culled {NodesCulled}";
}

/// <summary>
/// The output of the cull traversal for one frame, consumed by an <see cref="IRenderer"/>.
/// Passes are ordered: pre-render (RTT) passes, the main pass, then post-render (HUD) passes.
/// </summary>
public sealed class RenderList
{
    private readonly List<RenderPass> _pool = [];

    public List<RenderPass> Passes { get; } = [];
    public List<LightInstance> Lights { get; } = [];
    public SceneEnvironment Environment { get; internal set; } = new();
    public Camera Camera { get; internal set; } = new();
    public RenderStats Stats { get; } = new();

    /// <summary>Focus point used to centre shadow maps (the manipulator's centre when available).</summary>
    public Vector3 Focus { get; internal set; }

    public RenderPass MainPass => Passes.First(p => p.Kind == PassKind.Main);

    internal void Reset()
    {
        foreach (var p in Passes) { p.Clear(); _pool.Add(p); }
        Passes.Clear();
        Lights.Clear();
    }

    internal RenderPass RentPass(PassKind kind)
    {
        RenderPass p;
        if (_pool.Count > 0) { p = _pool[^1]; _pool.RemoveAt(_pool.Count - 1); }
        else p = new RenderPass();
        p.Kind = kind;
        Passes.Add(p);
        return p;
    }

    internal void Finish()
    {
        Passes.Sort(static (a, b) =>
        {
            int c = a.Kind.CompareTo(b.Kind);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
        foreach (var p in Passes) p.Sort();
    }
}

/// <summary>What a renderer backend can do; the gallery uses it to display feature badges.</summary>
[Flags]
public enum RendererCapabilities
{
    None = 0,
    Lighting = 1,
    Textures = 2,
    Shadows = 4,
    Fog = 8,
    CustomShaders = 16,
    RenderToTexture = 32,
    PostProcessing = 64,
    Stereo = 128,
    Skinning = 256,
}

/// <summary>A rendering backend (OpenGL, WebGL, native OpenSceneGraph...).</summary>
public interface IRenderer : IDisposable
{
    string Name { get; }
    RendererCapabilities Capabilities { get; }
    void Render(RenderList list);
}
