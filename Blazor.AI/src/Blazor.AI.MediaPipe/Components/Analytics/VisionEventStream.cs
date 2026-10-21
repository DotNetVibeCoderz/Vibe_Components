using MediaPipeNet;
using MediaPipeNet.Imaging;

namespace Blazor.AI.MediaPipe;

/// <summary>Severity of a <see cref="VisionEvent"/>.</summary>
public enum VisionEventSeverity
{
    Info,
    Warning,
    Alert,
}

/// <summary>Something a vision component noticed: a fall, an intrusion, a check-in, a count update.</summary>
public sealed record VisionEvent(DateTimeOffset At, string Source, string Kind, string Message, VisionEventSeverity Severity = VisionEventSeverity.Info)
{
    /// <summary>Optional JPEG data URL of the moment.</summary>
    public string? Snapshot { get; init; }

    /// <summary>Optional class counts (e.g. person=3, car=2) for statistics.</summary>
    public IReadOnlyDictionary<string, int>? Counts { get; init; }

    /// <summary>Optional normalized position (centre of what triggered the event) for heat maps.</summary>
    public (float X, float Y)? Position { get; init; }
}

/// <summary>
/// An in-memory stream of vision events shared by solution and analytics components. Pass one instance to producers
/// (Events parameter) and to dashboards, or use <see cref="Shared"/>. Thread-safe; keeps the latest
/// <see cref="Capacity"/> events.
/// </summary>
public sealed class VisionEventStream
{
    private readonly object _gate = new();
    private readonly LinkedList<VisionEvent> _events = new();

    /// <summary>A process-wide stream (handy for demos; prefer one per user or tenant in production).</summary>
    public static VisionEventStream Shared { get; } = new();

    public int Capacity { get; init; } = 2000;

    public event Action<VisionEvent>? Published;

    public void Publish(VisionEvent e)
    {
        lock (_gate)
        {
            _events.AddLast(e);
            while (_events.Count > Capacity)
            {
                _events.RemoveFirst();
            }
        }

        Published?.Invoke(e);
    }

    public IReadOnlyList<VisionEvent> Snapshot(int max = int.MaxValue)
    {
        lock (_gate)
        {
            return _events.Reverse().Take(max).Reverse().ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }

        Published?.Invoke(new VisionEvent(DateTimeOffset.UtcNow, "stream", "cleared", "Events cleared"));
    }
}

/// <summary>Pixel access, IoU tracking, hand shapes and simple colour heuristics used by the solution components.</summary>
public static class VisionSolutions
{
    /// <summary>RGB24 pixels of a frame, downsampled so the longest side is at most <paramref name="maxSide"/>.</summary>
    public static (byte[] Rgb, int Width, int Height) Pixels(MPImage image, int maxSide = 320)
    {
        var w = image.Width;
        var h = image.Height;
        var full = new byte[w * h * 3];
        image.CopyTo(full, PixelFormat.Rgb24);
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)maxSide));
        if (step == 1)
        {
            return (full, w, h);
        }

        var dw = w / step;
        var dh = h / step;
        var small = new byte[dw * dh * 3];
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                var s = ((y * step) * w + x * step) * 3;
                var d = (y * dw + x) * 3;
                small[d] = full[s];
                small[d + 1] = full[s + 1];
                small[d + 2] = full[s + 2];
            }
        }

        return (small, dw, dh);
    }

    /// <summary>RGB to hue (0–360), saturation and value (0–1).</summary>
    public static (double H, double S, double V) Hsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            h = max == rf ? 60 * (((gf - bf) / d) % 6) : max == gf ? 60 * ((bf - rf) / d + 2) : 60 * ((rf - gf) / d + 4);
        }

        return (h < 0 ? h + 360 : h, max < 1e-9 ? 0 : d / max, max);
    }

    /// <summary>Iterates pixels inside a normalized rectangle of a pixel buffer.</summary>
    public static IEnumerable<(byte R, byte G, byte B)> Region(byte[] rgb, int w, int h, float x, float y, float rw, float rh)
    {
        var x0 = Math.Clamp((int)(x * w), 0, w - 1);
        var y0 = Math.Clamp((int)(y * h), 0, h - 1);
        var x1 = Math.Clamp((int)((x + rw) * w), x0 + 1, w);
        var y1 = Math.Clamp((int)((y + rh) * h), y0 + 1, h);
        for (var yy = y0; yy < y1; yy++)
        {
            for (var xx = x0; xx < x1; xx++)
            {
                var i = (yy * w + xx) * 3;
                yield return (rgb[i], rgb[i + 1], rgb[i + 2]);
            }
        }
    }

    /// <summary>
    /// Hard-hat cue: share of saturated, bright "safety" colours (yellow, orange, white, red, blue) in the head region
    /// above the face. A heuristic, not a trained PPE detector.
    /// </summary>
    public static double HardHatScore(byte[] rgb, int w, int h, float x, float y, float rw, float rh)
    {
        int n = 0, hits = 0;
        foreach (var (r, g, b) in Region(rgb, w, h, x, y, rw, rh))
        {
            n++;
            var (hh, s, v) = Hsv(r, g, b);
            var safety = (s > 0.45 && v > 0.55 && (hh < 65 || hh > 340 || (hh > 195 && hh < 245))) || (s < 0.12 && v > 0.85);
            if (safety)
            {
                hits++;
            }
        }

        return n == 0 ? 0 : hits / (double)n;
    }

    /// <summary>Intersection over union of two normalized boxes.</summary>
    public static double Iou((float X, float Y, float W, float H) a, (float X, float Y, float W, float H) b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Normalized box of a pixel-space detection.</summary>
    public static (float X, float Y, float W, float H) Norm(Detection d, int w, int h) =>
        (d.BoundingBox.X / w, d.BoundingBox.Y / h, d.BoundingBox.Width / w, d.BoundingBox.Height / h);

    /// <summary>Wrist-relative, scale-normalised 2-D hand shape (42 values), mirrored to a right hand.</summary>
    public static float[] HandShape(MediaPipeNet.Tasks.Vision.HandLandmarks hand)
    {
        var l = hand.Landmarks;
        var w = l[0];
        var scale = (float)Math.Max(1e-4, Math.Sqrt(Math.Pow(l[9].X - w.X, 2) + Math.Pow(l[9].Y - w.Y, 2)));
        var flip = hand.IsLeft ? -1 : 1;
        var v = new float[l.Count * 2];
        for (var i = 0; i < l.Count; i++)
        {
            v[2 * i] = (l[i].X - w.X) / scale * flip;
            v[2 * i + 1] = (l[i].Y - w.Y) / scale;
        }

        return v;
    }

    public static double ShapeDistance(float[] a, float[] b)
    {
        double s = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            s += (a[i] - b[i]) * (a[i] - b[i]);
        }

        return Math.Sqrt(s / Math.Max(1, n));
    }

    public static bool Inside((float X, float Y) p, (float X, float Y, float W, float H) r) =>
        p.X >= r.X && p.X <= r.X + r.W && p.Y >= r.Y && p.Y <= r.Y + r.H;

    public static OverlayBox ZoneBox((float X, float Y, float W, float H) r, string label, string color, bool fill = true) =>
        new() { X = r.X, Y = r.Y, W = r.W, H = r.H, Label = label, Color = color, Dashed = true, Fill = fill };
}

