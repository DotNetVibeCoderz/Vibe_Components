using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GnuRadio.Sinks;

namespace GnuRadio.Avalonia;

/// <summary>A rotary control: drag vertically, scroll, or use the arrow keys.</summary>
public sealed class Knob : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Knob, double>(nameof(Value));
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<Knob, string>(nameof(Label), "");

    private Point? _dragStart;
    private double _dragValue;

    static Knob()
    {
        AffectsRender<Knob>(ValueProperty, LabelProperty);
        FocusableProperty.OverrideDefaultValue<Knob>(true);
    }

    public Knob()
    {
        Width = 210;
        Height = 58;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 1;
    public double Step { get; set; } = 0.01;
    public string Unit { get; set; } = "";
    public Func<double, string>? Format { get; set; }

    /// <summary>Raised when the user changes the value.</summary>
    public event Action<double>? ValueChangedByUser;

    public IBrush Panel { get; set; } = new SolidColorBrush(Color.Parse("#e9edf1"));
    public IBrush Seam { get; set; } = new SolidColorBrush(Color.Parse("#c3cad2"));
    public IBrush Ink { get; set; } = new SolidColorBrush(Color.Parse("#13202b"));
    public IBrush Ink2 { get; set; } = new SolidColorBrush(Color.Parse("#4b5b6b"));

    private void SetByUser(double v)
    {
        v = Math.Clamp(Math.Round((v - Minimum) / Step) * Step + Minimum, Minimum, Maximum);
        if (v == Value) return;
        Value = v;
        ValueChangedByUser?.Invoke(v);
    }

    public override void Render(DrawingContext context)
    {
        var b = new Rect(Bounds.Size);
        context.DrawRectangle(Panel, new Pen(Seam, 1), b.Deflate(0.5), b.Height / 2, b.Height / 2);
        var center = new Point(29, b.Height / 2);
        const double r = 20;
        var fraction = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        DrawArc(context, center, r, 1, new Pen(Seam, 4, lineCap: PenLineCap.Round));
        DrawArc(context, center, r, fraction, new Pen(new SolidColorBrush(GrTheme.Accent), 4, lineCap: PenLineCap.Round));
        context.DrawEllipse(Brushes.Transparent, new Pen(Seam, 1), center, 13, 13);
        var a = (-135 + fraction * 270) * Math.PI / 180;
        context.DrawLine(new Pen(Ink, 3, lineCap: PenLineCap.Round), center, new Point(center.X + 10 * Math.Sin(a), center.Y - 10 * Math.Cos(a)));
        Text(context, Label, new Point(60, 10), Ink2, 12, new Typeface(FontFamily.Default));
        var display = Format?.Invoke(Value) ?? $"{Axis.Format(Value)}{(Unit.Length > 0 ? " " + Unit : "")}";
        Text(context, display, new Point(60, 27), Ink, 14, GrTheme.DataTypeface);
        if (IsFocused) context.DrawRectangle(null, new Pen(new SolidColorBrush(GrTheme.Accent), 2), b.Deflate(1), b.Height / 2, b.Height / 2);
    }

    private static void Text(DrawingContext c, string s, Point at, IBrush brush, double size, Typeface face) =>
        c.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush), at);

    private static void DrawArc(DrawingContext context, Point c, double r, double fraction, IPen pen)
    {
        fraction = Math.Clamp(fraction, 0.0001, 1);
        var a0 = -135 * Math.PI / 180;
        var a1 = (-135 + fraction * 270) * Math.PI / 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X + r * Math.Sin(a0), c.Y - r * Math.Cos(a0)), false);
            ctx.ArcTo(new Point(c.X + r * Math.Sin(a1), c.Y - r * Math.Cos(a1)), new Size(r, r), 0, fraction * 270 > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, g);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragStart = e.GetPosition(this);
        _dragValue = Value;
        e.Pointer.Capture(this);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start) return;
        SetByUser(_dragValue + (start.Y - e.GetPosition(this).Y) / 150 * (Maximum - Minimum));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetByUser(Value + Math.Sign(e.Delta.Y) * Step);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var delta = e.Key switch
        {
            Key.Up or Key.Right => Step,
            Key.Down or Key.Left => -Step,
            Key.PageUp => Step * 10,
            Key.PageDown => -Step * 10,
            _ => 0,
        };
        if (delta == 0) return;
        SetByUser(Value + delta);
        e.Handled = true;
    }
}

