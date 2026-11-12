using AquaCulture.Core;
using AquaCulture.Gallery.Shared;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using static AquaCulture.Gallery.Desktop.Ui;

namespace AquaCulture.Gallery.Desktop;

/// <summary>Gallery shell: navigation rail on the left, demo page on the right, EN/ID toggle.</summary>
public sealed class MainWindow : Window
{
    private readonly StackPanel _nav = new() { Spacing = 1 };
    private readonly ContentControl _page = new();
    private readonly ScrollViewer _scroll;
    private string _lang = "en";
    private string _slug = "water";

    public MainWindow()
    {
        Title = "AquaCulture.Net Gallery";
        Width = 1440; Height = 940; MinWidth = 1080;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AquaCulture.Gallery.Desktop/Assets/icon.png"))); } catch (Exception) { }

        var langEn = new ToggleButton { Content = "EN", IsChecked = true, Classes = { "lang" } };
        var langId = new ToggleButton { Content = "ID", Classes = { "lang" } };
        langEn.Click += (_, _) => { SetLang("en"); langEn.IsChecked = true; langId.IsChecked = false; };
        langId.Click += (_, _) => { SetLang("id"); langId.IsChecked = true; langEn.IsChecked = false; };

        var logo = new Image { Width = 40, Height = 40 };
        try { logo.Source = new Bitmap(AssetLoader.Open(new Uri("avares://AquaCulture.Gallery.Desktop/Assets/icon.png"))); } catch (Exception) { }
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(4, 0, 0, 14),
            Children = { logo, new StackPanel { Children = { Display("AquaCulture.Net", 25), Eyebrow($"Gallery · v{AquaInfo.Version} · desktop") } } },
        };
        var rail = new Border
        {
            Background = Brush("PaperRaised"), BorderBrush = Brush("Rule"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(14, 18),
            Child = new ScrollViewer { Content = new StackPanel { Children = { brand, _nav } } },
        };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var langs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { langEn, langId } };
        DockPanel.SetDock(langs, Dock.Right);
        top.Children.Add(langs);
        top.Children.Add(new TextBlock { Text = $"{DemoFarm.Name} · {DemoFarm.Place} · {DemoFarm.Origin}", Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center });

        _scroll = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(30, 16, 30, 40), Children = { top, _page } } };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("290,*") };
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(rail);
        root.Children.Add(_scroll);
        Content = root;
        Navigate("water");
    }

    public void SetLang(string lang) { _lang = lang; Navigate(_slug); }

    public void Navigate(string slug)
    {
        _slug = slug;
        BuildNav();
        var d = GalleryCatalog.Get(slug);
        var head = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 4, 0, 18),
            Children =
            {
                new TextBlock { Text = GalleryCatalog.Groups.First(g => g.Key == d.Group).Name[_lang].ToUpperInvariant(), Classes = { "mono" }, Foreground = Brush("Sand") },
                Display(d.Title[_lang], 50),
                Text(d.Summary[_lang], 15, "soft"),
                new Border { BorderBrush = Brush("Rule"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 3), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = d.Module, FontFamily = Font("Mono"), FontSize = 11.5, Foreground = Brush("Teal") } },
            },
        };
        Control body;
        try { body = Views.Build(slug, _lang); }
        catch (Exception e) { body = Note($"{e.GetType().Name}: {e.Message}", null, "critical"); }
        var code = new StackPanel { Margin = new Thickness(0, 20, 0, 0), Spacing = 6, Children = { Eyebrow($"C# · {d.Module}"), Ui.Code(d.Code.Trim()) } };
        var credit = Text($"AquaCulture.Net — {GalleryCatalog.Credit[_lang]}", 12, "soft");
        credit.Margin = new Thickness(0, 26, 0, 0);
        _page.Content = new StackPanel { Children = { head, body, code, credit } };
        _scroll.Offset = default;
    }

    private void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var g in GalleryCatalog.Groups)
        {
            var title = Eyebrow(g.Name[_lang]);
            title.Margin = new Thickness(9, 12, 0, 3);
            title.Foreground = Brush("Sand");
            _nav.Children.Add(title);
            foreach (var d in GalleryCatalog.Demos.Where(x => x.Group == g.Key))
            {
                var dot = new Ellipse { Width = 8, Height = 8, Fill = Hex(d.Accent switch { "green" => "#2f8f6a", "coral" => "#d4553f", "amber" => "#c9963a", "violet" => "#6c5ca8", "blue" => "#2f6fb0", _ => "#12a4b6" }), VerticalAlignment = VerticalAlignment.Center };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { dot, new TextBlock { Text = d.Title[_lang], FontSize = 13.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 } } };
                var b = new Button { Content = row, Classes = { "nav" } };
                if (d.Slug == _slug) b.Classes.Add("active");
                var slug = d.Slug;
                b.Click += (_, _) => Navigate(slug);
                _nav.Children.Add(b);
            }
        }
    }
}
