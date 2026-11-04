using System.Numerics;
using OpenSceneGraph.Net.Math;

namespace OpenSceneGraph.Net.Scene;

/// <summary>A node with children (<c>osg::Group</c>).</summary>
public class Group : Node
{
    private readonly List<Node> _children = [];

    public IReadOnlyList<Node> Children => _children;
    public int ChildCount => _children.Count;

    public virtual Group AddChild(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (ReferenceEquals(child, this)) throw new InvalidOperationException("A node cannot be its own child.");
        _children.Add(child);
        child.AddParent(this);
        OnChildAdded(_children.Count - 1);
        DirtyBound();
        return this;
    }

    public Group AddChildren(params ReadOnlySpan<Node> children)
    {
        foreach (var c in children) AddChild(c);
        return this;
    }

    public virtual bool InsertChild(int index, Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (index < 0 || index > _children.Count) return false;
        _children.Insert(index, child);
        child.AddParent(this);
        OnChildAdded(index);
        DirtyBound();
        return true;
    }

    public bool RemoveChild(Node child)
    {
        int i = _children.IndexOf(child);
        if (i < 0) return false;
        RemoveChildAt(i);
        return true;
    }

    public virtual void RemoveChildAt(int index)
    {
        var c = _children[index];
        _children.RemoveAt(index);
        c.RemoveParent(this);
        OnChildRemoved(index);
        DirtyBound();
    }

    public void RemoveAllChildren()
    {
        for (int i = _children.Count - 1; i >= 0; i--) RemoveChildAt(i);
    }

    public bool ReplaceChild(Node oldChild, Node newChild)
    {
        int i = _children.IndexOf(oldChild);
        if (i < 0) return false;
        oldChild.RemoveParent(this);
        _children[i] = newChild;
        newChild.AddParent(this);
        DirtyBound();
        return true;
    }

    public Node GetChild(int index) => _children[index];

    protected virtual void OnChildAdded(int index) { }
    protected virtual void OnChildRemoved(int index) { }

    protected override BoundingSphere ComputeBound()
    {
        var bs = BoundingSphere.Invalid;
        foreach (var c in _children) bs = bs.ExpandBy(c.Bound);
        return bs;
    }

    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);

    public override void Traverse(NodeVisitor visitor)
    {
        // index loop: callbacks may add children during traversal
        for (int i = 0; i < _children.Count; i++) _children[i].Accept(visitor);
    }

    /// <summary>Depth-first search for the first node with the given name (including this node).</summary>
    public Node? FindByName(string name)
    {
        if (Name == name) return this;
        foreach (var c in _children)
        {
            if (c.Name == name) return c;
            if (c is Group g && g.FindByName(name) is { } found) return found;
        }
        return null;
    }
}

/// <summary>Convenience group for drawables, kept for OSG API familiarity (<c>osg::Geode</c>).</summary>
public class Geode : Group
{
    public Geode AddDrawable(Drawable drawable) { AddChild(drawable); return this; }
    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);
}

public enum ReferenceFrame { Relative, Absolute }

/// <summary>Base class of transforms (<c>osg::Transform</c>).</summary>
public abstract class Transform : Group
{
    /// <summary>Absolute transforms ignore parent transforms (handy for HUDs and sky domes).</summary>
    public ReferenceFrame ReferenceFrame { get; set; } = ReferenceFrame.Relative;

    /// <summary>Local matrix (child space to parent space), row-vector convention: <c>world = local * parent</c>.</summary>
    public abstract Matrix4x4 GetMatrix();

    protected override BoundingSphere ComputeBound()
    {
        var bs = base.ComputeBound();
        return ReferenceFrame == ReferenceFrame.Relative ? bs.Transform(GetMatrix()) : bs;
    }

    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);
}

/// <summary>Transform with an explicit matrix (<c>osg::MatrixTransform</c>).</summary>
public class MatrixTransform : Transform
{
    private Matrix4x4 _matrix = Matrix4x4.Identity;

    public MatrixTransform() { }
    public MatrixTransform(Matrix4x4 matrix) => _matrix = matrix;

    public Matrix4x4 Matrix
    {
        get => _matrix;
        set { _matrix = value; DirtyBound(); }
    }

    public override Matrix4x4 GetMatrix() => _matrix;

    public static MatrixTransform Translate(float x, float y, float z) => new(Matrix4x4.CreateTranslation(x, y, z));
    public static MatrixTransform Translate(Vector3 v) => new(Matrix4x4.CreateTranslation(v));
}

