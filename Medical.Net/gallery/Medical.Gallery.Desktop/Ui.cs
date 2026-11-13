using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Medical.Visualization;

namespace Medical.Gallery.Desktop;

/// <summary>Small code-first UI kit mirroring the Blazor gallery components.</summary>
internal static class Ui
{
    public static IBrush Brush(string key) => (IBrush)Application.Current!.Resources[key]!;
    public static FontFamily Font(string key) => (FontFamily)Application.Current!.Resources[key]!;

    public static TextBlock Text(string text, double size = 14.5, string? cls = null, IBrush? fg = null, FontWeight weight = FontWeight.Normal)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight };
        if (cls is not null) t.Classes.Add(cls);
        if (fg is not null) t.Foreground = fg;
        return t;
    }

    public static TextBlock Display(string text, double size) => Text(text, size, "display");
    public static TextBlock Eyebrow(string text) => Text(text.ToUpperInvariant(), 12, "eyebrow");
    public static TextBlock Mono(string text, double size = 13) => Text(text, size, "mono");

    public static Border Panel(string? title, Control content, string? sub = null)
    {
        var sp = new StackPanel { Spacing = 6 };
        if (title is not null)
        {
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(Display(title, 21));
            if (sub is not null) head.Children.Add(new TextBlock { Text = sub, FontSize = 12.5, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 3) });
            sp.Children.Add(head);
        }
        sp.Children.Add(content);
        return new Border { Classes = { "panel" }, Child = sp };
    }

    public static StackPanel Stack(double spacing, params Control[] items)
    {
        var sp = new StackPanel { Spacing = spacing };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    /// <summary>Columns with star widths (e.g. 3,2).</summary>
    public static Grid Columns(double[] stars, params Control[] items)
    {
        var g = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        foreach (var s in stars) g.ColumnDefinitions.Add(new ColumnDefinition(s, GridUnitType.Star));
        int n = stars.Length, rows = (items.Length + n - 1) / n;
        for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < items.Length; i++) { Grid.SetColumn(items[i], i % n); Grid.SetRow(items[i], i / n); g.Children.Add(items[i]); }
        return g;
    }

    public static Control SvgView(string svg, double? maxWidth = null)
    {
        var img = new Image { Source = new SvgImage { Source = SvgSource.LoadFromSvg(SvgTheme.Resolve(svg)) }, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        if (maxWidth is { } w) img.MaxWidth = w;
        return img;
    }

    public static Control Png(byte[] png, double size)
    {
        using var ms = new MemoryStream(png);
        return new Image { Source = new Bitmap(ms), Stretch = Stretch.Uniform, Width = size, Height = size };
    }

    public static Border Readout(string key, string value, string? unit, string note)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(Mono(value, 32));
        if (unit is not null) row.Children.Add(new TextBlock { Text = unit, FontSize = 13, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) });
        return new Border { Classes = { "panel" }, Child = Stack(2, Eyebrow(key), row, Text(note, 13.5, "muted")) };
    }

    public static Control Table(string[] headers, IEnumerable<string[]> rows, params int[] mono)
    {
        var g = new Grid { ColumnSpacing = 14 };
        foreach (var _ in headers) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.ColumnDefinitions[^1] = new ColumnDefinition(GridLength.Star);
        int r = 0;
        void Add(string[] cells, bool head)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cells.Length; c++)
            {
                var t = head ? Eyebrow(cells[c]) : mono.Contains(c) ? Mono(cells[c]) : Text(cells[c], 13.5);
                t.Margin = new Thickness(0, 5);
                Grid.SetRow(t, r); Grid.SetColumn(t, c); g.Children.Add(t);
            }
            var line = new Border { Height = 1, Background = Brush("Rule"), VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, cells.Length); g.Children.Add(line);
            r++;
        }
        Add(headers, true);
        foreach (var row in rows) Add(row, false);
        return g;
    }

    public static Control Slider(string label, double min, double max, double value, double step, Action<double> changed, Func<double, string> fmt)
    {
        var output = Mono(fmt(value));
        output.VerticalAlignment = VerticalAlignment.Center;
        var s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, Width = 200 };
        s.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            output.Text = fmt(s.Value);
            changed(s.Value);
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Text(label, 13.5, "muted").Also(t => t.VerticalAlignment = VerticalAlignment.Center), s, output } };
    }

    public static Control Segmented<T>(IEnumerable<(T Value, string Label)> options, T current, Action<T> picked)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (v, label) in options)
        {
            var b = new ToggleButton { Content = label, IsChecked = EqualityComparer<T>.Default.Equals(v, current), Classes = { "seg" } };
            b.Click += (_, _) => picked(v);
            sp.Children.Add(b);
        }
        return new Border { BorderBrush = Brush("Rule"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = sp };
    }

    public static Control Controls(params Control[] items)
    {
        var wp = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) { i.Margin = new Thickness(0, 4, 24, 4); wp.Children.Add(i); }
        return new Border { Classes = { "panel" }, Padding = new Thickness(14, 8), Child = wp };
    }

    public static Control Alert(string tone, string tag, string title, string body)
    {
        var color = tone switch { "critical" => "Red", "warning" => "Amber", _ => "Teal" };
        return new Border
        {
            BorderBrush = Brush(color), BorderThickness = new Thickness(4, 0, 0, 0), Background = Brush("Paper"),
            Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(0, 8, 8, 0),
            Child = Stack(2, Mono(tag, 11.5).Also(t => t.Foreground = Brush("Muted")), Text(title, 14.5, weight: FontWeight.Bold), Text(body, 13.5, "muted")),
        };
    }

    public static Border Pill(string text, string tone)
    {
        var (bg, fg) = tone switch
        {
            "high" or "critical" => (Brush("Red"), Brushes.White),
            "medium" or "lowmedium" or "moderate" => (Brush("Score1"), (IBrush)new SolidColorBrush(Color.Parse("#7A4B00"))),
            _ => (Brush("TealSoft"), Brush("Teal")),
        };
        return new Border
        {
            Background = bg, CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 2), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text.ToUpperInvariant(), Foreground = fg, FontFamily = Font("Display"), FontWeight = FontWeight.SemiBold, FontSize = 12.5, LetterSpacing = 1 },
        };
    }

    public static Control Code(string code) => new Border
    {
        Background = new SolidColorBrush(Color.Parse("#14233A")), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12),
        Child = new SelectableTextBlock { Text = code, FontFamily = Font("Mono"), FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#E6EDF3")), TextWrapping = TextWrapping.NoWrap },
    };

    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
