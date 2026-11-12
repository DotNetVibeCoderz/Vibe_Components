// AquaCulture.Net pond monitor (Avalonia desktop): simulated sensors, WQI, DO chart and decisions. Made by Gravicode Studios, led by Kang Fadhil.
using System.Text.RegularExpressions;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.DigitalTwin;
using AquaCulture.IoT;
using AquaCulture.Visualization;
using AquaCulture.WaterQuality;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d) d.MainWindow = new MonitorWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class MonitorWindow : Window
{
    private readonly SpeciesProfile _species = SpeciesCatalog.Get("SPECIES_ID");
    private readonly SimulatedSensorTransport _sim = new([new SimulatedPond("K-01", "buoy-1", SalinityPpt: 18)]) { DropoutProbability = 0 };
    private readonly PondTwin _twin;
    private readonly TextBlock _wqi = new() { FontSize = 40, FontWeight = FontWeight.Bold };
    private readonly Image _chart = new() { Height = 280 };
    private readonly StackPanel _decisions = new() { Spacing = 4 };
    private DateTime _clock = DateTime.Today.AddHours(6);

    public MonitorWindow()
    {
        Title = "AquaPondMonitor · AquaCulture.Net"; Width = 1100; Height = 760;
        _twin = new PondTwin(new Pond { Id = "K-01", AreaM2 = 2000, DepthM = 1.3, AerationKw = 4.4 }, _species, DateOnly.FromDateTime(DateTime.Today).AddDays(-50), _species.MarketWeightG * 0.5, 150_000);
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
        {
            new TextBlock { Text = $"Pond K-01 · {_species.Name.En}", FontSize = 26 }, _wqi, _chart, new TextBlock { Text = "Decisions", FontSize = 18 }, _decisions,
            new TextBlock { Text = "AquaCulture.Net — Made by Gravicode Studios, led by Kang Fadhil", Foreground = Brushes.Gray },
        } } };
        DispatcherTimer.Run(() => { Tick(); return true; }, TimeSpan.FromSeconds(1));
    }

    private void Tick()
    {
        _clock = _clock.AddMinutes(30);
        _twin.Ingest(_sim.Emit(_clock));
        var state = _twin.State(_clock);
        _wqi.Text = $"WQI {state.Wqi:0} · DO {state.Water.DissolvedOxygen:0.0} mg/L";
        var dox = _twin.History(WaterParameter.DissolvedOxygen);
        var svg = SvgCharts.Line([new Series("DO", dox.Points.Select(p => ((p.Time - DateTime.Today).TotalHours, p.Value)).ToList(), "#12a4b6", Area: true)],
            new ChartOptions { Height = 260, References = [new Reference(_species.DoCritical, "critical")] });
        svg = Regex.Replace(svg, @"var\(--[a-z0-9-]+,\s*(#[0-9a-fA-F]{3,8})\)", m => m.Groups[1].Value);
        _chart.Source = new SvgImage { Source = SvgSource.LoadFromSvg(svg) };
        _decisions.Children.Clear();
        foreach (var d in _twin.Evaluate(_clock).Take(6)) _decisions.Children.Add(new TextBlock { Text = $"[{d.Severity}] {d.Title.En} — {d.Rationale.En}", TextWrapping = TextWrapping.Wrap });
    }
}
