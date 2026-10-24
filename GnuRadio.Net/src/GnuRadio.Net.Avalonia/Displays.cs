using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GnuRadio.Sinks;

namespace GnuRadio.Avalonia;

/// <summary>Spectrum analyser display for a <see cref="FrequencySink"/>. Click to tune (<see cref="Tuned"/>).</summary>
public sealed class SpectrumView : GrDisplay
{
    private FrequencySink? _sink;
    private long _version = -1;
    private float[] _spectrum = [];
    private float[] _hold = [];

    public FrequencySink? Sink
    {
        get => _sink;
        set
        {
            _sink = value;
            _version = -1;
            InvalidateVisual();
        }
    }

    public float MinDb { get; set; } = -100;
    public float MaxDb { get; set; }
    public bool ShowMaxHold { get; set; }

    /// <summary>Polled for live markers at absolute frequencies (Hz).</summary>
    public Func<IReadOnlyList<(double Frequency, string Label)>>? Markers { get; set; }

    /// <summary>Raised with the clicked frequency in Hz.</summary>
    public event Action<double>? Tuned;

    protected override bool Poll()
    {
        if (_sink is null) return false;
        if (_spectrum.Length != _sink.FftSize)
        {
            _spectrum = new float[_sink.FftSize];
            _hold = new float[_sink.FftSize];
        }
        var changed = _sink.TryCopy(ref _version, _spectrum, ShowMaxHold ? _hold : default);
        if (changed) Meta = $"{Axis.FormatFrequency(_sink.CenterFrequency, 3)} · span {Axis.FormatFrequency(_sink.SampleRate, 1)}";
        return changed;
    }

    protected override void RenderPlot(DrawingContext context, Rect plot)
    {
        if (_sink is null) return;
        var start = _sink.CenterFrequency - _sink.SampleRate / 2;
        var end = start + _sink.SampleRate;
        DrawGrid(context, plot, Ticks.Frequency(start, end), Ticks.Linear(MinDb, MaxDb, 6, " dB"));
        if (ShowMaxHold) DrawTrace(context, plot, _hold, MinDb, MaxDb, Color.FromArgb(170, GrTheme.Trace2.R, GrTheme.Trace2.G, GrTheme.Trace2.B), false);
        DrawTrace(context, plot, _spectrum, MinDb, MaxDb, GrTheme.Trace, true);
        if (Markers?.Invoke() is { } markers)
            DrawMarkers(context, plot, markers.Select(m => ((m.Frequency - start) / (end - start), (string?)m.Label)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_sink is null || Tuned is null) return;
        var plot = PlotRect;
        var x = (e.GetPosition(this).X - plot.X) / plot.Width;
        if (x is >= 0 and <= 1) Tuned(_sink.CenterFrequency - _sink.SampleRate / 2 + x * _sink.SampleRate);
    }
}

/// <summary>Scrolling waterfall for a <see cref="WaterfallSink"/>, rendered into a bitmap that shifts down per row.</summary>
public sealed class WaterfallView : GrDisplay
{
    private WaterfallSink? _sink;
    private long _cursor;
    private float[] _rows = [];
    private byte[] _rgba = [];
    private uint[] _pixels = [];
    private WriteableBitmap? _bitmap;

    public WaterfallSink? Sink
    {
        get => _sink;
        set
        {
            _sink = value;
            _cursor = value is null ? 0 : Math.Max(0, value.RowCount - value.History);
            _bitmap = null;
            InvalidateVisual();
        }
    }

    public float MinDb { get; set; } = -100;
    public float MaxDb { get; set; } = -20;
    public Colormap Colormap { get; set; } = Colormap.Phosphor;
    public Func<IReadOnlyList<(double Frequency, string Label)>>? Markers { get; set; }
    public event Action<double>? Tuned;

    protected override Thickness PlotMargin => new(26, Title is null ? 10 : 30, 10, 22);

