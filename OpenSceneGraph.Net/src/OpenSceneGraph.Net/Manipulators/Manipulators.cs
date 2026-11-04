using System.Numerics;
using OpenSceneGraph.Net.Animation;
using OpenSceneGraph.Net.Math;

namespace OpenSceneGraph.Net.Manipulators;

/// <summary>Controls the view matrix from input (<c>osgGA::CameraManipulator</c>). The scene is Z-up.</summary>
public interface ICameraManipulator : IEventHandler
{
    string Name { get; }

    /// <summary>The view (world-to-eye) matrix.</summary>
    Matrix4x4 ViewMatrix { get; }

    /// <summary>The point the camera orbits or looks at; used to centre shadow maps.</summary>
    Vector3 Center { get; }

    /// <summary>Frames the given bound (the HOME position, triggered by the space key in OSG).</summary>
    void Home(Math.BoundingSphere bound);

    void Update(double deltaTime);
}

/// <summary>Orbits a centre point (<c>osgGA::TrackballManipulator</c> / <c>OrbitManipulator</c>), Z-up, with inertia.</summary>
public class TrackballManipulator : ICameraManipulator
{
    private float _lastX, _lastY;
    private MouseButtons _buttons;
    private float _spinHeading, _spinPitch;
    private bool _thrown;

    public virtual string Name => "Trackball";
    public Vector3 Center { get; set; }
    public float Distance { get; set; } = 10f;

    /// <summary>Heading in degrees around +Z (0 looks towards +Y).</summary>
    public float Heading { get; set; } = 30f;

    /// <summary>Elevation angle in degrees above the XY plane.</summary>
    public float Pitch { get; set; } = 25f;

    public float MinDistance { get; set; } = 0.05f;
    public float RotateSpeed { get; set; } = 0.35f;
    public float ZoomSpeed { get; set; } = 0.12f;
    public bool AllowThrow { get; set; } = true;
    public float MinPitch { get; set; } = -89f;
    public float MaxPitch { get; set; } = 89f;

