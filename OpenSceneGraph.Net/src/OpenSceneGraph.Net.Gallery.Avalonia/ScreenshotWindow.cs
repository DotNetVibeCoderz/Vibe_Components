using OpenSceneGraph.Net.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using OpenSceneGraph.Net.Avalonia;
using OpenSceneGraph.Net.IO;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Gallery.Desktop;

/// <summary>
/// Documentation screenshot mode (<c>--screenshot &lt;sampleId|all&gt; &lt;outputDir&gt;</c>): a borderless window whose
/// only content is an <see cref="OsgView"/> sized to exactly <c>--width</c> x <c>--height</c> physical pixels. Each sample
/// runs for <c>--frames</c> frames with a fixed 1/60 s time step (deterministic), then the framebuffer is read back with
/// <see cref="OsgView.CaptureNextFrameAsync"/> and written as PNG. The app exits when done (exit code 1 on failure).
/// </summary>
public sealed class ScreenshotWindow : Window
{
    private readonly GalleryOptions _opts;
    private readonly OsgView _view = new();
    private int _frames;

    public ScreenshotWindow(GalleryOptions opts)
    {
        _opts = opts;
        Title = "OpenSceneGraph.Net Gallery — screenshots";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = true;
        Background = Brushes.Black;
        SizeToContent = SizeToContent.WidthAndHeight;
        Position = new PixelPoint(16, 16);
        Content = _view;
        _view.FrameRendered += (_, _) => _frames++;
        _view.RenderingFailed += (_, ex) => Console.Error.WriteLine($"OpenGL failed: {ex.Message}");
        Opened += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        int code = 0;
        try
        {
            double scale = RenderScaling;
            _view.Width = _opts.Width / scale;
            _view.Height = _opts.Height / scale;
            Directory.CreateDirectory(_opts.OutputDir);

            var samples = string.Equals(_opts.ScreenshotTarget, "all", StringComparison.OrdinalIgnoreCase)
                ? SampleCatalog.All.ToList()
                : _opts.ScreenshotTarget!.Split(',').Select(id => SampleCatalog.Find(id.Trim()) ?? throw new ArgumentException($"Unknown sample '{id}'")).ToList();

            Viewer? previous = null;
            foreach (var s in samples)
            {
                var viewer = new Viewer { FixedTimeStep = 1.0 / 60 };
                SampleHost.Attach(_view, s, viewer, out var error);
                if (error is not null) throw new InvalidOperationException($"Sample '{s.Id}' failed to build", error);
                previous?.Dispose();
                previous = viewer;

                int start = _frames;
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (_frames - start < _opts.Frames && DateTime.UtcNow < deadline)
                {
                    _view.Invalidate();
                    await Task.Delay(5);
                }
                var image = await _view.CaptureNextFrameAsync().WaitAsync(TimeSpan.FromSeconds(20));
                var path = Path.Combine(_opts.OutputDir, s.Id + ".png");
                PngReader.Write(path, image);
                bool blank = WindowCapture.LooksBlank(image);
                if (blank) code = 1;
                Console.WriteLine($"{s.Id,-16} {image.Width}x{image.Height}  {_frames - start} frames  -> {path}{(blank ? "  (WARNING: black frame)" : "")}");
            }
            previous?.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Screenshot failed: {ex}");
            code = 1;
        }
        Program.ExitCode = code;
        await Dispatcher.UIThread.InvokeAsync(() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(code));
    }
}

/// <summary>
/// Whole-window screenshot (<c>--ui-screenshot &lt;path.png&gt; [--sample id]</c>): opens the gallery, optionally a sample,
/// lets it run, then captures the composed window (UI + GL) with Win32 PrintWindow / BitBlt.
/// </summary>
public static class UiScreenshot
{
    public static async Task RunAsync(MainWindow main, GalleryOptions opts, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var opened = new TaskCompletionSource();
        main.Opened += (_, _) => opened.TrySetResult();
        if (opts.WidthSet) { main.Width = opts.Width; main.Height = opts.Height; }
        main.Position = new PixelPoint(10, 10);
        main.Topmost = true; // BitBlt fallback copies the screen, so nothing may overlap the window
        int code = 0;
        try
        {
            await opened.Task;
            if (opts.WidthSet)
            {
                main.Width = opts.Width / main.RenderScaling;
                main.Height = opts.Height / main.RenderScaling;
            }
            if (opts.OpenSample is { } id) main.OpenSample(id);
            int start = main.FramesRendered;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            if (opts.OpenSample is not null)
                while (main.FramesRendered - start < opts.Frames && DateTime.UtcNow < deadline) await Task.Delay(10);
            else await Task.Delay(1500);
            await Task.Delay(600); // telemetry refresh + layout settle
            var img = WindowCapture.Capture(main) ?? throw new InvalidOperationException("Window capture is only implemented on Windows.");
            var dir = Path.GetDirectoryName(Path.GetFullPath(opts.UiScreenshotPath!));
            if (dir is not null) Directory.CreateDirectory(dir);
            PngReader.Write(opts.UiScreenshotPath!, img);
            var focused = main.FocusManager?.GetFocusedElement();
            Console.WriteLine($"window {img.Width}x{img.Height} -> {opts.UiScreenshotPath} (keyboard focus: {focused?.GetType().Name ?? "none"})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UI screenshot failed: {ex}");
            code = 1;
        }
        Program.ExitCode = code;
        desktop.Shutdown(code);
    }
}
