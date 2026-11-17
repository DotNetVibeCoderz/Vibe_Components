using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using GeoPandas.Net;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Plotting;

namespace GpdAvaloniaApp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Replace with Gpd.ReadFile("your.gpkg")
            var districts = SampleData.Districts();
            var scene = districts.Plot(new LayerStyle { Column = "population", ColorMap = ColorMaps.YlOrRd, LegendTitle = "Population", Stroke = "#ffffff" }, "Population by district");
            scene.ShowFrame = true;
            scene.Padding = 44;
            desktop.MainWindow = new Window
            {
                Title = "GpdAvaloniaApp",
                Width = 1100, Height = 760,
                Background = new SolidColorBrush(Color.Parse("#EDF1EA")),
                Content = new MapView { Scene = scene, Margin = new Thickness(12) }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
