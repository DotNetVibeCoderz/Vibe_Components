using System.Globalization;
using MediaPipeNet;
using MediaPipeNet.Tasks.Vision;

namespace Blazor.AI.MediaPipe;

/// <summary>Builds <see cref="VisionOverlay"/> parts from MediaPipe.NET results.</summary>
public static class Overlay
{
    public const string Nila = "#3cc4b1";
    public const string Soga = "#d9964f";
    public const string Tarum = "#7b8cff";
    public const string Danger = "#e5534b";
    public const string Warning = "#e8b84a";
    public const string White = "#ffffff";

    private static readonly int[] HandConn = Flatten(Connections.Hand);
    private static readonly int[] PoseConn = Flatten(Connections.Pose);
    private static readonly int[] FaceContours = Flatten(Connections.FaceContours);

    public static int[] Flatten(IReadOnlyList<(int A, int B)> pairs)
    {
        var a = new int[pairs.Count * 2];
        for (var i = 0; i < pairs.Count; i++)
        {
            a[2 * i] = pairs[i].A;
            a[2 * i + 1] = pairs[i].B;
        }

        return a;
    }

    public static float[] Points(IReadOnlyList<NormalizedLandmark> lms)
    {
        var p = new float[lms.Count * 2];
        for (var i = 0; i < lms.Count; i++)
        {
            p[2 * i] = lms[i].X;
            p[2 * i + 1] = lms[i].Y;
        }

        return p;
    }

    public static OverlayLandmarks Hand(HandLandmarks hand, string? color = null, int[]? highlight = null) => new()
    {
        Points = Points(hand.Landmarks), Connections = HandConn, Color = color ?? (hand.IsLeft ? Soga : Nila), PointColor = White, Radius = 3, LineWidth = 2.5f, Highlight = highlight,
    };

    public static OverlayLandmarks Pose(PoseLandmarks pose, string color = Nila, int[]? highlight = null) => new()
    {
        Points = Points(pose.Landmarks), Connections = PoseConn, Color = color, PointColor = White, Radius = 3.5f, LineWidth = 3, Highlight = highlight,
    };

    /// <summary>Face mesh: contours (eyes, brows, lips, oval) plus optionally all 478 points as a fine dot cloud.</summary>
    public static IEnumerable<OverlayLandmarks> Face(FaceLandmarks face, bool allPoints = true, string color = Nila)
    {
        if (allPoints)
        {
            yield return new OverlayLandmarks { Points = Points(face.Landmarks), Color = color, PointColor = "rgba(255,255,255,0.55)", Radius = 0.9f, LineWidth = 0 };
        }

        yield return new OverlayLandmarks { Points = Points(face.Landmarks), Connections = FaceContours, Color = color, Radius = 0, LineWidth = 1.4f };
    }

    /// <summary>A labelled box from a detection whose bounding box is in pixels.</summary>
    public static OverlayBox Box(Detection d, int width, int height, string? label = null, string color = Soga) => new()
    {
        X = d.BoundingBox.X / width, Y = d.BoundingBox.Y / height, W = d.BoundingBox.Width / width, H = d.BoundingBox.Height / height,
        Label = label ?? $"{d.TopCategory?.CategoryName ?? "object"} {d.Score.ToString("P0", CultureInfo.InvariantCulture)}", Color = color,
    };

    /// <summary>Downsamples a confidence mask (0–1 floats) to at most <paramref name="maxWidth"/> pixels wide, base64 bytes.</summary>
    public static OverlayMask Mask(SegmentationMask mask, string mode = "tint", string color = Nila, string? backgroundUrl = null, int maxWidth = 256, float opacity = 0.55f, bool invert = false)
    {
        var scale = Math.Min(1.0, maxWidth / (double)mask.Width);
        var w = Math.Max(1, (int)Math.Round(mask.Width * scale));
        var h = Math.Max(1, (int)Math.Round(mask.Height * scale));
        var bytes = new byte[w * h];
        var data = mask.Data;
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Min(mask.Height - 1, (int)(y / scale));
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Min(mask.Width - 1, (int)(x / scale));
                var v = data[sy * mask.Width + sx];
                if (invert)
                {
                    v = 1 - v;
                }

                bytes[y * w + x] = (byte)Math.Clamp((int)(v * 255), 0, 255);
            }
        }

        return new OverlayMask { Width = w, Height = h, Data = Convert.ToBase64String(bytes), Mode = mode, Color = color, BackgroundUrl = backgroundUrl, Opacity = opacity };
    }

    /// <summary>A category mask rendered as a multi-colour tint (one colour per class index).</summary>
    public static OverlayMask CategoryTint(CategoryMask mask, IReadOnlyList<int> classes, int maxWidth = 256)
    {
        // Encode as a single-channel alpha where any of the chosen classes is opaque.
        var scale = Math.Min(1.0, maxWidth / (double)mask.Width);
        var w = Math.Max(1, (int)Math.Round(mask.Width * scale));
        var h = Math.Max(1, (int)Math.Round(mask.Height * scale));
        var set = classes.ToHashSet();
        var bytes = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = mask.Data[Math.Min(mask.Height - 1, (int)(y / scale)) * mask.Width + Math.Min(mask.Width - 1, (int)(x / scale))];
                bytes[y * w + x] = set.Contains(c) ? (byte)255 : (byte)0;
            }
        }

        return new OverlayMask { Width = w, Height = h, Data = Convert.ToBase64String(bytes), Mode = "tint" };
    }

    public static OverlayText Label(float x, float y, string text, string color = White, float size = 14) => new() { X = x, Y = y, Text = text, Color = color, Size = size };

    /// <summary>Normalized bounding rectangle of a landmark set (with optional padding).</summary>
    public static (float X, float Y, float W, float H) Bounds(IReadOnlyList<NormalizedLandmark> lms, float pad = 0.02f)
    {
        float minX = 1, minY = 1, maxX = 0, maxY = 0;
        foreach (var l in lms)
        {
            minX = Math.Min(minX, l.X);
            minY = Math.Min(minY, l.Y);
            maxX = Math.Max(maxX, l.X);
            maxY = Math.Max(maxY, l.Y);
        }

        minX = Math.Clamp(minX - pad, 0, 1);
        minY = Math.Clamp(minY - pad, 0, 1);
        maxX = Math.Clamp(maxX + pad, 0, 1);
        maxY = Math.Clamp(maxY + pad, 0, 1);
        return (minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }
}

