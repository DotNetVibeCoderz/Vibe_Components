using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Gallery.Desktop;

public partial class MainWindow : Window
{
    private readonly List<SampleCardVm> _cards;
    private readonly List<CategoryVm> _categories;
    private readonly DispatcherTimer _telemetryTimer;
    private SampleCategory? _filter;
    private GallerySample? _current;
    private SampleContext? _context;
    private Viewer? _viewer;
    private RenderStats? _lastStats;
    private int _statsFrames;

    public MainWindow()
    {
        InitializeComponent();

        _cards = SampleCatalog.All.Select(s => new SampleCardVm(s)).ToList();
        _categories = [new CategoryVm(null, _cards.Count) { IsSelected = true }];
        foreach (var c in Enum.GetValues<SampleCategory>())
        {
            int n = _cards.Count(x => x.Sample.Category == c);
            if (n > 0) _categories.Add(new CategoryVm(c, n));
        }
        CategoryList.ItemsSource = _categories;
        ApplyFilter();

        SearchBox.TextChanged += (_, _) => ApplyFilter();
        ClearFilterButton.Click += (_, _) => { SearchBox.Text = ""; SelectCategory(_categories[0]); };
        LangEn.Click += (_, _) => SetLanguage("en");
        LangId.Click += (_, _) => SetLanguage("id");
        AboutButton.Click += async (_, _) => await new AboutWindow().ShowDialog(this);
        BackButton.Click += (_, _) => CloseSample();
        TabOverview.IsCheckedChanged += (_, _) => UpdateTabs();
        TabCode.IsCheckedChanged += (_, _) => UpdateTabs();
        CopyButton.Click += async (_, _) => await CopyCodeAsync();

        View.FrameRendered += (_, stats) => { _lastStats = stats; _statsFrames++; };
        View.RenderingFailed += (_, ex) => ShowError(Loc.Instance["RenderFailed"] + "\n\n" + ex.Message);

        // Esc leaves the sample even while the 3D view (which forwards keys to the viewer) has focus
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _current is not null) { CloseSample(); e.Handled = true; }
            else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { SearchBox.Focus(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);

        _telemetryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshLive());
        _telemetryTimer.Start();

