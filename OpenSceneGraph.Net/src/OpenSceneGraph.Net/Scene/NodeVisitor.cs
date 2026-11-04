using System.Numerics;


namespace OpenSceneGraph.Net.Scene;

public enum TraversalMode { None, ActiveChildren, AllChildren }

/// <summary>
/// Visitor over the scene graph (<c>osg::NodeVisitor</c>). Override the <c>Apply</c> overloads you need;
/// each default implementation forwards to the next more general overload and finally traverses.
/// </summary>
public abstract class NodeVisitor(TraversalMode mode = TraversalMode.ActiveChildren)
{
    public TraversalMode Mode { get; set; } = mode;
    public uint TraversalMask { get; set; } = 0xFFFFFFFF;

    /// <summary>Stack of nodes from the traversal root to the current node.</summary>
    protected List<Node> NodePath { get; } = new(32);

    public bool ValidNodeMask(Node node) => (node.NodeMask & TraversalMask) != 0;

    public void Traverse(Node node)
    {
        if (Mode == TraversalMode.None) return;
        NodePath.Add(node);
        node.Traverse(this);
        NodePath.RemoveAt(NodePath.Count - 1);
    }

    public virtual void Apply(Node node) => Traverse(node);
    public virtual void Apply(Drawable drawable) => Apply((Node)drawable);
    public virtual void Apply(Group group) => Apply((Node)group);
    public virtual void Apply(Geode geode) => Apply((Group)geode);
    public virtual void Apply(Transform transform) => Apply((Group)transform);
    public virtual void Apply(Switch sw) => Apply((Group)sw);
    public virtual void Apply(Lod lod) => Apply((Group)lod);
    public virtual void Apply(PagedLod lod) => Apply((Lod)lod);
    public virtual void Apply(ProxyNode proxy) => Apply((Group)proxy);
    public virtual void Apply(LightSource light) => Apply((Group)light);
    public virtual void Apply(CameraNode camera) => Apply((Group)camera);
    public virtual void Apply(OccluderNode occluder) => Apply((Group)occluder);
}

/// <summary>Visitor that tracks the accumulated local-to-world matrix.</summary>
public abstract class MatrixTrackingVisitor(TraversalMode mode = TraversalMode.ActiveChildren) : NodeVisitor(mode)
{
    private readonly Stack<Matrix4x4> _matrices = new();

    public Matrix4x4 CurrentMatrix { get; private set; } = Matrix4x4.Identity;

    public void ResetMatrix(Matrix4x4 m)
    {
        _matrices.Clear();
        CurrentMatrix = m;
    }

    public override void Apply(Transform transform)
    {
        _matrices.Push(CurrentMatrix);
        CurrentMatrix = transform.ReferenceFrame == ReferenceFrame.Absolute
            ? transform.GetMatrix()
            : transform.GetMatrix() * CurrentMatrix;
        Apply((Group)transform);
        CurrentMatrix = _matrices.Pop();
    }
}

/// <summary>Runs update callbacks (<c>osgUtil::UpdateVisitor</c>). Visits all children so hidden nodes keep animating.</summary>
public sealed class UpdateVisitor() : NodeVisitor(TraversalMode.AllChildren)
{
    public FrameStamp Frame { get; set; }

    public override void Apply(Node node)
    {
        if (node.HasUpdateCallbacks)
        {
            var cbs = node.UpdateCallbacks;
            for (int i = 0; i < cbs.Count; i++) cbs[i].Update(node, Frame);
        }
        Traverse(node);
    }
}

/// <summary>Collects nodes matching a predicate.</summary>
public sealed class FindNodesVisitor(Func<Node, bool> predicate) : NodeVisitor(TraversalMode.AllChildren)
{
    public List<Node> Found { get; } = [];

    public override void Apply(Node node)
    {
        if (predicate(node)) Found.Add(node);
        Traverse(node);
    }

    public static List<T> FindAll<T>(Node root) where T : Node
    {
        var v = new FindNodesVisitor(n => n is T);
        root.Accept(v);
        return v.Found.Cast<T>().ToList();
    }
}

/// <summary>Counts nodes, drawables, vertices and primitives (like <c>osgUtil::StatsVisitor</c>).</summary>
public sealed class StatsVisitor() : NodeVisitor(TraversalMode.AllChildren)
{
    private readonly HashSet<long> _seen = [];

    public int Nodes { get; private set; }
    public int Groups { get; private set; }
    public int Transforms { get; private set; }
    public int Drawables { get; private set; }
    public long Vertices { get; private set; }
    public long Triangles { get; private set; }
    public int UniqueDrawables => _seen.Count;

    public override void Apply(Node node) { Nodes++; Traverse(node); }
    public override void Apply(Group group) { Groups++; Apply((Node)group); }
    public override void Apply(Transform transform) { Transforms++; Apply((Group)transform); }

    public override void Apply(Drawable drawable)
    {
        Drawables++;
        _seen.Add(drawable.Id);
        if (drawable is Geometry g)
        {
            Vertices += g.VertexCount;
            Triangles += g.TriangleCount;
        }
        Apply((Node)drawable);
    }

    public override string ToString() =>
        $"nodes={Nodes} groups={Groups} transforms={Transforms} drawables={Drawables} (unique {UniqueDrawables}) vertices={Vertices} triangles={Triangles}";
}
