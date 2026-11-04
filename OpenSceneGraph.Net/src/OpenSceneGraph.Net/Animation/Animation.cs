using System.Numerics;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Animation;

public enum LoopMode { Loop, Swing, NoLoop }

/// <summary>A pose at a point in time.</summary>
public readonly record struct ControlPoint(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public ControlPoint(Vector3 position) : this(position, Quaternion.Identity, Vector3.One) { }
    public ControlPoint(Vector3 position, Quaternion rotation) : this(position, rotation, Vector3.One) { }

    public Matrix4x4 ToMatrix() => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    public static ControlPoint Interpolate(in ControlPoint a, in ControlPoint b, float t) =>
        new(Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t), Vector3.Lerp(a.Scale, b.Scale, t));
}

/// <summary>Time-stamped transform path (<c>osg::AnimationPath</c>).</summary>
public sealed class AnimationPath
{
    private readonly SortedList<double, ControlPoint> _points = [];

    public LoopMode LoopMode { get; set; } = LoopMode.Loop;
    public IReadOnlyList<double> Times => (IReadOnlyList<double>)_points.Keys;
    public double FirstTime => _points.Count > 0 ? _points.Keys[0] : 0;
    public double LastTime => _points.Count > 0 ? _points.Keys[^1] : 0;
    public double Period => LastTime - FirstTime;
    public int Count => _points.Count;

    public AnimationPath Insert(double time, ControlPoint cp) { _points[time] = cp; return this; }

    public ControlPoint GetControlPoint(double time)
    {
        if (_points.Count == 0) return new ControlPoint(Vector3.Zero);
        if (_points.Count == 1) return _points.Values[0];

        double period = Period;
        double t = time - FirstTime;
        switch (LoopMode)
        {
            case LoopMode.Loop: t = period > 0 ? ((t % period) + period) % period : 0; break;
            case LoopMode.Swing:
                double p2 = period * 2;
                t = p2 > 0 ? ((t % p2) + p2) % p2 : 0;
                if (t > period) t = p2 - t;
                break;
            case LoopMode.NoLoop: t = System.Math.Clamp(t, 0, period); break;
        }
        t += FirstTime;

        var keys = _points.Keys;
        int lo = 0, hi = keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (keys[mid] <= t) lo = mid; else hi = mid;
        }
        double span = keys[hi] - keys[lo];
        float f = span > 0 ? (float)((t - keys[lo]) / span) : 0;
        return ControlPoint.Interpolate(_points.Values[lo], _points.Values[hi], System.Math.Clamp(f, 0, 1));
    }

    public Matrix4x4 GetMatrix(double time) => GetControlPoint(time).ToMatrix();

    /// <summary>Circular path in the XY plane, facing along the direction of travel (+Y local forward).</summary>
    public static AnimationPath Circle(Vector3 center, float radius, double period, int samples = 64, float height = 0, bool clockwise = false)
    {
        var p = new AnimationPath { LoopMode = LoopMode.Loop };
        for (int i = 0; i <= samples; i++)
        {
            float a = 2 * MathF.PI * i / samples * (clockwise ? -1 : 1);
            var pos = center + new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, height);
            var tangent = new Vector3(-MathF.Sin(a), MathF.Cos(a), 0) * (clockwise ? -1 : 1);
            float heading = MathF.Atan2(tangent.X, tangent.Y);
            p.Insert(period * i / samples, new ControlPoint(pos, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -heading)));
        }
        return p;
    }

    /// <summary>Path through waypoints at constant speed, oriented along the track.</summary>
    public static AnimationPath ThroughWaypoints(IReadOnlyList<Vector3> waypoints, float speed, LoopMode mode = LoopMode.Loop)
    {
        var p = new AnimationPath { LoopMode = mode };
        double t = 0;
        for (int i = 0; i < waypoints.Count; i++)
        {
            var next = waypoints[(i + 1) % waypoints.Count];
            var dir = next - waypoints[i];
            if (dir.LengthSquared() < 1e-9f && i > 0) dir = waypoints[i] - waypoints[i - 1];
            float heading = MathF.Atan2(dir.X, dir.Y);
            float pitch = MathF.Atan2(dir.Z, new Vector2(dir.X, dir.Y).Length());
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -heading) * Quaternion.Identity;
            q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch) * q;
            p.Insert(t, new ControlPoint(waypoints[i], Quaternion.Normalize(q)));
            if (i < waypoints.Count - 1 || mode == LoopMode.Loop) t += Vector3.Distance(waypoints[i], next) / MathF.Max(speed, 1e-3f);
        }
        if (mode == LoopMode.Loop) p.Insert(t, p.GetControlPoint(0) with { });
        return p;
    }
}

