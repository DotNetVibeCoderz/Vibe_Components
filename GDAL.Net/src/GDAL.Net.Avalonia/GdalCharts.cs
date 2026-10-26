using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GDAL.Net.Rendering;

namespace GDAL.Net.Avalonia;

/// <summary>Custom-drawn histogram of a <see cref="Histogram"/> (from <see cref="RasterBand.GetHistogram"/>), optionally colored by a ramp.</summary>
public class GdalHistogramChart : Control
{
    public static readonly StyledProperty<Histogram?> HistogramProperty =
        AvaloniaProperty.Register<GdalHistogramChart, Histogram?>(nameof(Histogram));

    /// <summary>Colors bars by their position in [<see cref="RampMin"/>, <see cref="RampMax"/>] (solid <see cref="BarBrush"/> when null).</summary>
    public static readonly StyledProperty<ColorRamp?> RampProperty =
        AvaloniaProperty.Register<GdalHistogramChart, ColorRamp?>(nameof(Ramp));

    public static readonly StyledProperty<double?> RampMinProperty = AvaloniaProperty.Register<GdalHistogramChart, double?>(nameof(RampMin));
    public static readonly StyledProperty<double?> RampMaxProperty = AvaloniaProperty.Register<GdalHistogramChart, double?>(nameof(RampMax));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<GdalHistogramChart, IBrush?>(nameof(BarBrush), new SolidColorBrush(Color.Parse("#2A9D8F")));

    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<GdalHistogramChart, IBrush?>(nameof(AxisBrush), new SolidColorBrush(Color.Parse("#5B6B73")));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<GdalHistogramChart, IBrush?>(nameof(GridBrush), new SolidColorBrush(Color.Parse("#C9D3CF")));

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<GdalHistogramChart, string?>(nameof(Title));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<GdalHistogramChart, FontFamily>(nameof(FontFamily), FontFamily.Default);

    /// <summary>Logarithmic count axis (useful for skewed distributions).</summary>
    public static readonly StyledProperty<bool> LogScaleProperty = AvaloniaProperty.Register<GdalHistogramChart, bool>(nameof(LogScale));