/// <summary>Transform composed from position, attitude, scale and pivot (<c>osg::PositionAttitudeTransform</c>).</summary>
public class PositionAttitudeTransform : Transform
{
    private Vector3 _position;
    private Quaternion _attitude = Quaternion.Identity;
    private Vector3 _scale = Vector3.One;
    private Vector3 _pivot;

    public Vector3 Position { get => _position; set { _position = value; DirtyBound(); } }
    public Quaternion Attitude { get => _attitude; set { _attitude = value; DirtyBound(); } }
    public Vector3 Scale { get => _scale; set { _scale = value; DirtyBound(); } }
    public Vector3 PivotPoint { get => _pivot; set { _pivot = value; DirtyBound(); } }

    public override Matrix4x4 GetMatrix() =>
        Matrix4x4.CreateTranslation(-_pivot) *
        Matrix4x4.CreateScale(_scale) *
        Matrix4x4.CreateFromQuaternion(_attitude) *
        Matrix4x4.CreateTranslation(_position);
}

/// <summary>Rotates its children to face the camera and/or keeps a constant screen size (<c>osg::AutoTransform</c>).</summary>
public class AutoTransform : Transform
{
    public Vector3 Position { get; set; }
    public bool AutoRotateToScreen { get; set; } = true;

    /// <summary>When &gt; 0 the children keep this many world units per pixel-equivalent at unit distance.</summary>
    public float AutoScaleToScreen { get; set; }

    /// <summary>Set by the cull visitor each frame from the current view.</summary>
    internal Matrix4x4 CullMatrix { get; set; } = Matrix4x4.Identity;

    public override Matrix4x4 GetMatrix() => CullMatrix * Matrix4x4.CreateTranslation(Position);

    internal void UpdateForView(in Matrix4x4 view, Vector3 eye)
    {
        var m = Matrix4x4.Identity;
        if (AutoRotateToScreen && Matrix4x4.Invert(view, out var inv))
        {
            inv.Translation = Vector3.Zero;
            m = inv;
        }
        if (AutoScaleToScreen > 0)
        {
            float d = Vector3.Distance(eye, Position);
            m = Matrix4x4.CreateScale(d * AutoScaleToScreen) * m;
        }
        CullMatrix = m;
    }
}

/// <summary>Selects which children are active (<c>osg::Switch</c>).</summary>
public class Switch : Group
{
    private readonly List<bool> _values = [];

    public bool NewChildDefaultValue { get; set; } = true;

    protected override void OnChildAdded(int index) => _values.Insert(index, NewChildDefaultValue);
    protected override void OnChildRemoved(int index) => _values.RemoveAt(index);

    public bool GetValue(int index) => _values[index];
    public void SetValue(int index, bool value) { _values[index] = value; DirtyBound(); }

    public void SetAllChildrenOn() { for (int i = 0; i < _values.Count; i++) _values[i] = true; DirtyBound(); }
    public void SetAllChildrenOff() { for (int i = 0; i < _values.Count; i++) _values[i] = false; DirtyBound(); }

    /// <summary>Turns exactly one child on.</summary>
    public void SetSingleChildOn(int index)
    {
        for (int i = 0; i < _values.Count; i++) _values[i] = i == index;
        DirtyBound();
    }

    public Switch AddChild(Node child, bool value)
    {
        base.AddChild(child);
        _values[^1] = value;
        return this;
    }

    protected override BoundingSphere ComputeBound()
    {
        var bs = BoundingSphere.Invalid;
        for (int i = 0; i < ChildCount; i++)
            if (_values[i]) bs = bs.ExpandBy(GetChild(i).Bound);
        return bs;
    }

    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);

    public override void Traverse(NodeVisitor visitor)
    {
        if (visitor.Mode == TraversalMode.AllChildren) { base.Traverse(visitor); return; }
        for (int i = 0; i < ChildCount; i++)
            if (_values[i]) GetChild(i).Accept(visitor);
    }
}

public enum LodCenterMode { UseBoundingSphereCenter, UserDefinedCenter }
public enum LodRangeMode { DistanceFromEyePoint, PixelSizeOnScreen }

/// <summary>Level of detail: picks children by distance or projected pixel size (<c>osg::LOD</c>).</summary>
public class Lod : Group
{
    private readonly List<(float Min, float Max)> _ranges = [];

    public LodCenterMode CenterMode { get; set; } = LodCenterMode.UseBoundingSphereCenter;
    public LodRangeMode RangeMode { get; set; } = LodRangeMode.DistanceFromEyePoint;
    public Vector3 Center { get; set; }

    /// <summary>Radius used for pixel-size LOD when no children are loaded yet (PagedLOD).</summary>
    public float Radius { get; set; } = -1;

