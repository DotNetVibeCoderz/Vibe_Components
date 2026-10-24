using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GnuRadio.Sinks;

namespace GnuRadio.Avalonia;

/// <summary>An axis tick: position 0…1 and label.</summary>
public readonly record struct Tick(double Position, string Label, bool Strong = false);

/// <summary>Builds axis ticks (same rules as the Blazor components).</summary>
public static class Ticks
{
    public static Tick[] Frequency(double start, double end, int count = 8) =>
        [.. Axis.NiceTicks(start, end, count).Select(t => new Tick((t - start) / (end - start), Axis.FormatFrequency(t, t % 1e6 == 0 ? 0 : 3)))];

    public static Tick[] Linear(double min, double max, int count = 5, string unit = "") =>
        [.. Axis.NiceTicks(min, max, count).Select(t => new Tick((t - min) / (max - min), Axis.Format(t) + unit, t == 0))];

    public static Tick[] Time(double seconds, int count = 6) =>
        [.. Axis.NiceTicks(0, seconds, count).Select(t => new Tick(t / seconds, Axis.FormatTime(t)))];

    public static Tick[] LogDecades(double min, double max)
    {
        var lo = Math.Log10(min);
        var hi = Math.Log10(max);
        var ticks = new List<Tick>();
        for (var e = (int)Math.Ceiling(lo); e <= (int)Math.Floor(hi); e++) ticks.Add(new Tick((e - lo) / (hi - lo), $"1e{e}"));
        return [.. ticks];
    }
}

