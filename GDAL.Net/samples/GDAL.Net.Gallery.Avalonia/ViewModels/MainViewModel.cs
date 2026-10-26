using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GDAL.Net.Gallery.Avalonia.ViewModels;

public enum Page
{
    Home,
    Sample,
    Components,
}

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(SampleContext context)
    {
        Context = context;
        Categories = Catalog.ByCategory
            .Select(g => new CategoryViewModel(g.Key, g.Select(s => new SampleItemViewModel(s, this)).ToList(), this))
            .ToList();
        AllSamples = Categories.SelectMany(c => c.Samples).ToList();
        Footer = $"GDAL {Gdal.Version}  ·  PROJ {Gdal.ProjVersion}  ·  {Processing.Kernels.Backend}";
    }

    public SampleContext Context { get; }
    [ObservableProperty] private Loc _l = new();
    public IReadOnlyList<CategoryViewModel> Categories { get; }
    public IReadOnlyList<SampleItemViewModel> AllSamples { get; }
    public string Footer { get; }
    public string Credit => Branding.Credit[L.Lang];
    public string Tagline => Branding.Tagline[L.Lang];
    public string CountLabel => $"{AllSamples.Count} {L["samples_count"]}";

    [ObservableProperty] private Page _page = Page.Home;
    [ObservableProperty] private SampleViewModel? _current;
    [ObservableProperty] private bool _isDataReady;
    [ObservableProperty] private string? _dataStatus;
    [ObservableProperty] private string? _dataError;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private bool _isIndonesian;
    [ObservableProperty] private IReadOnlyList<(double Elevation, double[] Points)>? _contours;
    [ObservableProperty] private ComponentsViewModel? _components;

    public bool IsHome => Page == Page.Home;
    public bool IsSample => Page == Page.Sample;
    public bool IsComponents => Page == Page.Components;

    partial void OnPageChanged(Page value)
    {
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsSample));
        OnPropertyChanged(nameof(IsComponents));
    }

    partial void OnIsDarkChanged(bool value)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    partial void OnIsIndonesianChanged(bool value)
    {
        L = new Loc(value ? Lang.Id : Lang.En);
        Components?.RefreshLanguage();
        foreach (var c in Categories)
        {
            c.Refresh();
            foreach (var s in c.Samples) s.Refresh();
        }
        Current?.Refresh();
        OnPropertyChanged(nameof(Credit));
        OnPropertyChanged(nameof(Tagline));
        OnPropertyChanged(nameof(CountLabel));
    }

    /// <summary>Generates the sample data (first launch) and the signature contours, off the UI thread.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var progress = new Progress<string>(s => DataStatus = s);
            await Task.Run(() => Context.EnsureData(progress));
            Contours = await Task.Run(() => Branding.SignatureContours(Context, 60, 600));
            IsDataReady = true;
        }
        catch (Exception e)
        {
            DataError = e.Message;
        }
    }

    [RelayCommand]
    private void ShowHome()
    {
        CloseCurrent();
        Page = Page.Home;
    }

    [RelayCommand]
    private void ShowComponents()
    {
        CloseCurrent();
        Components ??= new ComponentsViewModel(this);
        Page = Page.Components;
    }

    [RelayCommand]
    public void OpenSample(SampleItemViewModel? item)
    {
        if (item is null) return;
        if (Current?.Item == item && Page == Page.Sample) return;
        CloseCurrent();
        item.IsSelected = true;
        Current = new SampleViewModel(item, this);
        Page = Page.Sample;
        if (IsDataReady) _ = Current.RunAsync();
    }

    /// <summary>Opens a sample by id and waits for its first result (used by the screenshot mode).</summary>
    public async Task OpenAndRunAsync(string id)
    {
        var item = AllSamples.First(s => s.Id == id);
        CloseCurrent();
        item.IsSelected = true;
        Current = new SampleViewModel(item, this);
        Page = Page.Sample;
        await Current.RunAsync();
    }

    partial void OnIsDataReadyChanged(bool value)
    {
        if (value && Current is { } c) _ = c.RunAsync();
    }

    private void CloseCurrent()
    {
        if (Current is { } c)
        {
            c.Item.IsSelected = false;
            c.Dispose();
            Current = null;
        }
    }
}