    public Vector3 Eye
    {
        get
        {
            float h = MathUtil.Radians(Heading), p = MathUtil.Radians(Pitch);
            // eye sits behind the centre along the heading, raised by the pitch
            var back = new Vector3(-MathF.Sin(h) * MathF.Cos(p), -MathF.Cos(h) * MathF.Cos(p), MathF.Sin(p));
            return Center + back * Distance;
        }
    }

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Eye, Center, Vector3.UnitZ);

    public virtual void Home(Math.BoundingSphere bound)
    {
        if (!bound.IsValid) return;
        Center = bound.Center;
        Distance = MathF.Max(bound.Radius * 2.6f, 0.5f);
        Heading = -35f;
        Pitch = 28f;
        _spinHeading = _spinPitch = 0;
    }

    /// <summary>Places the eye explicitly (like <c>setTransformation(eye, center, up)</c>).</summary>
    public void SetLookAt(Vector3 eye, Vector3 center)
    {
        Center = center;
        var d = eye - center;
        Distance = MathF.Max(d.Length(), MinDistance);
        Pitch = MathUtil.Degrees(MathF.Asin(System.Math.Clamp(d.Z / Distance, -1, 1)));
        Heading = MathUtil.Degrees(MathF.Atan2(-d.X, -d.Y));
    }

    public virtual bool Handle(in InputEvent e, Viewer viewer)
    {
        switch (e.Type)
        {
            case InputEventType.Push:
                _buttons = e.Buttons; _lastX = e.X; _lastY = e.Y; _thrown = false; _spinHeading = _spinPitch = 0;
                return true;
            case InputEventType.Release:
                _buttons = MouseButtons.None;
                _thrown = AllowThrow && (MathF.Abs(_spinHeading) > 0.05f || MathF.Abs(_spinPitch) > 0.05f);
                return true;
            case InputEventType.Drag:
            {
                float dx = e.X - _lastX, dy = e.Y - _lastY;
                _lastX = e.X; _lastY = e.Y;
                var b = e.Buttons != MouseButtons.None ? e.Buttons : _buttons;
                bool pan = b.HasFlag(MouseButtons.Middle) || b.HasFlag(MouseButtons.Left) && e.Modifiers.HasFlag(KeyModifiers.Shift);
                if (pan) Pan(dx, dy, e.Height);
                else if (b.HasFlag(MouseButtons.Right)) Zoom(dy * 0.01f);
                else if (b.HasFlag(MouseButtons.Left))
                {
                    _spinHeading = -dx * RotateSpeed;
                    _spinPitch = dy * RotateSpeed;
                    Rotate(_spinHeading, _spinPitch);
                }
                return true;
            }
            case InputEventType.Scroll:
                Zoom(-e.ScrollDelta * ZoomSpeed);
                return true;
            case InputEventType.KeyDown when e.IsKey(" ") || e.IsKey("Space") || e.IsKey("h"):
                viewer.Home();
                return true;
        }
        return false;
    }

    protected void Rotate(float dHeading, float dPitch)
    {
        Heading = (Heading + dHeading) % 360f;
        Pitch = System.Math.Clamp(Pitch + dPitch, MinPitch, MaxPitch);
    }

    protected void Zoom(float amount) => Distance = MathF.Max(MinDistance, Distance * MathF.Exp(amount));

    protected virtual void Pan(float dx, float dy, int viewHeight)
    {
        if (!Matrix4x4.Invert(ViewMatrix, out var inv)) return;
        float scale = Distance * 2f / MathF.Max(1, viewHeight);
        var right = new Vector3(inv.M11, inv.M12, inv.M13);
        var up = new Vector3(inv.M21, inv.M22, inv.M23);
        Center += (-right * dx + up * dy) * scale;
    }

    public virtual void Update(double deltaTime)
    {
        if (!_thrown || _buttons != MouseButtons.None) return;
        float k = MathF.Pow(0.08f, (float)deltaTime); // exponential decay of the throw
        Rotate(_spinHeading * (float)deltaTime * 60f, _spinPitch * (float)deltaTime * 60f);
        _spinHeading *= k;
        _spinPitch *= k;
        if (MathF.Abs(_spinHeading) < 0.01f && MathF.Abs(_spinPitch) < 0.01f) _thrown = false;
    }
}

/// <summary>
/// Trackball that keeps its centre on the terrain surface (<c>osgGA::TerrainManipulator</c>).
/// Supply <see cref="HeightAt"/> (for example from <see cref="Terrain.HeightField.Sample"/> or a ray intersection).
/// </summary>
public sealed class TerrainManipulator : TrackballManipulator
{
    public override string Name => "Terrain";
    public Func<float, float, float?>? HeightAt { get; set; }

    public TerrainManipulator() { MinPitch = 2f; }

    protected override void Pan(float dx, float dy, int viewHeight)
    {
        // pan across the ground plane rather than the screen plane
        float h = MathUtil.Radians(Heading);
        var fwd = new Vector3(MathF.Sin(h), MathF.Cos(h), 0);
        var right = new Vector3(fwd.Y, -fwd.X, 0);
        float scale = Distance * 2f / MathF.Max(1, viewHeight);
        var c = Center + (-right * dx + fwd * dy) * scale;
        if (HeightAt?.Invoke(c.X, c.Y) is { } z) c.Z = z;
        Center = c;
    }
}

