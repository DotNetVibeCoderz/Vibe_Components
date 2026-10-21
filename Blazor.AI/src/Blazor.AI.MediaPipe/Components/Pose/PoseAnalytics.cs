using System.Globalization;
using System.Numerics;
using System.Text;
using MediaPipeNet;
using MediaPipeNet.Tasks.Vision;

namespace Blazor.AI.MediaPipe;

/// <summary>
/// Pose analytics shared by the body components: joint angles from metric world landmarks, posture measures,
/// smoothing, repetition counting and BVH export. World landmarks are in metres with the origin between the hips and
/// Y pointing down (image convention).
/// </summary>
public static class PoseAnalytics
{
    /// <summary>The joints reported by the body components (angle at B of A-B-C).</summary>
    public static IReadOnlyList<(string Name, PoseLandmark A, PoseLandmark B, PoseLandmark C)> Joints => PoseTracker.Joints;

    public static Landmark W(PoseLandmarks p, PoseLandmark id) => p.WorldLandmarks[(int)id];

    public static NormalizedLandmark N(PoseLandmarks p, PoseLandmark id) => p.Landmarks[(int)id];

    public static double Angle(PoseLandmarks p, PoseLandmark a, PoseLandmark b, PoseLandmark c) =>
        VisionMath.Angle3(W(p, a), W(p, b), W(p, c));

    /// <summary>All standard joint angles by name (degrees).</summary>
    public static Dictionary<string, double> Angles(PoseLandmarks p) =>
        Joints.ToDictionary(j => j.Name, j => Angle(p, j.A, j.B, j.C));

    public static bool Visible(PoseLandmarks p, float min, params PoseLandmark[] ids) =>
        ids.All(i => (p.Landmarks[(int)i].Visibility ?? 1) >= min);

    public static Vector3 V(Landmark l) => new(l.X, l.Y, l.Z);

    public static Vector3 Mid(PoseLandmarks p, PoseLandmark a, PoseLandmark b) => (V(W(p, a)) + V(W(p, b))) / 2;