    protected override bool Poll()
    {
        if (_sink is null) return false;
        var bins = _sink.FftSize;
        var history = _sink.History;
        if (_bitmap is null || _bitmap.PixelSize.Width != bins || _bitmap.PixelSize.Height != history)
        {
            _bitmap = new WriteableBitmap(new PixelSize(bins, history), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _pixels = new uint[bins * history];
            Array.Fill(_pixels, 0xFF0B1820u); // the screen colour as BGRA
            _rows = new float[bins * history];
            _rgba = new byte[bins * 4];
        }
        var count = _sink.ReadNewRows(ref _cursor, _rows);
        if (count == 0) return false;
        // Shift existing rows down and write the new ones (newest at the top).
        var keep = Math.Max(0, history - count);
        Array.Copy(_pixels, 0, _pixels, (long)(history - keep) * bins, (long)keep * bins);
        for (var r = 0; r < Math.Min(count, history); r++)
        {
            Colormap.MapRow(_rows.AsSpan((count - 1 - r) * bins, bins), MinDb, MaxDb, _rgba);
            for (var i = 0; i < bins; i++)
            {
                // RGBA → BGRA
                _pixels[r * bins + i] = 0xFF000000u | ((uint)_rgba[4 * i] << 16) | ((uint)_rgba[4 * i + 1] << 8) | _rgba[4 * i + 2];
            }
        }
        CopyToBitmap(_bitmap, _pixels, bins, history);
        return true;
    }

    private static unsafe void CopyToBitmap(WriteableBitmap bitmap, uint[] pixels, int width, int height)
    {
        using var fb = bitmap.Lock();
        fixed (uint* src = pixels)
        {
            // Rows may be padded, so copy row by row.
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(src + (long)y * width, (byte*)fb.Address + (long)y * fb.RowBytes, fb.RowBytes, width * 4L);
        }
    }

    protected override void RenderPlot(DrawingContext context, Rect plot)
    {
        if (_sink is null || _bitmap is null) return;
        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), plot);
        var start = _sink.CenterFrequency - _sink.SampleRate / 2;
        var end = start + _sink.SampleRate;
        DrawGrid(context, plot, Ticks.Frequency(start, end), []);
        if (Markers?.Invoke() is { } markers)
            DrawMarkers(context, plot, markers.Select(m => ((m.Frequency - start) / (end - start), (string?)m.Label)), dashed: true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_sink is null || Tuned is null) return;
        var plot = PlotRect;
        var x = (e.GetPosition(this).X - plot.X) / plot.Width;
        if (x is >= 0 and <= 1) Tuned(_sink.CenterFrequency - _sink.SampleRate / 2 + x * _sink.SampleRate);
    }
}

/// <summary>Constellation scatter plot with reference points for a <see cref="ConstellationSink"/>.</summary>
public sealed class ConstellationView : GrDisplay
{
    private ConstellationSink? _sink;
    private long _version = -1;
    private Complex32[] _points = [];

    public ConstellationSink? Sink
    {
        get => _sink;
        set
        {
            _sink = value;
            _version = -1;
        }
    }

    public float Scale { get; set; } = 1.5f;
    public IReadOnlyList<Complex32>? Reference { get; set; }

    protected override bool Poll()
    {
        if (_sink is null) return false;
        if (_points.Length != _sink.Size) _points = new Complex32[_sink.Size];
        return _sink.TryCopy(ref _version, _points);
    }

