using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Medical.Core;
using Medical.Gallery.Shared;
using static Medical.Gallery.Desktop.Ui;

namespace Medical.Gallery.Desktop;

/// <summary>Gallery shell: navigation rail on the left, demo page on the right, EN/ID toggle.</summary>
public sealed class MainWindow : Window
{
    private readonly StackPanel _nav = new() { Spacing = 1 };
    private readonly ContentControl _page = new();
    private readonly ScrollViewer _scroll;
    private readonly ToggleButton _en = new() { Content = "EN", IsChecked = true, Classes = { "seg" } };
    private readonly ToggleButton _id = new() { Content = "ID", Classes = { "seg" } };
    private string _lang = "en";
    private string _slug = "news2";

    public MainWindow()
    {
        Title = "Medical.Net Gallery";
        Width = 1440; Height = 920; MinWidth = 1080;
        _en.Click += (_, _) => SetLang("en");
        _id.Click += (_, _) => SetLang("id");

        var glyph = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(7), Background = Brush("Ink"),
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M4,18 L10,18 L12.5,11 L16.5,24 L19.5,15 L21,18 L28,18"), Stroke = Brush("EcgMajor"), StrokeThickness = 2.4,
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 32, Height = 32, Stretch = Stretch.None,
            },
        };
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 14),
            Children = { glyph, Stack(0, Display("Medical.Net", 23), Mono($"Gallery · v{MedicalInfo.Version} · desktop", 11).Also(t => t.Foreground = Brush("Muted"))) },
        };
        var langs = new Border
        {
            BorderBrush = Brush("Rule"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _en, _id } },
        };
        var rail = new Border
        {
            Background = Brush("Raised"), BorderBrush = Brush("Rule"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(16, 20),
            Child = new ScrollViewer { Content = Stack(0, brand, langs, _nav) },
        };
        _scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(36, 28, 36, 40), Child = _page } };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("268,*") };
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(rail);
        root.Children.Add(_scroll);
        Content = root;
        Navigate("news2");
    }

    public void SetLang(string lang)
    {
        _lang = lang;
        _en.IsChecked = lang == "en";
        _id.IsChecked = lang == "id";
        Navigate(_slug);
    }

    public void Navigate(string slug)
    {
        _slug = slug;
        BuildNav();
        var d = GalleryCatalog.Get(slug);
        var header = Stack(6,
            Eyebrow($"{GalleryCatalog.Groups.First(g => g.Key == d.Group).Name[_lang]} · {d.Module}").Also(t => t.Foreground = Brush("Teal")),
            Display(d.Title[_lang], 46),
            Text(d.Summary[_lang], 16.5, "muted").Also(t => { t.MaxWidth = 860; t.HorizontalAlignment = HorizontalAlignment.Left; }));
        header.Margin = new Thickness(0, 0, 0, 18);
        var body = new ContentControl();
        void Render() => body.Content = Views.Build(slug, _lang, Render);
        Render();
        var code = Stack(8, Eyebrow($"C# · {d.Module}").Also(t => t.Foreground = Brush("Teal")), Ui.Code(d.Code.Trim()));
        code.Margin = new Thickness(0, 24, 0, 0);
        var foot = Text($"Medical.Net — {GalleryCatalog.Credit[_lang]} · {GalleryCatalog.Disclaimer[_lang]}", 12, "muted");
        foot.Margin = new Thickness(0, 22, 0, 0);
        _page.Content = Stack(0, header, new Border { Height = 1, Background = Brush("Rule"), Margin = new Thickness(0, 0, 0, 18) }, body, code, foot);
        _scroll.Offset = default;
    }

    private void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var g in GalleryCatalog.Groups)
        {
            var title = Eyebrow(g.Name[_lang]);
            title.Margin = new Thickness(10, 16, 0, 4);
            _nav.Children.Add(title);
            foreach (var d in GalleryCatalog.Demos.Where(x => x.Group == g.Key))
            {
                var b = new Button { Content = d.Title[_lang], Classes = { "nav" } };
                if (d.Slug == _slug) b.Classes.Add("active");
                var slug = d.Slug;
                b.Click += (_, _) => Navigate(slug);
                _nav.Children.Add(b);
            }
        }
        var credit = Text(GalleryCatalog.Credit[_lang], 11.5, "muted");
        credit.Margin = new Thickness(10, 26, 0, 0);
        _nav.Children.Add(credit);
    }
}
