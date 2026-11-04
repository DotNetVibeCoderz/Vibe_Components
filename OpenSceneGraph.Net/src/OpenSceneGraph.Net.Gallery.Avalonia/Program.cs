using System.Globalization;
using Avalonia;

namespace OpenSceneGraph.Net.Gallery.Desktop;

/// <summary>Command line options.</summary>
/// <remarks>
/// <code>
/// OpenSceneGraph.Net.Gallery                                    normal UI
/// OpenSceneGraph.Net.Gallery --sample flight-sim [--lang id]    open a sample directly
/// OpenSceneGraph.Net.Gallery --screenshot all docs/images/gallery [--width 1280 --height 720 --frames 90]
/// OpenSceneGraph.Net.Gallery --ui-screenshot docs/images/gallery-ui.png [--sample id] [--width 1600 --height 960]
/// </code>
/// </remarks>
public sealed class GalleryOptions
{
    public string? ScreenshotTarget { get; set; }
    public string OutputDir { get; set; } = "screenshots";
    public string? UiScreenshotPath { get; set; }
    public string? OpenSample { get; set; }
    public string? Lang { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Frames { get; set; } = 90;
    public bool WidthSet { get; set; }

    public static GalleryOptions Parse(string[] args)
    {
        var o = new GalleryOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            int NextInt() => int.Parse(Next(), CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case "--screenshot": o.ScreenshotTarget = Next(); if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o.OutputDir = Next(); break;
                case "--ui-screenshot": o.UiScreenshotPath = Next(); break;
                case "--sample": o.OpenSample = Next(); break;
                case "--lang": o.Lang = Next(); break;
                case "--width": o.Width = NextInt(); o.WidthSet = true; break;
                case "--height": o.Height = NextInt(); break;
                case "--frames": o.Frames = NextInt(); break;
            }
        }
        return o;
    }
}

internal static class Program
{
    public static GalleryOptions Options { get; private set; } = new();
    public static int ExitCode { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Options = GalleryOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return ExitCode;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
