using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GDAL.Net.Gallery.Avalonia.Controls;

/// <summary>
/// The gallery's signature: contour lines of the sample DEM, computed by GDAL.Net at startup
/// (<see cref="Branding.SignatureContours"/>), drawn as a topographic sheet behind the header.
/// Index contours (every <see cref="IndexInterval"/> m) are drawn in the accent color.
/// </summary>
public sealed class ContourHeader : Control
{
    public static readonly StyledProperty<IReadOnlyList<(double Elevation, double[] Points)>?> ContoursProperty =
        AvaloniaProperty.Register<ContourHeader, IReadOnlyList<(double Elevation, double[] Points)>?>(nameof(Contours));
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<ContourHeader, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> IndexBrushProperty = AvaloniaProperty.Register<ContourHeader, IBrush?>(nameof(IndexBrush));
    public static readonly StyledProperty<double> IndexIntervalProperty = AvaloniaProperty.Register<ContourHeader, double>(nameof(IndexInterval), 300);
    /// <summary>Vertical focus of the sheet in [0,1] (0 = north edge of the DEM).</summary>
    public static readonly StyledProperty<double> FocusYProperty = AvaloniaProperty.Register<ContourHeader, double>(nameof(FocusY), 0.22);

    public IReadOnlyList<(double Elevation, double[] Points)>? Contours { get => GetValue(ContoursProperty); set => SetValue(ContoursProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? IndexBrush { get => GetValue(IndexBrushProperty); set => SetValue(IndexBrushProperty, value); }
    public double IndexInterval { get => GetValue(IndexIntervalProperty); set => SetValue(IndexIntervalProperty, value); }
    public double FocusY { get => GetValue(FocusYProperty); set => SetValue(FocusYProperty, value); }

    private StreamGeometry? _minor;
    private StreamGeometry? _major;

    static ContourHeader()
    {
        AffectsRender<ContourHeader>(ContoursProperty, LineBrushProperty, IndexBrushProperty, FocusYProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ContourHeader>(true);
        IsHitTestVisibleProperty.OverrideDefaultValue<ContourHeader>(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContoursProperty || change.Property == IndexIntervalProperty)
        {
            _minor = _major = null;
        }
    }

    private void Build()
    {
        if (Contours is not { } lines) return;
        _minor = new StreamGeometry();
        _major = new StreamGeometry();
        using var minor = _minor.Open();
        using var major = _major.Open();
        foreach (var (elev, pts) in lines)
        {
            if (pts.Length < 4) continue;
            var ctx = Math.Abs(elev % IndexInterval) < 1e-6 ? major : minor;
            ctx.BeginFigure(new Point(pts[0], pts[1]), false);
            for (var i = 2; i + 1 < pts.Length; i += 2) ctx.LineTo(new Point(pts[i], pts[i + 1]));
            ctx.EndFigure(false);
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Contours is null) return;
        if (_minor is null) Build();
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        // Cover the width; the DEM is 4:3, so scale Y by the same factor * 0.75 and center on FocusY.
        var sx = w;
        var sy = w * 0.75;
        var offsetY = h / 2 - FocusY * sy;
        var matrix = new Matrix(sx, 0, 0, sy, 0, offsetY);
        using (context.PushTransform(matrix))
        {
            var thin = new Pen(LineBrush ?? Brushes.Gray, 0.8 / sx);
            var thick = new Pen(IndexBrush ?? Brushes.Sienna, 1.4 / sx);
            context.DrawGeometry(null, thin, _minor!);
            context.DrawGeometry(null, thick, _major!);
        }
    }
}