/// <summary>Mouse-steered flight (<c>osgGA::FlightManipulator</c>): pointer offset sets yaw/pitch rate, buttons set speed.</summary>
public sealed class FlightManipulator : ICameraManipulator
{
    private float _nx, _ny;
    private MouseButtons _buttons;
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Flight";
    public Vector3 Position { get; set; } = new(0, -50, 10);
    public Quaternion Orientation { get; set; } = Quaternion.Identity; // identity looks along +Y with +Z up
    public float Speed { get; set; } = 10f;
    public float Acceleration { get; set; } = 25f;
    public float MaxSpeed { get; set; } = 400f;
    public float YawRate { get; set; } = 60f;
    public float PitchRate { get; set; } = 45f;
    public Vector3 Center => Position + Forward * 10;

    public Vector3 Forward => Vector3.Transform(Vector3.UnitY, Orientation);
    public Vector3 Up => Vector3.Transform(Vector3.UnitZ, Orientation);

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);

    public void Home(Math.BoundingSphere bound)
    {
        if (!bound.IsValid) return;
        Position = bound.Center + new Vector3(0, -bound.Radius * 2.5f, bound.Radius * 0.6f);
        Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathUtil.Radians(-12));
        Speed = bound.Radius * 0.15f;
    }

    public bool Handle(in InputEvent e, Viewer viewer)
    {
        switch (e.Type)
        {
            case InputEventType.Push: _buttons = e.Buttons; _nx = e.NormalizedX; _ny = e.NormalizedY; return true;
            case InputEventType.Release: _buttons = MouseButtons.None; return true;
            case InputEventType.Move or InputEventType.Drag: _nx = e.NormalizedX; _ny = e.NormalizedY; return true;
            case InputEventType.KeyDown when e.Key is not null:
                if (e.IsKey(" ")) { viewer.Home(); return true; }
                _keys.Add(e.Key); return true;
            case InputEventType.KeyUp when e.Key is not null: _keys.Remove(e.Key); return true;
        }
        return false;
    }

    public void Update(double deltaTime)
    {
        float dt = (float)deltaTime;
        if (_buttons.HasFlag(MouseButtons.Left) || _keys.Contains("w")) Speed += Acceleration * dt;
        if (_buttons.HasFlag(MouseButtons.Right) || _keys.Contains("s")) Speed -= Acceleration * dt;
        if (_buttons.HasFlag(MouseButtons.Middle)) Speed = 0;
        Speed = System.Math.Clamp(Speed, -MaxSpeed * 0.25f, MaxSpeed);

        float dz = 0.08f; // dead zone around the view centre
        float yaw = MathF.Abs(_nx) > dz ? -_nx * YawRate : 0;
        float pitch = MathF.Abs(_ny) > dz ? _ny * PitchRate : 0;
        if (_keys.Contains("a")) yaw += YawRate * 0.6f;
        if (_keys.Contains("d")) yaw -= YawRate * 0.6f;

        var qYaw = Quaternion.CreateFromAxisAngle(Up, MathUtil.Radians(yaw * dt));
        var right = Vector3.Cross(Forward, Up);
        var qPitch = Quaternion.CreateFromAxisAngle(right, MathUtil.Radians(pitch * dt));
        Orientation = Quaternion.Normalize(qPitch * qYaw * Orientation);
        Position += Forward * Speed * dt;
    }
}

/// <summary>
/// Ground vehicle camera (<c>osgGA::DriveManipulator</c>): WASD/arrow keys drive, eye height is kept above
/// the ground using <see cref="HeightAt"/>.
/// </summary>
public sealed class DriveManipulator : ICameraManipulator
{
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private float _lastX;
    private bool _dragging;

    public string Name => "Drive";
    public Vector3 Position { get; set; } = new(0, -20, 0);
    public float Heading { get; set; }
    public float Pitch { get; set; } = -5f;
    public float EyeHeight { get; set; } = 1.7f;
    public float Speed { get; private set; }
    public float MaxSpeed { get; set; } = 25f;
    public float Acceleration { get; set; } = 12f;
    public Func<float, float, float?>? HeightAt { get; set; }

