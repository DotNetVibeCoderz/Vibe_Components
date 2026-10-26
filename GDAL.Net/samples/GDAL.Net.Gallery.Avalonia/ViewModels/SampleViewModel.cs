using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GDAL.Net.Avalonia;
using GDAL.Net.Rendering;

namespace GDAL.Net.Gallery.Avalonia.ViewModels;

public sealed record MetricItem(string Label, string Value, string? Unit);

/// <summary>A gallery entry in lists (rail, home grid).</summary>
public sealed partial class SampleItemViewModel(GallerySample sample, MainViewModel owner) : ObservableObject
{
    public GallerySample Sample { get; } = sample;
    public MainViewModel Owner { get; } = owner;
    public string Id => Sample.Id;
    public string Title => Sample.Title[Owner.L.Lang];
    public string Summary => Sample.Summary[Owner.L.Lang];
    public string CategoryTitle => CategoryInfo.Title(Sample.Category)[Owner.L.Lang];
    public IBrush Swatch { get; } = new SolidColorBrush(Color.Parse(CategoryInfo.Swatch(sample.Category)));
    public string? Equivalent => Sample.Equivalent;
    public bool RequiresNetwork => Sample.RequiresNetwork;

    [ObservableProperty] private bool _isSelected;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CategoryTitle));
    }
}

public sealed class CategoryViewModel(SampleCategory category, IReadOnlyList<SampleItemViewModel> samples, MainViewModel owner) : ObservableObject
{
    public SampleCategory Category { get; } = category;
    public IReadOnlyList<SampleItemViewModel> Samples { get; } = samples;
    public string Title => CategoryInfo.Title(Category)[owner.L.Lang];
    public IBrush Swatch { get; } = new SolidColorBrush(Color.Parse(CategoryInfo.Swatch(category)));
    public int Count => Samples.Count;
    public void Refresh() => OnPropertyChanged(nameof(Title));
}

// ---------------------------------------------------------------- parameters

public abstract partial class ParameterViewModel(SampleParameter parameter, SampleViewModel owner) : ObservableObject
{
    public SampleParameter Parameter { get; } = parameter;
    protected SampleViewModel Owner { get; } = owner;
    public string Key => Parameter.Key;
    public string Label => Parameter.Label[Owner.Lang];
    public string? Hint => Parameter.Hint?[Owner.Lang];
    public bool HasHint => Parameter.Hint is not null;
    public abstract string Value { get; }
    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Hint));
    }
    protected void Changed(bool runNow = false) => Owner.ScheduleRun(runNow);
}

public sealed partial class ChoiceParameterViewModel : ParameterViewModel
{
    public ChoiceParameterViewModel(SampleParameter p, SampleViewModel owner) : base(p, owner) => _selected = p.Default;
    public IReadOnlyList<string> Options => Parameter.Options;
    [ObservableProperty] private string _selected;
    partial void OnSelectedChanged(string value) => Changed(runNow: true);
    public override string Value => Selected;
}

public sealed partial class NumberParameterViewModel : ParameterViewModel
{
    public NumberParameterViewModel(SampleParameter p, SampleViewModel owner) : base(p, owner)
        => _number = double.Parse(p.Default, CultureInfo.InvariantCulture);
    public double Min => Parameter.Min;
    public double Max => Parameter.Max;
    public double Step => Parameter.Step;
    [ObservableProperty] private double _number;
    partial void OnNumberChanged(double value)
    {
        OnPropertyChanged(nameof(Display));
        Changed();
    }
    public string Display => Step < 0.01 ? Number.ToString("0.####", CultureInfo.InvariantCulture)
        : Step < 1 ? Number.ToString("0.##", CultureInfo.InvariantCulture) : Number.ToString("#,0", CultureInfo.InvariantCulture);
    /// <summary>Wide ranges with tiny steps (coordinates) are edited as text instead of a slider.</summary>
    public bool UseSlider => (Max - Min) / Math.Max(Step, 1e-9) <= 5000;
    public override string Value => Number.ToString("R", CultureInfo.InvariantCulture);
}

public sealed partial class ToggleParameterViewModel : ParameterViewModel
{
    public ToggleParameterViewModel(SampleParameter p, SampleViewModel owner) : base(p, owner) => _isOn = p.Default == "true";
    [ObservableProperty] private bool _isOn;
    partial void OnIsOnChanged(bool value) => Changed(runNow: true);
    public override string Value => IsOn ? "true" : "false";
}