        Loc.Instance.LanguageChanged += OnLanguageChanged;
        SyncLanguageButtons();
        Closed += (_, _) => { _telemetryTimer.Stop(); _viewer?.Dispose(); };
    }

    /// <summary>The live 3D view (used by screenshot mode).</summary>
    public Avalonia.OsgView OsgView => View;

    public GallerySample? CurrentSample => _current;

    // ------------------------------------------------------------------ home
    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CategoryVm c) SelectCategory(c);
    }

    private void SelectCategory(CategoryVm c)
    {
        foreach (var x in _categories) x.IsSelected = x == c;
        _filter = c.Category;
        if (_current is not null) CloseSample();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text ?? "";
        var visible = _cards.Where(c => (_filter is null || c.Sample.Category == _filter) && c.Matches(q)).ToList();
        CardList.ItemsSource = visible;
        EmptyState.IsVisible = visible.Count == 0;
        var t = Loc.Instance;
        CountText.Text = $"{t["Showing"]} {visible.Count} {t["Of"]} {_cards.Count} {t["Scenes"]}";
        HomeEyebrow.Text = (_filter is { } f ? t.Get(SampleCatalog.CategoryName(f)) : t["AllSamples"]).ToUpperInvariant();
        if (_current is not null && !string.IsNullOrWhiteSpace(q)) CloseSample();
    }

    private void OnCardClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SampleCardVm vm) OpenSample(vm.Sample);
    }

    // ------------------------------------------------------------------ sample view
    public void OpenSample(string id)
    {
        if (SampleCatalog.Find(id) is { } s) OpenSample(s);
    }

    /// <summary>Creates a fresh viewer, builds the sample into it and shows it in the 3D view.</summary>
    public void OpenSample(GallerySample sample)
    {
        var old = _viewer;
        var viewer = new Viewer();
        ErrorPanel.IsVisible = false;
        var ctx = SampleHost.Attach(View, sample, viewer, out var error);
        if (error is not null) ShowError($"{Loc.Instance["BuildFailed"]}\n{error.GetType().Name}: {error.Message}");
        old?.Dispose();

        _viewer = viewer;
        _context = ctx;
        _current = sample;
        _lastStats = null;

        var accent = Color.Parse(sample.Accent);
        SampleView.Resources["SampleAccent"] = new SolidColorBrush(accent);
        TabOverview.IsChecked = true;
        FillInspector();

        HomeView.IsVisible = false;
        SampleView.IsVisible = true;
        RefreshLive();
        // keyboard goes straight to the 3D view so vehicle samples can be driven immediately
        Dispatcher.UIThread.Post(() => View.Focus(NavigationMethod.Pointer), DispatcherPriority.Input);
    }

    private void CloseSample()
    {
        if (_current is null) return;
        var old = _viewer;
        View.Viewer = new Viewer();
        View.Scene = null;
        old?.Dispose();
        _viewer = null;
        _context = null;
        _current = null;
        SampleView.IsVisible = false;
        HomeView.IsVisible = true;
    }

    private void FillInspector()
    {
        if (_current is not { } s) return;
        var t = Loc.Instance;
        string path = $"/{SampleCardVm.Slug(s.Category)}/{s.Id}";
        PathText.Text = path;
        InspectorPath.Text = "root" + path.Replace("/", " / ");
        InspectorCategory.Text = t.Get(SampleCatalog.CategoryName(s.Category)).ToUpperInvariant();
        InspectorTitle.Text = t.Get(s.Title);
        InspectorDescription.Text = t.Get(s.Description);
        Title = $"{t.Get(s.Title)} · {t["AppTitle"]}";
        FeatureList.ItemsSource = s.Features;
        CodeText.Text = s.Code;
        CodeFileName.Text = $"{s.Id}.cs";

        var rows = new List<ControlRow>();
        if (_context is { } ctx) rows.AddRange(ctx.Controls.Select(c => new ControlRow(c.Keys, t.Get(c.Action))));
        bool keyboard = rows.Count > 0;
        if (!rows.Any(r => r.Keys.Contains("Mouse", StringComparison.OrdinalIgnoreCase)))
            rows.Add(new ControlRow(t["Mouse"], t["MouseHelp"]));
        ControlsList.ItemsSource = rows;
        DriveHint.IsVisible = keyboard && rows.Any(r => r.Keys.Contains('W') || r.Keys.Contains('A'));
    }

    private void RefreshLive()
    {
        if (_context is null) return;
        var rows = _context.Telemetry.Snapshot().Select(kv => new KeyValueRow(kv.Key, kv.Value)).ToList();
        TelemetryList.ItemsSource = rows;
        NoTelemetryText.IsVisible = rows.Count == 0;
        if (_lastStats is { } st)
        {
            FpsText.Text = (_viewer?.FramesPerSecond ?? st.FramesPerSecond).ToString("F0");
            DrawsText.Text = st.DrawItems.ToString("N0");
            TrisText.Text = st.Triangles.ToString("N0");
            RendererText.Text = View.RendererName ?? "";
        }
    }

    private void UpdateTabs()
    {
        bool code = TabCode.IsChecked == true;
        CodePane.IsVisible = code;
        OverviewPane.IsVisible = !code;
    }

    private async Task CopyCodeAsync()
    {
        if (_current is null || Clipboard is null) return;
        await Clipboard.SetTextAsync(_current.Code);
        CopyLabel.Text = Loc.Instance["Copied"] + " ✓";
        await Task.Delay(1500);
        CopyLabel.Text = Loc.Instance["CopyCode"];
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.IsVisible = true;
    }

    // ------------------------------------------------------------------ language
    private void SetLanguage(string lang)
    {
        Loc.Instance.Lang = lang;
        SyncLanguageButtons();
    }

    private void SyncLanguageButtons()
    {
        LangEn.IsChecked = !Loc.Instance.IsIndonesian;
        LangId.IsChecked = Loc.Instance.IsIndonesian;
    }

    private void OnLanguageChanged()
    {
        foreach (var c in _cards) c.Refresh();
        foreach (var c in _categories) c.Refresh();
        ApplyFilter();
        if (_current is not null) FillInspector();
        else Title = Loc.Instance["AppTitle"];
        CopyLabel.Text = Loc.Instance["CopyCode"];
        SyncLanguageButtons();
    }

    /// <summary>Total frames rendered since the current sample opened (screenshot mode waits on it).</summary>
    public int FramesRendered => _statsFrames;
}
