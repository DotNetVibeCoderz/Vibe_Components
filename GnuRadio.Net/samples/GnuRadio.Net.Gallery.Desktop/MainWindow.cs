using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GnuRadio.Avalonia;
using GnuRadio.Sinks;

namespace GnuRadio.Gallery.Desktop;

/// <summary>
/// The desktop gallery: navigation rail on the left, the selected demo on the right (front panel, instrument
/// screens, readouts, notes, sample code). Built in code from the shared demo descriptions.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly IBrush Panel = Brush("#e9edf1");
    private static readonly IBrush Seam = Brush("#c3cad2");
    private static readonly IBrush Ink = Brush("#13202b");
    private static readonly IBrush Ink2 = Brush("#4b5b6b");
    private static readonly IBrush Accent = Brush("#e8611a");
    private static readonly IBrush ScreenText = Brush("#8fb3c2");
    private static readonly IBrush Amber = Brush("#ffb23f");

    private readonly StackPanel _nav = new() { Spacing = 2 };
    private readonly ContentControl _content = new();
    private readonly Dictionary<string, Button> _navButtons = [];
    private readonly DispatcherTimer _ticker;
    private readonly List<Action> _tickActions = [];
    private Language _language = Language.En;
    private Demo? _demo;
    private DemoInfo? _info;

    public MainWindow()
    {
        Title = "GnuRadio.Net Gallery";
        Width = 1440;
        Height = 960;
        MinWidth = 960;
        MinHeight = 640;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://GnuRadio.Net.Gallery.Desktop/Assets/icon.ico")));

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("272,*") };
        root.Children.Add(BuildRail());
        var main = new DockPanel();
        Grid.SetColumn(main, 1);
        var top = BuildTopBar();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(new ScrollViewer { Content = _content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        root.Children.Add(main);
        Content = root;

        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var a in _tickActions.ToArray()) a();
        });
        _ticker.Start();
        ShowDemo("band-scanner");
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private string T(Text text) => text[_language];
    private string T(string en, string id) => _language == Language.Id ? id : en;

    private Control BuildRail()
    {
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 18) };
        brand.Children.Add(new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://GnuRadio.Net.Gallery.Desktop/Assets/icon.png"))), Width = 40, Height = 40 });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "GnuRadio.Net", FontSize = 19, Classes = { "display" }, FontWeight = FontWeight.Bold });
        names.Children.Add(new TextBlock { Text = "DESKTOP GALLERY", FontSize = 10.5, Classes = { "data" }, Foreground = Ink2, LetterSpacing = 1.4 });
        brand.Children.Add(names);

        var rail = new DockPanel { Margin = new Thickness(16, 20) };
        DockPanel.SetDock(brand, Dock.Top);
        rail.Children.Add(brand);
        var credit = new TextBlock { Text = DemoCatalog.Attribution, FontSize = 12, Foreground = Ink2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(credit, Dock.Bottom);
        rail.Children.Add(credit);
        rail.Children.Add(new ScrollViewer { Content = _nav });
        RebuildNav();
        return new Border { Background = Panel, BorderBrush = Seam, BorderThickness = new Thickness(0, 0, 1, 0), Child = rail };
    }

    private void RebuildNav()
    {
        _nav.Children.Clear();
        _navButtons.Clear();
        foreach (var group in DemoCatalog.All.GroupBy(d => d.Category))
        {
            _nav.Children.Add(new TextBlock
            {
                Text = T(Categories.Name(group.Key)).ToUpperInvariant(), Classes = { "data" }, FontSize = 10.5, LetterSpacing = 1.2,
                Foreground = Ink2, Margin = new Thickness(8, 12, 0, 4),
            });
            foreach (var d in group)
            {
                var led = new Ellipse { Width = 7, Height = 7, Fill = Seam, VerticalAlignment = VerticalAlignment.Center };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(led);
                row.Children.Add(new TextBlock { Text = T(d.Title), TextWrapping = TextWrapping.Wrap, MaxWidth = 200 });
                var button = new Button { Content = row, Classes = { "nav" }, Tag = led };
                var id = d.Id;
                button.Click += (_, _) => ShowDemo(id);
                _navButtons[id] = button;
                _nav.Children.Add(button);
            }
        }
        HighlightNav();
    }

    private void HighlightNav()
    {
        foreach (var (id, b) in _navButtons)
        {
            var active = id == _info?.Id;
            b.Classes.Set("active", active);
            ((Ellipse)b.Tag!).Fill = active ? Accent : Seam;
        }
    }

    private Control BuildTopBar()
    {
        var bar = new DockPanel { Margin = new Thickness(32, 10) };
        var langs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var en = new ToggleButton { Content = "EN", Classes = { "segment" }, IsChecked = true };
        var id = new ToggleButton { Content = "ID", Classes = { "segment" } };
        en.Click += (_, _) => SetLanguage(Language.En, en, id);
        id.Click += (_, _) => SetLanguage(Language.Id, en, id);
        langs.Children.Add(en);
        langs.Children.Add(id);
        var segmented = new Border { Child = langs, Background = Panel, BorderBrush = Seam, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(3) };
        DockPanel.SetDock(segmented, Dock.Right);
        bar.Children.Add(segmented);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brush("#4fd1e8") });
        status.Children.Add(new TextBlock { Text = "Flowgraphs run in-process on dedicated threads", Classes = { "data" }, FontSize = 12, Foreground = Ink2 });
        bar.Children.Add(status);
        return new Border { Child = bar, BorderBrush = Seam, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private void SetLanguage(Language language, ToggleButton en, ToggleButton id)
    {
        _language = language;
        en.IsChecked = language == Language.En;
        id.IsChecked = language == Language.Id;
        RebuildNav();
        BuildContent();
    }

    /// <summary>Stops the current demo and starts <paramref name="id"/>.</summary>
    public void ShowDemo(string id)
    {
        var info = DemoCatalog.Find(id);
        if (info is null) return;
        var old = _demo;
        if (old is not null)
        {
            old.Changed -= OnDemoChanged;
            Task.Run(old.Dispose);
        }
        _info = info;
        _demo = info.Create();
        _demo.Changed += OnDemoChanged;
        _demo.Start();
        HighlightNav();
        BuildContent();
    }

    private void OnDemoChanged() => Dispatcher.UIThread.Post(BuildContent);

    private void BuildContent()
    {
        _tickActions.Clear();
        if (_demo is null || _info is null) return;
        var page = new StackPanel { Margin = new Thickness(32, 26, 32, 40), Spacing = 16, MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Left };

        page.Children.Add(new TextBlock { Text = T(Categories.Name(_info.Category)).ToUpperInvariant(), Classes = { "eyebrow" } });
        page.Children.Add(new TextBlock { Text = T(_info.Title), Classes = { "display" }, FontSize = 36, Margin = new Thickness(0, -8, 0, 0) });
        page.Children.Add(new TextBlock { Text = T(_info.Summary), FontSize = 16, Foreground = Ink2, TextWrapping = TextWrapping.Wrap, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left });

        if (_demo.Error is { } error)
            page.Children.Add(new TextBlock { Text = error, Foreground = Brush("#ff5c8a") });

        if (_demo.Parameters.Count > 0) page.Children.Add(BuildFrontPanel(_demo.Parameters));
        page.Children.Add(BuildScreens(_demo.Views));
        if (_demo.Readouts.Count > 0) page.Children.Add(BuildReadouts(_demo.Readouts));

        if (_demo.Notes is { } notes)
        {
            var note = new StackPanel { Spacing = 6 };
            note.Children.Add(new TextBlock { Text = T("How it works", "Cara kerjanya"), Classes = { "display" }, FontSize = 18 });
            note.Children.Add(new TextBlock { Text = T(notes), Foreground = Ink2, TextWrapping = TextWrapping.Wrap });
            page.Children.Add(new Border
            {
                Child = note, Background = Panel, BorderBrush = Accent, BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(18, 14), CornerRadius = new CornerRadius(0, 10, 10, 0), MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Left,
            });
        }

        page.Children.Add(new TextBlock { Text = T("Build it yourself", "Buat sendiri"), Classes = { "display" }, FontSize = 18, Margin = new Thickness(0, 8, 0, -6) });
        page.Children.Add(BuildCode(_demo.Code));
        page.Children.Add(new TextBlock { Text = DemoCatalog.Attribution + " · GnuRadio.Net · MIT · .NET 10", FontSize = 12, Foreground = Ink2, Margin = new Thickness(0, 12, 0, 0) });
        _content.Content = page;
    }

    private Control BuildFrontPanel(IReadOnlyList<DemoParameter> parameters)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 22, LineSpacing = 14 };
        foreach (var p in parameters)
        {
            switch (p)
            {
                case NumberParameter n when n.IsFrequency:
                {
                    var readout = new FrequencyReadout { Minimum = n.Min, Maximum = n.Max, Value = n.Value };
                    readout.ValueChangedByUser += v => Task.Run(() => n.Set(v));
                    _tickActions.Add(() =>
                    {
                        if (readout.Value != n.Value) readout.Value = n.Value;
                    });
                    wrap.Children.Add(Labeled(T(n.Label), readout, n.Help));
                    break;
                }
                case NumberParameter n:
                {
                    var knob = new Knob { Label = T(n.Label), Minimum = n.Min, Maximum = n.Max, Step = n.Step, Unit = n.Unit, Format = n.Format, Value = n.Value };
                    knob.ValueChangedByUser += v => Task.Run(() => n.Set(v));
                    wrap.Children.Add(Labeled(null, knob, n.Help));
                    break;
                }
                case ChoiceParameter c:
                {
                    var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                    var toggles = new List<ToggleButton>();
                    foreach (var (value, label) in c.Options)
                    {
                        var t = new ToggleButton { Content = T(label), Classes = { "segment" }, IsChecked = c.Value == value };
                        var v = value;
                        t.Click += (_, _) =>
                        {
                            foreach (var other in toggles) other.IsChecked = ReferenceEquals(other, t);
                            Task.Run(() => c.Set(v));
                        };
                        toggles.Add(t);
                        options.Children.Add(t);
                    }
                    var segmented = new Border { Child = options, Background = Panel, BorderBrush = Seam, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(3) };
                    wrap.Children.Add(Labeled(T(c.Label), segmented, c.Help));
                    break;
                }
                case ActionParameter a:
                {
                    var b = new Button { Content = T(a.Label), Classes = { "hardkey" }, VerticalAlignment = VerticalAlignment.Bottom };
                    b.Click += (_, _) => Task.Run(a.Invoke);
                    wrap.Children.Add(b);
                    break;
                }
            }
        }
        return new Border { Child = wrap, Background = Panel, BorderBrush = Seam, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 16) };
    }

    private Control Labeled(string? label, Control control, Text? help)
    {
        var s = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Bottom };
        if (label is not null) s.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Ink2 });
        s.Children.Add(control);
        if (help is not null) s.Children.Add(new TextBlock { Text = T(help), FontSize = 12, Foreground = Ink2, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left });
        return s;
    }

    private Control BuildScreens(IReadOnlyList<DemoView> views)
    {
        // Six-column grid; Full = 6, Half = 3, Third = 2, filled row by row.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*") };
        var column = 0;
        var row = 0;
        foreach (var view in views)
        {
            var span = view.Size switch { PanelSize.Half => 3, PanelSize.Third => 2, _ => 6 };
            if (column + span > 6)
            {
                column = 0;
                row++;
            }
            if (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var control = CreateView(view);
            control.Margin = new Thickness(column == 0 ? 0 : 7, 0, column + span == 6 ? 0 : 7, 14);
            Grid.SetColumn(control, column);
            Grid.SetColumnSpan(control, span);
            Grid.SetRow(control, row);
            grid.Children.Add(control);
            column += span;
        }
        return grid;
    }

    private Control CreateView(DemoView view)
    {
        Control c = view switch
        {
            SpectrumPanel s => Tunable(new SpectrumView { Sink = s.Sink, MinDb = s.MinDb, MaxDb = s.MaxDb, Markers = s.Markers, ShowMaxHold = s.MaxHold }, s.OnTune),
            WaterfallPanel w => TunableWaterfall(new WaterfallView { Sink = w.Sink, MinDb = w.MinDb, MaxDb = w.MaxDb, Markers = w.Markers }, w.OnTune),
            ConstellationPanel k => new ConstellationView { Sink = k.Sink, Reference = k.Reference, Scale = k.Scale },
            ScopePanel { Sink: ITimeSinkView t } sc => new TimeScope { Sink = t, Min = sc.Min, Max = sc.Max, Traces = sc.Traces },
            VectorPanel v => new VectorView
            {
                Sink = v.Sink, DataSource = v.Data, Min = v.Min, Max = v.Max, XStart = v.XStart, XEnd = v.XEnd,
                XUnit = v.XUnit, YUnit = v.YUnit, Markers = v.Markers,
            },
            ChartPanel ch => new LineChart
            {
                Series = () => [.. ch.Series().Select(l => new LineSeries(l.Name, l.X, l.Y, l.Dashed, l.Points))],
                Revision = ch.Revision, XMin = ch.XMin, XMax = ch.XMax, YMin = ch.YMin, YMax = ch.YMax, LogY = ch.LogY, XUnit = ch.XUnit, YUnit = ch.YUnit, Fps = 4,
            },
            FlowgraphPanel f => new Border
            {
                Child = new ScrollViewer { Content = new FlowgraphView { Flowgraph = f.Flowgraph() }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
                Background = Panel, BorderBrush = Seam, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
            },
            TextPanel t => LiveText(t),
            _ => new TextBlock { Text = view.GetType().Name },
        };
        if (c is GrDisplay d)
        {
            d.Title = T(view.Title);
            d.Height = view.Height;
        }
        return c;
    }

    private static SpectrumView Tunable(SpectrumView view, Action<double>? tune)
    {
        if (tune is not null) view.Tuned += f => Task.Run(() => tune(f));
        return view;
    }

    private static WaterfallView TunableWaterfall(WaterfallView view, Action<double>? tune)
    {
        if (tune is not null) view.Tuned += f => Task.Run(() => tune(f));
        return view;
    }

    private Control LiveText(TextPanel panel)
    {
        var text = new SelectableTextBlock { Classes = { "data" }, FontSize = 13, Foreground = Amber, TextWrapping = TextWrapping.Wrap, Text = panel.Content() };
        _tickActions.Add(() =>
        {
            var next = panel.Content();
            if (text.Text != next) text.Text = next;
        });
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = T(panel.Title).ToUpperInvariant(), Classes = { "data" }, FontSize = 11, Foreground = Brush("#d6e6ec") });
        stack.Children.Add(new ScrollViewer { Content = text, MaxHeight = panel.Height });
        return new Border { Child = stack, Background = GrTheme.Screen, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10) };
    }

    private Control BuildReadouts(IReadOnlyList<Readout> readouts)
    {
        var wrap = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        foreach (var r in readouts)
        {
            var value = new TextBlock { Classes = { "data" }, FontSize = 15, Foreground = Amber, TextWrapping = TextWrapping.Wrap };
            void Update()
            {
                try
                {
                    value.Text = r.Value();
                }
                catch (Exception)
                {
                    value.Text = "—";
                }
            }
            Update();
            _tickActions.Add(Update);
            var cell = new StackPanel { Spacing = 2 };
            cell.Children.Add(new TextBlock { Text = T(r.Label).ToUpperInvariant(), Classes = { "data" }, FontSize = 10.5, LetterSpacing = 1, Foreground = ScreenText });
            cell.Children.Add(value);
            wrap.Children.Add(new Border { Child = cell, Background = GrTheme.Screen, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Width = 230 });
        }
        return wrap;
    }

    private static Control BuildCode(string code)
    {
        var text = new SelectableTextBlock { Classes = { "data" }, FontSize = 13, TextWrapping = TextWrapping.NoWrap, LineHeight = 21 };
        foreach (var (kind, token) in CodeHighlighter.Tokenize(code))
        {
            var run = new Run(token)
            {
                Foreground = kind switch
                {
                    CodeToken.Keyword => Brush("#4fd1e8"),
                    CodeToken.Type => Brush("#ffcf7a"),
                    CodeToken.String => Brush("#9be564"),
                    CodeToken.Number => Brush("#ff8fb0"),
                    CodeToken.Comment => Brush("#6f8a96"),
                    _ => Brush("#d6e2e8"),
                },
            };
            if (kind == CodeToken.Comment) run.FontStyle = FontStyle.Italic;
            text.Inlines!.Add(run);
        }
        return new Border
        {
            Child = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
            Background = GrTheme.Screen, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14),
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _ticker.Stop();
        _demo?.Dispose();
        base.OnClosed(e);
    }
}