public sealed partial class TextParameterViewModel : ParameterViewModel
{
    public TextParameterViewModel(SampleParameter p, SampleViewModel owner) : base(p, owner) => _text = p.Default;
    public bool Multiline => Parameter.Multiline;
    [ObservableProperty] private string _text;
    partial void OnTextChanged(string value) => Changed();
    public override string Value => Text;
}

// ---------------------------------------------------------------- sample page

/// <summary>A running sample: parameters, execution off the UI thread, and the displayable result.</summary>
public sealed partial class SampleViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private SampleResult? _result;
    private int _version;

    public SampleViewModel(SampleItemViewModel item, MainViewModel main)
    {
        Item = item;
        _main = main;
        foreach (var p in item.Sample.Parameters)
        {
            Parameters.Add(p.Kind switch
            {
                ParameterKind.Choice => new ChoiceParameterViewModel(p, this),
                ParameterKind.Number => new NumberParameterViewModel(p, this),
                ParameterKind.Toggle => new ToggleParameterViewModel(p, this),
                _ => new TextParameterViewModel(p, this),
            });
        }
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunAsync();
        };
    }

    public SampleItemViewModel Item { get; }
    public GallerySample Sample => Item.Sample;
    public Lang Lang => _main.L.Lang;
    public Loc L => _main.L;
    public string Title => Sample.Title[Lang];
    public string Summary => Sample.Summary[Lang];
    public string CategoryTitle => CategoryInfo.Title(Sample.Category)[Lang];
    public IBrush Swatch => Item.Swatch;
    public string Code => Sample.Code;
    public string? Equivalent => Sample.Equivalent;
    public bool HasParameters => Parameters.Count > 0;

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _elapsed;

    [ObservableProperty] private IReadOnlyList<MapRasterLayer>? _rasters;
    [ObservableProperty] private IReadOnlyList<StyledVector>? _vectors;
    [ObservableProperty] private Envelope? _extent;
    [ObservableProperty] private string? _crs;
    [ObservableProperty] private bool _hasMap;

    [ObservableProperty] private IReadOnlyList<MetricItem> _metrics = [];
    [ObservableProperty] private Histogram? _histogram;
    [ObservableProperty] private string? _histogramTitle;
    [ObservableProperty] private ColorRamp? _legendRamp;
    [ObservableProperty] private double _legendMin;
    [ObservableProperty] private double _legendMax = 1;
    [ObservableProperty] private string? _legendTitle;
    [ObservableProperty] private IReadOnlyList<string>? _tableColumns;
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<string>>? _tableRows;
    [ObservableProperty] private string? _output;
    [ObservableProperty] private string _outputLanguage = "text";
    [ObservableProperty] private string? _note;
    [ObservableProperty] private Dataset? _infoDataset;
    [ObservableProperty] private string? _clickedFeature;
    [ObservableProperty] private bool _codeCopied;

    public bool HasHistogram => Histogram is not null;
    public bool HasLegend => LegendRamp is not null;
    public bool HasTable => TableColumns is { Count: > 0 };
    public bool HasOutput => !string.IsNullOrEmpty(Output);
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasInfo => InfoDataset is not null;
    public bool HasClicked => !string.IsNullOrEmpty(ClickedFeature);
    public bool ShowTableInMain => !HasMap && HasTable;
    public bool ShowOutputInMain => !HasMap && !HasTable && HasOutput;

    partial void OnHistogramChanged(Histogram? value) => OnPropertyChanged(nameof(HasHistogram));
    partial void OnLegendRampChanged(ColorRamp? value) => OnPropertyChanged(nameof(HasLegend));
    partial void OnTableColumnsChanged(IReadOnlyList<string>? value) { OnPropertyChanged(nameof(HasTable)); OnPropertyChanged(nameof(ShowTableInMain)); OnPropertyChanged(nameof(ShowOutputInMain)); }
    partial void OnOutputChanged(string? value) { OnPropertyChanged(nameof(HasOutput)); OnPropertyChanged(nameof(ShowOutputInMain)); }
    partial void OnNoteChanged(string? value) => OnPropertyChanged(nameof(HasNote));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnInfoDatasetChanged(Dataset? value) => OnPropertyChanged(nameof(HasInfo));
    partial void OnClickedFeatureChanged(string? value) => OnPropertyChanged(nameof(HasClicked));
    partial void OnHasMapChanged(bool value) { OnPropertyChanged(nameof(ShowTableInMain)); OnPropertyChanged(nameof(ShowOutputInMain)); }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Lang));
        OnPropertyChanged(nameof(L));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CategoryTitle));
        foreach (var p in Parameters) p.Refresh();
        if (_result is not null) Apply(_result, keepMap: true);
    }

    public void ScheduleRun(bool now = false)
    {
        _debounce.Stop();
        if (now) _ = RunAsync();
        else _debounce.Start();
    }

    [RelayCommand]
    public Task Run() => RunAsync();

    [RelayCommand]
    private async Task CopyCode()
    {
        var top = (global::Avalonia.Application.Current?.ApplicationLifetime as global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top?.Clipboard is { } clipboard)
        {
            await global::Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, Code);
            CodeCopied = true;
            await Task.Delay(1500);
            CodeCopied = false;
        }
    }

    public async Task RunAsync()
    {
        var version = ++_version;
        IsRunning = true;
        Error = null;
        var values = Parameters.ToDictionary(p => p.Key, p => p.Value);
        SampleResult? result = null;
        string? error = null;
        try
        {
            result = await Task.Run(() => Sample.Execute(_main.Context, values));
        }
        catch (Exception e)
        {
            error = e.InnerException?.Message ?? e.Message;
        }

        if (version != _version || _disposed)
        {
            // A newer run started (or the page closed) — drop this result.
            result?.Dispose();
            return;
        }

        IsRunning = false;
        if (error is not null)
        {
            Error = error;
            return;
        }
        Swap(result!);
    }

    private void Swap(SampleResult result)
    {
        var old = _result;
        _result = result;
        Apply(result, keepMap: false);
        if (old is not null)
        {
            lock (GdalUiSync.Lock) old.Dispose();
        }
    }

    private void Apply(SampleResult r, bool keepMap)
    {
        var lang = Lang;
        if (!keepMap)
        {
            Rasters = r.Rasters.Select(x => new MapRasterLayer(x.Dataset, x.Options, x.Title)).ToList();
            Vectors = r.Vectors.ToList();
            Extent = r.HasMap ? r.MapExtent : null;
            Crs = r.Crs;
            HasMap = r.HasMap;
            InfoDataset = r.Rasters.Count > 0 ? r.Rasters[^1].Dataset : null;
            ClickedFeature = null;
        }

        Metrics = r.Metrics.Select(m => new MetricItem(m.Label[lang], m.Value, m.Unit)).ToList();
        Histogram = r.Histogram;
        HistogramTitle = r.HistogramTitle?[lang];
        var legendLayer = r.Rasters.LastOrDefault(x => x.LegendMin is not null && x.LegendMax is not null);
        if (legendLayer is not null)
        {
            LegendRamp = legendLayer.Options.Ramp;
            LegendMin = legendLayer.LegendMin!.Value;
            LegendMax = legendLayer.LegendMax!.Value;
            LegendTitle = legendLayer.Title;
        }
        else
        {
            LegendRamp = null;
        }
        TableColumns = r.Table?.Columns;
        TableRows = r.Table?.Rows;
        OutputLanguage = r.OutputLanguage;
        Output = r.Output;
        Note = r.Note?[lang];
        Elapsed = $"{L["took"]} {r.Elapsed.TotalMilliseconds.ToString("#,0", CultureInfo.InvariantCulture)} ms";
    }

    public void OnFeatureClicked(MapFeatureEventArgs e)
    {
        var attrs = e.Feature.Attributes.Count == 0
            ? $"#{e.Feature.Id}"
            : string.Join("   ", e.Feature.Attributes.Take(6).Select(kv => $"{kv.Key}: {GdalAttributeGrid.Format(kv.Value)}"));
        ClickedFeature = $"{e.Layer.Title}  ·  {attrs}";
    }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _debounce.Stop();
        Rasters = null;
        Vectors = null;
        InfoDataset = null;
        if (_result is not null)
        {
            lock (GdalUiSync.Lock) _result.Dispose();
            _result = null;
        }
    }
}
