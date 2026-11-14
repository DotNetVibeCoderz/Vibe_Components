using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Electrical.Gallery.Shared;
using Electrical.Visualization;

namespace Electrical.Gallery.Desktop;

/// <summary>Small code-first UI kit mirroring the Blazor gallery components (panels, annunciator, segmented controls, tables).</summary>
internal static partial class Ui
{
    public static string Lang { get; set; } = "en";
    public static string T(string en, string id) => Lang == "id" ? id : en;
    public static string T(L text) => text[Lang];
    public static string F(double v, string fmt = "0.0") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    public static FontFamily Font(string key) => (FontFamily)Application.Current!.Resources[key]!;

    public static TextBlock Text(string text, double size = 14, IBrush? fg = null, FontWeight weight = FontWeight.Normal, string font = "Body", bool wrap = true) => new()
    {
        Text = text, FontSize = size, Foreground = fg ?? Pal.Ink, FontWeight = weight, FontFamily = Font(font),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Display(string text, double size, IBrush? fg = null) =>
        Text(text.ToUpperInvariant(), size, fg, FontWeight.Bold, "Display");

    public static TextBlock Eyebrow(string text, IBrush? fg = null) =>
        new() { Text = text.ToUpperInvariant(), FontSize = 10.5, FontFamily = Font("Mono"), LetterSpacing = 1.4, Foreground = fg ?? Pal.Ink3, FontWeight = FontWeight.SemiBold };

    public static TextBlock Mono(string text, double size = 12.5, IBrush? fg = null) =>
        new() { Text = text, FontSize = size, FontFamily = Font("Mono"), Foreground = fg ?? Pal.Ink2, TextWrapping = TextWrapping.Wrap };

    public static StackPanel Stack(double spacing, params Control[] items)
    {
        var sp = new StackPanel { Spacing = spacing };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    public static StackPanel Row(double spacing, params Control[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    /// <summary>Instrument panel with an engraved title plate.</summary>
    public static Border Panel(string title, Control content, string? meta = null)
    {
        var head = new DockPanel { Margin = new Thickness(16, 12) };
        if (meta is not null)
        {
            var m = Mono(meta, 11.5, Pal.Ink3);
            m.TextWrapping = TextWrapping.NoWrap; m.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(m, Dock.Right);
            head.Children.Add(m);
        }
        var t = Display(title, 16);
        t.LetterSpacing = 0.8;
        head.Children.Add(t);
        return new Border
        {
            Background = Pal.Panel, BorderBrush = Pal.Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(new Border { BorderBrush = Pal.Rule, BorderThickness = new Thickness(0, 0, 0, 1), Child = head }, Avalonia.Controls.Dock.Top),
                    new Border { Padding = new Thickness(16, 14), Child = content },
                },
            },
        };
    }

    private static Control Docked(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }

    /// <summary>Annunciator strip: one window per key figure with a status lamp along the top edge.</summary>
    public static Control Annunciator(IReadOnlyList<Kpi> items, int? columns = null)
    {
        var g = new UniformGrid { Columns = columns ?? Math.Min(items.Count, 6) };
        foreach (var k in items)
        {
            var value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            value.Children.Add(Text(k.Value, 26, k.Tone == "alarm" ? Pal.Red : Pal.Ink, FontWeight.Bold, "Display", wrap: false));
            if (k.Unit is not null)
            {
                var u = Mono(k.Unit, 12, Pal.Ink3);
                u.VerticalAlignment = VerticalAlignment.Bottom; u.Margin = new Thickness(0, 0, 0, 5);
                value.Children.Add(u);
            }
            var body = new StackPanel { Spacing = 3, Margin = new Thickness(16, 14, 12, 12), Children = { Eyebrow(T(k.Label)), value } };
            if (k.Note is { } n) body.Children.Add(Text(n[Lang], 11.5, Pal.Ink3, wrap: false));
            var tile = new Grid
            {
                Background = k.Tone is "alarm" or "warn" ? Pal.Glow(k.Tone) : Pal.Panel,
                Children = { new Border { Height = 3, VerticalAlignment = VerticalAlignment.Top, Background = Pal.Tone(k.Tone) }, body },
            };
            g.Children.Add(new Border { Background = Pal.Panel, Margin = new Thickness(0, 0, 1, 1), Child = tile });
        }
        return new Border
        {
            Background = Pal.Rule2, BorderBrush = Pal.Rule2, BorderThickness = new Thickness(1, 1, 0, 0), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Child = g,
        };
    }

    /// <summary>Equal-width columns.</summary>
    public static Grid Columns(string defs, params Control[] items)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(defs), ColumnSpacing = 16 };
        for (int i = 0; i < items.Length; i++) { Grid.SetColumn(items[i], i); g.Children.Add(items[i]); }
        return g;
    }

