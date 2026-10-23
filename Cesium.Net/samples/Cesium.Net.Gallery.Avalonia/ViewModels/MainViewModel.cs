using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CesiumNet.Gallery.Avalonia.ViewModels;

/// <summary>An entry in the sample index: a category header or a sample.</summary>
public abstract class IndexItem;

public sealed class IndexHeader(string title) : IndexItem
{
    public string Title { get; } = title;
}

public sealed class IndexSample(GallerySample sample, string title) : IndexItem
{
    public GallerySample Sample { get; } = sample;
    public string Title { get; } = title;
    public string Coord { get; } = sample.Location is { } p ? GeoFormat.Short(p) : "";
}

/// <summary>A category card on the home screen.</summary>
public sealed class TopicCard(string title, string blurb, IReadOnlyList<IndexSample> samples)
{
    public string Title { get; } = title;
    public string Blurb { get; } = blurb;
    public IReadOnlyList<IndexSample> Samples { get; } = samples;
}

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cesium.Net.Gallery", "settings.json");

    private readonly string? _startSample;
    private bool _suppressSelection;

    public MainViewModel(string? startSample = null)
    {
        _startSample = startSample;
        LoadSettings();
        L.Language = Language;
        RebuildIndex();
        RebuildTopics();
        if (SampleCatalog.Find(startSample) is { } s) Open(s);
    }

    public Strings L { get; } = new();

    public ObservableCollection<IndexItem> Index { get; } = [];

    public ObservableCollection<TopicCard> Topics { get; } = [];

    [ObservableProperty] private GalleryLanguage _language = GalleryLanguage.En;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private IndexItem? _selectedItem;
    [ObservableProperty] private SampleViewModel? _current;
    [ObservableProperty] private string? _ionToken;
    [ObservableProperty] private string? _tokenDraft;
    [ObservableProperty] private bool _tokenSaved;

    public bool IsHome => Current is null;
    public bool IsEnglish { get => Language == GalleryLanguage.En; set { if (value) Language = GalleryLanguage.En; } }
    public bool IsIndonesian { get => Language == GalleryLanguage.Id; set { if (value) Language = GalleryLanguage.Id; } }
    public string SampleCountText => $"{SampleCatalog.Samples.Count} {L.Get(GalleryText.Samples)} · {SampleCatalog.Categories.Count} {L.Get(("topics", "topik"))}";
    public string StartText => L.Get(("Start with the first sample", "Mulai dari contoh pertama"));
    public string FieldGuideText => L.Get(("Field guide", "Panduan lapangan"));
    public string CesiumVersion => $"V1.0 · CESIUMJS {CesiumViewerOptions.DefaultCesiumVersion}";

    /// <summary>Raised when a new sample (or home) needs a fresh viewer.</summary>
    public event Action? ViewerRequested;

    partial void OnLanguageChanged(GalleryLanguage value)
    {
        L.Language = value;
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsIndonesian));
        OnPropertyChanged(nameof(SampleCountText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(FieldGuideText));
        RebuildIndex();
        RebuildTopics();
        Current?.Refresh();
        SaveSettings();
    }

    partial void OnQueryChanged(string value) => RebuildIndex();

    partial void OnSelectedItemChanged(IndexItem? value)
    {
        if (_suppressSelection) return;
        if (value is IndexSample s && !ReferenceEquals(s.Sample, Current?.Sample)) Open(s.Sample);
        else if (value is IndexHeader) SelectCurrentInIndex();
    }

    partial void OnCurrentChanged(SampleViewModel? oldValue, SampleViewModel? newValue)
    {
        oldValue?.Stop();
        OnPropertyChanged(nameof(IsHome));
        ViewerRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenSample(IndexSample item) => Open(item.Sample);

    [RelayCommand]
    private void GoHome()
    {
        Current = null;
        SelectCurrentInIndex();
    }

    [RelayCommand]
    private void StartFirst() => Open(SampleCatalog.Samples[0]);

    [RelayCommand]
    private void Restart()
    {
        if (Current is null) return;
        Current = new SampleViewModel(Current.Sample, L, IonToken);
    }

    [RelayCommand]
    private void SaveToken()
    {
        IonToken = string.IsNullOrWhiteSpace(TokenDraft) ? null : TokenDraft.Trim();
        TokenSaved = true;
        SaveSettings();
    }

    private void Open(GallerySample sample)
    {
        Current = new SampleViewModel(sample, L, IonToken);
        SelectCurrentInIndex();
    }

    private void SelectCurrentInIndex()
    {
        _suppressSelection = true;
        SelectedItem = Index.OfType<IndexSample>().FirstOrDefault(i => ReferenceEquals(i.Sample, Current?.Sample));
        _suppressSelection = false;
    }

    private void RebuildIndex()
    {
        _suppressSelection = true;
        Index.Clear();
        var q = Query.Trim();
        foreach (var category in SampleCatalog.Categories)
        {
            var items = SampleCatalog.InCategory(category.Id)
                .Where(s => q.Length == 0 || s.Title.En.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Title.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (items.Count == 0) continue;
            Index.Add(new IndexHeader(L.Get(category.Title).ToUpperInvariant()));
            foreach (var s in items) Index.Add(new IndexSample(s, L.Get(s.Title)));
        }
        _suppressSelection = false;
        SelectCurrentInIndex();
    }

    private void RebuildTopics()
    {
        Topics.Clear();
        foreach (var category in SampleCatalog.Categories)
        {
            var samples = SampleCatalog.InCategory(category.Id).Select(s => new IndexSample(s, L.Get(s.Title))).ToList();
            if (samples.Count > 0) Topics.Add(new TopicCard(L.Get(category.Title), L.Get(category.Blurb), samples));
        }
    }

    private sealed record Settings(string? Language, string? IonToken);

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
#pragma warning disable MVVMTK0034 // set backing fields directly: no change notifications during construction
            _language = s?.Language == "id" ? GalleryLanguage.Id : GalleryLanguage.En;
            _ionToken = string.IsNullOrWhiteSpace(s?.IonToken) ? null : s.IonToken;
            _tokenDraft = _ionToken;
#pragma warning restore MVVMTK0034
        }
        catch (Exception) { /* corrupt settings: use defaults */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(Language == GalleryLanguage.Id ? "id" : "en", IonToken)));
        }
        catch (Exception) { /* read-only profile: settings are optional */ }
    }
}