/// <summary>Assigns stable ids to boxes across frames by greedy IoU matching.</summary>
public sealed class IouTracker(double minIou = 0.25, int maxMissed = 8)
{
    private int _next = 1;
    private readonly Dictionary<int, Track> _tracks = [];

    public sealed class Track(int id, (float X, float Y, float W, float H) box, string label)
    {
        public int Id { get; } = id;
        public string Label { get; set; } = label;
        public (float X, float Y, float W, float H) Box { get; set; } = box;
        public int Missed { get; set; }
        public DateTime FirstSeen { get; } = DateTime.UtcNow;
        public List<(float X, float Y)> Trail { get; } = [];
        public double Motion { get; set; }
    }

    public IReadOnlyCollection<Track> Tracks => _tracks.Values;

    public IReadOnlyList<Track> Update(IReadOnlyList<((float X, float Y, float W, float H) Box, string Label)> detections)
    {
        var unmatched = _tracks.Values.ToList();
        var result = new List<Track>();
        foreach (var (box, label) in detections.OrderByDescending(d => d.Box.W * d.Box.H))
        {
            var best = unmatched.Where(t => t.Label == label).Select(t => (t, iou: VisionSolutions.Iou(t.Box, box))).OrderByDescending(x => x.iou).FirstOrDefault();
            Track track;
            if (best.t is not null && best.iou >= minIou)
            {
                track = best.t;
                unmatched.Remove(track);
                var (ox, oy) = (track.Box.X + track.Box.W / 2, track.Box.Y + track.Box.H / 2);
                var (nx, ny) = (box.X + box.W / 2, box.Y + box.H / 2);
                track.Motion = track.Motion * 0.6 + Math.Sqrt(Math.Pow(nx - ox, 2) + Math.Pow(ny - oy, 2)) * 0.4;
                track.Box = box;
                track.Missed = 0;
            }
            else
            {
                track = new Track(_next++, box, label);
                _tracks[track.Id] = track;
            }

            track.Trail.Add((box.X + box.W / 2, box.Y + box.H));
            if (track.Trail.Count > 40)
            {
                track.Trail.RemoveAt(0);
            }

            result.Add(track);
        }

        foreach (var t in unmatched)
        {
            if (++t.Missed > maxMissed)
            {
                _tracks.Remove(t.Id);
            }
        }

        return result;
    }

    public void Reset()
    {
        _tracks.Clear();
        _next = 1;
    }
}