    protected override void RenderPlot(DrawingContext context, Rect plot)
    {
        var size = Math.Min(plot.Width + 38, plot.Height) - 8;
        var square = new Rect(Bounds.Width / 2 - size / 2, plot.Y + (plot.Height - size) / 2, size, size);
        double X(float v) => square.X + (v / Scale * 0.5 + 0.5) * square.Width;
        double Y(float v) => square.Y + (0.5 - v / Scale * 0.5) * square.Height;
        for (var k = -2; k <= 2; k++)
        {
            var t = k / 2f * Scale;
            context.DrawLine(GrTheme.Grid, new Point(X(t), square.Y), new Point(X(t), square.Bottom));
            context.DrawLine(GrTheme.Grid, new Point(square.X, Y(t)), new Point(square.Right, Y(t)));
        }
        context.DrawRectangle(null, GrTheme.GridStrong, square);
        if (Reference is not null)
        {
            var ring = new Pen(new SolidColorBrush(GrTheme.Trace2), 1);
            foreach (var p in Reference) context.DrawEllipse(null, ring, new Point(X(p.Real), Y(p.Imaginary)), 5, 5);
        }
        var dot = new SolidColorBrush(GrTheme.Trace);
        foreach (var p in _points)
        {
            var x = X(p.Real);
            var y = Y(p.Imaginary);
            if (x < square.X || x > square.Right || y < square.Y || y > square.Bottom) continue;
            context.FillRectangle(dot, new Rect(x - 0.9, y - 0.9, 1.8, 1.8));
        }
        DrawText(context, "I", new Point(square.Right - 10, Y(0) + 3), GrTheme.ScreenText);
        DrawText(context, "Q", new Point(X(0) + 4, square.Y + 3), GrTheme.ScreenText);
    }
}

/// <summary>Oscilloscope for any <see cref="TimeSink{T}"/>.</summary>
public sealed class TimeScope : GrDisplay
{
    private ITimeSinkView? _sink;
    private long _version = -1;
    private float[][] _traces = [];

    public ITimeSinkView? Sink
    {
        get => _sink;
        set
        {
            _sink = value;
            _version = -1;
            _traces = [];
        }
    }

    public float Min { get; set; } = -1.2f;
    public float Max { get; set; } = 1.2f;
    public int[]? Traces { get; set; }

    protected override bool Poll()
    {
        if (_sink is null) return false;
        if (_traces.Length != _sink.TraceCount) _traces = Enumerable.Range(0, _sink.TraceCount).Select(_ => new float[_sink.Size]).ToArray();
        return _sink.TryCopy(ref _version, _traces);
    }

    protected override void RenderPlot(DrawingContext context, Rect plot)
    {
        if (_sink is null) return;
        DrawGrid(context, plot, Ticks.Time(_sink.Size / _sink.SampleRate), Ticks.Linear(Min, Max, 4));
        var shown = Traces ?? Enumerable.Range(0, _traces.Length).ToArray();
        var palette = GrTheme.Palette;
        var range = Max - Min;
        for (var s = 0; s < shown.Length; s++)
        {
            var data = _traces[shown[s]];
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (var i = 0; i < data.Length; i++)
                {
                    var p = new Point(plot.X + i * plot.Width / Math.Max(1, data.Length - 1), plot.Y + (1 - (data[i] - Min) / range) * plot.Height);
                    if (i == 0) g.BeginFigure(p, false);
                    else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(palette[s % palette.Length]), 1.25), geometry);
            if (shown.Length > 1)
                DrawText(context, _sink.TraceName(shown[s]), new Point(plot.Right - 6, plot.Y + 4 + s * 13), new SolidColorBrush(palette[s % palette.Length]), alignment: TextAlignment.Right);
        }
    }
}

/// <summary>Plots a float vector: a <see cref="VectorFrameSink"/> or a polled array.</summary>
public sealed class VectorView : GrDisplay
{
    private VectorFrameSink? _sink;
    private long _version = -1;
    private float[] _data = [];
    private float[]? _last;

    public VectorFrameSink? Sink
    {
        get => _sink;
        set
        {
            _sink = value;
            _version = -1;
        }
    }

    public Func<float[]?>? DataSource { get; set; }
    public float Min { get; set; } = -100;
    public float Max { get; set; }
    public double XStart { get; set; }
    public double XEnd { get; set; } = 1;
    public string XUnit { get; set; } = "";
    public string YUnit { get; set; } = "";
    public Func<IReadOnlyList<(double X, string Label)>>? Markers { get; set; }

