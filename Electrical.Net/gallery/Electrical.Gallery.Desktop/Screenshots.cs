using Avalonia.Headless;
using Electrical.Gallery.Shared;

namespace Electrical.Gallery.Desktop;

/// <summary>Off-screen renderer used to produce documentation screenshots of the desktop gallery.</summary>
internal static class Screenshots
{
    public static int Capture(string outDir, string lang, string theme)
    {
        Directory.CreateDirectory(outDir);
        Pal.Dark = theme != "light";
        var window = new MainWindow { Width = 1440, Height = 1000 };
        window.Show();
        window.SetLang(lang == "id" ? "id" : "en");
        string suffix = (lang == "id" ? "-id" : "") + (theme == "light" ? "-light" : "");
        foreach (var id in GalleryCatalog.Demos.Select(d => d.Id).Prepend(MainWindow.HomeId))
        {
            window.Navigate(id);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            var file = Path.Combine(outDir, $"desktop-{id}{suffix}.png");
#pragma warning disable CS0618 // default PNG encoder options are what we want
            frame?.Save(file);
#pragma warning restore CS0618
            Console.WriteLine(file);
        }
        window.Close();
        return 0;
    }
}
