using System.Numerics;
using Blazor.AI.ThreeD;
using MediaPipeNet;
using MediaPipeNet.Tasks.Vision;

namespace Blazor.AI.MediaPipe;

/// <summary>
/// Builds Blazor.AI.ThreeD scene objects from MediaPipe landmarks: skeletons, capsule bodies, faces and hands.
/// World landmarks (metres, Y down, origin between the hips) are converted to scene space (Y up) and stood on the floor.
/// </summary>
public static class Body3D
{
    private static readonly (int A, int B)[] Bones = [.. Connections.Pose];

    /// <summary>Scene options for a small stage with a grid, framed for a standing person.</summary>
    public static Scene3DOptions Stage(string? ariaLabel = null, double distance = 3.2, bool autoRotate = false) => new()
    {
        Camera = new() { Position = [distance * 0.55, 1.35, distance], Target = [0, 0.95, 0], Fov = 42 },
        Controls = new() { AutoRotate = autoRotate, AutoRotateSpeed = 0.6, MinDistance = 1, MaxDistance = 8 },
        Grid = new(6, 12, "#5b6bd6", 0.25),
        Lights = "studio",
        Shadows = false,
        AriaLabel = ariaLabel ?? "3-D body",
    };

    /// <summary>Vertical offset that puts the lowest landmark on the floor (y = 0).</summary>
    public static double FloorLift(IReadOnlyList<Landmark> w)
    {
        var lowest = w.Count == 0 ? 0 : w.Max(l => l.Y);
        return lowest;
    }

    /// <summary>World landmark → scene position. <paramref name="mirror"/> flips left/right like a mirror.</summary>
    public static double[] ToScene(Landmark l, double lift, bool mirror = false, double[]? offset = null) =>
    [
        (mirror ? -l.X : l.X) + (offset?[0] ?? 0),
        -l.Y + lift + (offset?[1] ?? 0),
        -l.Z + (offset?[2] ?? 0),
    ];

    /// <summary>Euler XYZ rotation (radians) that turns the +Y axis to point from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static double[] AlignY(double[] a, double[] b)
    {
        var d = new Vector3((float)(b[0] - a[0]), (float)(b[1] - a[1]), (float)(b[2] - a[2]));
        if (d.LengthSquared() < 1e-10f)
        {
            return [0, 0, 0];
        }

        var q = MocapExport.FromTo(Vector3.UnitY, Vector3.Normalize(d));
        var m = Matrix4x4.CreateFromQuaternion(q);
        // Column-convention elements (System.Numerics stores the transpose).
        double m11 = m.M11, m12 = m.M21, m13 = m.M31, m22 = m.M22, m23 = m.M32, m32 = m.M23, m33 = m.M33;
        var y = Math.Asin(Math.Clamp(m13, -1, 1));
        double x, z;
        if (Math.Abs(m13) < 0.9999999)
        {
            x = Math.Atan2(-m23, m33);
            z = Math.Atan2(-m12, m11);
        }
        else
        {
            x = Math.Atan2(m32, m22);
            z = 0;
        }

        return [x, y, z];
    }