    /// <summary>Renders an Electrical.Net SVG: CSS variables resolved to literal colors, percentage width replaced by the viewBox size.</summary>
    public static Control SvgView(string svg, double? maxHeight = null)
    {
        string resolved = SvgTheme.Resolve(svg, Pal.Dark);
        var m = ViewBoxRx().Match(resolved);
        if (m.Success)
            resolved = resolved.Replace("width=\"100%\"", $"width=\"{m.Groups[1].Value}\" height=\"{m.Groups[2].Value}\"", StringComparison.Ordinal);
        var img = new Image { Source = new SvgImage { Source = SvgSource.LoadFromSvg(resolved) }, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, VerticalAlignment = VerticalAlignment.Top };
        if (maxHeight is { } h) img.MaxHeight = h;
        return img;
    }

    [GeneratedRegex("viewBox=\"0 0 ([0-9.]+) ([0-9.]+)\"")]
    private static partial Regex ViewBoxRx();

    /// <summary>Segmented control.</summary>
    public static Control Seg(string label, IEnumerable<(string Value, string Text, bool Enabled)> options, string value, Action<string> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (v, text, enabled) in options)
        {
            bool on = v == value;
            var b = new Button
            {
                Content = text, IsEnabled = enabled, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(0), FontSize = 13,
                Background = on ? Pal.Ink : Pal.Panel, Foreground = on ? Pal.Bg : Pal.Ink2, BorderBrush = Pal.Rule2, BorderThickness = new Thickness(1, 1, 0, 1),
            };
            b.Click += (_, _) => { if (v != value) changed(v); };
            row.Children.Add(b);
        }
        if (row.Children.LastOrDefault() is Button last) last.BorderThickness = new Thickness(1);
        return new StackPanel { Spacing = 6, Children = { Eyebrow(label), new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = row } } };
    }

    /// <summary>Labelled slider; <paramref name="changed"/> fires when the thumb is released or the value is set by keyboard.</summary>
    public static Control Slider(string label, double min, double max, double value, double step, Action<double> changed, string format = "0.##", string unit = "")
    {
        var output = Mono(value.ToString(format, CultureInfo.InvariantCulture) + unit, 13.5, Pal.Ink);
        output.VerticalAlignment = VerticalAlignment.Center; output.MinWidth = 70;
        var s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, Width = 190, Foreground = Pal.Cyan };
        double last = value;
        s.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            output.Text = s.Value.ToString(format, CultureInfo.InvariantCulture) + unit;
        };
        void Commit() { if (Math.Abs(s.Value - last) > 1e-9) { last = s.Value; changed(s.Value); } }
        s.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (_, _) => Commit(), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        s.KeyUp += (_, _) => Commit();
        return new StackPanel { Spacing = 2, Children = { Eyebrow(label), Row(12, s, output) } };
    }

    /// <summary>Control bar (wraps).</summary>
    public static Control Controls(params Control[] items)
    {
        var wp = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) { i.Margin = new Thickness(0, 4, 28, 4); wp.Children.Add(i); }
        return new Border
        {
            Background = Pal.Panel, BorderBrush = Pal.Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 10), Child = wp,
        };
    }

    /// <summary>Data table (header + rows) built from a Grid. Cells may be strings or controls.</summary>
    public static Control Table(string[] headers, IEnumerable<object[]> rows, int[]? numeric = null, int maxRows = int.MaxValue)
    {
        numeric ??= [];
        var g = new Grid { ColumnSpacing = 14 };
        foreach (var _ in headers) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.ColumnDefinitions[^1] = new ColumnDefinition(GridLength.Star);
        int r = 0;
        void Add(object[] cells, bool head)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cells.Length; c++)
            {
                Control cell = cells[c] switch
                {
                    Control ctl => ctl,
                    var o when head => Eyebrow(o?.ToString() ?? ""),
                    var o when numeric.Contains(c) => Mono(o?.ToString() ?? "", 12.5, Pal.Ink),
                    var o => Text(o?.ToString() ?? "", 13, wrap: false),
                };
                if (numeric.Contains(c)) cell.HorizontalAlignment = HorizontalAlignment.Right;
                cell.Margin = new Thickness(0, 5);
                cell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c); g.Children.Add(cell);
            }
            var line = new Border { Height = 1, Background = head ? Pal.Rule2 : Pal.Rule, VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, cells.Length); g.Children.Add(line);
            r++;
        }
        Add(headers, true);
        foreach (var row in rows.Take(maxRows)) Add(row, false);
        return g;
    }

    /// <summary>Status chip (ok / warn / alarm / na).</summary>
    public static Control State(string text, string tone) => new Border
    {
        Background = tone == "na" ? Pal.BgSunk : Pal.Glow(tone), CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text.ToUpperInvariant(), FontFamily = Font("Mono"), FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = tone == "na" ? Pal.Ink3 : Pal.Tone(tone) },
    };

    /// <summary>Horizontal meter bar (0..100 %).</summary>
    public static Control Meter(double percent, string? tone = null)
    {
        var fill = new Border { Width = 90 * Math.Clamp(percent, 0, 100) / 100, Background = tone is null ? Pal.Green : Pal.Tone(tone), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) };
        return new Border { Width = 90, Height = 6, Background = Pal.BgSunk, CornerRadius = new CornerRadius(3), Child = fill, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Code listing on the dark code surface with simple C#/XML/JSON colouring.</summary>
    public static Control Code(string code, string caption, string language = "csharp", double? maxHeight = null)
    {
        var tb = new SelectableTextBlock { FontFamily = Font("Mono"), FontSize = 12.5, Foreground = Pal.CodeInk, TextWrapping = TextWrapping.NoWrap };
        tb.Inlines = new InlineCollection();
        foreach (var (cls, text) in CodeTokens.Tokens(code, language))
            tb.Inlines.Add(new Run(text) { Foreground = cls switch
            {
                "key" => Pal.Hex("#F5B342"), "type" => Pal.Hex("#22D3EE"), "str" => Pal.Hex("#9BE59B"), "num" => Pal.Hex("#F0A3C8"),
                "com" => Pal.Hex("#6D84A8"), "pun" => Pal.Hex("#8FA3BF"), _ => Pal.CodeInk,
            } });
        var scroll = new ScrollViewer { Content = tb, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (maxHeight is { } h) scroll.MaxHeight = h;
        return new Border
        {
            Background = Pal.CodeBg, CornerRadius = new CornerRadius(8), BorderBrush = Pal.Hex("#1D3157"), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new Border { BorderBrush = Pal.Hex("#1D3157"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 8),
                        Child = Mono(caption, 11.5, Pal.Hex("#7F92B0")) },
                    new Border { Padding = new Thickness(16, 12), Child = scroll },
                },
            },
        };
    }

    public static Border Lamp(bool on, double size = 8) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = on ? Pal.Cyan : Pal.LampOff, VerticalAlignment = VerticalAlignment.Center,
    };

    public static Control Notice(string text) => new Border
    {
        Background = Pal.Glow("warn"), BorderBrush = Pal.Amber, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8),
        CornerRadius = new CornerRadius(0, 6, 6, 0), Child = Text(text, 13, Pal.Ink2),
    };
}

