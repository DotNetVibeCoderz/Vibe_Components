using Agritech.Gallery.Shared;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;

namespace Agritech.Gallery.Desktop;

/// <summary>Small code-first UI kit mirroring the Blazor gallery components.</summary>
internal static class Ui
{
    public static IBrush Brush(string key) => (IBrush)Application.Current!.Resources[key]!;
    public static FontFamily Font(string key) => (FontFamily)Application.Current!.Resources[key]!;

    public static TextBlock Text(string text, double size = 14, string? cls = null, IBrush? fg = null, FontWeight weight = FontWeight.Normal)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight };
        if (cls is not null) t.Classes.Add(cls);
        if (fg is not null) t.Foreground = fg;
        return t;
    }

    public static TextBlock Display(string text, double size) => Text(text.ToUpperInvariant(), size, "display");
    public static TextBlock Eyebrow(string text) => Text(text.ToUpperInvariant(), 11, "mono");

    public static Border Panel(string title, Control content, string? sub = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Display(title, 22));
        if (sub is not null) head.Children.Add(new TextBlock { Text = sub, FontSize = 11, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) });
        return new Border { Classes = { "panel" }, Child = new StackPanel { Children = { head, content } } };
    }

    public static Border Kpi(string label, string value, string? unit = null, string? note = null, string? tone = null)
    {
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var v = Display(value, 34);
        v.Foreground = tone switch { "good" => Brush("Chlorophyll"), "alert" => Brush("Lesion"), _ => Brush("Ink") };
        valueRow.Children.Add(v);
        if (unit is not null) valueRow.Children.Add(new TextBlock { Text = unit, Classes = { "mono" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) });
        var sp = new StackPanel { Spacing = 2, Children = { Eyebrow(label), valueRow } };
        if (note is not null) sp.Children.Add(Text(note, 11.5, "soft"));
        return new Border { Classes = { "panel" }, Padding = new Thickness(14, 10), Child = sp };
    }

    /// <summary>Row of KPI cards that wraps.</summary>
    public static Control Kpis(params Control[] cards)
    {
        var g = new UniformGrid { Columns = Math.Min(cards.Length, 5) };
        foreach (var c in cards) { c.Margin = new Thickness(0, 0, 10, 10); g.Children.Add(c); }
        return g;
    }

    public static Control Columns(int n, params Control[] items)
    {
        var g = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        for (int i = 0; i < n; i++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        int rows = (items.Length + n - 1) / n;
        for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < items.Length; i++) { Grid.SetColumn(items[i], i % n); Grid.SetRow(items[i], i / n); g.Children.Add(items[i]); }
        return g;
    }

    public static Control SvgView(string svg, double? maxHeight = null)
    {
        var img = new Image { Source = new SvgImage { Source = SvgSource.LoadFromSvg(SvgTheme.Resolve(svg)) }, Stretch = Stretch.Uniform };
        if (maxHeight is { } h) img.MaxHeight = h;
        return img;
    }

    public static Control Png(byte[] png, double? maxHeight = null)
    {
        using var ms = new MemoryStream(png);
        var img = new Image { Source = new Bitmap(ms), Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        if (maxHeight is { } h) img.MaxHeight = h;
        return img;
    }

    /// <summary>Simple data table (header + rows) built from a Grid.</summary>
    public static Control Table(string[] headers, IEnumerable<string[]> rows, params int[] numericColumns)
    {
        var g = new Grid { ColumnSpacing = 12 };
        foreach (var _ in headers) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.ColumnDefinitions[^1] = new ColumnDefinition(GridLength.Star);
        int r = 0;
        void AddRow(string[] cells, bool head)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cells.Length; c++)
            {
                var t = head ? Eyebrow(cells[c]) : Text(cells[c], 13);
                if (!head && numericColumns.Contains(c)) { t.FontFamily = Font("Mono"); t.HorizontalAlignment = HorizontalAlignment.Right; }
                t.Margin = new Thickness(0, 5);
                Grid.SetRow(t, r); Grid.SetColumn(t, c); g.Children.Add(t);
            }
            var line = new Border { Height = 1, Background = Brush("Rule"), VerticalAlignment = VerticalAlignment.Bottom, Opacity = head ? 1 : 0.6 };
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, cells.Length); g.Children.Add(line);
            r++;
        }
        AddRow(headers, true);
        foreach (var row in rows) AddRow(row, false);
        return g;
    }

    /// <summary>Labelled slider that invokes a callback when released or changed.</summary>
    public static Control Slider(string label, double min, double max, double value, double step, Action<double> changed, string format = "0.##")
    {
        var output = new TextBlock { Text = value.ToString(format), FontFamily = Font("Mono"), FontSize = 13 };
        var s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, Width = 170 };
        s.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            output.Text = s.Value.ToString(format);
            changed(s.Value);
        };
        return new StackPanel { Spacing = 0, Children = { Eyebrow(label), s, output } };
    }

    public static Control Controls(params Control[] items)
    {
        var wp = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) { i.Margin = new Thickness(0, 0, 22, 0); wp.Children.Add(i); }
        return new Border
        {
            BorderBrush = Brush("Rule"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 0, 0, 14), Child = wp,
        };
    }

    public static Control Decision(string title, string body, string tone)
    {
        var color = tone switch { "Irrigate" => "#2F7DA0", "ScoutDisease" => "#A5432F", "Harvest" => "#2E6B3B", _ => "#B5762E" };
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(color)), BorderThickness = new Thickness(3, 0, 0, 0), Background = Brush("Paper"),
            Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(0, 6, 6, 0),
            Child = new StackPanel { Children = { Text(title, 13.5, weight: FontWeight.SemiBold), Text(body, 12.5, "soft") } },
        };
    }

    private static GradientStops Stops(string ramp)
    {
        var gs = new GradientStops();
        foreach (var (stop, hex) in Ramps.Get(ramp)) gs.Add(new GradientStop(Color.Parse(hex), stop));
        return gs;
    }

    /// <summary>Vertical gradient bar painted with a demo's data ramp (the gallery's "spectral rail").</summary>
    public static Border Rail(string ramp, double width = 6) => new()
    {
        Width = width, CornerRadius = new CornerRadius(3),
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = Stops(ramp),
        },
    };
}

internal static partial class UiExt { }

internal static class Ramps
{
    public static (double Stop, string Hex)[] Get(string name) => name switch
    {
        "water" => [(0, "#123f6b"), (0.3, "#2f8fb5"), (0.65, "#9fd3d6"), (1, "#f3ead7")],
        "heat" => [(0, "#a8281f"), (0.2, "#e8833a"), (0.4, "#f2d16b"), (0.65, "#9ec5a8"), (1, "#2b4c7e")],
        "soil" => [(0, "#3b2a1d"), (0.25, "#6b4a2f"), (0.5, "#9a6b3f"), (0.75, "#c49a5a"), (1, "#e2cc9c")],
        "change" => [(0, "#2e7d4a"), (0.5, "#f4f1e8"), (1, "#b2432f")],
        _ => [(0, "#0b3d1a"), (0.15, "#1f6f2b"), (0.3, "#4f9f3a"), (0.45, "#a6cf5d"), (0.6, "#e9d98b"), (0.75, "#c49a5a"), (1, "#7a4b2a")],
    };
}
