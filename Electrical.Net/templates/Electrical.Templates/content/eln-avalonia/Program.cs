// Single-line viewer — built with Electrical.Net (Gravicode Studios, led by Kang Fadhil)
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Avalonia.Themes.Fluent;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.Visualization;

AppBuilder.Configure<GridViewerApp>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);

public sealed class GridViewerApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var net = TestCases.ByName("CASE_NAME");
            var pf = PowerFlowSolver.Solve(net);
            var svg = SvgTheme.Resolve(SldRenderer.Render(net, pf), dark: true);
            var header = new TextBlock { Text = pf.ToString(), Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(header, Dock.Top);
            var image = new Image { Source = new SvgImage { Source = SvgSource.LoadFromSvg(svg) }, Stretch = Stretch.Uniform };
            desktop.MainWindow = new Window
            {
                Title = $"GridViewer — {net.Name}", Width = 1280, Height = 820,
                Background = new SolidColorBrush(Color.Parse("#0b1626")),
                Content = new DockPanel { Children = { header, new ScrollViewer { Content = image } } },
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
