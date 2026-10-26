using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using GDAL.Net.Gallery.Avalonia.ViewModels;
using GDAL.Net.Gallery.Avalonia.Views;

namespace GDAL.Net.Gallery.Avalonia;

// GDAL.Net Gallery (Avalonia). Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
// `--screenshot <dir>` renders the gallery off-screen (Avalonia.Headless + Skia) into PNGs for the documentation.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var shot = Array.IndexOf(args, "--screenshot");
        if (shot >= 0 && shot + 1 < args.Length) return Screenshots(args[shot + 1]);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static int Screenshots(string directory)
    {
        Directory.CreateDirectory(directory);
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var vm = new MainViewModel(new SampleContext());
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Wait(vm.InitializeAsync());
        Pump(1.5);

        var only = Environment.GetEnvironmentVariable("GALLERY_SHOTS")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool Want(string name) => only is null || only.Contains(name);

        void Save(string name)
        {
            var frame = window.CaptureRenderedFrame();
            #pragma warning disable CS0618 // PNG is the default encoding.
            frame?.Save(Path.Combine(directory, $"avalonia-{name}.png"));
#pragma warning restore CS0618
            Console.WriteLine($"saved avalonia-{name}.png");
        }

        if (Want("home")) Save("home");

        foreach (var id in new[] { "hillshade", "viewshed", "composites", "sql", "climate-cube", "ndvi", "land-cover", "contours", "cities", "transform-coordinates" })
        {
            if (!Want(id)) continue;
            Wait(vm.OpenAndRunAsync(id));
            Pump(3.5);
            Save(id);
        }

        if (Want("components"))
        {
            vm.ShowComponentsCommand.Execute(null);
            Pump(6);
            Save("components");
        }

        if (Want("dark"))
        {
            vm.IsDark = true;
            Wait(vm.OpenAndRunAsync("raster-styling"));
            Pump(3.5);
            Save("dark");
            vm.ShowHomeCommand.Execute(null);
            Pump(1.5);
            Save("dark-home");
        }

        if (Want("indonesian"))
        {
            vm.IsDark = false;
            vm.IsIndonesian = true;
            Wait(vm.OpenAndRunAsync("ndvi"));
            Pump(3.5);
            Save("indonesian");
        }

        window.Close();
        return 0;
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted) Pump(0.05);
        task.GetAwaiter().GetResult();
    }

    private static void Pump(double seconds)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }
    }
}