/// <summary>Receiver-style frequency display; scroll over a digit to tune by that digit's step.</summary>
public sealed class FrequencyReadout : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<FrequencyReadout, double>(nameof(Value));
    private readonly List<(Rect Box, double Step)> _digits = [];

    static FrequencyReadout()
    {
        AffectsRender<FrequencyReadout>(ValueProperty);
    }

    public FrequencyReadout()
    {
        Height = 76;
        Width = 400;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 6e9;
    public event Action<double>? ValueChangedByUser;

    public override void Render(DrawingContext context)
    {
        var b = new Rect(Bounds.Size);
        context.DrawRectangle(GrTheme.Screen, new Pen(GrTheme.ScreenEdge, 1), b, 10, 10);
        var hz = (long)Math.Round(Value);
        var text = $"{hz / 1_000_000:D4}.{hz % 1_000_000 / 1000:D3} {hz % 1000:D3}";
        var face = new Typeface(GrTheme.DisplayFont, FontStyle.Normal, FontWeight.SemiBold);
        const double size = 42;
        var x = 18.0;
        _digits.Clear();
        var leading = true;
        var digitIndex = 0;
        var glow = new SolidColorBrush(GrTheme.Trace);
        var dim = new SolidColorBrush(Color.FromArgb(56, GrTheme.Trace.R, GrTheme.Trace.G, GrTheme.Trace.B));
        foreach (var ch in text)
        {
            var isDigit = char.IsDigit(ch);
            if (isDigit && (ch != '0' || digitIndex == 3)) leading = false;
            var ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, isDigit && leading ? dim : glow);
            var cell = ch == ' ' ? 10 : ch == '.' ? 16 : Math.Max(ft.Width, 25);
            context.DrawText(ft, new Point(x + (cell - ft.Width) / 2, (b.Height - ft.Height) / 2));
            if (isDigit)
            {
                _digits.Add((new Rect(x, 0, cell, b.Height), Math.Pow(10, 9 - digitIndex)));
                digitIndex++;
            }
            x += cell;
        }
        var unit = new FormattedText("MHz", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, GrTheme.DataTypeface, 13, GrTheme.ScreenText);
        context.DrawText(unit, new Point(x + 10, b.Height / 2 + 4));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        foreach (var (box, step) in _digits)
        {
            if (!box.Contains(p)) continue;
            Value += Math.Sign(e.Delta.Y) * step;
            ValueChangedByUser?.Invoke(Value);
            e.Handled = true;
            return;
        }
    }
}

/// <summary>Left-to-right block diagram of a <see cref="Flowgraph"/>.</summary>
public sealed class FlowgraphView : Control
{
    private const double NodeWidth = 168;
    private const double NodeHeight = 52;
    private Flowgraph? _flowgraph;
    private List<(Block Block, Point At)> _nodes = [];
    private List<(Point From, Point To, bool Message)> _edges = [];

    public Flowgraph? Flowgraph
    {
        get => _flowgraph;
        set
        {
            _flowgraph = value;
            Layout();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public IBrush NodeFill { get; set; } = new SolidColorBrush(Color.Parse("#dde2e7"));
    public IBrush NodeStroke { get; set; } = new SolidColorBrush(Color.Parse("#c3cad2"));
    public IBrush Ink { get; set; } = new SolidColorBrush(Color.Parse("#13202b"));
    public IBrush Ink2 { get; set; } = new SolidColorBrush(Color.Parse("#4b5b6b"));

    private void Layout()
    {
        _nodes = [];
        _edges = [];
        if (_flowgraph is null) return;
        var (blocks, edges, messages) = _flowgraph.Flatten();
        var layer = blocks.ToDictionary(b => b, _ => 0);
        for (var pass = 0; pass < blocks.Count; pass++)
        {
            var changed = false;
            foreach (var e in edges)
                if (layer[e.Destination.Block] < layer[e.Source.Block] + 1)
                {
                    layer[e.Destination.Block] = layer[e.Source.Block] + 1;
                    changed = true;
                }
            if (!changed) break;
        }
        var at = new Dictionary<Block, Point>();
        foreach (var g in blocks.GroupBy(b => layer[b]).OrderBy(g => g.Key))
        {
            var row = 0;
            foreach (var b in g)
            {
                var p = new Point(16 + g.Key * (NodeWidth + 60), 16 + row++ * (NodeHeight + 20));
                at[b] = p;
                _nodes.Add((b, p));
            }
        }
        foreach (var e in edges)
            _edges.Add((at[e.Source.Block] + new Point(NodeWidth, NodeHeight / 2), at[e.Destination.Block] + new Point(-2, 14 + e.Destination.Port * 12), false));
        foreach (var m in messages)
            _edges.Add((at[m.Source] + new Point(NodeWidth, NodeHeight / 2), at[m.Destination] + new Point(-2, NodeHeight / 2), true));
    }

    protected override Size MeasureOverride(Size availableSize) =>
        _nodes.Count == 0 ? new Size(400, 80) : new Size(_nodes.Max(n => n.At.X) + NodeWidth + 24, _nodes.Max(n => n.At.Y) + NodeHeight + 24);

    public override void Render(DrawingContext context)
    {
        var edge = new Pen(Ink2, 1.5);
        var message = new Pen(new SolidColorBrush(GrTheme.Accent), 1.5, new DashStyle([5, 4], 0));
        foreach (var (from, to, isMessage) in _edges)
        {
            var dx = Math.Max(30, (to.X - from.X) / 2);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(from, false);
                c.CubicBezierTo(new Point(from.X + dx, from.Y), new Point(to.X - dx, to.Y), to);
                c.EndFigure(false);
            }
            context.DrawGeometry(null, isMessage ? message : edge, g);
        }
        foreach (var (block, p) in _nodes)
        {
            context.DrawRectangle(NodeFill, new Pen(NodeStroke, 1), new Rect(p, new Size(NodeWidth, NodeHeight)), 6, 6);
            context.DrawText(new FormattedText(block.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, GrTheme.DataTypeface, 12, Ink), p + new Point(12, 8));
            context.DrawText(new FormattedText(block.Alias, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 11, Ink2), p + new Point(12, 28));
        }
    }
}
