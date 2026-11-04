using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSceneGraph.Net.Gallery.Desktop;

/// <summary>A sample card on the home grid.</summary>
public sealed partial class SampleCardVm : ObservableObject
{
    public SampleCardVm(GallerySample sample)
    {
        Sample = sample;
        var c = Color.Parse(sample.Accent);
        Accent = new SolidColorBrush(c);
        AccentSoft = new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B));
        AccentGlow = new LinearGradientBrush
        {
            StartPoint = new global::Avalonia.RelativePoint(0, 0, global::Avalonia.RelativeUnit.Relative),
            EndPoint = new global::Avalonia.RelativePoint(1, 1, global::Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(150, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(30, c.R, c.G, c.B), 0.55),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        };
        Thumbnail = LoadThumbnail(sample.Id);
        NodePath = $"/{Slug(sample.Category)}/{sample.Id}";
    }

    public GallerySample Sample { get; }
    public IBrush Accent { get; }
    public IBrush AccentSoft { get; }
    public IBrush AccentGlow { get; }
    public Bitmap? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is not null;
    public string NodePath { get; }
    public string Title => Loc.Instance.Get(Sample.Title);
    public string Description => Loc.Instance.Get(Sample.Description);
    public string CategoryName => Loc.Instance.Get(SampleCatalog.CategoryName(Sample.Category));
    public IReadOnlyList<string> Tags => Sample.Tags;
    public string Monogram => string.Concat(Sample.Title.En.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(CategoryName));
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        bool Has(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var s = Sample;
        return Has(s.Title.En) || Has(s.Title.Id) || Has(s.Description.En) || Has(s.Description.Id) || Has(s.Id)
               || s.Tags.Any(Has) || s.Features.Any(Has) || Has(SampleCatalog.CategoryName(s.Category).En) || Has(SampleCatalog.CategoryName(s.Category).Id);
    }

    public static string Slug(SampleCategory c) => c.ToString().ToLowerInvariant();

    private static Bitmap? LoadThumbnail(string id)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "thumbnails", id + ".png");
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, 640);
        }
        catch { return null; }
    }
}

/// <summary>A navigation entry (all samples or one category).</summary>
public sealed partial class CategoryVm : ObservableObject
{
    public CategoryVm(SampleCategory? category, int count)
    {
        Category = category;
        Count = count;
    }

    public SampleCategory? Category { get; }
    public int Count { get; }
    public string CountText => Count.ToString("00");

    public string Name => Category is { } c ? Loc.Instance.Get(SampleCatalog.CategoryName(c)) : Loc.Instance["AllSamples"];

    [ObservableProperty] private bool _isSelected;

    public void Refresh() => OnPropertyChanged(nameof(Name));
}

public sealed record KeyValueRow(string Key, string Value);

public sealed record ControlRow(string Keys, string Action);