    protected override bool Poll()
    {
        if (_sink is not null)
        {
            if (_data.Length != _sink.Length) _data = new float[_sink.Length];
            return _sink.TryCopy(ref _version, _data);
        }
        var d = DataSource?.Invoke();
        if (d is null || ReferenceEquals(d, _last)) return false;
        _last = d;
        _data = d;
        return true;
    }

    protected override void RenderPlot(DrawingContext context, Rect plot)
    {
        DrawGrid(context, plot, Ticks.Linear(XStart, XEnd, 8, XUnit), Ticks.Linear(Min, Max, 5, YUnit));
        DrawTrace(context, plot, _data, Min, Max, GrTheme.Trace, true);
        if (Markers?.Invoke() is { } markers)
            DrawMarkers(context, plot, markers.Select(m => ((m.X - XStart) / (XEnd - XStart), (string?)m.Label)));
    }
}

/// <summary>A line of a <see cref="LineChart"/>.</summary>
public sealed record LineSeries(string Name, double[] X, double[] Y, bool Dashed = false, bool Points = false);

/// <summary>XY line chart with optional logarithmic Y axis (BER curves, filter responses).</summary>
public sealed class LineChart : GrDisplay
{
    private int _drawn = -1;

    public Func<IReadOnlyList<LineSeries>> Series { get; set; } = () => [];
    public Func<int> Revision { get; set; } = () => 0;
    public double XMin { get; set; }
    public double XMax { get; set; } = 1;
    public double YMin { get; set; }
    public double YMax { get; set; } = 1;
    public bool LogY { get; set; }
    public string XUnit { get; set; } = "";
    public string YUnit { get; set; } = "";

    protected override Thickness PlotMargin => new(58, Title is null ? 10 : 30, 10, 22);

    protected override bool Poll()
    {
        var r = Revision();
        if (r == _drawn) return false;
        _drawn = r;
        return true;
    }

    protected override void RenderPlot(DrawingContext context, Rect plot)
    {
        DrawGrid(context, plot, Ticks.Linear(XMin, XMax, 8, XUnit), LogY ? Ticks.LogDecades(YMin, YMax) : Ticks.Linear(YMin, YMax, 5, YUnit));
        double T(double v) => LogY ? Math.Log10(Math.Max(v, 1e-30)) : v;
        var y0 = T(YMin);
        var y1 = T(YMax);
        var palette = GrTheme.Palette;
        var series = Series();
        for (var s = 0; s < series.Count; s++)
        {
            var line = series[s];
            var brush = new SolidColorBrush(palette[s % palette.Length]);
            var pen = new Pen(brush, 1.6, line.Dashed ? new DashStyle([5, 4], 0) : null);
            var geometry = new StreamGeometry();
            var points = new List<Point>();
            using (var g = geometry.Open())
            {
                var started = false;
                for (var i = 0; i < line.X.Length; i++)
                {
                    if (LogY && !(line.Y[i] > 0)) continue;
                    var p = new Point(plot.X + (line.X[i] - XMin) / (XMax - XMin) * plot.Width, plot.Y + (1 - (T(line.Y[i]) - y0) / (y1 - y0)) * plot.Height);
                    points.Add(p);
                    if (!started)
                    {
                        g.BeginFigure(p, false);
                        started = true;
                    }
                    else
                    {
                        g.LineTo(p);
                    }
                }
                if (started) g.EndFigure(false);
            }
            using (context.PushClip(plot)) context.DrawGeometry(null, pen, geometry);
            if (line.Points)
                foreach (var p in points.Where(plot.Contains)) context.DrawEllipse(brush, null, p, 2.6, 2.6);
            DrawText(context, line.Name, new Point(plot.Right - 8, plot.Y + 6 + s * 14), brush, 11, TextAlignment.Right);
        }
    }
}
