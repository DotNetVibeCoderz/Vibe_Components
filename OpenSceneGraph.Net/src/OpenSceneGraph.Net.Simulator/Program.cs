using System.Globalization;
using Avalonia;

namespace OpenSceneGraph.Net.Simulator;

/// <summary>Command line options.</summary>
/// <remarks>
/// <code>
/// OpenSceneGraph.Net.Simulator [--vehicle aircraft|quadcopter|ground|ship] [--terrain mountains|desert|highlands|archipelago]
///                              [--hour 17.5] [--visibility 4000] [--weather none|rain|snow] [--camera chase|cockpit] [--dis] [--lang id]
/// OpenSceneGraph.Net.Simulator --screenshot docs/images/simulator.png [--frames 240] [--width 1360 --height 760] [scenario options]
/// </code>
/// </remarks>
public sealed class SimOptions
{
    public SimSettings Settings { get; } = new();
    public string? ScreenshotPath { get; set; }
    public int Frames { get; set; } = 240;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool Dis { get; set; }
    public CameraMode Camera { get; set; } = CameraMode.Chase;
    public string? Lang { get; set; }

    public static SimOptions Parse(string[] args)
    {
        var o = new SimOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            float NextFloat() => float.Parse(Next(), CultureInfo.InvariantCulture);
            T NextEnum<T>() where T : struct, Enum => Enum.Parse<T>(Next(), ignoreCase: true);
            switch (args[i])
            {
                case "--screenshot": o.ScreenshotPath = Next(); break;
                case "--frames": o.Frames = (int)NextFloat(); break;
                case "--width": o.Width = (int)NextFloat(); break;
                case "--height": o.Height = (int)NextFloat(); break;
                case "--vehicle": o.Settings.Vehicle = NextEnum<VehicleKind>(); break;
                case "--terrain": o.Settings.Terrain = NextEnum<TerrainKind>(); break;
                case "--seed": o.Settings.Seed = (int)NextFloat(); break;
                case "--hour": o.Settings.Hour = NextFloat(); break;
                case "--visibility": o.Settings.Visibility = NextFloat(); break;
                case "--weather": o.Settings.Precipitation = NextEnum<Precipitation>(); break;
                case "--camera": o.Camera = NextEnum<CameraMode>(); break;
                case "--dis": o.Dis = true; break;
                case "--lang": o.Lang = Next(); break;
            }
        }
        return o;
    }
}

internal static class Program
{
    public static SimOptions Options { get; private set; } = new();
    public static int ExitCode { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Options = SimOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return ExitCode;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
