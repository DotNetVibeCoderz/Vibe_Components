using MediaPipeNet;
using MediaPipeNet.Imaging;

namespace Blazor.AI.MediaPipe;

/// <summary>A detection in normalized coordinates with a stable track id.</summary>
public sealed record TrackedObject(int Id, string Label, float Score, float X, float Y, float W, float H)
{
    public float CenterX => X + W / 2;
    public float CenterY => Y + H / 2;
}

/// <summary>
/// Greedy IoU tracker: matches detections of the same class frame to frame and keeps ids stable while objects move.
/// Remembers each track's previous centre so callers can detect line crossings.
/// </summary>
public sealed class SimpleTracker(float iouThreshold = 0.25f, int maxMissed = 8)
{
    private sealed class Track
    {
        public required TrackedObject Last;
        public (float X, float Y)? Previous;
        public int Missed;
    }

    private readonly List<Track> _tracks = [];
    private int _nextId = 1;

    /// <summary>Updates the tracker with this frame's detections (pixel boxes) and returns tracked objects.</summary>
    public IReadOnlyList<TrackedObject> Update(IEnumerable<Detection> detections, int width, int height)
    {
        var current = detections.Select(d => new TrackedObject(0, d.TopCategory?.CategoryName ?? "object", d.Score,
            d.BoundingBox.X / width, d.BoundingBox.Y / height, d.BoundingBox.Width / width, d.BoundingBox.Height / height)).ToList();
        var result = new List<TrackedObject>(current.Count);
        var unmatched = new HashSet<Track>(_tracks);
        foreach (var det in current.OrderByDescending(d => d.Score))
        {
            Track? best = null;
            var bestIou = iouThreshold;
            foreach (var t in unmatched)
            {
                if (t.Last.Label != det.Label)
                {
                    continue;
                }

                var iou = Iou(t.Last, det);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    best = t;
                }
            }

            if (best is null)
            {
                best = new Track { Last = det with { Id = _nextId++ } };
                _tracks.Add(best);
            }
            else
            {
                unmatched.Remove(best);
                best.Previous = (best.Last.CenterX, best.Last.CenterY);
                best.Last = det with { Id = best.Last.Id };
                best.Missed = 0;
            }

            result.Add(best.Last);
        }

        foreach (var t in unmatched)
        {
            t.Missed++;
            t.Previous = null;
        }

        _tracks.RemoveAll(t => t.Missed > maxMissed);
        return result;
    }

    /// <summary>The centre of a track in the previous frame (null for new tracks).</summary>
    public (float X, float Y)? PreviousCentre(int id) => _tracks.FirstOrDefault(t => t.Last.Id == id)?.Previous;

    public void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
    }

    public static float Iou(TrackedObject a, TrackedObject b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

/// <summary>RGB pixels of a frame for simple colour and texture analysis.</summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgb { get; }

    public PixelBuffer(MPImage image)
    {
        Width = image.Width;
        Height = image.Height;
        Rgb = new byte[Width * Height * 3];
        image.CopyTo(Rgb, PixelFormat.Rgb24);
    }

    /// <summary>Hue (0–360), saturation and value (0–1) of a pixel.</summary>
    public (double H, double S, double V) Hsv(int x, int y)
    {
        var i = (y * Width + x) * 3;
        double r = Rgb[i] / 255.0, g = Rgb[i + 1] / 255.0, b = Rgb[i + 2] / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = d < 1e-6 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0)
        {
            h += 360;
        }

        return (h, max == 0 ? 0 : d / max, max);
    }

    public double Luma(int x, int y)
    {
        var i = (y * Width + x) * 3;
        return (0.299 * Rgb[i] + 0.587 * Rgb[i + 1] + 0.114 * Rgb[i + 2]) / 255.0;
    }

    /// <summary>
    /// Share of pixels in a normalized region that look like high-visibility PPE: saturated yellow/orange/red or bright
    /// white (hard hats, vests). Samples every other pixel for speed.
    /// </summary>
    public double HiVisFraction(float nx, float ny, float nw, float nh)
    {
        int x0 = Clamp(nx * Width, Width), y0 = Clamp(ny * Height, Height), x1 = Clamp((nx + nw) * Width, Width), y1 = Clamp((ny + nh) * Height, Height);
        int hits = 0, total = 0;
        for (var y = y0; y < y1; y += 2)
        {
            for (var x = x0; x < x1; x += 2)
            {
                var (h, s, v) = Hsv(x, y);
                total++;
                var yellowOrange = h is >= 18 and <= 62 && s > 0.45 && v > 0.55;
                var red = (h <= 12 || h >= 345) && s > 0.55 && v > 0.55;
                var white = s < 0.12 && v > 0.82;
                if (yellowOrange || red || white)
                {
                    hits++;
                }
            }
        }

        return total == 0 ? 0 : hits / (double)total;
    }

    /// <summary>
    /// Mean local gradient magnitude (texture) in a normalized region; empty shelf space, bare walls and floors are
    /// low-texture, stocked products are high-texture.
    /// </summary>
    public double Texture(float nx, float ny, float nw, float nh)
    {
        int x0 = Math.Max(1, Clamp(nx * Width, Width)), y0 = Math.Max(1, Clamp(ny * Height, Height));
        int x1 = Math.Min(Width - 1, Clamp((nx + nw) * Width, Width)), y1 = Math.Min(Height - 1, Clamp((ny + nh) * Height, Height));
        double sum = 0;
        var n = 0;
        for (var y = y0; y < y1; y += 2)
        {
            for (var x = x0; x < x1; x += 2)
            {
                var gx = Luma(x + 1, y) - Luma(x - 1, y);
                var gy = Luma(x, y + 1) - Luma(x, y - 1);
                sum += Math.Sqrt(gx * gx + gy * gy);
                n++;
            }
        }

        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Per-row strength of horizontal edges (for finding shelf boards), normalized 0–1, one value per pixel row.</summary>
    public double[] HorizontalEdgeProfile()
    {
        var p = new double[Height];
        for (var y = 1; y < Height - 1; y++)
        {
            double s = 0;
            for (var x = 0; x < Width; x += 2)
            {
                s += Math.Abs(Luma(x, y + 1) - Luma(x, y - 1));
            }

            p[y] = s / (Width / 2.0);
        }

        var max = p.Max();
        return max <= 0 ? p : p.Select(v => v / max).ToArray();
    }

    private static int Clamp(double v, int max) => Math.Clamp((int)v, 0, max);
}

