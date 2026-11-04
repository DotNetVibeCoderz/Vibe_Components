using System.Numerics;
using OpenSceneGraph.Net.Math;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Scene;

/// <summary>Timing information for a frame, mirroring <c>osg::FrameStamp</c>.</summary>
public readonly record struct FrameStamp(long FrameNumber, double ReferenceTime, double SimulationTime, double DeltaTime);

/// <summary>Called during the update traversal, like <c>osg::NodeCallback</c> installed as an update callback.</summary>
public interface IUpdateCallback
{
    void Update(Node node, in FrameStamp frame);
}

internal sealed class DelegateUpdateCallback(Action<Node, FrameStamp> action) : IUpdateCallback
{
    public void Update(Node node, in FrameStamp frame) => action(node, frame);
}

/// <summary>
/// Base class of every scene graph node (<c>osg::Node</c>).
/// Nodes can be shared by several parents (instancing); bounds are cached and invalidated upward.
/// </summary>
public abstract class Node
{
    private static long s_nextId;
    private readonly List<Group> _parents = new(1);
    private BoundingSphere _bound = BoundingSphere.Invalid;
    private bool _boundDirty = true;
    private List<IUpdateCallback>? _updateCallbacks;

    protected Node() => Id = Interlocked.Increment(ref s_nextId);

    /// <summary>Process-wide unique identifier; renderers use it as a cache key.</summary>
    public long Id { get; }
    public string? Name { get; set; }

    /// <summary>Visitors skip nodes whose mask AND the visitor's traversal mask is zero (like <c>setNodeMask</c>).</summary>
    public uint NodeMask { get; set; } = 0xFFFFFFFF;

    /// <summary>When false the cull visitor never frustum-culls this node (useful for skies and HUDs).</summary>
    public bool CullingActive { get; set; } = true;

    public StateSet? StateSet { get; set; }

    /// <summary>Optional back-face style culling for whole subgraphs (see <see cref="Scene.ClusterCull"/>).</summary>
    public ClusterCull? ClusterCull { get; set; }

    /// <summary>Lazily creates the state set, like <c>getOrCreateStateSet()</c>.</summary>
    public StateSet GetOrCreateStateSet() => StateSet ??= new StateSet();

    public object? UserData { get; set; }
    public IReadOnlyList<Group> Parents => _parents;

    internal void AddParent(Group g) => _parents.Add(g);
    internal void RemoveParent(Group g) => _parents.Remove(g);

    public IReadOnlyList<IUpdateCallback> UpdateCallbacks => (IReadOnlyList<IUpdateCallback>?)_updateCallbacks ?? [];
    internal bool HasUpdateCallbacks => _updateCallbacks is { Count: > 0 };

    public Node AddUpdateCallback(IUpdateCallback callback)
    {
        (_updateCallbacks ??= new List<IUpdateCallback>(1)).Add(callback);
        return this;
    }

    public Node OnUpdate(Action<Node, FrameStamp> action) => AddUpdateCallback(new DelegateUpdateCallback(action));
    public bool RemoveUpdateCallback(IUpdateCallback callback) => _updateCallbacks?.Remove(callback) ?? false;

    /// <summary>World-space-agnostic bound of this node in its parent's coordinate frame.</summary>
    public BoundingSphere Bound
    {
        get
        {
            if (_boundDirty)
            {
                _bound = ComputeBound();
                _boundDirty = false;
            }
            return _bound;
        }
    }

    /// <summary>Marks this node's bound and all ancestors' bounds dirty.</summary>
    public void DirtyBound()
    {
        if (_boundDirty) return;
        _boundDirty = true;
        foreach (var p in _parents) p.DirtyBound();
    }

    protected abstract BoundingSphere ComputeBound();

    /// <summary>Double dispatch entry point: calls the most specific <c>NodeVisitor.Apply</c> overload.</summary>
    public abstract void Accept(NodeVisitor visitor);

    /// <summary>Visits this node's children (no-op for leaves).</summary>
    public virtual void Traverse(NodeVisitor visitor) { }

    /// <summary>Accumulates the local-to-world matrix along the first parent path.</summary>
    public Matrix4x4 ComputeWorldMatrix()
    {
        var m = Matrix4x4.Identity;
        for (Node? cur = this; cur is not null; cur = cur.Parents.Count > 0 ? cur.Parents[0] : null)
        {
            if (cur is not Transform t) continue;
            m *= t.GetMatrix();
            if (t.ReferenceFrame == ReferenceFrame.Absolute) break;
        }
        return m;
    }

    public override string ToString() => $"{GetType().Name}({Name ?? Id.ToString()})";
}