    public Vector3 Forward
    {
        get
        {
            float h = MathUtil.Radians(Heading), p = MathUtil.Radians(Pitch);
            return new Vector3(MathF.Sin(h) * MathF.Cos(p), MathF.Cos(h) * MathF.Cos(p), MathF.Sin(p));
        }
    }

    public Vector3 Center => Position + Forward * 10;
    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitZ);

    public void Home(Math.BoundingSphere bound)
    {
        if (!bound.IsValid) return;
        Position = bound.Center + new Vector3(0, -bound.Radius, 0);
        Heading = 0;
        Speed = 0;
        SnapToGround();
    }

    public bool Handle(in InputEvent e, Viewer viewer)
    {
        switch (e.Type)
        {
            case InputEventType.KeyDown when e.Key is not null:
                if (e.IsKey(" ")) { viewer.Home(); return true; }
                _keys.Add(e.Key); return true;
            case InputEventType.KeyUp when e.Key is not null: _keys.Remove(e.Key); return true;
            case InputEventType.Push: _dragging = true; _lastX = e.X; return true;
            case InputEventType.Release: _dragging = false; return true;
            case InputEventType.Drag when _dragging:
                Heading += (e.X - _lastX) * 0.3f; _lastX = e.X; return true;
        }
        return false;
    }

    private bool Key(string a, string b) => _keys.Contains(a) || _keys.Contains(b);

    public void Update(double deltaTime)
    {
        float dt = (float)deltaTime;
        if (Key("w", "ArrowUp")) Speed += Acceleration * dt;
        else if (Key("s", "ArrowDown")) Speed -= Acceleration * dt;
        else Speed *= MathF.Pow(0.3f, dt);
        Speed = System.Math.Clamp(Speed, -MaxSpeed * 0.4f, MaxSpeed);
        float turn = (Key("a", "ArrowLeft") ? -1 : 0) + (Key("d", "ArrowRight") ? 1 : 0);
        Heading += turn * 70f * dt * MathF.Min(1, MathF.Abs(Speed) / 3f + 0.3f);

        float h = MathUtil.Radians(Heading);
        Position += new Vector3(MathF.Sin(h), MathF.Cos(h), 0) * Speed * dt;
        SnapToGround();
    }

    private void SnapToGround()
    {
        if (HeightAt?.Invoke(Position.X, Position.Y) is { } z) Position = Position with { Z = z + EyeHeight };
    }
}

/// <summary>WASD + mouse-look camera, the common game-style control (not in OSG core but frequently added).</summary>
public sealed class FirstPersonManipulator : ICameraManipulator
{
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private float _lastX, _lastY;
    private bool _looking;

    public string Name => "FirstPerson";
    public Vector3 Position { get; set; } = new(0, -10, 2);
    public float Heading { get; set; }
    public float Pitch { get; set; }
    public float MoveSpeed { get; set; } = 6f;
    public Vector3 Forward
    {
        get
        {
            float h = MathUtil.Radians(Heading), p = MathUtil.Radians(Pitch);
            return new Vector3(MathF.Sin(h) * MathF.Cos(p), MathF.Cos(h) * MathF.Cos(p), MathF.Sin(p));
        }
    }
    public Vector3 Center => Position + Forward * 5;
    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitZ);

    public void Home(Math.BoundingSphere bound)
    {
        if (!bound.IsValid) return;
        Position = bound.Center + new Vector3(0, -bound.Radius * 2, bound.Radius * 0.5f);
        Heading = 0; Pitch = -12;
        MoveSpeed = MathF.Max(1, bound.Radius * 0.4f);
    }

    public bool Handle(in InputEvent e, Viewer viewer)
    {
        switch (e.Type)
        {
            case InputEventType.KeyDown when e.Key is not null:
                if (e.IsKey(" ")) { viewer.Home(); return true; }
                _keys.Add(e.Key); return true;
            case InputEventType.KeyUp when e.Key is not null: _keys.Remove(e.Key); return true;
            case InputEventType.Push: _looking = true; _lastX = e.X; _lastY = e.Y; return true;
            case InputEventType.Release: _looking = false; return true;
            case InputEventType.Drag when _looking:
                Heading += (e.X - _lastX) * 0.25f;
                Pitch = System.Math.Clamp(Pitch - (e.Y - _lastY) * 0.25f, -89, 89);
                _lastX = e.X; _lastY = e.Y;
                return true;
        }
        return false;
    }

    public void Update(double deltaTime)
    {
        float dt = (float)deltaTime;
        var fwd = Forward;
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
        var move = Vector3.Zero;
        if (_keys.Contains("w")) move += fwd;
        if (_keys.Contains("s")) move -= fwd;
        if (_keys.Contains("d")) move += right;
        if (_keys.Contains("a")) move -= right;
        if (_keys.Contains("e")) move += Vector3.UnitZ;
        if (_keys.Contains("q")) move -= Vector3.UnitZ;
        if (move != Vector3.Zero) Position += Vector3.Normalize(move) * MoveSpeed * dt * (_keys.Contains("Shift") ? 3 : 1);
    }
}

