using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenSceneGraph.Net.Terrain;
using AvColor = Avalonia.Media.Color;
using AvPoint = Avalonia.Point;

namespace OpenSceneGraph.Net.Simulator;

internal static class HudPalette
{
    public static readonly AvColor Phosphor = AvColor.Parse("#8CF7C4");
    public static readonly AvColor Caution = AvColor.Parse("#FFB547");
    public static readonly IBrush PhosphorBrush = new SolidColorBrush(Phosphor);
    public static readonly IBrush PhosphorDim = new SolidColorBrush(Phosphor, 0.5);
    public static readonly IBrush CautionBrush = new SolidColorBrush(Caution);
    public static readonly IPen PhosphorPen = new Pen(PhosphorBrush, 1.4);
    public static readonly IPen PhosphorThin = new Pen(PhosphorDim, 1);
    public static readonly IPen CautionPen = new Pen(CautionBrush, 2.2, lineCap: PenLineCap.Round);
    public static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas, Courier New"));

    public static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);
}

/// <summary>Attitude director indicator: sky/ground ball rotated by roll and shifted by pitch, bank scale on top.</summary>
public sealed class AttitudeIndicator : Control
{
    private static readonly IBrush Sky = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(AvColor.Parse("#1d4f91"), 0), new GradientStop(AvColor.Parse("#3f86c9"), 1) },
    };
    private static readonly IBrush Ground = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(AvColor.Parse("#7a4f27"), 0), new GradientStop(AvColor.Parse("#3e2914"), 1) },
    };
    private static readonly IPen White = new Pen(Brushes.White, 1.6);
    private static readonly IPen WhiteThin = new Pen(new SolidColorBrush(Colors.White, 0.8), 1.1);
    private static readonly IPen Rim = new Pen(new SolidColorBrush(AvColor.Parse("#24324F")), 3);

    public float Pitch { get; set; }
    public float Roll { get; set; }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        double r = System.Math.Min(b.Width, b.Height) / 2 - 2;
        var c = new AvPoint(b.Width / 2, b.Height / 2);
        double k = r / 32; // px per degree of pitch
        var circle = new EllipseGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
        using (ctx.PushGeometryClip(circle))
        {
            var m = Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateTranslation(0, Pitch * k) *
                    Matrix.CreateRotation(-Roll * System.Math.PI / 180) * Matrix.CreateTranslation(c.X, c.Y);
            using (ctx.PushTransform(m))
            {
                double big = r * 4;
                ctx.FillRectangle(Sky, new Rect(c.X - big, c.Y - big, 2 * big, big));
                ctx.FillRectangle(Ground, new Rect(c.X - big, c.Y, 2 * big, big));
                ctx.DrawLine(White, new AvPoint(c.X - big, c.Y), new AvPoint(c.X + big, c.Y));
                for (int deg = -60; deg <= 60; deg += 5)
                {
                    if (deg == 0) continue;
                    double y = c.Y - deg * k;
                    double w = deg % 10 == 0 ? r * 0.36 : r * 0.16;
                    ctx.DrawLine(deg % 10 == 0 ? White : WhiteThin, new AvPoint(c.X - w, y), new AvPoint(c.X + w, y));
                    if (deg % 10 == 0)
                    {
                        var t = HudPalette.Text(System.Math.Abs(deg).ToString(CultureInfo.InvariantCulture), 9, Brushes.White);
                        ctx.DrawText(t, new AvPoint(c.X + w + 3, y - t.Height / 2));
                        ctx.DrawText(t, new AvPoint(c.X - w - 3 - t.Width, y - t.Height / 2));
                    }
                }
            }
        }
        ctx.DrawEllipse(null, Rim, c, r, r);

        // bank scale (fixed) and roll pointer (rotates)
        foreach (int a in (ReadOnlySpan<int>)[-60, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60])
        {
            double rad = (a - 90) * System.Math.PI / 180;
            double len = a % 30 == 0 ? 9 : 5;
            var p0 = new AvPoint(c.X + System.Math.Cos(rad) * (r - 1), c.Y + System.Math.Sin(rad) * (r - 1));
            var p1 = new AvPoint(c.X + System.Math.Cos(rad) * (r - 1 - len), c.Y + System.Math.Sin(rad) * (r - 1 - len));
            ctx.DrawLine(White, p0, p1);
        }
        using (ctx.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(-Roll * System.Math.PI / 180) * Matrix.CreateTranslation(c.X, c.Y)))
        {
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new AvPoint(c.X, c.Y - r + 11), true);
                g.LineTo(new AvPoint(c.X - 6, c.Y - r + 21));
                g.LineTo(new AvPoint(c.X + 6, c.Y - r + 21));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(HudPalette.CautionBrush, null, tri);
        }

        // fixed aircraft symbol
        double s = r * 0.42;
        ctx.DrawLine(HudPalette.CautionPen, new AvPoint(c.X - s, c.Y), new AvPoint(c.X - s * 0.35, c.Y));
        ctx.DrawLine(HudPalette.CautionPen, new AvPoint(c.X - s * 0.35, c.Y), new AvPoint(c.X - s * 0.18, c.Y + 7));
        ctx.DrawLine(HudPalette.CautionPen, new AvPoint(c.X + s, c.Y), new AvPoint(c.X + s * 0.35, c.Y));
        ctx.DrawLine(HudPalette.CautionPen, new AvPoint(c.X + s * 0.35, c.Y), new AvPoint(c.X + s * 0.18, c.Y + 7));
        ctx.DrawEllipse(HudPalette.CautionBrush, null, c, 2.5, 2.5);
    }
}

