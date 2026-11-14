using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;

namespace Medical.Gallery.Desktop;

// Medical.Net Gallery (desktop) — Made by Gravicode Studios, led by Kang Fadhil.
internal static class Program
{
    /// <summary>
    /// Normal run: desktop window. <c>--screenshot &lt;dir&gt; [en|id]</c> renders every demo off-screen with the headless
    /// Skia backend and saves PNGs (used for the documentation).
    /// </summary>
    /// <summary>True in --screenshot mode: GPU views render offscreen to images instead of live controls.</summary>
    public static bool Headless { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            Headless = true;
            BuildHeadless().SetupWithoutStarting();
            return Screenshots.Capture(args[1], args.Length > 2 ? args[2] : "en", args.Length > 3 ? args[3].Split(',') : null);
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static AppBuilder BuildHeadless() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Off-screen renderer producing documentation screenshots of the desktop gallery.</summary>
internal static class Screenshots
{
    public static int Capture(string outDir, string lang, string[]? only = null)
    {
        Directory.CreateDirectory(outDir);
        var window = new MainWindow { Width = 1440, Height = 960 };
        window.Show();
        window.SetLang(lang);
        foreach (var slug in new[] { "news2", "cds", "dicom", "hemorrhage", "ecg", "terminology", "risk", "assistant", "fhir-server", "hl7-orders", "satusehat", "bpjs", "mpr", "volume3d", "codecs", "imaging-ai", "ecg12", "icu-scores", "cvrisk", "growth", "drug-safety", "clinical-nlp", "guidelines", "federated", "his", "live-monitoring", "offline-sync" }.Where(s => only is null || only.Contains(s)))
        {
            window.Navigate(slug);
            for (int i = 0; i < 5; i++) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            var file = Path.Combine(outDir, $"desktop-{slug}{(lang == "id" ? "-id" : "")}.png");
#pragma warning disable CS0618 // default PNG encoder options are what we want
            frame?.Save(file);
#pragma warning restore CS0618
            Console.WriteLine(file);
        }
        window.Close();
        return 0;
    }
}
