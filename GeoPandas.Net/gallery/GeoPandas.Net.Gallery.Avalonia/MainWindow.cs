using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace GeoPandas.Net.Gallery.Desktop;

/// <summary>Desktop gallery window (built in code; mirrors the Blazor gallery layout and design tokens).</summary>
public sealed class MainWindow : Window
{
    // design tokens (see gallery/README)
    private static readonly IBrush Paper = Brush("#EDF1EA"), Sheet = Brush("#F7F9F5"), Ink = Brush("#1B2A33"), Ink2 = Brush("#4A5A63"),
        Ink3 = Brush("#7B8A90"), Contour = Brush("#9A6B3F"), Magenta = Brush("#B0306A"), Rule = Brush("#CBD5CC"), IndexBg = Brush("#1B2A33"),
        IndexInk = Brush("#E6ECE4"), IndexDim = Brush("#93A4A9"), Water = Brush("#2F6F9F");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");
    private static readonly FontFamily Display = new("Bahnschrift, Inter");

    private readonly StackPanel _toc = new() { Margin = new Thickness(12, 4, 12, 32) };
    private readonly TextBox _search = new() { PlaceholderText = "Filter samples…", Margin = new Thickness(24, 16, 24, 4) };
    private readonly TextBlock _eyebrow = new() { FontFamily = Mono, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Contour, LetterSpacing = 1 };
    private readonly TextBlock _title = new() { FontFamily = Display, FontSize = 38, FontWeight = FontWeight.Bold, Foreground = Ink, TextWrapping = TextWrapping.Wrap, LetterSpacing = -0.5 };
    private readonly TextBlock _summary = new() { FontSize = 17, Foreground = Ink2, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, LineHeight = 26 };
    private readonly MapView _map = new() { Height = 620 };
    private readonly TextBlock _caption = new() { FontFamily = Mono, FontSize = 12, Foreground = Ink3 };
    private readonly StackPanel _readout = new() { Spacing = 14 };
    private readonly TextBlock _readoutTitle = new() { FontFamily = Mono, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Magenta, LetterSpacing = 1 };
    private readonly ToggleButton _codeTab = new() { Classes = { "tab" } }, _dataTab = new() { Classes = { "tab" } };
    private readonly ContentControl _detail = new();
    private readonly TextBlock _credit = new() { Foreground = Ink2, FontSize = 13 };
    private readonly ToggleButton _en = new() { Content = "EN", Classes = { "pill" } }, _id = new() { Content = "ID", Classes = { "pill" } };

    private Lang _lang = Lang.En;
    private GallerySample _current = GalleryCatalog.Samples[0];

    public MainWindow(string[] args)
    {
        Title = "GeoPandas.Net Gallery";
        Width = 1440; Height = 1000;
        Background = Paper;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://GeoPandas.Net.Gallery.Avalonia/Assets/icon.png"))); } catch { /* icon optional */ }

        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Arg("--lang") == "id") _lang = Lang.Id;
        if (Arg("--sample") is { } sid && GalleryCatalog.Samples.FirstOrDefault(s => s.Id == sid) is { } found) _current = found;

        Content = BuildLayout();
        _search.TextChanged += (_, _) => BuildToc();
        _en.Click += (_, _) => SetLang(Lang.En);
        _id.Click += (_, _) => SetLang(Lang.Id);
        _codeTab.Click += (_, _) => ShowTab(0);
        _dataTab.Click += (_, _) => ShowTab(1);
        SetLang(_lang);