    private static double AngleBetween(Vector3 a, Vector3 b)
    {
        var n = a.Length() * b.Length();
        return n < 1e-9 ? 0 : Math.Acos(Math.Clamp(Vector3.Dot(a, b) / n, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>Trunk lean from vertical in degrees (0 = upright).</summary>
    public static double TrunkLean(PoseLandmarks p)
    {
        var hips = Mid(p, PoseLandmark.LeftHip, PoseLandmark.RightHip);
        var shoulders = Mid(p, PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder);
        return AngleBetween(shoulders - hips, new Vector3(0, -1, 0));
    }

    /// <summary>Neck flexion relative to the trunk in degrees (ear line vs trunk line; 0 = in line).</summary>
    public static double NeckFlexion(PoseLandmarks p)
    {
        var hips = Mid(p, PoseLandmark.LeftHip, PoseLandmark.RightHip);
        var shoulders = Mid(p, PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder);
        var ears = Mid(p, PoseLandmark.LeftEar, PoseLandmark.RightEar);
        return AngleBetween(ears - shoulders, shoulders - hips);
    }

    /// <summary>Ear-to-shoulder distance over shoulder width; low values mean raised (shrugged) shoulders.</summary>
    public static double ShoulderElevationRatio(PoseLandmarks p)
    {
        var width = Vector3.Distance(V(W(p, PoseLandmark.LeftShoulder)), V(W(p, PoseLandmark.RightShoulder)));
        var l = Vector3.Distance(V(W(p, PoseLandmark.LeftEar)), V(W(p, PoseLandmark.LeftShoulder)));
        var r = Vector3.Distance(V(W(p, PoseLandmark.RightEar)), V(W(p, PoseLandmark.RightShoulder)));
        return (l + r) / 2 / Math.Max(1e-4, width);
    }

    /// <summary>Upper-arm flexion (angle between trunk-down and the upper arm), the larger of both arms.</summary>
    public static double UpperArmFlexion(PoseLandmarks p) => Math.Max(
        Angle(p, PoseLandmark.LeftHip, PoseLandmark.LeftShoulder, PoseLandmark.LeftElbow),
        Angle(p, PoseLandmark.RightHip, PoseLandmark.RightShoulder, PoseLandmark.RightElbow));

    /// <summary>Horizontal distance between two landmarks in metres (X/Z plane).</summary>
    public static double Spread(PoseLandmarks p, PoseLandmark a, PoseLandmark b)
    {
        var d = V(W(p, a)) - V(W(p, b));
        return Math.Sqrt(d.X * d.X + d.Z * d.Z);
    }

    public static string F(double v, string format = "0") => v.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>A simplified RULA-style posture assessment for seated or standing work.</summary>
public sealed record ErgonomicAssessment(double NeckFlexion, double TrunkLean, double UpperArm, bool ShouldersRaised, int Score, string Risk, IReadOnlyList<string> Issues)
{
    public static ErgonomicAssessment From(PoseLandmarks p)
    {
        var neck = PoseAnalytics.NeckFlexion(p);
        var trunk = PoseAnalytics.TrunkLean(p);
        var arm = PoseAnalytics.UpperArmFlexion(p);
        var raised = PoseAnalytics.ShoulderElevationRatio(p) < 0.42;
        var issues = new List<string>();
        var neckScore = neck switch { < 10 => 1, < 20 => 2, _ => 3 };
        if (neck >= 20)
        {
            issues.Add($"Neck bent {PoseAnalytics.F(neck)}° forward: raise the screen to eye level.");
        }

        var trunkScore = trunk switch { < 5 => 1, < 20 => 2, < 60 => 3, _ => 4 };
        if (trunk >= 20)
        {
            issues.Add($"Trunk leans {PoseAnalytics.F(trunk)}°: sit back against the backrest.");
        }

        var armScore = arm switch { < 20 => 1, < 45 => 2, < 90 => 3, _ => 4 };
        if (arm >= 45)
        {
            issues.Add($"Upper arm raised {PoseAnalytics.F(arm)}°: bring the keyboard and mouse closer.");
        }

        if (raised)
        {
            armScore++;
            issues.Add("Shoulders are raised: relax them and lower the desk or raise the chair.");
        }

        var score = Math.Clamp((int)Math.Round((neckScore + trunkScore + armScore) / 11.0 * 7), 1, 7);
        var risk = score switch { <= 2 => "Low", <= 4 => "Medium", <= 6 => "High", _ => "Very high" };
        return new ErgonomicAssessment(neck, trunk, arm, raised, score, risk, issues);
    }
}

/// <summary>
/// Counts repetitions of a cyclic movement from one metric with hysteresis: a rep starts when the metric drops below
/// <see cref="Low"/> and completes when it rises back above <see cref="High"/>. Also tracks depth, tempo and sets.
/// </summary>
public sealed class RepCounter(double low, double high, double restSecondsForNewSet = 8)
{
    private bool _down;
    private double _downAt, _lastRepAt = double.NaN, _currentMin = double.MaxValue;
    private readonly List<double> _durations = [];
    private readonly List<int> _sets = [];

    public double Low { get; } = low;
    public double High { get; } = high;
    public int Reps { get; private set; }
    public int RepsInSet { get; private set; }
    public IReadOnlyList<int> CompletedSets => _sets;
    public bool IsDown => _down;
    public double LastDepth { get; private set; } = double.NaN;
    public double AverageTempo => _durations.Count == 0 ? 0 : _durations.TakeLast(5).Average();

    /// <summary>Feeds one sample; returns true when a repetition completes.</summary>
    public bool Update(double value, double timeSeconds)
    {
        if (!double.IsFinite(value))
        {
            return false;
        }

        if (RepsInSet > 0 && !double.IsNaN(_lastRepAt) && timeSeconds - _lastRepAt > restSecondsForNewSet && !_down)
        {
            _sets.Add(RepsInSet);
            RepsInSet = 0;
        }

        if (!_down && value < Low)
        {
            _down = true;
            _downAt = timeSeconds;
            _currentMin = value;
        }
        else if (_down)
        {
            _currentMin = Math.Min(_currentMin, value);
            if (value > High)
            {
                _down = false;
                Reps++;
                RepsInSet++;
                LastDepth = _currentMin;
                _durations.Add(Math.Max(0, timeSeconds - _downAt) * 2);
                _lastRepAt = timeSeconds;
                return true;
            }
        }

        return false;
    }

    public void Reset()
    {
        _down = false;
        Reps = RepsInSet = 0;
        _sets.Clear();
        _durations.Clear();
        _lastRepAt = double.NaN;
        LastDepth = double.NaN;
    }
}

/// <summary>One-Euro filter over landmark arrays (adaptive smoothing: steady when still, responsive when moving).</summary>
public sealed class LandmarkFilter(double minCutoff = 1.2, double beta = 0.02, double dCutoff = 1.0)
{
    private Vector3[]? _x, _dx;
    private double _lastT = double.NaN;

    private static double Alpha(double cutoff, double dt) => 1 / (1 + 1 / (2 * Math.PI * cutoff * dt));

    public Landmark[] Filter(IReadOnlyList<Landmark> input, double timeSeconds)
    {
        var dt = double.IsNaN(_lastT) ? 1.0 / 15 : Math.Max(1e-3, timeSeconds - _lastT);
        _lastT = timeSeconds;
        var output = new Landmark[input.Count];
        if (_x is null || _x.Length != input.Count)
        {
            _x = input.Select(PoseAnalytics.V).ToArray();
            _dx = new Vector3[input.Count];
            return [.. input];
        }

        var ad = (float)Alpha(dCutoff, dt);
        for (var i = 0; i < input.Count; i++)
        {
            var v = PoseAnalytics.V(input[i]);
            var d = (v - _x[i]) / (float)dt;
            _dx![i] = _dx[i] + ad * (d - _dx[i]);
            var a = (float)Alpha(minCutoff + beta * _dx[i].Length(), dt);
            _x[i] += a * (v - _x[i]);
            output[i] = input[i] with { X = _x[i].X, Y = _x[i].Y, Z = _x[i].Z };
        }

        return output;
    }

    public void Reset()
    {
        _x = null;
        _dx = null;
        _lastT = double.NaN;
    }
}

/// <summary>A recorded pose frame for motion capture: metric world landmarks plus the 2-D landmarks for overlays.</summary>
public sealed record MocapFrame(double Time, Landmark[] World, NormalizedLandmark[] Image);

/// <summary>Exports pose recordings as JSON or BVH (a simple 15-joint hierarchy, Y up, centimetres).</summary>
public static class MocapExport
{
    private sealed record Joint(string Name, Func<IReadOnlyList<Landmark>, Vector3> Pos, Func<IReadOnlyList<Landmark>, Vector3>? Aim, Joint[] Children);

    private static Vector3 P(IReadOnlyList<Landmark> w, PoseLandmark id) => ToBvh(w[(int)id]);

    private static Vector3 M(IReadOnlyList<Landmark> w, PoseLandmark a, PoseLandmark b) => (P(w, a) + P(w, b)) / 2;

    /// <summary>MediaPipe world (Y down, metres) to BVH (Y up, centimetres).</summary>
    public static Vector3 ToBvh(Landmark l) => new(l.X * 100, -l.Y * 100, -l.Z * 100);

    private static Joint Chain(string side, PoseLandmark a, PoseLandmark b, PoseLandmark c, PoseLandmark end, string[] names) =>
        new(side + names[0], w => P(w, a), w => P(w, b),
        [
            new Joint(side + names[1], w => P(w, b), w => P(w, c),
            [
                new Joint(side + names[2], w => P(w, c), w => P(w, end), []),
            ]),
        ]);

    private static readonly Joint Root = new("Hips", w => M(w, PoseLandmark.LeftHip, PoseLandmark.RightHip), null,
    [
        new Joint("Spine", w => M(w, PoseLandmark.LeftHip, PoseLandmark.RightHip), w => M(w, PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder),
        [
            new Joint("Neck", w => M(w, PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder), w => M(w, PoseLandmark.LeftEar, PoseLandmark.RightEar),
            [
                new Joint("Head", w => M(w, PoseLandmark.LeftEar, PoseLandmark.RightEar), w => P(w, PoseLandmark.Nose), []),
            ]),
            Chain("Left", PoseLandmark.LeftShoulder, PoseLandmark.LeftElbow, PoseLandmark.LeftWrist, PoseLandmark.LeftIndex, ["UpperArm", "ForeArm", "Hand"]),
            Chain("Right", PoseLandmark.RightShoulder, PoseLandmark.RightElbow, PoseLandmark.RightWrist, PoseLandmark.RightIndex, ["UpperArm", "ForeArm", "Hand"]),
        ]),
        Chain("Left", PoseLandmark.LeftHip, PoseLandmark.LeftKnee, PoseLandmark.LeftAnkle, PoseLandmark.LeftFootIndex, ["UpLeg", "Leg", "Foot"]),
        Chain("Right", PoseLandmark.RightHip, PoseLandmark.RightKnee, PoseLandmark.RightAnkle, PoseLandmark.RightFootIndex, ["UpLeg", "Leg", "Foot"]),
    ]);

    public static string ToJson(IReadOnlyList<MocapFrame> frames)
    {
        var sb = new StringBuilder("{\"format\":\"blazorai-mocap\",\"units\":\"m\",\"axes\":\"x right, y down, z toward camera negative\",\"landmarks\":33,\"frames\":[");
        for (var i = 0; i < frames.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(CultureInfo.InvariantCulture, $"{{\"t\":{frames[i].Time:0.###},\"world\":[");
            sb.AppendJoin(',', frames[i].World.Select(l => string.Create(CultureInfo.InvariantCulture, $"[{l.X:0.####},{l.Y:0.####},{l.Z:0.####}]")));
            sb.Append("]}");
        }

        return sb.Append("]}").ToString();
    }

    /// <summary>Reads a take written by <see cref="ToJson"/> (world landmarks only; image landmarks are left empty).</summary>
    public static IReadOnlyList<MocapFrame> FromJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var frames = new List<MocapFrame>();
        if (!doc.RootElement.TryGetProperty("frames", out var list))
        {
            return frames;
        }

        foreach (var f in list.EnumerateArray())
        {
            var world = f.GetProperty("world").EnumerateArray()
                .Select(p => new Landmark(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle(), null, null))
                .ToArray();
            if (world.Length == 33)
            {
                frames.Add(new MocapFrame(f.GetProperty("t").GetDouble(), world, []));
            }
        }

        return frames;
    }

    /// <summary>BVH with the first frame as rest pose; joint rotations are bone swings (no twist), channels ZXY.</summary>
    public static string ToBvh(IReadOnlyList<MocapFrame> frames, double fps)
    {
        if (frames.Count == 0)
        {
            return string.Empty;
        }

        var rest = frames[0].World;
        var sb = new StringBuilder("HIERARCHY\n");
        var order = new List<Joint>();
        void Write(Joint j, Joint? parent, int depth)
        {
            var pad = new string('\t', depth);
            var offset = parent is null ? Vector3.Zero : j.Pos(rest) - parent.Pos(rest);
            sb.Append(pad).Append(parent is null ? "ROOT " : "JOINT ").Append(j.Name).Append('\n').Append(pad).Append("{\n");
            sb.Append(pad).Append('\t').Append(CultureInfo.InvariantCulture, $"OFFSET {offset.X:0.###} {offset.Y:0.###} {offset.Z:0.###}\n");
            sb.Append(pad).Append('\t').Append(parent is null ? "CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation\n" : "CHANNELS 3 Zrotation Xrotation Yrotation\n");
            order.Add(j);
            if (j.Children.Length == 0)
            {
                var tip = (j.Aim?.Invoke(rest) ?? j.Pos(rest)) - j.Pos(rest);
                sb.Append(pad).Append("\tEnd Site\n").Append(pad).Append("\t{\n").Append(pad).Append(CultureInfo.InvariantCulture, $"\t\tOFFSET {tip.X:0.###} {tip.Y:0.###} {tip.Z:0.###}\n").Append(pad).Append("\t}\n");
            }

            foreach (var c in j.Children)
            {
                Write(c, j, depth + 1);
            }

            sb.Append(pad).Append("}\n");
        }

        Write(Root, null, 0);
        sb.Append(CultureInfo.InvariantCulture, $"MOTION\nFrames: {frames.Count}\nFrame Time: {1 / Math.Max(1, fps):0.######}\n");
        foreach (var f in frames)
        {
            var world = new Dictionary<Joint, Quaternion>();
            var values = new List<double>();
            void Rot(Joint j, Joint? parent)
            {
                var q = Quaternion.Identity;
                if (j.Aim is not null)
                {
                    q = FromTo(Vector3.Normalize(j.Aim(rest) - j.Pos(rest)), Vector3.Normalize(j.Aim(f.World) - j.Pos(f.World)));
                }
                else if (parent is not null)
                {
                    q = world[parent];
                }

                world[j] = q;
                var local = parent is null ? q : Quaternion.Normalize(Quaternion.Conjugate(world[parent]) * q);
                if (parent is null)
                {
                    var p = j.Pos(f.World);
                    values.AddRange([p.X, p.Y, p.Z]);
                }

                var (z, x, y) = EulerZxy(local);
                values.AddRange([z, x, y]);
                foreach (var c in j.Children)
                {
                    Rot(c, j);
                }
            }

            Rot(Root, null);
            sb.AppendJoin(' ', values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Rotation taking unit vector <paramref name="a"/> to unit vector <paramref name="b"/>.</summary>
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var r = Vector3.Dot(a, b) + 1;
        if (r < 1e-6f)
        {
            var axis = Math.Abs(a.X) > Math.Abs(a.Z) ? new Vector3(-a.Y, a.X, 0) : new Vector3(0, -a.Z, a.Y);
            return Quaternion.Normalize(new Quaternion(axis, 0));
        }

        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, r));
    }

    /// <summary>Euler angles in degrees for R = Rz·Rx·Ry (BVH "Zrotation Xrotation Yrotation").</summary>
    public static (double Z, double X, double Y) EulerZxy(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        // System.Numerics is row-vector: transpose to column convention (m12 → M21 etc.).
        double m11 = m.M11, m12 = m.M21, m21 = m.M12, m22 = m.M22, m31 = m.M13, m32 = m.M23, m33 = m.M33;
        var x = Math.Asin(Math.Clamp(m32, -1, 1));
        double y, z;
        if (Math.Abs(m32) < 0.9999999)
        {
            y = Math.Atan2(-m31, m33);
            z = Math.Atan2(-m12, m22);
        }
        else
        {
            y = 0;
            z = Math.Atan2(m21, m11);
        }

        const double deg = 180 / Math.PI;
        return (z * deg, x * deg, y * deg);
    }
}
