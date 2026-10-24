using Avalonia.Media;

namespace GnuRadio.Avalonia;

/// <summary>
/// Colours and fonts shared by the controls: dark instrument screens with amber/cyan traces, matching
/// GnuRadio.Net.Blazor. Assign different brushes before creating controls to re-theme.
/// </summary>
public static class GrTheme
{
    public static IBrush Screen { get; set; } = new SolidColorBrush(Color.Parse("#0b1820"));
    public static IBrush ScreenEdge { get; set; } = new SolidColorBrush(Color.Parse("#04090d"));
    public static IBrush ScreenText { get; set; } = new SolidColorBrush(Color.Parse("#8fb3c2"));
    public static IBrush Title { get; set; } = new SolidColorBrush(Color.Parse("#d6e6ec"));
    public static Color Trace { get; set; } = Color.Parse("#ffb23f");
    public static Color Trace2 { get; set; } = Color.Parse("#4fd1e8");
    public static Color Marker { get; set; } = Color.Parse("#ff5c8a");
    public static Color Trace4 { get; set; } = Color.Parse("#9be564");
    public static Color Accent { get; set; } = Color.Parse("#e8611a");
    public static IPen Grid { get; set; } = new Pen(new SolidColorBrush(Color.FromArgb(34, 120, 170, 190)), 1);
    public static IPen GridStrong { get; set; } = new Pen(new SolidColorBrush(Color.FromArgb(82, 120, 170, 190)), 1);

    /// <summary>Monospace face for axis labels and readouts.</summary>
    public static FontFamily DataFont { get; set; } = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>Face of the frequency readout digits.</summary>
    public static FontFamily DisplayFont { get; set; } = new("Bahnschrift, Segoe UI, Inter, sans-serif");

    public static Color[] Palette => [Trace, Trace2, Marker, Trace4, Color.Parse("#c792ea"), Color.Parse("#f78c6c")];

    internal static Typeface DataTypeface => new(DataFont);
}
