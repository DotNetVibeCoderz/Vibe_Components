using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GDAL.Net.Srs;

namespace GDAL.Net.Avalonia;

/// <summary>
/// Summary card of a raster/vector dataset: driver, size, CRS, geotransform, bands (type, nodata, color), layers and metadata.
/// Style it with the classes <c>gdal-section</c>, <c>gdal-key</c>, <c>gdal-value</c>.
/// </summary>
public class GdalDatasetInfoPanel : UserControl
{
    public static readonly StyledProperty<Dataset?> DatasetProperty =
        AvaloniaProperty.Register<GdalDatasetInfoPanel, Dataset?>(nameof(Dataset));

    /// <summary>Show the default metadata domain.</summary>
    public static readonly StyledProperty<bool> ShowMetadataProperty =
        AvaloniaProperty.Register<GdalDatasetInfoPanel, bool>(nameof(ShowMetadata), true);

    public Dataset? Dataset { get => GetValue(DatasetProperty); set => SetValue(DatasetProperty, value); }
    public bool ShowMetadata { get => GetValue(ShowMetadataProperty); set => SetValue(ShowMetadataProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DatasetProperty || change.Property == ShowMetadataProperty) Rebuild();
    }

    private void Rebuild()
    {
        var ds = Dataset;
        var root = new StackPanel { Spacing = 2 };
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (ds is null || ds.IsDisposed) return;

        lock (GdalUiSync.Lock)
        {
            Fill(root, ds);
        }
    }

    private void Fill(StackPanel root, Dataset ds)
    {
        try
        {
            Section(root, "Dataset");
            Row(root, "Driver", $"{ds.Driver.ShortName} — {ds.Driver.LongName}");
            Row(root, "File", Path.GetFileName(ds.Description));
            if (ds.RasterCount > 0)
            {
                Row(root, "Size", $"{ds.RasterWidth:#,0} × {ds.RasterHeight:#,0} px, {ds.RasterCount} band(s)");
                if (ds.HasGeoTransform)
                {
                    var gt = ds.GeoTransform;
                    Row(root, "Pixel size", $"{gt.PixelWidth.ToString("G6", CultureInfo.InvariantCulture)} × {Math.Abs(gt.PixelHeight).ToString("G6", CultureInfo.InvariantCulture)}");
                    Row(root, "Extent", ds.Extent.ToString());
                }
            }
            using (var srs = ds.SpatialReference)
            {
                Row(root, "CRS", srs?.ToString() ?? "—");
            }

            if (ds.RasterCount > 0)
            {
                Section(root, "Bands");
                foreach (var b in ds.Bands)
                {
                    var name = string.IsNullOrEmpty(b.Description) ? $"Band {b.Number}" : b.Description;
                    var details = $"{b.DataType} · {b.ColorInterpretation}" + (b.NoDataValue is { } nd ? $" · nodata {nd.ToString("G6", CultureInfo.InvariantCulture)}" : "")
                        + (b.OverviewCount > 0 ? $" · {b.OverviewCount} overviews" : "") + $" · block {b.BlockSize.Width}×{b.BlockSize.Height}";
                    Row(root, name, details);
                }
            }

            if (ds.LayerCount > 0)
            {
                Section(root, "Layers");
                foreach (var l in ds.Layers)
                {
                    Row(root, l.Name, $"{l.GeometryType} · {l.GetFeatureCount(false):#,0} features · {l.Fields.Count} fields");
                }
            }

            if (ShowMetadata)
            {
                var md = ds.GetMetadata();
                if (md.Count > 0)
                {
                    Section(root, "Metadata");
                    foreach (var kv in md.Take(40)) Row(root, kv.Key, kv.Value);
                }
            }
        }
        catch (Exception e) when (e is GdalException or ObjectDisposedException)
        {
            root.Children.Add(new TextBlock { Text = e.Message, TextWrapping = TextWrapping.Wrap });
        }
    }

    private static void Section(Panel root, string title)
    {
        var tb = new TextBlock { Text = title.ToUpperInvariant(), Margin = new Thickness(0, root.Children.Count == 0 ? 0 : 12, 0, 4) };
        tb.Classes.Add("gdal-section");
        root.Children.Add(tb);
    }

    private static void Row(Panel root, string key, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 1) };
        var k = new TextBlock { Text = key, TextTrimming = TextTrimming.CharacterEllipsis };
        k.Classes.Add("gdal-key");
        var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        v.Classes.Add("gdal-value");
        Grid.SetColumn(v, 1);
        grid.Children.Add(k);
        grid.Children.Add(v);
        root.Children.Add(grid);
    }
}

/// <summary>
/// Coordinate reference system explorer: full-text search of the PROJ database, export of the selected CRS as WKT2 / WKT1 /
/// PROJJSON / PROJ / ESRI, and transformation of a WGS 84 longitude/latitude into it.
/// </summary>
public class GdalSrsExplorer : UserControl
{
    public static readonly StyledProperty<string> QueryProperty =
        AvaloniaProperty.Register<GdalSrsExplorer, string>(nameof(Query), "UTM zone 48S");

    /// <summary>Selected CRS identifier, e.g. "EPSG:32748".</summary>
    public static readonly StyledProperty<string?> SelectedCrsProperty =
        AvaloniaProperty.Register<GdalSrsExplorer, string?>(nameof(SelectedCrs), "EPSG:32748");

