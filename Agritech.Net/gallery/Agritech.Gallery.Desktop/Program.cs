using Avalonia;
using Avalonia.Headless;

namespace Agritech.Gallery.Desktop;

internal static class Program
{
    /// <summary>
    /// Normal run: desktop window. <c>--screenshot &lt;dir&gt; [lang]</c> renders every demo off-screen with the headless
    /// Skia backend and saves PNGs (used to produce the documentation screenshots).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            BuildHeadless().SetupWithoutStarting();
            return Screenshots.Capture(args[1], args.Length > 2 ? args[2] : "en");
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    private static AppBuilder BuildHeadless() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont();
}
