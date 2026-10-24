using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace GnuRadio.Gallery.Desktop;

// GnuRadio.Net Gallery (Avalonia). Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
// `--screenshots <dir>` renders every demo off-screen to PNG (used for the documentation).
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var shots = Array.IndexOf(args, "--screenshots");
        if (shots >= 0 && shots + 1 < args.Length) return Screenshots(args[shots + 1]);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static int Screenshots(string directory)
    {
        Directory.CreateDirectory(directory);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window = new MainWindow { Width = 1440, Height = 1000 };
        window.Show();
        var only = Environment.GetEnvironmentVariable("GR_SHOTS")?.Split(',');
        var targets = DemoCatalog.All.Where(d => only is null || only.Contains(d.Id)).Select(d => (d.Id, Seconds: d.Id == "ber-lab" ? 40.0 : 10.0));
        foreach (var (id, seconds) in targets)
        {
            window.ShowDemo(id);
            Pump(TimeSpan.FromSeconds(seconds));
            var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(directory, $"desktop-{id}.png"));
            Console.WriteLine($"saved desktop-{id}.png");
        }
        window.Close();
        return 0;
    }

    private static void Pump(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }
    }
}