    public static readonly StyledProperty<FontFamily> MonoFontFamilyProperty =
        AvaloniaProperty.Register<GdalSrsExplorer, FontFamily>(nameof(MonoFontFamily), new FontFamily("Consolas, Menlo, monospace"));

    public string Query { get => GetValue(QueryProperty); set => SetValue(QueryProperty, value); }
    public string? SelectedCrs { get => GetValue(SelectedCrsProperty); set => SetValue(SelectedCrsProperty, value); }
    public FontFamily MonoFontFamily { get => GetValue(MonoFontFamilyProperty); set => SetValue(MonoFontFamilyProperty, value); }

    private readonly TextBox _search = new() { PlaceholderText = "Search code, name or area…" };
    private readonly ListBox _results = new();
    private readonly ComboBox _format = new() { ItemsSource = new[] { "WKT2", "WKT1", "PROJJSON", "PROJ", "ESRI WKT" }, SelectedIndex = 0, MinWidth = 120 };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBox _lon = new() { Text = "107.6191", Width = 110 };
    private readonly TextBox _lat = new() { Text = "-6.9175", Width = 110 };
    private readonly TextBlock _transformed = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };

    public GdalSrsExplorer()
    {
        _title.Classes.Add("gdal-title");
        _output.Classes.Add("gdal-code");
        _transformed.Classes.Add("gdal-value");
        _results.Classes.Add("gdal-crs-list");
        _results.ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<CrsInfo>((crs, _) =>
        {
            if (crs is null) return new TextBlock();
            var tb = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            var code = new Run(crs.Id + "  ") { FontWeight = FontWeight.SemiBold };
            tb.Inlines = [code, new Run(crs.Name)];
            ToolTip.SetTip(tb, crs.AreaName);
            return tb;
        });

        var left = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_search, Dock.Top);
        _search.Margin = new Thickness(0, 0, 0, 8);
        left.Children.Add(_search);
        left.Children.Add(_results);

        var formatRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8) };
        formatRow.Children.Add(new TextBlock { Text = "Format", VerticalAlignment = VerticalAlignment.Center });
        formatRow.Children.Add(_format);

        var transformRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var button = new Button { Content = "Transform" };
        button.Click += (_, _) => UpdateTransform();
        transformRow.Children.Add(new TextBlock { Text = "WGS 84 lon/lat", VerticalAlignment = VerticalAlignment.Center });
        transformRow.Children.Add(_lon);
        transformRow.Children.Add(_lat);
        transformRow.Children.Add(button);
        transformRow.Children.Add(_transformed);

        var right = new DockPanel { Margin = new Thickness(16, 0, 0, 0) };
        DockPanel.SetDock(_title, Dock.Top);
        DockPanel.SetDock(formatRow, Dock.Top);
        DockPanel.SetDock(transformRow, Dock.Bottom);
        right.Children.Add(_title);
        right.Children.Add(formatRow);
        right.Children.Add(transformRow);
        right.Children.Add(_output);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;

        _search.Text = Query;
        _search.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Query = _search.Text ?? string.Empty;
        };
        _results.SelectionChanged += (_, _) =>
        {
            if (_results.SelectedItem is CrsInfo c) SelectedCrs = c.Id;
        };
        _format.SelectionChanged += (_, _) => UpdateOutput();
        _output.FontFamily = MonoFontFamily;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RunSearch();
        UpdateOutput();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == QueryProperty)
        {
            if (_search.Text != Query) _search.Text = Query;
            RunSearch();
        }
        else if (change.Property == SelectedCrsProperty)
        {
            UpdateOutput();
            UpdateTransform();
        }
        else if (change.Property == MonoFontFamilyProperty)
        {
            _output.FontFamily = MonoFontFamily;
        }
    }

    private void RunSearch()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var query = Query;
        Task.Run(() => CrsDatabase.Search(query).Take(300).ToList(), cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !cts.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(() => _results.ItemsSource = t.Result);
            }
        }, TaskScheduler.Default);
    }

    private void UpdateOutput()
    {
        if (SelectedCrs is not { } id) return;
        try
        {
            using var srs = SpatialReference.FromUserInput(id);
            _title.Text = srs.ToString() + (srs.AreaOfUse is { } a ? $"\n{a.Name}" : "");
            _output.Text = (_format.SelectedItem as string) switch
            {
                "WKT1" => srs.ToPrettyWkt(),
                "PROJJSON" => srs.ToProjJson(),
                "PROJ" => srs.ToProj4(),
                "ESRI WKT" => srs.ToEsriWkt(),
                _ => srs.ToWkt(WktFormat.Wkt2_2019, multiline: true),
            };
        }
        catch (GdalException e)
        {
            _output.Text = e.Message;
        }
    }

    private void UpdateTransform()
    {
        if (SelectedCrs is not { } id) return;
        if (!double.TryParse(_lon.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ||
            !double.TryParse(_lat.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
        {
            _transformed.Text = "Enter decimal degrees";
            return;
        }
        try
        {
            using var target = SpatialReference.FromUserInput(id);
            using var wgs84 = SpatialReference.Wgs84;
            using var ct = CoordinateTransformation.Create(wgs84, target);
            var (x, y) = ct.Transform(lon, lat);
            var fmt = target.IsGeographic ? "0.000000" : "#,0.00";
            _transformed.Text = $"→  {x.ToString(fmt, CultureInfo.InvariantCulture)},  {y.ToString(fmt, CultureInfo.InvariantCulture)}";
        }
        catch (GdalException e)
        {
            _transformed.Text = e.Message;
        }
    }
}