/// <summary>Heading tape (±60°) with cardinal letters, the current heading box and a waypoint bearing bug.</summary>
public sealed class HeadingTape : Control
{
    public float Heading { get; set; }
    public float? Bug { get; set; }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        double w = b.Width, h = b.Height;
        double k = w / 120;
        double cx = w / 2;
        using (ctx.PushClip(new Rect(0, 0, w, h)))
        {
            int start = (int)MathF.Floor((Heading - 62) / 5) * 5;
            for (int d = start; d <= Heading + 62; d += 5)
            {
                double x = cx + (d - Heading) * k;
                int norm = ((d % 360) + 360) % 360;
                bool major = norm % 10 == 0;
                ctx.DrawLine(major ? HudPalette.PhosphorPen : HudPalette.PhosphorThin, new AvPoint(x, h - 2), new AvPoint(x, h - (major ? 12 : 7)));
                if (norm % 30 == 0)
                {
                    string label = norm switch { 0 => "N", 90 => "E", 180 => "S", 270 => "W", _ => (norm / 10).ToString("00", CultureInfo.InvariantCulture) };
                    var t = HudPalette.Text(label, norm % 90 == 0 ? 13 : 11, HudPalette.PhosphorBrush);
                    ctx.DrawText(t, new AvPoint(x - t.Width / 2, h - 30));
                }
            }
            if (Bug is { } bug)
            {
                float delta = ((bug - Heading) % 360 + 540) % 360 - 180;
                double x = cx + System.Math.Clamp(delta, -58, 58) * k;
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(new AvPoint(x, h - 1), true);
                    gc.LineTo(new AvPoint(x - 6, h - 9));
                    gc.LineTo(new AvPoint(x, h - 17));
                    gc.LineTo(new AvPoint(x + 6, h - 9));
                    gc.EndFigure(true);
                }
                ctx.DrawGeometry(System.Math.Abs(delta) > 58 ? null : HudPalette.CautionBrush, HudPalette.CautionPen, g);
            }
        }
        // centre caret
        ctx.DrawLine(new Pen(Brushes.White, 2), new AvPoint(cx, h - 2), new AvPoint(cx, h - 18));
    }
}