/// <summary>Drives a transform along an <see cref="AnimationPath"/> (<c>osg::AnimationPathCallback</c>).</summary>
public sealed class AnimationPathCallback(AnimationPath path) : IUpdateCallback
{
    public AnimationPath Path { get; } = path;
    public double TimeOffset { get; set; }
    public double TimeMultiplier { get; set; } = 1;
    public bool Paused { get; set; }
    private double _elapsed;

    public void Update(Node node, in FrameStamp frame)
    {
        if (Paused) return;
        _elapsed += frame.DeltaTime;
        double t = (_elapsed + TimeOffset) * TimeMultiplier;
        var cp = Path.GetControlPoint(t);
        switch (node)
        {
            case MatrixTransform mt: mt.Matrix = cp.ToMatrix(); break;
            case PositionAttitudeTransform pat: pat.Position = cp.Position; pat.Attitude = cp.Rotation; pat.Scale = cp.Scale; break;
        }
    }
}

/// <summary>Spins a transform around an axis at a constant rate (degrees per second).</summary>
public sealed class SpinCallback(Vector3 axis, float degreesPerSecond) : IUpdateCallback
{
    private readonly Vector3 _axis = Vector3.Normalize(axis);
    public float DegreesPerSecond { get; set; } = degreesPerSecond;

    public void Update(Node node, in FrameStamp frame)
    {
        float angle = (float)(frame.SimulationTime * DegreesPerSecond * System.Math.PI / 180);
        var q = Quaternion.CreateFromAxisAngle(_axis, angle);
        switch (node)
        {
            case PositionAttitudeTransform pat: pat.Attitude = q; break;
            case MatrixTransform mt:
                var t = mt.Matrix.Translation;
                mt.Matrix = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(t);
                break;
        }
    }
}

public enum Interpolation { Step, Linear, CubicHermite }

/// <summary>Keyframes of one value type (<c>osgAnimation::TemplateKeyframeContainer</c>).</summary>
public sealed class KeyframeChannel<T>(string targetName, string property, Func<T, T, float, T> lerp) where T : struct
{
    private readonly List<(double Time, T Value)> _keys = [];

    /// <summary>Name of the node this channel animates.</summary>
    public string TargetName { get; } = targetName;

    /// <summary>"translation", "rotation", "scale" or a custom property understood by <see cref="AnimationClip.Apply"/>.</summary>
    public string Property { get; } = property;

    public Interpolation Interpolation { get; set; } = Interpolation.Linear;
    public IReadOnlyList<(double Time, T Value)> Keys => _keys;
    public double Duration => _keys.Count > 0 ? _keys[^1].Time : 0;

    public KeyframeChannel<T> Add(double time, T value)
    {
        int i = _keys.FindIndex(k => k.Time > time);
        if (i < 0) _keys.Add((time, value)); else _keys.Insert(i, (time, value));
        return this;
    }

    public T Evaluate(double time)
    {
        if (_keys.Count == 0) return default;
        if (time <= _keys[0].Time) return _keys[0].Value;
        if (time >= _keys[^1].Time) return _keys[^1].Value;
        int hi = _keys.FindIndex(k => k.Time > time);
        var (t0, v0) = _keys[hi - 1];
        var (t1, v1) = _keys[hi];
        float f = (float)((time - t0) / (t1 - t0));
        return Interpolation switch
        {
            Interpolation.Step => v0,
            Interpolation.CubicHermite => lerp(v0, v1, f * f * (3 - 2 * f)),
            _ => lerp(v0, v1, f),
        };
    }
}

/// <summary>
/// A named set of keyframe channels (<c>osgAnimation::Animation</c>), applied to nodes by name. Targets are
/// <see cref="PositionAttitudeTransform"/> or <see cref="Bone"/> nodes.
/// </summary>
public sealed class AnimationClip(string name)
{
    public string Name { get; } = name;
    public List<KeyframeChannel<Vector3>> Vector3Channels { get; } = [];
    public List<KeyframeChannel<Quaternion>> RotationChannels { get; } = [];
    public LoopMode LoopMode { get; set; } = LoopMode.Loop;

