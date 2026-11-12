using System.Globalization;
using AquaCulture.Gallery.Shared;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;

namespace AquaCulture.Gallery.Desktop;

/// <summary>Small code-first UI kit mirroring the Blazor gallery components.</summary>
internal static class Ui
{
    public static IBrush Brush(string key) => (IBrush)Application.Current!.Resources[key]!;
    public static FontFamily Font(string key) => (FontFamily)Application.Current!.Resources[key]!;
    public static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static TextBlock Text(string text, double size = 14, string? cls = null, IBrush? fg = null, FontWeight weight = FontWeight.Normal)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight };
        if (cls is not null) t.Classes.Add(cls);
        if (fg is not null) t.Foreground = fg;
        return t;
    }

    public static TextBlock Display(string text, double size) => Text(text, size, "display");
    public static TextBlock Eyebrow(string text) => Text(text.ToUpperInvariant(), 10.5, "mono");

    public static Border Panel(string title, Control content, string? sub = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Display(title, 24));
        if (sub is not null) head.Children.Add(new TextBlock { Text = sub.ToUpperInvariant(), FontSize = 10.5, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) });
        return new Border { Classes = { "panel" }, Child = new StackPanel { Children = { head, content } } };
    }

    public static Border Kpi(string label, string value, string? unit = null, string? note = null, string? tone = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var v = new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeight.Bold, FontFamily = Font("Mono") };
        v.Foreground = tone switch { "good" => Brush("Kelp"), "alert" => Brush("Coral"), "warn" => Brush("Sand"), _ => Brush("Ink") };
        row.Children.Add(v);
        if (unit is not null) row.Children.Add(new TextBlock { Text = unit, Classes = { "mono" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) });
        var sp = new StackPanel { Spacing = 3, Children = { Eyebrow(label), row } };
        if (note is not null) sp.Children.Add(Text(note, 12, "soft"));
        return new Border { Classes = { "panel" }, Padding = new Thickness(14, 10), Child = sp };
    }

    public static Control Kpis(params Control[] cards)
    {
        var g = new UniformGrid { Columns = Math.Min(cards.Length, 5), Margin = new Thickness(0, 0, -10, 4) };
        foreach (var c in cards) { c.Margin = new Thickness(0, 0, 10, 10); g.Children.Add(c); }
        return g;
    }

    public static Control Columns(int n, params Control[] items) => Columns(Enumerable.Repeat(1.0, n).ToArray(), items);

    public static Control Columns(double[] weights, params Control[] items)
    {
        var g = new Grid { ColumnSpacing = 14, RowSpacing = 14, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var w in weights) g.ColumnDefinitions.Add(new ColumnDefinition(w, GridUnitType.Star));
        int n = weights.Length, rows = (items.Length + n - 1) / n;
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

    public static Control Png(byte[] png, double? maxHeight = null, bool pixelated = true)
    {
        using var ms = new MemoryStream(png);
        var img = new Image { Source = new Bitmap(ms), Stretch = Stretch.Uniform };
        if (pixelated) RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.None);
        if (maxHeight is { } h) img.MaxHeight = h;
        return img;
    }

    /// <summary>Labelled slider that calls <paramref name="changed"/> when released or moved.</summary>
    public static Control Slider(string label, double min, double max, double value, double step, string fmt, Action<double> changed)
    {
        var output = new TextBlock { Text = value.ToString(fmt, CultureInfo.InvariantCulture), FontFamily = Font("Mono"), FontSize = 12, Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var s = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, TickFrequency = step, IsSnapToTickEnabled = true, Width = 150 };
        s.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            output.Text = s.Value.ToString(fmt, CultureInfo.InvariantCulture);
            changed(s.Value);
        };
        return new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 18, 0), Children = { Eyebrow(label), new StackPanel { Orientation = Orientation.Horizontal, Children = { s, output } } } };
    }

    public static Control Combo(string label, IReadOnlyList<(string Value, string Text)> items, string selected, Action<string> changed)
    {
        var c = new ComboBox { ItemsSource = items.Select(i => i.Text).ToList(), SelectedIndex = Math.Max(0, items.ToList().FindIndex(i => i.Value == selected)), MinWidth = 170, Margin = new Thickness(0, 4, 0, 0) };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(items[c.SelectedIndex].Value); };
        return new StackPanel { Margin = new Thickness(0, 0, 18, 0), Children = { Eyebrow(label), c } };
    }

    public static Border Controls(params Control[] items) => new()
    {
        Classes = { "panel" }, Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 14),
        Child = new WrapPanel { Orientation = Orientation.Horizontal, Children = { } }.With(w => { foreach (var i in items) { i.Margin = new Thickness(0, 4, 18, 4); w.Children.Add(i); } }),
    };

    /// <summary>Coloured-edge note row (decisions, alerts, actions).</summary>
    public static Control Note(string title, string? sub = null, string tone = "info", string? tag = null)
    {
        var edge = tone switch { "critical" => Brush("Coral"), "warn" => Brush("Sand"), "good" => Brush("Kelp"), _ => Brush("TealBright") };
        var sp = new StackPanel { Spacing = 2 };
        if (tag is not null) sp.Children.Add(Eyebrow(tag));
        sp.Children.Add(Text(title, 13.5, weight: FontWeight.SemiBold));
        if (sub is not null) sp.Children.Add(Text(sub, 12.5, "soft"));
        return new Border { Background = Brush("Paper"), BorderBrush = edge, BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 0, 6), Child = sp };
    }

    public static Control Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, int? numericFrom = 1)
    {
        var g = new Grid { ColumnSpacing = 10, RowSpacing = 5 };
        for (int c = 0; c < headers.Count; c++) g.ColumnDefinitions.Add(new ColumnDefinition(c == 0 ? GridLength.Star : GridLength.Auto));
        int r = 0;
        void Row(IReadOnlyList<string> cells, bool head)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cells.Count; c++)
            {
                var t = head ? Eyebrow(cells[c]) : new TextBlock { Text = cells[c], FontSize = 13, TextWrapping = TextWrapping.Wrap };
                if (!head && c >= (numericFrom ?? 99)) { t.FontFamily = Font("Mono"); t.TextAlignment = TextAlignment.Right; t.HorizontalAlignment = HorizontalAlignment.Right; }
                Grid.SetRow(t, r); Grid.SetColumn(t, c); g.Children.Add(t);
            }
            r++;
        }
        Row(headers, true);
        foreach (var row in rows) Row(row, false);
        return g;
    }

    public static Control Code(string text) => new Border
    {
        Background = Brush("CodeBg"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10),
        Child = new SelectableTextBlock { Text = text, FontFamily = Font("Mono"), FontSize = 12, Foreground = Hex("#d9efec"), TextWrapping = TextWrapping.Wrap },
    };

    public static Control Stack(params Control[] items) { var s = new StackPanel { Spacing = 0 }; foreach (var i in items) s.Children.Add(i); return s; }

    public static T With<T>(this T c, Action<T> a) { a(c); return c; }
    public static string N(double v, string f = "0.0") => Fmt.N(v, f);
}