/// <summary>Vertical bar gauge (throttle).</summary>
public sealed class BarGauge : Control
{
    public float Value { get; set; }
    public float Minimum { get; set; }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        var frame = new Rect(0.5, 0.5, b.Width - 1, b.Height - 1);
        ctx.DrawRectangle(new SolidColorBrush(AvColor.Parse("#0A0F1C"), 0.6), HudPalette.PhosphorThin, frame, 3, 3);
        double range = 1 - Minimum;
        double zeroY = frame.Bottom - (0 - Minimum) / range * frame.Height;
        double valY = frame.Bottom - (System.Math.Clamp(Value, Minimum, 1) - Minimum) / range * frame.Height;
        var fill = new Rect(frame.X + 3, System.Math.Min(zeroY, valY), frame.Width - 6, System.Math.Abs(zeroY - valY));
        ctx.FillRectangle(Value < 0 ? HudPalette.CautionBrush : HudPalette.PhosphorBrush, fill, 2);
        if (Minimum < 0) ctx.DrawLine(new Pen(Brushes.White, 1), new AvPoint(frame.X, zeroY), new AvPoint(frame.Right, zeroY));
        for (int i = 1; i < 4; i++)
        {
            double y = frame.Bottom - i / 4.0 * frame.Height;
            ctx.DrawLine(HudPalette.PhosphorThin, new AvPoint(frame.Right - 5, y), new AvPoint(frame.Right, y));
        }
    }
}

/// <summary>North-up moving map from the height field with the route, waypoints and own vehicle.</summary>
public sealed class Minimap : Control
{
    private WriteableBitmap? _map;
    private HeightField? _field;

    public Vector3 Position { get; set; }
    public float Heading { get; set; }
    public IReadOnlyList<Vector3> Waypoints { get; set; } = [];
    public int Active { get; set; }

    /// <summary>Side of the visible window in metres (0 = whole training area), centred on the vehicle.</summary>
    public float ViewSize { get; set; }

    private (float X0, float Y0, float Size) Window()
    {
        if (_field is null) return (0, 0, 1);
        float full = MathF.Max(_field.Width, _field.Depth);
        float size = ViewSize <= 0 || ViewSize >= full ? full : ViewSize;
        float x0 = System.Math.Clamp(Position.X - size / 2, _field.Origin.X, _field.Origin.X + _field.Width - size);
        float y0 = System.Math.Clamp(Position.Y - size / 2, _field.Origin.Y, _field.Origin.Y + _field.Depth - size);
        return (x0, y0, size);
    }

    /// <summary>Renders a hill-shaded colour image of the terrain (once per world).</summary>
    public unsafe void SetTerrain(HeightField field, ColorRamp ramp, float min, float max, bool water)
    {
        _field = field;
        const int size = 512;
        var bmp = new WriteableBitmap(new PixelSize(size, size), new global::Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            var light = Vector3.Normalize(new Vector3(-1, 1, 1.4f));
            for (int py = 0; py < size; py++)
            {
                uint* row = (uint*)(fb.Address + py * fb.RowBytes);
                for (int px = 0; px < size; px++)
                {
                    float x = field.Origin.X + (px + 0.5f) / size * field.Width;
                    float y = field.Origin.Y + (1 - (py + 0.5f) / size) * field.Depth;
                    float h = field.Sample(x, y) ?? min;
                    float step = field.Width / size;
                    float hx = (field.Sample(x + step, y) ?? h) - h, hy = (field.Sample(x, y + step) ?? h) - h;
                    var n = Vector3.Normalize(new Vector3(-hx, -hy, step));
                    float shade = 0.55f + 0.6f * MathF.Max(0, Vector3.Dot(n, light));
                    Vector4 c = water && h < 0
                        ? Vector4.Lerp(new Vector4(0.05f, 0.2f, 0.36f, 1), new Vector4(0.15f, 0.42f, 0.6f, 1), System.Math.Clamp(1 + h / 60, 0, 1))
                        : ramp.Evaluate(h, min, max, n.Z) * shade;
                    if (water && h < 0) c *= 0.9f + 0.1f * shade;
                    c = Vector4.Lerp(c, new Vector4(0.04f, 0.06f, 0.11f, 1), 0.28f); // tint towards the UI ink
                    byte R = (byte)System.Math.Clamp(c.X * 255, 0, 255), G = (byte)System.Math.Clamp(c.Y * 255, 0, 255), B = (byte)System.Math.Clamp(c.Z * 255, 0, 255);
                    row[px] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
                }
            }
        }
        _map?.Dispose();
        _map = bmp;
        InvalidateVisual();
    }