    public static double Length(double[] a, double[] b) =>
        Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2) + Math.Pow(b[2] - a[2], 2));

    public static double[] Mid(double[] a, double[] b) => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2];

    /// <summary>Joints as small spheres plus bones as lines (or tubes).</summary>
    public static List<Object3D> Skeleton(string prefix, IReadOnlyList<Landmark> w, string color, bool mirror = false, double[]? offset = null, bool tubes = false, IReadOnlyList<NormalizedLandmark>? visibility = null)
    {
        var list = new List<Object3D>();
        if (w.Count < 33)
        {
            return list;
        }

        var lift = FloorLift(w);
        var p = w.Select(l => ToScene(l, lift, mirror, offset)).ToArray();
        for (var i = 0; i < Bones.Length; i++)
        {
            var (a, b) = Bones[i];
            var dim = visibility is not null && ((visibility[a].Visibility ?? 1) < 0.4 || (visibility[b].Visibility ?? 1) < 0.4);
            list.Add(tubes
                ? new Object3D { Id = $"{prefix}b{i}", Kind = "tube", Points = [p[a], p[b]], Radius = 0.018, Color = color, Emissive = color, EmissiveIntensity = 0.35, Opacity = dim ? 0.35 : 1, Pickable = false }
                : new Object3D { Id = $"{prefix}b{i}", Kind = "line", Points = [p[a], p[b]], Color = color, Opacity = dim ? 0.35 : 1, Pickable = false });
        }

        for (var i = 0; i < p.Length; i++)
        {
            if (i is > 0 and < 11)
            {
                continue; // skip small face points; the nose stands in for the head
            }

            list.Add(new Object3D { Id = $"{prefix}j{i}", Kind = "sphere", Position = p[i], Radius = i == 0 ? 0.06 : 0.028, Segments = 12, Color = "#ffffff", Emissive = color, EmissiveIntensity = 0.4, Tooltip = ((PoseLandmark)i).ToString() });
        }

        return list;
    }

    private static readonly (PoseLandmark A, PoseLandmark B, double R, string Part)[] Segments =
    [
        (PoseLandmark.LeftShoulder, PoseLandmark.LeftElbow, 0.055, "arm"), (PoseLandmark.LeftElbow, PoseLandmark.LeftWrist, 0.045, "arm"),
        (PoseLandmark.RightShoulder, PoseLandmark.RightElbow, 0.055, "arm"), (PoseLandmark.RightElbow, PoseLandmark.RightWrist, 0.045, "arm"),
        (PoseLandmark.LeftHip, PoseLandmark.LeftKnee, 0.075, "leg"), (PoseLandmark.LeftKnee, PoseLandmark.LeftAnkle, 0.06, "leg"),
        (PoseLandmark.RightHip, PoseLandmark.RightKnee, 0.075, "leg"), (PoseLandmark.RightKnee, PoseLandmark.RightAnkle, 0.06, "leg"),
        (PoseLandmark.LeftAnkle, PoseLandmark.LeftFootIndex, 0.04, "foot"), (PoseLandmark.RightAnkle, PoseLandmark.RightFootIndex, 0.04, "foot"),
    ];

    /// <summary>
    /// A capsule body (torso, limbs, head with a visor) driven by world landmarks. <paramref name="headYawPitch"/> (degrees)
    /// turns the head; when null the head follows the nose.
    /// </summary>
    public static List<Object3D> Capsules(string prefix, IReadOnlyList<Landmark> w, string bodyColor, string accent, bool mirror = false, double[]? offset = null, (double Yaw, double Pitch)? headYawPitch = null, string? headColor = null)
    {
        var list = new List<Object3D>();
        if (w.Count < 33)
        {
            return list;
        }

        var lift = FloorLift(w);
        double[] P(PoseLandmark id) => ToScene(w[(int)id], lift, mirror, offset);
        var hip = Mid(P(PoseLandmark.LeftHip), P(PoseLandmark.RightHip));
        var shoulder = Mid(P(PoseLandmark.LeftShoulder), P(PoseLandmark.RightShoulder));
        var torsoLen = Length(hip, shoulder);
        var shoulderWidth = Length(P(PoseLandmark.LeftShoulder), P(PoseLandmark.RightShoulder));
        list.Add(new Object3D { Id = prefix + "torso", Kind = "capsule", Position = Mid(hip, shoulder), Rotation = AlignY(hip, shoulder), Radius = Math.Max(0.08, shoulderWidth * 0.38), Height = Math.Max(0.01, torsoLen - shoulderWidth * 0.5), Color = bodyColor, Roughness = 0.45, Tooltip = "Torso" });
        list.Add(new Object3D { Id = prefix + "pelvis", Kind = "sphere", Position = hip, Radius = shoulderWidth * 0.32, Color = bodyColor, Roughness = 0.45, Pickable = false });
        foreach (var (a, b, r, part) in Segments)
        {
            var pa = P(a);
            var pb = P(b);
            list.Add(new Object3D { Id = $"{prefix}{a}-{b}", Kind = "capsule", Position = Mid(pa, pb), Rotation = AlignY(pa, pb), Radius = r, Height = Math.Max(0.005, Length(pa, pb) - r), Color = part == "foot" ? accent : bodyColor, Roughness = 0.4, Pickable = false });
        }

        foreach (var hand in new[] { PoseLandmark.LeftWrist, PoseLandmark.RightWrist })
        {
            list.Add(new Object3D { Id = $"{prefix}{hand}", Kind = "sphere", Position = P(hand), Radius = 0.05, Color = accent, Emissive = accent, EmissiveIntensity = 0.3, Pickable = false });
        }

        // Head: centred between the ears, a little above.
        var ears = Mid(P(PoseLandmark.LeftEar), P(PoseLandmark.RightEar));
        var head = new[] { ears[0], ears[1] + 0.04, ears[2] };
        var nose = P(PoseLandmark.Nose);
        double yaw, pitch;
        if (headYawPitch is { } hp)
        {
            yaw = (mirror ? -hp.Yaw : hp.Yaw) * Math.PI / 180;
            pitch = hp.Pitch * Math.PI / 180;
        }
        else
        {
            yaw = Math.Atan2(nose[0] - head[0], nose[2] - head[2]);
            pitch = -Math.Atan2(nose[1] - head[1], Math.Sqrt(Math.Pow(nose[0] - head[0], 2) + Math.Pow(nose[2] - head[2], 2)));
        }

        list.Add(new Object3D { Id = prefix + "neck", Kind = "capsule", Position = Mid(shoulder, head), Rotation = AlignY(shoulder, head), Radius = 0.045, Height = Math.Max(0.005, Length(shoulder, head) - 0.14), Color = bodyColor, Pickable = false });
        list.Add(new Object3D { Id = prefix + "head", Kind = "sphere", Position = head, Radius = 0.12, Segments = 24, Color = headColor ?? "#eef0ff", Roughness = 0.3, Rotation = [pitch, yaw, 0], Tooltip = "Head" });
        list.Add(new Object3D { Id = prefix + "visor", Kind = "box", Parent = prefix + "head", Position = [0, 0.01, 0.1], Size = [0.15, 0.06, 0.04], Color = accent, Emissive = accent, EmissiveIntensity = 0.9, Pickable = false });
        return list;
    }

    /// <summary>Face mesh landmarks → scene points (centred, about <paramref name="size"/> metres wide).</summary>
    public static double[][] FacePoints(IReadOnlyList<NormalizedLandmark> f, double aspect, double size = 2.4, double y = 1.4)
    {
        var cx = f.Average(l => l.X);
        var cy = f.Average(l => l.Y);
        var cz = f.Average(l => l.Z);
        return f.Select(l => new[] { (l.X - cx) * size * aspect, -(l.Y - cy) * size + y, -(l.Z - cz) * size * aspect }).ToArray();
    }

    /// <summary>Hand landmarks (normalized image coordinates + relative depth) → a hand-sized cloud in a 3-D "gesture space".</summary>
    public static double[] HandSpace(NormalizedLandmark l, double aspect, double width = 3.2, double y = 1.2) =>
        [(l.X - 0.5) * width * aspect, -(l.Y - 0.5) * width + y, -l.Z * width * 2];

    /// <summary>Hand bones as lines in gesture space.</summary>
    public static List<Object3D> HandSkeleton(string prefix, IReadOnlyList<NormalizedLandmark> lms, double aspect, string color, bool mirror)
    {
        var p = lms.Select(l =>
        {
            var v = HandSpace(l, aspect);
            if (mirror)
            {
                v[0] = -v[0];
            }

            return v;
        }).ToArray();
        var list = new List<Object3D>();
        var conn = Connections.Hand;
        for (var i = 0; i < conn.Count; i++)
        {
            list.Add(new Object3D { Id = $"{prefix}b{i}", Kind = "tube", Points = [p[conn[i].Item1], p[conn[i].Item2]], Radius = 0.012, Color = color, Emissive = color, EmissiveIntensity = 0.4, Pickable = false });
        }

        for (var i = 0; i < p.Length; i++)
        {
            var tip = i is 4 or 8 or 12 or 16 or 20;
            list.Add(new Object3D { Id = $"{prefix}j{i}", Kind = "sphere", Position = p[i], Radius = tip ? 0.04 : 0.025, Segments = 12, Color = tip ? ThreeDPalette.Soga : "#ffffff", Emissive = tip ? ThreeDPalette.Soga : color, EmissiveIntensity = 0.5, Tooltip = ((HandLandmark)i).ToString() });
        }

        return list;
    }

    /// <summary>
    /// Hand world landmarks (metres around the hand centre, Y down) attached at <paramref name="wrist"/> (a scene position),
    /// as tube bones and joint spheres.
    /// </summary>
    public static List<Object3D> HandWorld(string prefix, IReadOnlyList<Landmark> hand, double[] wrist, string color, bool mirror = false)
    {
        var list = new List<Object3D>();
        if (hand.Count < 21)
        {
            return list;
        }

        var w0 = hand[0];
        var p = hand.Select(l => new[] { wrist[0] + (mirror ? -(l.X - w0.X) : l.X - w0.X), wrist[1] - (l.Y - w0.Y), wrist[2] - (l.Z - w0.Z) }).ToArray();
        var conn = Connections.Hand;
        for (var i = 0; i < conn.Count; i++)
        {
            list.Add(new Object3D { Id = $"{prefix}b{i}", Kind = "tube", Points = [p[conn[i].Item1], p[conn[i].Item2]], Radius = 0.006, Color = color, Pickable = false });
        }

        for (var i = 0; i < p.Length; i++)
        {
            var tip = i is 4 or 8 or 12 or 16 or 20;
            list.Add(new Object3D { Id = $"{prefix}j{i}", Kind = "sphere", Position = p[i], Radius = tip ? 0.011 : 0.008, Segments = 8, Color = tip ? ThreeDPalette.Soga : color, Pickable = false });
        }

        return list;
    }

    /// <summary>Scene position of a pose landmark, using the same floor lift as <see cref="Skeleton"/> and <see cref="Capsules"/>.</summary>
    public static double[] Joint(IReadOnlyList<Landmark> w, PoseLandmark id, bool mirror = false, double[]? offset = null) =>
        ToScene(w[(int)id], FloorLift(w), mirror, offset);

    /// <summary>A colour for a facial expression label.</summary>
    public static string ExpressionColor(string label) => label switch
    {
        "Happy" => ThreeDPalette.Soga,
        "Surprised" => ThreeDPalette.Warning,
        "Sad" => "#7b8cff",
        "Angry" => ThreeDPalette.Danger,
        _ => ThreeDPalette.Nila,
    };
}

