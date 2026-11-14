using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Electrical.Core;
using Electrical.Gallery.Shared;
using static Electrical.Gallery.Desktop.Ui;

namespace Electrical.Gallery.Desktop;

/// <summary>Gallery shell: top bar (brand, EN/ID, light/dark), demo rail on the left, demo page on the right.</summary>
public sealed class MainWindow : Window
{
    public const string HomeId = "home";
    private ContentControl _page = new();
    private StackPanel _nav = new() { Spacing = 2 };
    private ScrollViewer _scroll = new();
    private string _id = HomeId;

    public MainWindow()
    {
        Title = "Electrical.Net Gallery";
        Width = 1440; Height = 940; MinWidth = 1040; MinHeight = 640;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Electrical.Gallery.Desktop/Assets/icon.png"))); } catch (FileNotFoundException) { }
        FontFamily = Font("Body");
        Rebuild();
    }

    public void SetLang(string lang) { Ui.Lang = lang; Rebuild(); }

    public void SetTheme(bool dark)
    {
        Pal.Dark = dark;
        if (Application.Current is { } app) app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Rebuild();
    }

    private void Rebuild()
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = Pal.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Background = Pal.Bg;
        _page = new ContentControl();
        _nav = new StackPanel { Spacing = 2 };

        var logo = new Image { Width = 34, Height = 34, Source = new Bitmap(AssetLoader.Open(new Uri("avares://Electrical.Gallery.Desktop/Assets/icon.png"))) };
        var word = new TextBlock { FontFamily = Font("Display"), FontWeight = FontWeight.Bold, FontSize = 23, VerticalAlignment = VerticalAlignment.Center };
        word.Inlines = [new Avalonia.Controls.Documents.Run("Electrical") { Foreground = Pal.Ink }, new Avalonia.Controls.Documents.Run(".Net") { Foreground = Pal.Cyan }];
        var tag = new Border
        {
            BorderBrush = Pal.Rule2, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center,
            Child = Eyebrow(T("Gallery · desktop", "Galeri · desktop")),
        };
        var brand = Row(10, new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = logo }, word, tag);

        Button Toggle(string text, bool on, Action click)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(11, 5), FontFamily = Font("Mono"), FontSize = 12, CornerRadius = new CornerRadius(5),
                Background = on ? Pal.Ink : Pal.Panel, Foreground = on ? Pal.Bg : Pal.Ink2, BorderBrush = Pal.Rule2, BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) => click();
            return b;
        }
        var actions = Row(6,
            Toggle("EN", Ui.Lang == "en", () => SetLang("en")), Toggle("ID", Ui.Lang == "id", () => SetLang("id")),
            Toggle(Pal.Dark ? T("☾ Dark", "☾ Gelap") : T("☀ Light", "☀ Terang"), false, () => SetTheme(!Pal.Dark)));
        actions.Margin = new Thickness(0, 0, 4, 0);
        var stamp = Mono($"{ElectricalInfo.Version} · .NET 10 · {T("computed live in-process", "dihitung langsung di dalam proses")}", 11.5, Pal.Ink3);
        stamp.VerticalAlignment = VerticalAlignment.Center; stamp.Margin = new Thickness(22, 0, 0, 0);
        var top = new DockPanel { Margin = new Thickness(20, 0), Height = 58 };
        DockPanel.SetDock(actions, Dock.Right); actions.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(actions);
        DockPanel.SetDock(brand, Dock.Left); brand.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(brand);
        top.Children.Add(stamp);
        var topBar = new Border { Background = Pal.Panel, BorderBrush = Pal.Rule, BorderThickness = new Thickness(0, 0, 0, 1), Child = top };

        var rail = new Border
        {
            Background = Pal.Panel2, BorderBrush = Pal.Rule, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(12, 18),
            Child = new ScrollViewer { Content = _nav },
        };
        _scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(32, 26, 32, 30), Child = _page } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("252,*") };
        Grid.SetColumn(_scroll, 1);
        body.Children.Add(rail);
        body.Children.Add(_scroll);

        var root = new DockPanel();
        DockPanel.SetDock(topBar, Dock.Top);
        root.Children.Add(topBar);
        root.Children.Add(body);
        Content = root;
        Navigate(_id);
    }

    public void Navigate(string id)
    {
        _id = id;
        BuildNav();
        _page.Content = id == HomeId ? Views.Home(Navigate) : DemoPage(GalleryCatalog.Get(id));
        _scroll.Offset = default;
    }

    private Control DemoPage(DemoInfo d)
    {
        var chips = new WrapPanel();
        foreach (var p in d.Packages)
            chips.Children.Add(new Border
            {
                BorderBrush = Pal.Rule2, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 0),
                Background = Pal.Panel2, Child = Mono(p, 11.5, Pal.Ink2),
            });
        var title = Display(d.Title[Ui.Lang], 40);
        var head = Stack(8, Eyebrow(GalleryCatalog.CategoryOf(d).Name[Ui.Lang], Pal.Cyan), title, Text(d.Summary[Ui.Lang], 15.5, Pal.Ink2), chips);
        head.MaxWidth = 1000; head.HorizontalAlignment = HorizontalAlignment.Left;
        var code = Panel(T("C# code", "Kode C#"), Code(d.Code, $"C# · {string.Join(" · ", d.Packages)}"), T("the code behind this demo", "kode di balik demo ini"));
        var credit = Text($"Electrical.Net — {GalleryCatalog.Credit[Ui.Lang]}", 12.5, Pal.Ink3);
        return Stack(18, head, Views.Build(d.Id), code, credit);
    }

    private void BuildNav()
    {
        _nav.Children.Clear();
        _nav.Children.Add(NavButton(HomeId, T("Overview", "Ringkasan")));
        foreach (var c in GalleryCatalog.Categories)
        {
            var t = Eyebrow(c.Name[Ui.Lang]);
            t.Margin = new Thickness(10, 16, 0, 6);
            t.TextWrapping = TextWrapping.Wrap;
            _nav.Children.Add(t);
            foreach (var d in GalleryCatalog.Demos.Where(x => x.Category == c.Key))
                _nav.Children.Add(NavButton(d.Id, d.Title[Ui.Lang]));
        }
        var credit = Text(GalleryCatalog.Credit[Ui.Lang], 11.5, Pal.Ink3);
        credit.Margin = new Thickness(10, 24, 6, 0);
        _nav.Children.Add(credit);
    }

    private Button NavButton(string id, string label)
    {
        bool active = id == _id;
        var lamp = Lamp(active || id == HomeId && active);
        lamp.Margin = new Thickness(0, 5, 0, 0); lamp.VerticalAlignment = VerticalAlignment.Top;
        if (active) lamp.Background = Pal.Cyan;
        var text = Text(label, 13.5, active ? Pal.Ink : Pal.Ink2, active ? FontWeight.SemiBold : FontWeight.Normal);
        var b = new Button
        {
            Content = new DockPanel { Children = { Docked(lamp), text } }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(6), Background = active ? Pal.Panel : Brushes.Transparent,
            BorderBrush = active ? Pal.Cyan : Brushes.Transparent, BorderThickness = new Thickness(active ? 2 : 0, 0, 0, 0),
        };
        b.Click += (_, _) => Navigate(id);
        return b;
    }

    private static Control Docked(Control c) { c.Margin = new Thickness(0, 5, 10, 0); DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); return c; }
}
