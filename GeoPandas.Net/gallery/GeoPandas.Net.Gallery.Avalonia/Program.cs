using Avalonia;

namespace GeoPandas.Net.Gallery.Desktop;

internal static class Program
{
    /// <summary>
    /// Usage: GeoPandas.Net.Gallery.Avalonia [--sample id] [--lang id] [--screenshot out.png]
    /// --screenshot renders the window off-screen to a PNG and exits (used for docs).
    /// </summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