    public Histogram? Histogram { get => GetValue(HistogramProperty); set => SetValue(HistogramProperty, value); }
    public ColorRamp? Ramp { get => GetValue(RampProperty); set => SetValue(RampProperty, value); }
    public double? RampMin { get => GetValue(RampMinProperty); set => SetValue(RampMinProperty, value); }
    public double? RampMax { get => GetValue(RampMaxProperty); set => SetValue(RampMaxProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public IBrush? AxisBrush { get => GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public bool LogScale { get => GetValue(LogScaleProperty); set => SetValue(LogScaleProperty, value); }

    static GdalHistogramChart()
    {
        AffectsRender<GdalHistogramChart>(HistogramProperty, RampProperty, RampMinProperty, RampMaxProperty, BarBrushProperty,
            AxisBrushProperty, GridBrushProperty, TitleProperty, LogScaleProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 180 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var h = Histogram;
        var size = Bounds.Size;
        var typeface = new Typeface(FontFamily);
        var axis = AxisBrush ?? Brushes.Gray;
        var top = string.IsNullOrEmpty(Title) ? 6.0 : 26.0;
        if (!string.IsNullOrEmpty(Title))
        {
            context.DrawText(new FormattedText(Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, axis), new Point(0, 2));
        }
        if (h is null || h.BucketCount == 0 || size.Width < 40 || size.Height < 40) return;

        var plot = new Rect(0, top, size.Width, size.Height - top - 20);
        var grid = new Pen(GridBrush ?? Brushes.LightGray, 1, dashStyle: new DashStyle([3, 3], 0));
        for (var i = 1; i <= 3; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        double Scale(ulong c) => LogScale ? Math.Log10(1 + c) : c;
        var max = Math.Max(1e-9, h.Counts.Max(c => Scale(c)));
        var bw = plot.Width / h.BucketCount;
        var ramp = Ramp;
        var lo = RampMin ?? h.Minimum;
        var hi = RampMax ?? h.Maximum;
        var bar = BarBrush ?? Brushes.Teal;
        for (var i = 0; i < h.BucketCount; i++)
        {
            var v = Scale(h.Counts[i]);
            if (v <= 0) continue;
            var bh = plot.Height * v / max;
            IBrush brush = bar;
            if (ramp is not null)
            {
                var c = ramp.Evaluate((h.BucketCenter(i) - lo) / Math.Max(1e-12, hi - lo));
                brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            }
            context.FillRectangle(brush, new Rect(plot.Left + i * bw + (bw > 4 ? 0.5 : 0), plot.Bottom - bh, Math.Max(1, bw - (bw > 4 ? 1 : 0)), bh));
        }
        context.DrawLine(new Pen(axis, 1), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));

        var minText = Format(h.Minimum);
        var maxText = Format(h.Maximum);
        var midText = Format((h.Minimum + h.Maximum) / 2);
        var ft = new FormattedText(minText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, axis);
        context.DrawText(ft, new Point(plot.Left, plot.Bottom + 4));
        ft = new FormattedText(midText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, axis);
        context.DrawText(ft, new Point(plot.Center.X - ft.Width / 2, plot.Bottom + 4));
        ft = new FormattedText(maxText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, axis);
        context.DrawText(ft, new Point(plot.Right - ft.Width, plot.Bottom + 4));
        var peak = new FormattedText($"max {h.MaxCount:#,0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, axis);
        context.DrawText(peak, new Point(plot.Right - peak.Width, plot.Top));
    }

    private static string Format(double v) => Math.Abs(v) >= 1000 ? v.ToString("#,0", CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Gradient legend for a <see cref="ColorRamp"/> with min/max labels.</summary>
public class GdalColorLegend : Control
{
    public static readonly StyledProperty<ColorRamp?> RampProperty = AvaloniaProperty.Register<GdalColorLegend, ColorRamp?>(nameof(Ramp));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<GdalColorLegend, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<GdalColorLegend, double>(nameof(Maximum), 1);
    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<GdalColorLegend, string?>(nameof(Unit));
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<GdalColorLegend, string?>(nameof(Title));
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<GdalColorLegend, Orientation>(nameof(Orientation), Orientation.Horizontal);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<GdalColorLegend, IBrush?>(nameof(Foreground), new SolidColorBrush(Color.Parse("#12263A")));
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<GdalColorLegend, FontFamily>(nameof(FontFamily), FontFamily.Default);
    /// <summary>Number of intermediate tick labels.</summary>
    public static readonly StyledProperty<int> TicksProperty = AvaloniaProperty.Register<GdalColorLegend, int>(nameof(Ticks), 3);

    public ColorRamp? Ramp { get => GetValue(RampProperty); set => SetValue(RampProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public int Ticks { get => GetValue(TicksProperty); set => SetValue(TicksProperty, value); }

    static GdalColorLegend()
    {
        AffectsRender<GdalColorLegend>(RampProperty, MinimumProperty, MaximumProperty, UnitProperty, TitleProperty, OrientationProperty, ForegroundProperty, TicksProperty);
        AffectsMeasure<GdalColorLegend>(OrientationProperty, TitleProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var titled = string.IsNullOrEmpty(Title) ? 0 : 18;
        return Orientation == Orientation.Horizontal
            ? new Size(double.IsInfinity(availableSize.Width) ? 260 : availableSize.Width, 40 + titled)
            : new Size(90, double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        var ramp = Ramp;
        if (ramp is null) return;
        var fg = Foreground ?? Brushes.Black;
        var typeface = new Typeface(FontFamily);
        var size = Bounds.Size;
        var y0 = 0.0;
        if (!string.IsNullOrEmpty(Title))
        {
            context.DrawText(new FormattedText(Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11.5, fg), new Point(0, 0));
            y0 = 18;
        }
        var horizontal = Orientation == Orientation.Horizontal;
        var bar = horizontal ? new Rect(0, y0 + 2, size.Width, 12) : new Rect(0, y0, 14, size.Height - y0);
        var stops = new GradientStops();
        foreach (var (pos, c) in ramp.Stops)
        {
            stops.Add(new GradientStop(Color.FromArgb(c.A, c.R, c.G, c.B), horizontal ? pos : 1 - pos));
        }
        var brush = new LinearGradientBrush
        {
            GradientStops = stops,
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = horizontal ? new RelativePoint(1, 0, RelativeUnit.Relative) : new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        context.DrawRectangle(brush, new Pen(new SolidColorBrush(Colors.Black, 0.15), 1), bar, 2, 2);

        var n = Math.Max(0, Ticks) + 2;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / (n - 1);
            var v = Minimum + (Maximum - Minimum) * t;
            var label = Format(v) + (i == n - 1 && !string.IsNullOrEmpty(Unit) ? " " + Unit : "");
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, fg);
            if (horizontal)
            {
                var x = bar.Left + bar.Width * t;
                context.DrawLine(new Pen(fg, 1), new Point(x, bar.Bottom), new Point(x, bar.Bottom + 3));
                var tx = Math.Clamp(x - ft.Width / 2, 0, Math.Max(0, size.Width - ft.Width));
                context.DrawText(ft, new Point(tx, bar.Bottom + 5));
            }
            else
            {
                var y = bar.Bottom - bar.Height * t;
                context.DrawLine(new Pen(fg, 1), new Point(bar.Right, y), new Point(bar.Right + 3, y));
                context.DrawText(ft, new Point(bar.Right + 6, Math.Clamp(y - ft.Height / 2, y0, size.Height - ft.Height)));
            }
        }
    }

    private static string Format(double v) => Math.Abs(v) >= 1000 ? v.ToString("#,0", CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
}