    private AvPoint ToMap(Vector3 p, Rect r)
    {
        if (_field is null) return r.Center;
        var (x0, y0, size) = Window();
        double u = (p.X - x0) / size, v = 1 - (p.Y - y0) / size;
        return new AvPoint(r.X + u * r.Width, r.Y + v * r.Height);
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        using (ctx.PushClip(new RoundedRect(r, 6)))
        {
            if (_map is not null && _field is not null)
            {
                var (x0, y0, size) = Window();
                double sx = _map.PixelSize.Width / _field.Width, sy = _map.PixelSize.Height / _field.Depth;
                var src = new Rect((x0 - _field.Origin.X) * sx, (_field.Origin.Y + _field.Depth - (y0 + size)) * sy, size * sx, size * sy);
                ctx.DrawImage(_map, src, r);
            }
            // grid every 1 km
            if (_field is not null)
            {
                var gridPen = new Pen(new SolidColorBrush(Colors.White, 0.08), 1);
                float cell = Window().Size > 2000 ? 1000 : 100;
                for (float g = MathF.Ceiling(_field.Origin.X / cell) * cell; g < _field.Origin.X + _field.Width; g += cell)
                {
                    var a = ToMap(new Vector3(g, _field.Origin.Y, 0), r);
                    ctx.DrawLine(gridPen, new AvPoint(a.X, r.Top), new AvPoint(a.X, r.Bottom));
                    var bpt = ToMap(new Vector3(_field.Origin.X, g, 0), r);
                    ctx.DrawLine(gridPen, new AvPoint(r.Left, bpt.Y), new AvPoint(r.Right, bpt.Y));
                }
            }
            // route
            var routePen = new Pen(new SolidColorBrush(HudPalette.Phosphor, 0.55), 1.4, new DashStyle([3, 3], 0));
            for (int i = System.Math.Max(0, Active); i < Waypoints.Count; i++)
            {
                var from = i == Active ? ToMap(Position, r) : ToMap(Waypoints[i - 1], r);
                ctx.DrawLine(routePen, from, ToMap(Waypoints[i], r));
            }
            for (int i = 0; i < Waypoints.Count; i++)
            {
                var p = ToMap(Waypoints[i], r);
                bool done = i < Active, active = i == Active;
                ctx.DrawEllipse(done ? HudPalette.PhosphorDim : active ? HudPalette.CautionBrush : new SolidColorBrush(AvColor.Parse("#0A0F1C"), 0.8),
                    active ? null : HudPalette.PhosphorPen, p, active ? 5.5 : 4.5, active ? 5.5 : 4.5);
                var t = HudPalette.Text((i + 1).ToString(CultureInfo.InvariantCulture), 9, active ? HudPalette.CautionBrush : HudPalette.PhosphorBrush);
                ctx.DrawText(t, new AvPoint(p.X + 7, p.Y - t.Height - 1));
            }
            // own vehicle
            var me = ToMap(Position, r);
            using (ctx.PushTransform(Matrix.CreateRotation(Heading * System.Math.PI / 180) * Matrix.CreateTranslation(me.X, me.Y)))
            {
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(new AvPoint(0, -9), true);
                    gc.LineTo(new AvPoint(6, 7));
                    gc.LineTo(new AvPoint(0, 3.5));
                    gc.LineTo(new AvPoint(-6, 7));
                    gc.EndFigure(true);
                }
                ctx.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(AvColor.Parse("#0A0F1C")), 1.2), g);
            }
            var n = HudPalette.Text("N", 11, Brushes.White);
            ctx.DrawText(n, new AvPoint(r.Right - n.Width - 8, 6));
        }
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(HudPalette.Phosphor, 0.35), 1), r.Deflate(0.5), 6, 6);
    }
}