/// <summary>Tokenizer for code listings (same rules as the web gallery).</summary>
internal static partial class CodeTokens
{
    private static readonly HashSet<string> Keywords =
    [
        "using", "var", "new", "foreach", "in", "for", "if", "else", "return", "true", "false", "null", "string", "double", "int", "bool",
        "byte", "await", "async", "public", "static", "class", "record", "void", "out", "is", "not", "and", "or", "switch", "with",
    ];

    [GeneratedRegex("""(?<c>//[^\n]*)|(?<s>\$?@?"(?:[^"\\\n]|\\.)*")|(?<n>\b\d+(?:\.\d+)?(?:e[+-]?\d+)?\b)|(?<w>\b[A-Za-z_][A-Za-z0-9_]*\b)""")]
    private static partial Regex CSharpRx();

    [GeneratedRegex("""(?<c><!--[\s\S]*?-->)|(?<t></?[A-Za-z_][\w:.-]*)|(?<a>\s[\w:.-]+(?==))|(?<s>"[^"]*")|(?<p>/?>|\?>|<\?xml)""")]
    private static partial Regex XmlRx();

    [GeneratedRegex("""(?<k>"(?:[^"\\]|\\.)*"(?=\s*:))|(?<s>"(?:[^"\\]|\\.)*")|(?<n>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|(?<w>\btrue\b|\bfalse\b|\bnull\b)""")]
    private static partial Regex JsonRx();

    public static IReadOnlyList<(string? Cls, string Text)> Tokens(string code, string language) => language switch
    {
        "xml" => Run(code, XmlRx(), m => m.Groups["c"].Success ? "com" : m.Groups["t"].Success ? "key" : m.Groups["a"].Success ? "type" : m.Groups["s"].Success ? "str" : "pun"),
        "json" => Run(code, JsonRx(), m => m.Groups["k"].Success ? "type" : m.Groups["s"].Success ? "str" : m.Groups["n"].Success ? "num" : "key"),
        _ => Run(code, CSharpRx(), m =>
            m.Groups["c"].Success ? "com" : m.Groups["s"].Success ? "str" : m.Groups["n"].Success ? "num"
            : Keywords.Contains(m.Value) ? "key" : char.IsUpper(m.Value[0]) ? "type" : null),
    };

    private static List<(string?, string)> Run(string code, Regex rx, Func<Match, string?> cls)
    {
        var list = new List<(string?, string)>();
        int pos = 0;
        foreach (Match m in rx.Matches(code))
        {
            if (m.Index > pos) list.Add((null, code[pos..m.Index]));
            list.Add((cls(m), m.Value));
            pos = m.Index + m.Length;
        }
        if (pos < code.Length) list.Add((null, code[pos..]));
        return list;
    }
}
