using OpenSceneGraph.Net.Desktop;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenSceneGraph.Net.IO;

namespace OpenSceneGraph.Net.Simulator;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var o = Program.Options;
            if (o.Lang is { } lang) Loc.Instance.Lang = lang;
            var main = new MainWindow(o.Settings);
            desktop.MainWindow = main;
            main.Opened += (_, _) => main.ApplyStartOptions(o.Camera, o.Dis);
            if (o.ScreenshotPath is not null) _ = ScreenshotAsync(main, o, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// <c>--screenshot path.png</c>: runs the scenario for <c>--frames</c> fixed 1/60 s steps, then captures the whole
    /// window (HUD + OpenGL view) with Win32 PrintWindow and exits.
    /// </summary>
    private static async Task ScreenshotAsync(MainWindow main, SimOptions o, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var opened = new TaskCompletionSource();
        main.Opened += (_, _) => opened.TrySetResult();
        main.Position = new PixelPoint(0, 0);
        main.Topmost = true;
        int code = 0;
        try
        {
            await opened.Task;
            double s = main.RenderScaling;
            main.Width = (o.Width ?? 1360) / s;
            main.Height = (o.Height ?? 740) / s;
            int start = main.FramesRendered;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (main.FramesRendered - start < o.Frames && DateTime.UtcNow < deadline) await Task.Delay(10);
            await Task.Delay(300);
            var img = WindowCapture.Capture(main) ?? throw new InvalidOperationException("Window capture is only implemented on Windows.");
            var dir = Path.GetDirectoryName(Path.GetFullPath(o.ScreenshotPath!));
            if (dir is not null) Directory.CreateDirectory(dir);
            PngReader.Write(o.ScreenshotPath!, img);
            Console.WriteLine($"simulator {img.Width}x{img.Height} after {main.FramesRendered - start} frames -> {o.ScreenshotPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Screenshot failed: {ex}");
            code = 1;
        }
        Program.ExitCode = code;
        desktop.Shutdown(code);
    }
}
