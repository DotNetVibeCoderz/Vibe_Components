using Avalonia.Headless;

namespace AquaCulture.Gallery.Desktop;

/// <summary>Off-screen renderer used to produce documentation screenshots of the desktop gallery.</summary>
internal static class Screenshots
{
    public static bool Active { get; private set; }

#pragma warning disable CS0618
    public static int Capture(string outDir, string lang)
    {
        Active = true;
        Directory.CreateDirectory(outDir);
        var window = new MainWindow { Width = 1440, Height = 1080 };
        window.Show();
        if (lang == "id") window.SetLang("id");
        foreach (var slug in new[] { "water", "twin", "growth", "hydro", "disease", "stock", "agent" })
        {
            window.Navigate(slug);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            var file = Path.Combine(outDir, $"desktop-{slug}{(lang == "id" ? "-id" : "")}.png");
            frame?.Save(file, (int?)null);
            Console.WriteLine(file);
        }
        window.Close();
        return 0;
    }
}