/// <summary>Coarse expression from face blendshapes (smile, frown, brows, jaw).</summary>
public static class ExpressionMood
{
    public static (string Label, double Confidence) From(FaceLandmarks face)
    {
        double S(string n) => face.Blendshapes is null ? 0 : face.GetBlendshape(n);
        var smile = (S("mouthSmileLeft") + S("mouthSmileRight")) / 2;
        var frown = (S("mouthFrownLeft") + S("mouthFrownRight")) / 2;
        var browDown = (S("browDownLeft") + S("browDownRight")) / 2;
        var browUp = S("browInnerUp");
        var jaw = S("jawOpen");
        var eyeWide = (S("eyeWideLeft") + S("eyeWideRight")) / 2;
        var scores = new (string, double)[]
        {
            ("Happy", smile),
            ("Surprised", Math.Min(1, (jaw + eyeWide + browUp) / 2)),
            ("Sad", Math.Min(1, frown * 1.5 + browUp * 0.5 * (1 - smile))),
            ("Angry", Math.Min(1, browDown * 1.3 + frown * 0.3)),
        };
        var best = scores.MaxBy(s => s.Item2);
        return best.Item2 < 0.35 ? ("Neutral", 1 - best.Item2) : best;
    }
}

/// <summary>Throttles 3-D scene pushes from the processing thread (live sources at most every <see cref="IntervalMs"/>).</summary>
public sealed class SceneClock(int intervalMs = 100)
{
    private long _last;

    public int IntervalMs { get; } = intervalMs;

    /// <summary>True when a scene update is due (always for still pictures).</summary>
    public bool Due(VisionFrame frame)
    {
        var now = Environment.TickCount64;
        if (frame.IsLive && now - Interlocked.Read(ref _last) < IntervalMs)
        {
            return false;
        }

        Interlocked.Exchange(ref _last, now);
        return true;
    }
}