    public IReadOnlyList<(float Min, float Max)> Ranges => _ranges;

    public Lod AddChild(Node child, float min, float max)
    {
        base.AddChild(child);
        _ranges[ChildCount - 1] = (min, max);
        return this;
    }

    public void SetRange(int index, float min, float max)
    {
        while (_ranges.Count <= index) _ranges.Add((0, float.MaxValue));
        _ranges[index] = (min, max);
    }

    protected override void OnChildAdded(int index)
    {
        if (_ranges.Count < ChildCount) _ranges.Insert(index, (0, float.MaxValue));
    }

    protected override void OnChildRemoved(int index)
    {
        if (index < _ranges.Count) _ranges.RemoveAt(index);
    }

    public Vector3 GetCenter() => CenterMode == LodCenterMode.UserDefinedCenter ? Center : Bound.Center;

    protected override BoundingSphere ComputeBound()
    {
        if (CenterMode == LodCenterMode.UserDefinedCenter && Radius >= 0) return new BoundingSphere(Center, Radius);
        return base.ComputeBound();
    }

    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);

    /// <summary>Traverses only the children whose range contains <paramref name="value"/>.</summary>
    public virtual void TraverseInRange(NodeVisitor visitor, float value)
    {
        for (int i = 0; i < ChildCount && i < _ranges.Count; i++)
        {
            var (min, max) = _ranges[i];
            if (value >= min && value < max) GetChild(i).Accept(visitor);
        }
    }
}

/// <summary>
/// LOD whose children are loaded on demand from files by the <see cref="IO.DatabasePager"/> (<c>osg::PagedLOD</c>).
/// Child 0 is usually an embedded low-resolution version; other ranges reference files.
/// </summary>
public class PagedLod : Lod
{
    private readonly List<PagedChild> _files = [];

    public sealed class PagedChild
    {
        public required string FileName { get; init; }
        public float PriorityOffset { get; init; }
        public float PriorityScale { get; init; } = 1f;
        public double LastUsedTime;
        public volatile bool Requested;
    }

    public string? DatabasePath { get; set; }

    /// <summary>Loaded children that were not visible for this long (seconds) can be expired by the pager.</summary>
    public double ExpiryTime { get; set; } = 30;

    public IReadOnlyList<PagedChild> Files => _files;

    public void SetFileName(int index, string fileName)
    {
        while (_files.Count <= index) _files.Add(new PagedChild { FileName = string.Empty });
        _files[index] = new PagedChild { FileName = fileName };
        SetRange(index, Ranges.Count > index ? Ranges[index].Min : 0, Ranges.Count > index ? Ranges[index].Max : float.MaxValue);
    }

    public string ResolvePath(string fileName) => DatabasePath is null ? fileName : Path.Combine(DatabasePath, fileName);

    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);
}

/// <summary>Loads external files as children once, on first traversal (<c>osg::ProxyNode</c>).</summary>
public class ProxyNode : Group
{
    public List<string> FileNames { get; } = [];
    public string? DatabasePath { get; set; }
    internal volatile bool Requested;

    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);
}

/// <summary>Positions a light in the scene; the light inherits the accumulated transform (<c>osg::LightSource</c>).</summary>
public class LightSource : Group
{
    public LightSource(State.Light light) => Light = light;
    public State.Light Light { get; set; }
    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);
}

public enum CameraRenderOrder { PreRender, NestedRender, PostRender }

/// <summary>
/// A camera inside the scene graph (<c>osg::Camera</c> used as a node): HUD overlays (post-render, absolute)
/// and render-to-texture passes (pre-render with a <see cref="RenderTarget"/>).
/// </summary>
public class CameraNode : Group
{
    public CameraRenderOrder RenderOrder { get; set; } = CameraRenderOrder.PostRender;
    public ReferenceFrame ReferenceFrame { get; set; } = ReferenceFrame.Absolute;
    public Matrix4x4 ViewMatrix { get; set; } = Matrix4x4.Identity;

    /// <summary>When null, the camera uses an orthographic projection matching the viewport in pixels (HUD).</summary>
    public Matrix4x4? ProjectionMatrix { get; set; }

    public bool ClearDepth { get; set; } = true;
    public Vector4? ClearColor { get; set; }

    /// <summary>When set, the subgraph is rendered into this texture instead of the window.</summary>
    public State.Texture2D? RenderTarget { get; set; }

    public int RenderOrderNumber { get; set; }

    protected override BoundingSphere ComputeBound() => BoundingSphere.Invalid; // never culled by parents

    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);
}