/// <summary>Shelf analysis: finds shelf rows from horizontal edges and estimates stocked vs. empty tiles per row by texture.</summary>
public static class ShelfAnalysis
{
    public sealed record ShelfRow(float Top, float Bottom, IReadOnlyList<bool> Stocked, IReadOnlyList<(float X0, float X1)> Gaps)
    {
        public double Fill => Stocked.Count == 0 ? 0 : Stocked.Count(s => s) / (double)Stocked.Count;
    }

    /// <summary>Splits the frame into shelf rows and evaluates <paramref name="tiles"/> tiles per row.</summary>
    public static IReadOnlyList<ShelfRow> Analyse(PixelBuffer px, int tiles = 24, double textureThreshold = 0.045, int minGapTiles = 2)
    {
        var profile = px.HorizontalEdgeProfile();
        // Smooth and find peaks (shelf boards) at least 8% of the height apart.
        var smooth = new double[profile.Length];
        for (var y = 0; y < profile.Length; y++)
        {
            var lo = Math.Max(0, y - 3);
            var hi = Math.Min(profile.Length - 1, y + 3);
            double s = 0;
            for (var k = lo; k <= hi; k++)
            {
                s += profile[k];
            }

            smooth[y] = s / (hi - lo + 1);
        }

        var minDist = (int)(px.Height * 0.08);
        var peaks = new List<int>();
        foreach (var y in Enumerable.Range(0, smooth.Length).OrderByDescending(y => smooth[y]))
        {
            if (smooth[y] < 0.25 || peaks.Count >= 8)
            {
                break;
            }

            if (peaks.All(p => Math.Abs(p - y) >= minDist))
            {
                peaks.Add(y);
            }
        }

        peaks.Sort();
        var bounds = new List<int> { 0 };
        bounds.AddRange(peaks);
        bounds.Add(px.Height - 1);
        var rows = new List<ShelfRow>();
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var top = bounds[i] / (float)px.Height;
            var bottom = bounds[i + 1] / (float)px.Height;
            // Skip slivers and the strip above the first board (ceiling, signage).
            if (bottom - top < 0.07f || (i == 0 && bounds.Count > 2 && bottom - top < 0.15f))
            {
                continue;
            }

            // Look at the middle of the row, where products stand (skips the boards themselves).
            var bandTop = top + (bottom - top) * 0.2f;
            var bandH = (bottom - top) * 0.55f;
            var stocked = new List<bool>(tiles);
            for (var t = 0; t < tiles; t++)
            {
                stocked.Add(px.Texture(t / (float)tiles, bandTop, 1f / tiles, bandH) >= textureThreshold);
            }

            var gaps = new List<(float, float)>();
            var run = 0;
            for (var t = 0; t <= tiles; t++)
            {
                if (t < tiles && !stocked[t])
                {
                    run++;
                    continue;
                }

                if (run >= minGapTiles)
                {
                    gaps.Add(((t - run) / (float)tiles, t / (float)tiles));
                }

                run = 0;
            }

            rows.Add(new ShelfRow(top, bottom, stocked, gaps));
        }

        return rows;
    }
}