/// <summary>Plays back an <see cref="AnimationPath"/> as the camera (<c>osgGA::AnimationPathManipulator</c>).</summary>
public sealed class AnimationPathManipulator(AnimationPath path) : ICameraManipulator
{
    private double _time;
    public string Name => "AnimationPath";
    public AnimationPath Path { get; } = path;
    public double TimeScale { get; set; } = 1;
    public Vector3 Center => Vector3.Transform(Vector3.UnitY * 10, Matrix);
    public Matrix4x4 Matrix { get; private set; } = Matrix4x4.Identity;

    /// <summary>The path's local frame looks along +Y with +Z up.</summary>
    public Matrix4x4 ViewMatrix
    {
        get
        {
            var eye = Matrix.Translation;
            var fwd = Vector3.TransformNormal(Vector3.UnitY, Matrix);
            var up = Vector3.TransformNormal(Vector3.UnitZ, Matrix);
            return Matrix4x4.CreateLookAt(eye, eye + fwd, up);
        }
    }

    public void Home(Math.BoundingSphere bound) => _time = 0;
    public bool Handle(in InputEvent e, Viewer viewer) => false;

    public void Update(double deltaTime)
    {
        _time += deltaTime * TimeScale;
        Matrix = Path.GetMatrix(_time);
    }
}

/// <summary>Switches between manipulators with number keys 1..9 (<c>osgGA::KeySwitchMatrixManipulator</c>).</summary>
public sealed class KeySwitchManipulator : ICameraManipulator
{
    private readonly List<(string Key, ICameraManipulator M)> _items = [];
    private int _current;

    public string Name => $"KeySwitch({Current.Name})";
    public ICameraManipulator Current => _items[_current].M;
    public IReadOnlyList<(string Key, ICameraManipulator M)> Items => _items;

    public KeySwitchManipulator Add(string key, ICameraManipulator m) { _items.Add((key, m)); return this; }

    public void Select(int index) => _current = System.Math.Clamp(index, 0, _items.Count - 1);

    public Matrix4x4 ViewMatrix => Current.ViewMatrix;
    public Vector3 Center => Current.Center;
    public void Home(Math.BoundingSphere bound) => Current.Home(bound);
    public void Update(double deltaTime) => Current.Update(deltaTime);

    public bool Handle(in InputEvent e, Viewer viewer)
    {
        if (e.Type == InputEventType.KeyDown && e.Key is { } k)
        {
            for (int i = 0; i < _items.Count; i++)
                if (string.Equals(_items[i].Key, k, StringComparison.OrdinalIgnoreCase))
                {
                    _current = i;
                    if (viewer.SceneData is { } s) Current.Home(s.Bound);
                    return true;
                }
        }
        return Current.Handle(e, viewer);
    }
}
