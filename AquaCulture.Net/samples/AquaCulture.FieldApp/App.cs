using AquaCulture.Core;

namespace AquaCulture.FieldApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<App>().Build();
}

public sealed class App : Application
{
    private readonly FieldState _state = new();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(BuildRoot()) { Title = "AquaCulture Field" };
        _state.LanguageChanged += () => window.Page = BuildRoot(SettingsTab);
        window.Resumed += async (_, _) => await _state.SyncAsync();       // background upload when the app comes back with signal
        return window;
    }

    private const int SettingsTab = 2;

    private Page BuildRoot(int selected = 0)
    {
        var tabs = new TabbedPage { BarBackgroundColor = Ui.Primary, BarTextColor = Colors.White };
        tabs.Children.Add(new NavigationPage(new SamplePage(_state)) { Title = _state.T("Sample", "Sampling"), BarBackgroundColor = Ui.Primary, BarTextColor = Colors.White });
        tabs.Children.Add(new NavigationPage(new HistoryPage(_state)) { Title = _state.T("History", "Riwayat"), BarBackgroundColor = Ui.Primary, BarTextColor = Colors.White });
        tabs.Children.Add(new NavigationPage(new SettingsPage(_state)) { Title = _state.T("Settings", "Pengaturan"), BarBackgroundColor = Ui.Primary, BarTextColor = Colors.White });
        tabs.CurrentPage = tabs.Children[selected];
        return tabs;
    }
}

/// <summary>Small code-first UI kit.</summary>
internal static class Ui
{
    public static readonly Color Primary = Color.FromArgb("#0E6E7E");
    public static readonly Color Good = Color.FromArgb("#2E7D32"), Warn = Color.FromArgb("#E08A00"), Bad = Color.FromArgb("#C62828");

    public static Label H(string text) => new() { Text = text, FontSize = 18, FontAttributes = FontAttributes.Bold, Margin = new(0, 12, 0, 4), TextColor = Primary };
    public static Label P(string text, Color? color = null) => new() { Text = text, FontSize = 14, TextColor = color ?? Colors.Black, LineBreakMode = LineBreakMode.WordWrap };
    public static Label Credit() => new() { Text = $"{AquaInfo.CreditId}\n{AquaInfo.CreditEn} · v{AquaInfo.Version}", FontSize = 11, TextColor = Colors.Gray, Margin = new(0, 16), HorizontalTextAlignment = TextAlignment.Center };

    public static Entry Number(string placeholder) => new() { Placeholder = placeholder, Keyboard = Keyboard.Numeric, ClearButtonVisibility = ClearButtonVisibility.WhileEditing };

    /// <summary>Accepts "7.8" and the Indonesian "7,8".</summary>
    public static double? Parse(Entry e) =>
        double.TryParse((e.Text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    public static Grid Row(string label, View input)
    {
        var g = new Grid { ColumnDefinitions = [new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition(new GridLength(3, GridUnitType.Star))], ColumnSpacing = 8 };
        g.Add(new Label { Text = label, VerticalTextAlignment = TextAlignment.Center, FontSize = 14 }, 0, 0);
        g.Add(input, 1, 0);
        return g;
    }

    public static Button Action(string text, EventHandler onClick, Color? color = null)
    {
        var b = new Button { Text = text, BackgroundColor = color ?? Primary, TextColor = Colors.White, Margin = new(0, 6), CornerRadius = 8 };
        b.Clicked += onClick;
        return b;
    }
}
