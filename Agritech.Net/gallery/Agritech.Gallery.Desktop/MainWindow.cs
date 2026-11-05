using Agritech.Gallery.Shared;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using static Agritech.Gallery.Desktop.Ui;

namespace Agritech.Gallery.Desktop;

/// <summary>Gallery shell: spectral-rail navigation on the left, demo page on the right, EN/ID toggle.</summary>
public sealed class MainWindow : Window
{
    private readonly StackPanel _nav = new() { Spacing = 2 };
    private readonly ContentControl _page = new();
    private readonly ScrollViewer _scroll;
    private string _lang = "en";
    private string _slug = "ndvi";

    public MainWindow()
    {
        Title = "Agritech.Net Gallery";
        Width = 1400; Height = 900; MinWidth = 1000;

        var langEn = new ToggleButton { Content = "EN", IsChecked = true, Classes = { "lang" } };
        var langId = new ToggleButton { Content = "ID", Classes = { "lang" } };
        langEn.Click += (_, _) => { SetLang("en"); langEn.IsChecked = true; langId.IsChecked = false; };
        langId.Click += (_, _) => { SetLang("id"); langId.IsChecked = true; langEn.IsChecked = false; };

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 18),
            Children =
            {
                new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Brush("Ink"),
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,6),
                        Children = { Bar("#c49a5a", 9), Bar("#a6cf5d", 14), Bar("#4f9f3a", 19) } } },
                new StackPanel { Children = { Display("Agritech.Net", 26), Eyebrow("Gallery · v1.0 · desktop") } },
            },
        };

        var rail = new Border
        {
            Background = Brush("PaperRaised"), BorderBrush = Brush("Rule"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(16, 20),
            Child = new ScrollViewer { Content = new StackPanel { Children = { brand, _nav } } },
        };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var langs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { langEn, langId } };
        DockPanel.SetDock(langs, Dock.Right);
        top.Children.Add(langs);
        top.Children.Add(Text("Blok A · Karawang −6.3045, 107.3012", 11, "mono"));

        _scroll = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(30, 18, 30, 40), Children = { top, _page } } };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*") };
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(rail);
        root.Children.Add(_scroll);
        Content = root;
        Navigate("ndvi");
    }

    private static Border Bar(string hex, double h) => new() { Width = 5, Height = h, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Color.Parse(hex)) };

    public void SetLang(string lang) { _lang = lang; Navigate(_slug); }

    public void Navigate(string slug)
    {
        _slug = slug;
        BuildNav();
        var d = GalleryCatalog.Get(slug);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("10,*"), Margin = new Thickness(0, 6, 0, 18) };
        var rail = Rail(d.Ramp, 8);
        var text = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Eyebrow(GalleryCatalog.Groups.First(g => g.Key == d.Group).Name[_lang]),
                Display(d.Title[_lang], 54),
                Text(d.Summary[_lang], 15, "soft"),
                new Border { BorderBrush = Brush("Rule"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = d.Module, FontFamily = Font("Mono"), FontSize = 11.5, Foreground = Brush("Chlorophyll") } },
            },
        };
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(14, 0, 0, 0);
        head.Children.Add(rail);
        head.Children.Add(text);

        var code = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#17201a")), CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 12), Margin = new Thickness(0, 22, 0, 0),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"C# · {d.Module}", FontFamily = Font("Mono"), FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9fb09a")) },
                    new SelectableTextBlock { Text = d.Code.Trim(), FontFamily = Font("Mono"), FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#dfe8da")) },
                },
            },
        };
        var credit = Text($"Agritech.Net — {GalleryCatalog.Credit[_lang]}", 12, "soft");
        credit.Margin = new Thickness(0, 26, 0, 0);
        _page.Content = new StackPanel { Children = { head, Views.Build(slug, _lang), code, credit } };
        _scroll.Offset = default;
    }

    private void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var g in GalleryCatalog.Groups)
        {
            var title = Eyebrow(g.Name[_lang]);
            title.Margin = new Thickness(8, 14, 0, 4);
            title.Foreground = Brush("Ochre");
            _nav.Children.Add(title);
            foreach (var d in GalleryCatalog.Demos.Where(x => x.Group == g.Key))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("4,*") };
                var label = Text(d.Title[_lang], 13.5);
                label.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(label, 1);
                row.Children.Add(Rail(d.Ramp, 4));
                row.Children.Add(label);
                var b = new Button { Content = row, Classes = { "nav" } };
                if (d.Slug == _slug) b.Classes.Add("active");
                var slug = d.Slug;
                b.Click += (_, _) => Navigate(slug);
                _nav.Children.Add(b);
            }
        }
    }
}