        if (Arg("--screenshot") is { } shot)
        {
            Opened += async (_, _) =>
            {
                await Task.Delay(700);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                    using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
                    rtb.Render(this);
                    rtb.Save(shot);
                    Close();
                });
            };
        }
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
    private string T(string en, string id) => _lang == Lang.Id ? id : en;

    private Control BuildLayout()
    {
        // ---------------- index (sidebar)
        var wordmark = new TextBlock { FontFamily = Display, FontSize = 30, FontWeight = FontWeight.Black, Foreground = IndexInk };
        wordmark.Inlines!.Add(new Run("GeoPandas"));
        wordmark.Inlines.Add(new Run(".Net") { Foreground = Brush("#E47AA6") });
        var brand = new Border
        {
            Padding = new Thickness(24, 28, 24, 22), BorderBrush = Brush("#2C3D47"), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Spacing = 8, Children = { wordmark, new TextBlock { FontFamily = Mono, FontSize = 12, Foreground = IndexDim, Text = $"Gallery · v{Gpd.Version}" } } }
        };
        _search.Background = Brush("#223540"); _search.Foreground = IndexInk; _search.BorderBrush = Brush("#34505E");
        var index = new Border
        {
            Background = IndexBg,
            Child = new DockPanel
            {
                Children =
                {
                    Dock(brand, Avalonia.Controls.Dock.Top),
                    Dock(_search, Avalonia.Controls.Dock.Top),
                    new ScrollViewer { Content = _toc }
                }
            }
        };

        // ---------------- sheet
        var langGroup = new Border { BorderBrush = Ink, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), VerticalAlignment = VerticalAlignment.Bottom, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _en, _id } } };
        var collarGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        collarGrid.Children.Add(new StackPanel { Spacing = 6, Children = { _eyebrow, _title } });
        Grid.SetColumn(langGroup, 1);
        collarGrid.Children.Add(langGroup);
        var collar = new Border
        {
            BorderBrush = Ink, BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(0, 0, 0, 16),
            Child = new StackPanel { Spacing = 12, Children = { collarGrid, _summary } }
        };

        var mapCard = new Border
        {
            Background = Sheet, BorderBrush = Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Child = new StackPanel
            {
                Children =
                {
                    _map,
                    new Border { BorderBrush = Rule, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 8), Child = _caption }
                }
            }
        };
        var readout = new Border { BorderBrush = Magenta, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(16, 2, 0, 2), Child = new StackPanel { Spacing = 12, Children = { _readoutTitle, _readout } } };
        var stage = new Grid { ColumnDefinitions = new ColumnDefinitions("*,240"), ColumnSpacing = 22 };
        stage.Children.Add(mapCard);
        Grid.SetColumn(readout, 1);
        stage.Children.Add(readout);

        var detail = new Border
        {
            Background = Sheet, BorderBrush = Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Background = Paper, BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 6, 6, 0),
                        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _codeTab, _dataTab } } },
                    _detail
                }
            }
        };

        var footer = new DockPanel { Children = { Dock(_credit, Avalonia.Controls.Dock.Left), new TextBlock { FontFamily = Mono, FontSize = 11, Foreground = Ink3, HorizontalAlignment = HorizontalAlignment.Right, Text = $"GeoPandas.Net {Gpd.Version} · .NET 10 · {GeoPandas.Net.Geometry.Native.NativeKernels.Backend}" } } };

        var sheet = new ScrollViewer
        {
            Content = new StackPanel { Margin = new Thickness(44, 28, 44, 24), Spacing = 22, Children = { collar, stage, detail, footer } }
        };

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        root.Children.Add(index);
        Grid.SetColumn(sheet, 1);
        root.Children.Add(sheet);
        return root;
    }

    private static Control Dock(Control c, Avalonia.Controls.Dock d) { DockPanel.SetDock(c, d); return c; }

    private void SetLang(Lang lang)
    {
        _lang = lang;
        _en.IsChecked = lang == Lang.En; _id.IsChecked = lang == Lang.Id;
        _search.PlaceholderText = T("Filter samples…", "Saring contoh…");
        _readoutTitle.Text = T("READOUT", "HASIL");
        _codeTab.Content = T("Code", "Kode");
        _credit.Text = lang == Lang.Id ? GalleryCatalog.Credit : GalleryCatalog.CreditEn;
        BuildToc();
        Show(_current);
    }

    private void BuildToc()
    {
        _toc.Children.Clear();
        var q = _search.Text ?? "";
        foreach (var cat in GalleryCatalog.Categories)
        {
            var items = GalleryCatalog.Samples.Where(s => s.Category == cat.Key &&
                (q.Length == 0 || s.Title[_lang].Contains(q, StringComparison.OrdinalIgnoreCase) || s.Code.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            if (items.Count == 0) continue;
            var header = new TextBlock { Margin = new Thickness(12, 18, 12, 6), FontFamily = Mono, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = IndexDim, LetterSpacing = 1 };
            header.Inlines!.Add(new Run(cat.Glyph + "  ") { Foreground = Contour });
            header.Inlines.Add(new Run(cat.Name[_lang].ToUpperInvariant()));
            _toc.Children.Add(header);
            foreach (var s in items)
            {
                var b = new Button { Content = s.Title[_lang], Classes = { "toc" } };
                if (s.Id == _current.Id) b.Classes.Add("active");
                b.Click += (_, _) => { _current = s; BuildToc(); Show(s); };
                _toc.Children.Add(b);
            }
        }
        if (_toc.Children.Count == 0)
            _toc.Children.Add(new TextBlock { Margin = new Thickness(12, 20), Foreground = IndexDim, TextWrapping = TextWrapping.Wrap, Text = T("No sample matches that filter. Try “route”, “raster” or “DXF”.", "Tidak ada contoh yang cocok. Coba “route”, “raster” atau “DXF”.") });
    }

    private void Show(GallerySample s)
    {
        var cat = GalleryCatalog.Categories.First(c => c.Key == s.Category);
        var r = s.Run();
        _eyebrow.Text = $"{cat.Glyph}  {cat.Name[_lang].ToUpperInvariant()}";
        _title.Text = s.Title[_lang];
        _summary.Text = s.Summary[_lang];
        _map.Scene = r.Scene;
        _caption.Text = $"{r.Scene.Layers.Count} {T("layers", "layer")}    {T("computed in", "dihitung dalam")} {r.Elapsed.TotalMilliseconds:0} ms";
        _readout.Children.Clear();
        foreach (var (label, value) in r.Figures)
            _readout.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = label[_lang], FontSize = 13, Foreground = Ink2 },
                    new TextBlock { Text = value, FontFamily = Mono, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Ink, TextWrapping = TextWrapping.Wrap }
                }
            });
        _dataTab.IsVisible = r.Table is not null;
        _dataTab.Content = r.Table is null ? "" : $"{T("Data", "Data")}  {r.Table.RowCount}×{r.Table.ColumnCount}";
        ShowTab(0);
    }

    private void ShowTab(int tab)
    {
        _codeTab.IsChecked = tab == 0; _dataTab.IsChecked = tab == 1;
        var r = _current.Run();
        if (tab == 0 || r.Table is null)
        {
            var code = new SelectableTextBlock { FontFamily = Mono, FontSize = 13.5, Foreground = Ink, Margin = new Thickness(20, 18), LineHeight = 22 };
            foreach (var (kind, text) in CodeHighlighter.Tokenize(_current.Code))
            {
                var run = new Run(text);
                switch (kind)
                {
                    case TokenKind.Keyword: run.Foreground = Magenta; run.FontWeight = FontWeight.SemiBold; break;
                    case TokenKind.Type: run.Foreground = Water; break;
                    case TokenKind.Method: run.Foreground = Brush("#6B4FA0"); break;
                    case TokenKind.String: run.Foreground = Brush("#3F7A3A"); break;
                    case TokenKind.Number: run.Foreground = Contour; break;
                    case TokenKind.Comment: run.Foreground = Ink3; run.FontStyle = FontStyle.Italic; break;
                }
                code.Inlines!.Add(run);
            }
            _detail.Content = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = code };
            return;
        }
        var df = r.Table;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        for (int c = 0; c < df.ColumnCount; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        int rows = Math.Min(df.RowCount, 25);
        for (int i = 0; i <= rows; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < df.ColumnCount; c++)
        {
            var col = df.Columns[c];
            var head = new Border { Background = Paper, Padding = new Thickness(14, 7), Child = new TextBlock { Text = col.Name, FontFamily = Mono, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Ink2, HorizontalAlignment = col.IsNumeric ? HorizontalAlignment.Right : HorizontalAlignment.Left } };
            Grid.SetColumn(head, c);
            grid.Children.Add(head);
            for (int i = 0; i < rows; i++)
            {
                var cell = new Border
                {
                    BorderBrush = Brush("#E1E7DF"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 6),
                    Child = new TextBlock { Text = col.Format(i), FontSize = 13, FontFamily = col.IsNumeric ? Mono : FontFamily.Default, Foreground = Ink, HorizontalAlignment = col.IsNumeric ? HorizontalAlignment.Right : HorizontalAlignment.Left }
                };
                Grid.SetColumn(cell, c); Grid.SetRow(cell, i + 1);
                grid.Children.Add(cell);
            }
        }
        _detail.Content = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = grid };
    }
}