    public double Duration => System.Math.Max(
        Vector3Channels.Count > 0 ? Vector3Channels.Max(c => c.Duration) : 0,
        RotationChannels.Count > 0 ? RotationChannels.Max(c => c.Duration) : 0);

    public KeyframeChannel<Vector3> Translation(string target)
    {
        var c = new KeyframeChannel<Vector3>(target, "translation", Vector3.Lerp);
        Vector3Channels.Add(c);
        return c;
    }

    public KeyframeChannel<Vector3> Scale(string target)
    {
        var c = new KeyframeChannel<Vector3>(target, "scale", Vector3.Lerp);
        Vector3Channels.Add(c);
        return c;
    }

    public KeyframeChannel<Quaternion> Rotation(string target)
    {
        var c = new KeyframeChannel<Quaternion>(target, "rotation", Quaternion.Slerp);
        RotationChannels.Add(c);
        return c;
    }

    public double WrapTime(double t)
    {
        double d = Duration;
        if (d <= 0) return 0;
        switch (LoopMode)
        {
            case LoopMode.Loop: return ((t % d) + d) % d;
            case LoopMode.Swing:
                double s = ((t % (2 * d)) + 2 * d) % (2 * d);
                return s > d ? 2 * d - s : s;
            default: return System.Math.Clamp(t, 0, d);
        }
    }

    /// <summary>Evaluates all channels at <paramref name="time"/> and writes the result into the targets, blended by <paramref name="weight"/>.</summary>
    public void Apply(IReadOnlyDictionary<string, Node> targets, double time, float weight = 1f)
    {
        double t = WrapTime(time);
        foreach (var c in Vector3Channels)
        {
            if (!targets.TryGetValue(c.TargetName, out var n)) continue;
            var v = c.Evaluate(t);
            switch (n, c.Property)
            {
                case (PositionAttitudeTransform p, "translation"): p.Position = Vector3.Lerp(p.Position, v, weight); break;
                case (PositionAttitudeTransform p, "scale"): p.Scale = Vector3.Lerp(p.Scale, v, weight); break;
                case (Bone b, "translation"): b.Translation = Vector3.Lerp(b.Translation, v, weight); break;
                case (Bone b, "scale"): b.Scale = Vector3.Lerp(b.Scale, v, weight); break;
            }
        }
        foreach (var c in RotationChannels)
        {
            if (!targets.TryGetValue(c.TargetName, out var n)) continue;
            var q = c.Evaluate(t);
            switch (n)
            {
                case PositionAttitudeTransform p: p.Attitude = Quaternion.Slerp(p.Attitude, q, weight); break;
                case Bone b: b.Rotation = Quaternion.Slerp(b.Rotation, q, weight); break;
            }
        }
    }
}

/// <summary>
/// Plays and blends <see cref="AnimationClip"/>s on a subgraph (<c>osgAnimation::BasicAnimationManager</c>).
/// Install it as an update callback on the root of the animated subgraph.
/// </summary>
public sealed class AnimationManager : IUpdateCallback
{
    private readonly Dictionary<string, Node> _targets = new(StringComparer.Ordinal);
    private readonly List<(AnimationClip Clip, double Start, float Weight)> _playing = [];
    private Node? _boundRoot;

    public Dictionary<string, AnimationClip> Clips { get; } = new(StringComparer.Ordinal);
    public double Speed { get; set; } = 1;
    private double _now;

    public AnimationManager Register(AnimationClip clip) { Clips[clip.Name] = clip; return this; }

    public void Play(string clip, float weight = 1f)
    {
        Stop(clip);
        if (Clips.TryGetValue(clip, out var c)) _playing.Add((c, _now, weight));
    }

    public void Stop(string clip) => _playing.RemoveAll(p => p.Clip.Name == clip);
    public void StopAll() => _playing.Clear();
    public bool IsPlaying(string clip) => _playing.Exists(p => p.Clip.Name == clip);

    public void Update(Node node, in FrameStamp frame)
    {
        _now = frame.SimulationTime;
        if (!ReferenceEquals(_boundRoot, node)) Bind(node);
        foreach (var (clip, start, weight) in _playing) clip.Apply(_targets, (_now - start) * Speed, weight);
    }

    private void Bind(Node root)
    {
        _boundRoot = root;
        _targets.Clear();
        var finder = new FindNodesVisitor(n => n.Name is not null);
        root.Accept(finder);
        foreach (var n in finder.Found) _targets.TryAdd(n.Name!, n);
    }
}