/// <summary>Geometry helpers for landmark analytics (angles, distances, finger states, eye openness).</summary>
public static class VisionMath
{
    /// <summary>Angle ABC in degrees (at B), in the image plane, aspect-corrected.</summary>
    public static double Angle(NormalizedLandmark a, NormalizedLandmark b, NormalizedLandmark c, double aspect = 1)
    {
        var (ax, ay) = ((a.X - b.X) * aspect, a.Y - b.Y);
        var (cx, cy) = ((c.X - b.X) * aspect, c.Y - b.Y);
        var dot = ax * cx + ay * cy;
        var n = Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(cx * cx + cy * cy);
        return n < 1e-9 ? 0 : Math.Acos(Math.Clamp(dot / n, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>Angle ABC in degrees using metric 3-D world landmarks (more stable than image coordinates).</summary>
    public static double Angle3(Landmark a, Landmark b, Landmark c)
    {
        var (ax, ay, az) = (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        var (cx, cy, cz) = (c.X - b.X, c.Y - b.Y, c.Z - b.Z);
        var dot = ax * cx + ay * cy + az * cz;
        var n = Math.Sqrt(ax * ax + ay * ay + az * az) * Math.Sqrt(cx * cx + cy * cy + cz * cz);
        return n < 1e-9 ? 0 : Math.Acos(Math.Clamp(dot / n, -1, 1)) * 180 / Math.PI;
    }

    public static double Distance(NormalizedLandmark a, NormalizedLandmark b, double aspect = 1) =>
        Math.Sqrt(Math.Pow((a.X - b.X) * aspect, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>Which fingers are extended: thumb, index, middle, ring, pinky.</summary>
    public static bool[] ExtendedFingers(HandLandmarks hand)
    {
        var l = hand.Landmarks;
        var wrist = l[0];
        bool Ext(int tip, int pip) => Distance(l[tip], wrist) > Distance(l[pip], wrist) * 1.12;
        // Thumb: tip farther from the index MCP than the IP joint is.
        var thumb = Distance(l[4], l[5]) > Distance(l[3], l[5]) * 1.1 && Distance(l[4], l[17]) > Distance(l[2], l[17]);
        return [thumb, Ext(8, 6), Ext(12, 10), Ext(16, 14), Ext(20, 18)];
    }

    public static int CountFingers(HandLandmarks hand) => ExtendedFingers(hand).Count(x => x);

    /// <summary>Eye aspect ratio from the 478-point mesh (low values mean closed eyes). Returns (left, right).</summary>
    public static (double Left, double Right) EyeAspectRatio(IReadOnlyList<NormalizedLandmark> f, double aspect = 1)
    {
        double Ear(int p1, int p2, int p3, int p4, int p5, int p6) =>
            (Distance(f[p2], f[p6], aspect) + Distance(f[p3], f[p5], aspect)) / (2 * Math.Max(1e-6, Distance(f[p1], f[p4], aspect)));
        return (Ear(362, 385, 387, 263, 373, 380), Ear(33, 160, 158, 133, 153, 144));
    }

    /// <summary>Head yaw/pitch estimate in degrees from nose and face oval landmarks (rough, no camera intrinsics).</summary>
    public static (double Yaw, double Pitch) HeadPose(IReadOnlyList<NormalizedLandmark> f)
    {
        var nose = f[1];
        var left = f[234];
        var right = f[454];
        var top = f[10];
        var chin = f[152];
        var yaw = ((nose.X - left.X) / Math.Max(1e-6, right.X - left.X) - 0.5) * 120;
        var pitch = ((nose.Y - top.Y) / Math.Max(1e-6, chin.Y - top.Y) - 0.52) * 120;
        return (yaw, pitch);
    }

    /// <summary>Normalized landmark → flat [x, y] for paths.</summary>
    public static float[] Flat(params NormalizedLandmark[] pts) => pts.SelectMany(p => new[] { p.X, p.Y }).ToArray();
}
