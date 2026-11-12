using Avalonia;
using Avalonia.Headless;

namespace AquaCulture.Gallery.Desktop;

internal static class Program
{
    /// <summary>
    /// Normal run: desktop window. <c>--screenshot &lt;dir&gt; [en|id]</c> renders demos off-screen with the headless Skia backend
    /// (the 3D pond is rendered with ThreeNet's offscreen renderer) and saves PNGs for the documentation.
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

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static AppBuilder BuildHeadless() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
