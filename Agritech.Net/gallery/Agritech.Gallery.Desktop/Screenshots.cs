using Agritech.Gallery.Shared;
using Avalonia.Headless;

namespace Agritech.Gallery.Desktop;

/// <summary>Off-screen renderer used to produce documentation screenshots of the desktop gallery.</summary>
internal static class Screenshots
{
    public static bool Active { get; private set; }

    public static int Capture(string outDir, string lang)
    {
        Active = true;
        Directory.CreateDirectory(outDir);
        var window = new MainWindow { Width = 1440, Height = 1000 };
        window.Show();
        if (lang == "id") window.SetLang("id");
        foreach (var slug in new[] { "ndvi", "simulation", "twin", "drones", "soil", "agent" })
        {
            window.Navigate(slug);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            var file = Path.Combine(outDir, $"desktop-{slug}{(lang == "id" ? "-id" : "")}.png");
#pragma warning disable CS0618 // PNG default encoder options are what we want
            frame?.Save(file);
#pragma warning restore CS0618
            Console.WriteLine(file);
        }
        window.Close();
        return 0;
    }
}
