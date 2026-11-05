using Agritech.Core.Geo;
using Agritech.RemoteSensing;
using Agritech.Visualization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;

namespace AgritechFieldViewer;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
}

internal sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.MainWindow = new FieldWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Renders a (synthetic) Sentinel-2 field, its NDVI map and zonal statistics.</summary>
internal sealed class FieldWindow : Window
{
    private readonly Image _map = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _stats = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly GeoPolygon _field = GeoPolygon.Rectangle(new GeoCoordinate(-6.3045, 107.3012), 320, 220);
    private int _seed = 7;

    public FieldWindow()
    {
        Title = "AgritechFieldViewer";
        Width = 960; Height = 640;
        Background = new SolidColorBrush(Color.Parse("#E6ECE4"));
        var next = new Button { Content = "New scene" };
        next.Click += (_, _) => { _seed++; Render(); };
        RenderOptions.SetBitmapInterpolationMode(_map, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new StackPanel { Width = 280, Spacing = 12, [DockPanel.DockProperty] = Dock.Left,
                    Children = { new TextBlock { Text = "Field NDVI", FontSize = 26, FontWeight = FontWeight.Bold }, _stats, next } },
                _map,
            },
        };
        Render();
    }

    private void Render()
    {
        var ndvi = VegetationIndices.Ndvi(SyntheticScene.Create(_field.Bounds, 5, _seed));
        var zonal = RasterAnalysis.Zonal(ndvi, _field, "Block A");
        using var ms = new MemoryStream(PngEncoder.FromRaster(ndvi.Data, ndvi.Width, ndvi.Height, ColorMap.Ndvi, 4));
        _map.Source = new Bitmap(ms);
        _stats.Text = $"Area {_field.AreaHectares:0.00} ha\nMean NDVI {zonal.Stats.Mean:0.000} ± {zonal.Stats.StdDev:0.000}\n" +
                      string.Join("\n", zonal.HealthShare.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value:0.0}%")) +
                      $"\n\n{Agritech.Core.AgritechInfo.CreditEn}";
    }
}