/// <summary>
/// Base of the instrument displays: a rounded dark screen with a title bar and a plot area. While the control is
/// on screen a timer polls the data source at <see cref="Fps"/> and repaints only when something changed.
/// </summary>
public abstract class GrDisplay : Control
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<GrDisplay, string?>(nameof(Title));
    public static readonly StyledProperty<string?> MetaProperty = AvaloniaProperty.Register<GrDisplay, string?>(nameof(Meta));

    private DispatcherTimer? _timer;

    protected GrDisplay()
    {
        ClipToBounds = true;
        MinHeight = 120;
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Text at the right of the title bar.</summary>
    public string? Meta
    {
        get => GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public double Fps { get; set; } = 30;

    /// <summary>Margins of the plot area inside the screen.</summary>
    protected virtual Thickness PlotMargin => new(48, Title is null ? 10 : 30, 10, 22);

    static GrDisplay()
    {
        AffectsRender<GrDisplay>(TitleProperty, MetaProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1 / Math.Max(1, Fps)), DispatcherPriority.Render, (_, _) =>
        {
            if (Poll()) InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Copies new data if available; return true to repaint.</summary>
    protected abstract bool Poll();

    /// <summary>Draws the content inside <paramref name="plot"/>.</summary>
    protected abstract void RenderPlot(DrawingContext context, Rect plot);

    protected Rect PlotRect
    {
        get
        {
            var m = PlotMargin;
            return new Rect(m.Left, m.Top, Math.Max(1, Bounds.Width - m.Left - m.Right), Math.Max(1, Bounds.Height - m.Top - m.Bottom));
        }
    }

    public sealed override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(GrTheme.Screen, new Pen(GrTheme.ScreenEdge, 1), bounds, 10, 10);
        if (Title is not null)
        {
            DrawText(context, Title.ToUpperInvariant(), new Point(12, 8), GrTheme.Title, 11);
            if (Meta is not null) DrawText(context, Meta, new Point(bounds.Width - 12, 8), GrTheme.ScreenText, 11, TextAlignment.Right);
        }
        using (context.PushClip(new RoundedRect(bounds, 10)))
            RenderPlot(context, PlotRect);
    }

    protected static void DrawText(DrawingContext context, string text, Point at, IBrush brush, double size = 10.5,
        TextAlignment alignment = TextAlignment.Left, bool middle = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, GrTheme.DataTypeface, size, brush);
        var x = alignment switch
        {
            TextAlignment.Right => at.X - ft.Width,
            TextAlignment.Center => at.X - ft.Width / 2,
            _ => at.X,
        };
        context.DrawText(ft, new Point(x, middle ? at.Y - ft.Height / 2 : at.Y));
    }

    protected static void DrawGrid(DrawingContext context, Rect plot, IReadOnlyList<Tick> xTicks, IReadOnlyList<Tick> yTicks)
    {
        foreach (var t in yTicks)
        {
            var y = Math.Round(plot.Y + (1 - t.Position) * plot.Height) + 0.5;
            context.DrawLine(t.Strong ? GrTheme.GridStrong : GrTheme.Grid, new Point(plot.X, y), new Point(plot.Right, y));
            DrawText(context, t.Label, new Point(plot.X - 6, y), GrTheme.ScreenText, alignment: TextAlignment.Right, middle: true);
        }
        foreach (var t in xTicks)
        {
            var x = Math.Round(plot.X + t.Position * plot.Width) + 0.5;
            context.DrawLine(t.Strong ? GrTheme.GridStrong : GrTheme.Grid, new Point(x, plot.Y), new Point(x, plot.Bottom));
            DrawText(context, t.Label, new Point(x, plot.Bottom + 5), GrTheme.ScreenText, alignment: TextAlignment.Center);
        }
        context.DrawRectangle(null, GrTheme.GridStrong, plot);
    }

    protected static void DrawMarkers(DrawingContext context, Rect plot, IEnumerable<(double Position, string? Label)> markers, bool dashed = false)
    {
        var brush = new SolidColorBrush(GrTheme.Marker);
        var pen = new Pen(brush, 1, dashed ? new DashStyle([4, 4], 0) : null);
        var row = 0;
        foreach (var (p, label) in markers)
        {
            if (p is < 0 or > 1) continue;
            var x = Math.Round(plot.X + p * plot.Width) + 0.5;
            context.DrawLine(pen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            if (label is not null) DrawText(context, label, new Point(x + 4, plot.Y + 4 + (row++ % 3) * 13), brush);
        }
    }

    /// <summary>
    /// Draws a trace with peak-preserving decimation (min/max per pixel column) and an optional gradient fill.
    /// </summary>
    protected static void DrawTrace(DrawingContext context, Rect plot, ReadOnlySpan<float> data, double min, double max, Color color, bool fill)
    {
        if (data.IsEmpty) return;
        var cols = Math.Max(1, (int)plot.Width);
        var range = max - min;
        double Y(float v) => plot.Y + (1 - (Math.Clamp(v, min, max) - min) / range) * plot.Height;
        var top = new StreamGeometry();
        var fillGeometry = new StreamGeometry();
        using (var g = top.Open())
        using (var f = fillGeometry.Open())
        {
            f.BeginFigure(new Point(plot.X, plot.Bottom), true);
            for (var x = 0; x < cols; x++)
            {
                var a = (int)((long)x * data.Length / cols);
                var b = Math.Max(a + 1, (int)((long)(x + 1) * data.Length / cols));
                float lo = float.MaxValue, hi = float.MinValue;
                for (var i = a; i < b && i < data.Length; i++)
                {
                    if (data[i] < lo) lo = data[i];
                    if (data[i] > hi) hi = data[i];
                }
                var px = plot.X + x * plot.Width / cols;
                if (x == 0) g.BeginFigure(new Point(px, Y(hi)), false);
                else g.LineTo(new Point(px, Y(hi)));
                if (lo < hi) g.LineTo(new Point(px, Y(lo)));
                g.LineTo(new Point(px, Y(hi)));
                f.LineTo(new Point(px, Y(hi)));
            }
            g.EndFigure(false);
            f.LineTo(new Point(plot.Right, plot.Bottom));
            f.EndFigure(true);
        }
        if (fill)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Color.FromArgb(90, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(5, color.R, color.G, color.B), 1)],
            };
            context.DrawGeometry(gradient, null, fillGeometry);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.25), top);
    }
}
