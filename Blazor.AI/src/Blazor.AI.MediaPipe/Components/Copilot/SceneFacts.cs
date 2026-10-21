using System.Globalization;
using MediaPipeNet;
using MediaPipeNet.Tasks.Vision;

namespace Blazor.AI.MediaPipe;

/// <summary>Grounded facts about one frame from the local detectors: objects, faces, posture and scene classes.</summary>
public sealed record SceneFacts(
    IReadOnlyList<(string Label, int Count)> Objects,
    int Faces,
    string? Posture,
    IReadOnlyList<(string Label, float Score)> Classes,
    IReadOnlyList<OverlayBox> Boxes,
    int Width,
    int Height)
{
    public static readonly SceneFacts Empty = new([], 0, null, [], [], 0, 0);

    public int People => Objects.FirstOrDefault(o => o.Label == "person").Count;

    public bool IsEmpty => Width == 0;

    /// <summary>One paragraph for the model's context.</summary>
    public string Describe()
    {
        if (IsEmpty)
        {
            return "No frame analysed yet.";
        }

        var objects = Objects.Count == 0 ? "none" : string.Join(", ", Objects.Select(o => $"{o.Count} {o.Label}"));
        var classes = Classes.Count == 0 ? "none" : string.Join(", ", Classes.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Label} {c.Score:P0}")));
        return string.Create(CultureInfo.InvariantCulture,
            $"Frame {Width}x{Height}. Objects detected (COCO, EfficientDet-Lite0): {objects}. Faces: {Faces}. Posture of the main person: {Posture ?? "no person pose"}. Scene classes (ImageNet, EfficientNet-Lite0): {classes}.");
    }

    /// <summary>Runs the detectors on <paramref name="frame"/>. Call from the frame worker (it uses <paramref name="tasks"/>).</summary>
    public static SceneFacts From(VisionTasks tasks, VisionFrame frame, bool full = true, float threshold = 0.35f)
    {
        var detections = tasks.Objects(frame, threshold, 30).Detections;
        var boxes = detections.Select(d =>
        {
            var label = d.TopCategory?.CategoryName ?? "object";
            return Overlay.Box(d, frame.Width, frame.Height, string.Create(CultureInfo.InvariantCulture, $"{label} {d.Score:P0}"), ObjectDetectionPanel.ColorFor(label));
        }).ToList();
        var objects = detections.GroupBy(d => d.TopCategory?.CategoryName ?? "object")
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        if (!full)
        {
            return new SceneFacts(objects, 0, null, [], boxes, frame.Width, frame.Height);
        }

        var faces = tasks.DetectFaces(frame).Detections.Count;
        var posture = tasks.Pose(frame).Poses.FirstOrDefault() is { } pose ? Classify(pose.Landmarks) : null;
        var classes = tasks.Classify(frame, 3).Categories.Where(c => c.Score >= 0.05f).Select(c => (c.CategoryName ?? "?", c.Score)).ToList();
        return new SceneFacts(objects, faces, posture, classes, boxes, frame.Width, frame.Height);
    }

    /// <summary>Coarse posture from 2-D landmarks: lying, sitting/crouching or standing, plus raised arms.</summary>
    private static string Classify(IReadOnlyList<NormalizedLandmark> lm)
    {
        float X(PoseLandmark i) => lm[(int)i].X;
        float Y(PoseLandmark i) => lm[(int)i].Y;
        var shoulderY = (Y(PoseLandmark.LeftShoulder) + Y(PoseLandmark.RightShoulder)) / 2;
        var shoulderX = (X(PoseLandmark.LeftShoulder) + X(PoseLandmark.RightShoulder)) / 2;
        var hipY = (Y(PoseLandmark.LeftHip) + Y(PoseLandmark.RightHip)) / 2;
        var hipX = (X(PoseLandmark.LeftHip) + X(PoseLandmark.RightHip)) / 2;
        var kneeY = (Y(PoseLandmark.LeftKnee) + Y(PoseLandmark.RightKnee)) / 2;
        var torso = Math.Max(1e-3f, Math.Abs(hipY - shoulderY));
        var basePosture = Math.Abs(hipX - shoulderX) > torso * 1.3f ? "lying or bending"
            : kneeY - hipY < torso * 0.45f ? "sitting or crouching"
            : "standing";
        var arms = Y(PoseLandmark.LeftWrist) < shoulderY && Y(PoseLandmark.RightWrist) < shoulderY ? ", both arms raised"
            : Y(PoseLandmark.LeftWrist) < shoulderY || Y(PoseLandmark.RightWrist) < shoulderY ? ", one arm raised"
            : string.Empty;
        return basePosture + arms;
    }
}
